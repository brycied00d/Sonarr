﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Extras.Files;
using NzbDrone.Core.Extras.Metadata.Files;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Extras.Metadata
{
    public class MetadataService : ExtraFileManager<MetadataFile>
    {
        private readonly IMetadataFactory _metadataFactory;
        private readonly ICleanMetadataService _cleanMetadataService;
        private readonly IDiskTransferService _diskTransferService;
        private readonly IDiskProvider _diskProvider;
        private readonly IHttpClient _httpClient;
        private readonly IMediaFileAttributeService _mediaFileAttributeService;
        private readonly IMetadataFileService _metadataFileService;
        private readonly Logger _logger;

        public MetadataService(IConfigService configService,
                               IDiskTransferService diskTransferService,
                               IMetadataFactory metadataFactory,
                               ICleanMetadataService cleanMetadataService,
                               IDiskProvider diskProvider,
                               IHttpClient httpClient,
                               IMediaFileAttributeService mediaFileAttributeService,
                               IMetadataFileService metadataFileService,
                               Logger logger)
            : base(configService, diskTransferService, metadataFileService)
        {
            _metadataFactory = metadataFactory;
            _cleanMetadataService = cleanMetadataService;
            _diskTransferService = diskTransferService;
            _diskProvider = diskProvider;
            _httpClient = httpClient;
            _mediaFileAttributeService = mediaFileAttributeService;
            _metadataFileService = metadataFileService;
            _logger = logger;
        }

        public override int Order
        {
            get
            {
                return 0;
            }
        }

        public override IEnumerable<ExtraFile> CreateAfterSeriesScan(Series series, List<EpisodeFile> episodeFiles)
        {
            var metadataFiles = _metadataFileService.GetFilesBySeries(series.Id);
            _cleanMetadataService.Clean(series);

            if (!_diskProvider.FolderExists(series.Path))
            {
                _logger.Info("Series folder does not exist, skipping metadata creation");
                return Enumerable.Empty<MetadataFile>();
            }

            var files = new List<MetadataFile>();

            foreach (var consumer in _metadataFactory.Enabled())
            {
                var consumerFiles = GetMetadataFilesForConsumer(consumer, metadataFiles);

                files.AddIfNotNull(ProcessSeriesMetadata(consumer, series, consumerFiles));
                files.AddRange(ProcessSeriesImages(consumer, series, consumerFiles));
                files.AddRange(ProcessSeasonImages(consumer, series, consumerFiles));

                foreach (var episodeFile in episodeFiles)
                {
                    files.AddIfNotNull(ProcessEpisodeMetadata(consumer, series, episodeFile, consumerFiles));
                    files.AddRange(ProcessEpisodeImages(consumer, series, episodeFile, consumerFiles));
                }
            }

            _metadataFileService.Upsert(files);

            return files;
        }

        public override IEnumerable<ExtraFile> CreateAfterEpisodeImport(Series series, EpisodeFile episodeFile)
        {
            var files = new List<MetadataFile>();

            foreach (var consumer in _metadataFactory.Enabled())
            {

                files.AddIfNotNull(ProcessEpisodeMetadata(consumer, series, episodeFile, new List<MetadataFile>()));
                files.AddRange(ProcessEpisodeImages(consumer, series, episodeFile, new List<MetadataFile>()));
            }

            _metadataFileService.Upsert(files);

            return files;
        }

        public override IEnumerable<ExtraFile> CreateAfterEpisodeImport(Series series, string seriesFolder, string seasonFolder)
        {
            var metadataFiles = _metadataFileService.GetFilesBySeries(series.Id);

            if (seriesFolder.IsNullOrWhiteSpace() && seasonFolder.IsNullOrWhiteSpace())
            {
                return new List<MetadataFile>();
            }

            var files = new List<MetadataFile>();

            foreach (var consumer in _metadataFactory.Enabled())
            {
                var consumerFiles = GetMetadataFilesForConsumer(consumer, metadataFiles);

                if (seriesFolder.IsNotNullOrWhiteSpace())
                {
                    files.AddIfNotNull(ProcessSeriesMetadata(consumer, series, consumerFiles));
                    files.AddRange(ProcessSeriesImages(consumer, series, consumerFiles));
                }

                if (seasonFolder.IsNotNullOrWhiteSpace())
                {
                    files.AddRange(ProcessSeasonImages(consumer, series, consumerFiles));
                }
            }

            _metadataFileService.Upsert(files);

            return files;
        }

        public override IEnumerable<ExtraFile> MoveFilesAfterRename(Series series, List<EpisodeFile> episodeFiles)
        {
            var metadataFiles = _metadataFileService.GetFilesBySeries(series.Id);
            var movedFiles = new List<MetadataFile>();

            // TODO: Move EpisodeImage and EpisodeMetadata metadata files, instead of relying on consumers to do it
            // (Xbmc's EpisodeImage is more than just the extension)

            foreach (var consumer in _metadataFactory.GetAvailableProviders())
            {
                foreach (var episodeFile in episodeFiles)
                {
                    var metadataFilesForConsumer = GetMetadataFilesForConsumer(consumer, metadataFiles).Where(m => m.EpisodeFileId == episodeFile.Id).ToList();

                    foreach (var metadataFile in metadataFilesForConsumer)
                    {
                        var newFileName = consumer.GetFilenameAfterMove(series, episodeFile, metadataFile);
                        var existingFileName = Path.Combine(series.Path, metadataFile.RelativePath);

                        if (newFileName.PathNotEquals(existingFileName))
                        {
                            try
                            {
                                _diskProvider.MoveFile(existingFileName, newFileName);
                                metadataFile.RelativePath = series.Path.GetRelativePath(newFileName);
                                movedFiles.Add(metadataFile);
                            }
                            catch (Exception ex)
                            {
                                _logger.Warn(ex, "Unable to move metadata file: {0}", existingFileName);
                            }
                        }
                    }
                }
            }

            _metadataFileService.Upsert(movedFiles);

            return movedFiles;
        }

        public override ExtraFile Import(Series series, EpisodeFile episodeFile, string path, string extension, bool readOnly)
        {
            return null;
        }

        private List<MetadataFile> GetMetadataFilesForConsumer(IMetadata consumer, List<MetadataFile> seriesMetadata)
        {
            return seriesMetadata.Where(c => c.Consumer == consumer.GetType().Name).ToList();
        }

        private MetadataFile ProcessSeriesMetadata(IMetadata consumer, Series series, List<MetadataFile> existingMetadataFiles)
        {
            var seriesMetadata = consumer.SeriesMetadata(series);

            if (seriesMetadata == null)
            {
                return null;
            }

            var hash = seriesMetadata.Contents.SHA256Hash();

            var metadata = GetMetadataFile(series, existingMetadataFiles, e => e.Type == MetadataType.SeriesMetadata) ??
                               new MetadataFile
                               {
                                   SeriesId = series.Id,
                                   Consumer = consumer.GetType().Name,
                                   Type = MetadataType.SeriesMetadata
                               };

            if (hash == metadata.Hash)
            {
                if (seriesMetadata.RelativePath != metadata.RelativePath)
                {
                    metadata.RelativePath = seriesMetadata.RelativePath;

                    return metadata;
                }

                return null;
            }

            var fullPath = Path.Combine(series.Path, seriesMetadata.RelativePath);

            _logger.Debug("Writing Series Metadata to: {0}", fullPath);
            SaveMetadataFile(fullPath, seriesMetadata.Contents);

            metadata.Hash = hash;
            metadata.RelativePath = seriesMetadata.RelativePath;

            return metadata;
        }

        private MetadataFile ProcessEpisodeMetadata(IMetadata consumer, Series series, EpisodeFile episodeFile, List<MetadataFile> existingMetadataFiles)
        {
            var episodeMetadata = consumer.EpisodeMetadata(series, episodeFile);

            if (episodeMetadata == null)
            {
                return null;
            }

            var fullPath = Path.Combine(series.Path, episodeMetadata.RelativePath);

            var existingMetadata = GetMetadataFile(series, existingMetadataFiles, c => c.Type == MetadataType.EpisodeMetadata &&
                                                                                  c.EpisodeFileId == episodeFile.Id);

            if (existingMetadata != null)
            {
                var existingFullPath = Path.Combine(series.Path, existingMetadata.RelativePath);
                if (fullPath.PathNotEquals(existingFullPath))
                {
                    _diskTransferService.TransferFile(existingFullPath, fullPath, TransferMode.Move);
                    existingMetadata.RelativePath = episodeMetadata.RelativePath;
                }
            }

            var hash = episodeMetadata.Contents.SHA256Hash();

            var metadata = existingMetadata ??
                           new MetadataFile
                           {
                               SeriesId = series.Id,
                               SeasonNumber = episodeFile.SeasonNumber,
                               EpisodeFileId = episodeFile.Id,
                               Consumer = consumer.GetType().Name,
                               Type = MetadataType.EpisodeMetadata,
                               RelativePath = episodeMetadata.RelativePath
                           };

            if (hash == metadata.Hash)
            {
                return null;
            }

            _logger.Debug("Writing Episode Metadata to: {0}", fullPath);
            SaveMetadataFile(fullPath, episodeMetadata.Contents);

            metadata.Hash = hash;

            return metadata;
        }

        private List<MetadataFile> ProcessSeriesImages(IMetadata consumer, Series series, List<MetadataFile> existingMetadataFiles)
        {
            var result = new List<MetadataFile>();

            foreach (var image in consumer.SeriesImages(series))
            {
                if (_diskProvider.FileExists(image.RelativePath))
                {
                    _logger.Debug("Series image already exists: {0}", image.RelativePath);
                    continue;
                }

                var metadata = GetMetadataFile(series, existingMetadataFiles, c => c.Type == MetadataType.SeriesImage &&
                                                                              c.RelativePath == image.RelativePath) ??
                               new MetadataFile
                               {
                                   SeriesId = series.Id,
                                   Consumer = consumer.GetType().Name,
                                   Type = MetadataType.SeriesImage,
                                   RelativePath = image.RelativePath
                               };

                _diskProvider.CopyFile(image.Url, Path.Combine(series.Path, image.RelativePath));
                _mediaFileAttributeService.SetFilePermissions(image.RelativePath);

                result.Add(metadata);
            }

            return result;
        }

        private List<MetadataFile> ProcessSeasonImages(IMetadata consumer, Series series, List<MetadataFile> existingMetadataFiles)
        {
            var result = new List<MetadataFile>();

            foreach (var seasonItem in series.Seasons)
            {
                var season = seasonItem;

                foreach (var imageItem in consumer.SeasonImages(series, season))
                {
                    var image = imageItem;

                    if (_diskProvider.FileExists(image.RelativePath))
                    {
                        _logger.Debug("Season image already exists: {0}", image.RelativePath);
                        continue;
                    }

                    var metadata = GetMetadataFile(series, existingMetadataFiles, c => c.Type == MetadataType.SeasonImage &&
                                                                                  c.SeasonNumber == season.SeasonNumber &&
                                                                                  c.RelativePath == image.RelativePath) ??
                                new MetadataFile
                                {
                                    SeriesId = series.Id,
                                    SeasonNumber = season.SeasonNumber,
                                    Consumer = consumer.GetType().Name,
                                    Type = MetadataType.SeasonImage,
                                    RelativePath = image.RelativePath
                                };

                    DownloadImage(series, image.Url, image.RelativePath);

                    result.Add(metadata);
                }
            }

            return result;
        }

        private List<MetadataFile> ProcessEpisodeImages(IMetadata consumer, Series series, EpisodeFile episodeFile, List<MetadataFile> existingMetadataFiles)
        {
            var result = new List<MetadataFile>();

            foreach (var image in consumer.EpisodeImages(series, episodeFile))
            {
                var fullPath = Path.Combine(series.Path, image.RelativePath);

                if (_diskProvider.FileExists(fullPath))
                {
                    _logger.Debug("Episode image already exists: {0}", image.RelativePath);
                    continue;
                }

                var existingMetadata = GetMetadataFile(series, existingMetadataFiles, c => c.Type == MetadataType.EpisodeImage &&
                                                                                      c.EpisodeFileId == episodeFile.Id);

                if (existingMetadata != null)
                {
                    var existingFullPath = Path.Combine(series.Path, existingMetadata.RelativePath);
                    if (fullPath.PathNotEquals(existingFullPath))
                    {
                        _diskTransferService.TransferFile(existingFullPath, fullPath, TransferMode.Move);
                        existingMetadata.RelativePath = image.RelativePath;

                        return new List<MetadataFile>{ existingMetadata };
                    }
                }

                var metadata = existingMetadata ??
                               new MetadataFile
                               {
                                   SeriesId = series.Id,
                                   SeasonNumber = episodeFile.SeasonNumber,
                                   EpisodeFileId = episodeFile.Id,
                                   Consumer = consumer.GetType().Name,
                                   Type = MetadataType.EpisodeImage,
                                   RelativePath = image.RelativePath
                               };

                DownloadImage(series, image.Url, fullPath);

                result.Add(metadata);
            }

            return result;
        }

        private void DownloadImage(Series series, string url, string relativePath)
        {
            try
            {
                _httpClient.DownloadFile(url, Path.Combine(series.Path, relativePath));
                _mediaFileAttributeService.SetFilePermissions(relativePath);
            }
            catch (WebException e)
            {
                _logger.Warn("Couldn't download image {0} for {1}. {2}", url, series, e.Message);
            }
            catch (Exception e)
            {
                _logger.Error(e, "Couldn't download image " + url + " for " + series);
            }
        }

        private void SaveMetadataFile(string path, string contents)
        {
            _diskProvider.WriteAllText(path, contents);
            _mediaFileAttributeService.SetFilePermissions(path);
        }

        private MetadataFile GetMetadataFile(Series series, List<MetadataFile> existingMetadataFiles, Func<MetadataFile, bool> predicate)
        {
            var matchingMetadataFiles = existingMetadataFiles.Where(predicate).ToList();

            if (matchingMetadataFiles.Empty())
            {
                return null;
            }

            //Remove duplicate metadata files from DB and disk
            foreach (var file in matchingMetadataFiles.Skip(1))
            {
                var path = Path.Combine(series.Path, file.RelativePath);

                _logger.Debug("Removing duplicate Metadata file: {0}", path);

                _diskProvider.DeleteFile(path);
                _metadataFileService.Delete(file.Id);
            }

            
            return matchingMetadataFiles.First();
        }
    }
}