﻿using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.IO;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using RokuMetadata.Configuration;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RokuMetadata.Drawing
{
    public class VideoProcessor
    {
        private readonly ILogger _logger;
        private readonly IMediaEncoder _mediaEncoder;
        private readonly IFileSystem _fileSystem;
        private readonly IApplicationPaths _appPaths;

        public VideoProcessor(ILogger logger, IMediaEncoder mediaEncoder, IFileSystem fileSystem, IApplicationPaths appPaths)
        {
            _logger = logger;
            _mediaEncoder = mediaEncoder;
            _fileSystem = fileSystem;
            _appPaths = appPaths;
        }

        public async Task Run(Video item, CancellationToken cancellationToken)
        {
            var mediaSources = ((IHasMediaSources)item).GetMediaSources(false)
                .ToList();

            var modifier = GetItemModifier(item);

            var audioMode = Plugin.Instance.Configuration.AudioOutputMode;
            var profile = new RokuDeviceProfile(audioMode >= AudioOutputMode.DDPlus, audioMode >= AudioOutputMode.DTS);

            foreach (var mediaSource in mediaSources)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var streamInfo = new StreamBuilder().BuildVideoItem(new VideoOptions
                {
                    Context = EncodingContext.Streaming,
                    ItemId = item.Id.ToString("N"),
                    MediaSources = new List<MediaSourceInfo> { mediaSource },
                    Profile = profile,
                    MaxBitrate = Plugin.Instance.Configuration.MaxBitrate,
                    DeviceId = Guid.NewGuid().ToString("N"),
                    AudioStreamIndex = mediaSource.MediaStreams.Where(i => i.Type == MediaStreamType.Audio).Select(i => i.Index).FirstOrDefault(),
                    MediaSourceId = mediaSource.Id
                });

                if (!streamInfo.IsDirectStream)
                {
                    continue;
                }

                if (Plugin.Instance.Configuration.EnableHdThumbnails)
                {
                    await Run(item, modifier, mediaSource, 320, cancellationToken).ConfigureAwait(false);
                }
                if (Plugin.Instance.Configuration.EnableSdThumbnails)
                {
                    await Run(item, modifier, mediaSource, 240, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private async Task Run(Video item, string itemModifier, MediaSourceInfo mediaSource, int width, CancellationToken cancellationToken)
        {
            var path = GetBifPath(item, itemModifier, mediaSource.Id, width);

            if (!File.Exists(path))
            {
                await BifWriterSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

                try
                {
                    if (!File.Exists(path))
                    {
                        await CreateBif(path, width, mediaSource, cancellationToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    BifWriterSemaphore.Release();
                }
            }
        }

        public static string GetItemModifier(BaseItem item)
        {
            return item.DateModified.Ticks.ToString(CultureInfo.InvariantCulture);
        }

        public static string GetBifPath(BaseItem item, string mediaSourceId, int width)
        {
            return GetBifPath(item, GetItemModifier(item), mediaSourceId, width);
        }

        public static string GetBifPath(BaseItem item, string modifier, string mediaSourceId, int width)
        {
            return Path.Combine(item.GetInternalMetadataPath(), "bif", modifier, mediaSourceId, width.ToString(CultureInfo.InvariantCulture), "index.bif");
        }

        private async Task CreateBif(string path, int width, MediaSourceInfo mediaSource, CancellationToken cancellationToken)
        {
            _logger.Info("Creating roku thumbnails at {0} width, for {1}", width, mediaSource.Path);

            var protocol = mediaSource.Protocol;

            var inputPath = MediaEncoderHelpers.GetInputArgument(mediaSource.Path, protocol, null, mediaSource.PlayableStreamFileNames);

            var directory = Path.GetDirectoryName(path);

            Directory.CreateDirectory(directory);

            DeleteImages(directory);

            await _mediaEncoder.ExtractVideoImagesOnInterval(inputPath, protocol, mediaSource.Video3DFormat,
                    TimeSpan.FromSeconds(10), directory, "img_", width, cancellationToken)
                    .ConfigureAwait(false);

            var images = new DirectoryInfo(directory)
                .EnumerateFiles()
                .Where(img => string.Equals(img.Extension, ".jpg", StringComparison.Ordinal))
                .OrderBy(i => i.FullName)
                .ToList();

            using (var fs = _fileSystem.GetFileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, true))
            {
                await CreateBif(fs, images).ConfigureAwait(false);
            }

            DeleteImages(directory);
        }

        private static readonly SemaphoreSlim BifWriterSemaphore = new SemaphoreSlim(1, 1);
        public async Task<string> GetEmptyBif()
        {
            var path = Path.Combine(_appPaths.CachePath, "roku-thumbs", "empty.bif");

            if (!File.Exists(path))
            {
                await BifWriterSemaphore.WaitAsync().ConfigureAwait(false);

                try
                {
                    if (!File.Exists(path))
                    {
                        using (var fs = _fileSystem.GetFileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, true))
                        {
                            await CreateBif(fs, new List<FileInfo>()).ConfigureAwait(false);
                        }
                    }
                }
                finally
                {
                    BifWriterSemaphore.Release();
                }
            }

            return path;
        }

        public async Task CreateBif(Stream stream, List<FileInfo> images)
        {
            var magicNumber = new byte[] { 0x89, 0x42, 0x49, 0x46, 0x0d, 0x0a, 0x1a, 0x0a };
            await stream.WriteAsync(magicNumber, 0, magicNumber.Length);

            // version
            var bytes = GetBytes(0);
            await stream.WriteAsync(bytes, 0, bytes.Length);

            // image count
            bytes = GetBytes(images.Count);
            await stream.WriteAsync(bytes, 0, bytes.Length);

            // interval in ms
            bytes = GetBytes(10000);
            await stream.WriteAsync(bytes, 0, bytes.Length);

            // reserved
            for (var i = 20; i <= 63; i++)
            {
                bytes = new byte[] { 0x00 };
                await stream.WriteAsync(bytes, 0, bytes.Length);
            }

            // write the bif index
            var index = 0;
            long imageOffset = 64 + (8 * images.Count) + 8;

            foreach (var img in images)
            {
                bytes = GetBytes(index);
                await stream.WriteAsync(bytes, 0, bytes.Length);

                bytes = GetBytes(imageOffset);
                await stream.WriteAsync(bytes, 0, bytes.Length);

                imageOffset += img.Length;

                index++;
            }

            bytes = new byte[] { 0xff, 0xff, 0xff, 0xff };
            await stream.WriteAsync(bytes, 0, bytes.Length);

            bytes = GetBytes(imageOffset);
            await stream.WriteAsync(bytes, 0, bytes.Length);

            // write the images
            foreach (var img in images)
            {
                using (var imgStream = _fileSystem.GetFileStream(img.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, true))
                {
                    await imgStream.CopyToAsync(stream).ConfigureAwait(false);
                }
            }
        }

        private void DeleteImages(string directory)
        {
            var images = new DirectoryInfo(directory)
                .EnumerateFiles()
                .Where(img => string.Equals(img.Extension, ".jpg", StringComparison.Ordinal))
                .ToList();

            foreach (var file in images)
            {
                try
                {
                    file.Delete();
                }
                catch (Exception ex)
                {
                    _logger.ErrorException("Error deleting {0}", ex, file.FullName);
                }
            }
        }

        private byte[] GetBytes(int value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            if (!BitConverter.IsLittleEndian)
                Array.Reverse(bytes);
            return bytes;
        }

        private byte[] GetBytes(long value)
        {
            var intVal = Convert.ToInt32(value);
            return GetBytes(intVal);

            //byte[] bytes = BitConverter.GetBytes(value);
            //if (BitConverter.IsLittleEndian)
            //    Array.Reverse(bytes);
            //return bytes;
        }
    }
}