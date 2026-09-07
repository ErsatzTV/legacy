using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO.Abstractions;
using System.Security.Cryptography;
using System.Text;
using Blurhash.SkiaSharp;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.FFmpeg;
using ErsatzTV.Core.Interfaces.FFmpeg;
using ErsatzTV.Core.Interfaces.Images;
using ErsatzTV.Core.Interfaces.Metadata;
using SkiaSharp;

namespace ErsatzTV.Infrastructure.Images;

[SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms")]
public class ImageCache(IFileSystem fileSystem, ILocalFileSystem localFileSystem, ITempFilePool tempFilePool)
    : IImageCache
{
    public static string GetBlurHashFileName(string blurHash)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(blurHash);
        return Convert.ToBase64String(bytes).Replace("+", "_").Replace("/", "-").Replace("=", "");
    }

    public async Task<Either<BaseError, string>> SaveArtworkToCache(Stream stream, ArtworkKind artworkKind)
    {
        try
        {
            string tempFileName = tempFilePool.GetNextTempFile(TempFileCategory.CachedArtwork);
            // ReSharper disable once UseAwaitUsing
            using (var fs = new FileStream(tempFileName, FileMode.OpenOrCreate, FileAccess.Write))
            {
                // overwrite anything that's already there
                fs.SetLength(0);

                await stream.CopyToAsync(fs);
            }

            byte[] hash = await localFileSystem.GetHash(tempFileName);
            string hex = Convert.ToHexString(hash);
            string subfolder = hex[..2];
            string baseFolder = artworkKind switch
            {
                ArtworkKind.Poster => Path.Combine(FileSystemLayout.PosterCacheFolder, subfolder),
                ArtworkKind.Thumbnail => Path.Combine(FileSystemLayout.ThumbnailCacheFolder, subfolder),
                ArtworkKind.Logo => Path.Combine(FileSystemLayout.LogoCacheFolder, subfolder),
                ArtworkKind.FanArt => Path.Combine(FileSystemLayout.FanArtCacheFolder, subfolder),
                ArtworkKind.Watermark => Path.Combine(FileSystemLayout.WatermarkCacheFolder, subfolder),
                _ => FileSystemLayout.LegacyImageCacheFolder
            };
            string target = Path.Combine(baseFolder, hex);

            if (!Directory.Exists(baseFolder))
            {
                Directory.CreateDirectory(baseFolder);
            }

            await localFileSystem.CopyFile(tempFileName, target);

            return hex;
        }
        catch (Exception ex)
        {
            return BaseError.New(ex.Message);
        }
    }

    public async Task<Either<BaseError, string>> CopyArtworkToCache(string path, ArtworkKind artworkKind)
    {
        try
        {
            var filenameKey = $"{path}:{localFileSystem.GetLastWriteTime(path).ToFileTimeUtc()}";
            byte[] hash = SHA1.HashData(Encoding.UTF8.GetBytes(filenameKey));
            string hex = Convert.ToHexString(hash);
            string subfolder = hex[..2];
            string baseFolder = artworkKind switch
            {
                ArtworkKind.Poster => Path.Combine(FileSystemLayout.PosterCacheFolder, subfolder),
                ArtworkKind.Thumbnail => Path.Combine(FileSystemLayout.ThumbnailCacheFolder, subfolder),
                ArtworkKind.Logo => Path.Combine(FileSystemLayout.LogoCacheFolder, subfolder),
                ArtworkKind.FanArt => Path.Combine(FileSystemLayout.FanArtCacheFolder, subfolder),
                _ => FileSystemLayout.LegacyImageCacheFolder
            };
            string target = Path.Combine(baseFolder, hex);
            Either<BaseError, Unit> maybeResult = await localFileSystem.CopyFile(path, target);
            return maybeResult.Match<Either<BaseError, string>>(
                _ => hex,
                error => error);
        }
        catch (Exception ex)
        {
            return BaseError.New(ex.ToString());
        }
    }

    public bool IsCached(string path, ArtworkKind artworkKind)
    {
        string finalPath = GetPathForImage(path, artworkKind, Option<int>.None);
        return fileSystem.File.Exists(finalPath);
    }

    public virtual string GetPathForImage(string fileName, ArtworkKind artworkKind, Option<int> maybeMaxHeight)
    {
        string subfolder = maybeMaxHeight.Match(
            maxHeight => Path.Combine(maxHeight.ToString(CultureInfo.InvariantCulture), fileName[..2]),
            () => fileName[..2]);

        string baseFolder = artworkKind switch
        {
            ArtworkKind.Poster => Path.Combine(FileSystemLayout.PosterCacheFolder, subfolder),
            ArtworkKind.Thumbnail => Path.Combine(FileSystemLayout.ThumbnailCacheFolder, subfolder),
            ArtworkKind.Logo => Path.Combine(FileSystemLayout.LogoCacheFolder, subfolder),
            ArtworkKind.FanArt => Path.Combine(FileSystemLayout.FanArtCacheFolder, subfolder),
            ArtworkKind.Watermark => Path.Combine(FileSystemLayout.WatermarkCacheFolder, subfolder),
            _ => FileSystemLayout.LegacyImageCacheFolder
        };

        return Path.Combine(baseFolder, fileName);
    }

    public Task<string> CalculateBlurHash(string fileName, ArtworkKind artworkKind, int x, int y)
    {
        string targetFile = GetPathForImage(fileName, artworkKind, Option<int>.None);

        // ReSharper disable once ConvertToUsingDeclaration
        using (var image = SKBitmap.Decode(targetFile))
        {
            // resize before calculating blur hash; it doesn't need giant images
            if (image.Height > 200 || image.Width > 200)
            {
                int width, height;
                if (image.Width > image.Height)
                {
                    width = 200;
                    height = (int)Math.Round(image.Height * (200.0 / image.Width));
                }
                else
                {
                    height = 200;
                    width = (int)Math.Round(image.Width * (200.0 / image.Height));
                }

                var info = new SKImageInfo(width, height);

                // ReSharper disable once ConvertToUsingDeclaration
                using (SKBitmap resized = image.Resize(info, SKSamplingOptions.Default))
                {
                    return Task.FromResult(Blurhasher.Encode(resized, x, y));
                }
            }

            return Task.FromResult(Blurhasher.Encode(image, x, y));
        }
    }

    public Task<string> WriteBlurHash(string blurHash, IDisplaySize targetSize)
    {
        string base64 = GetBlurHashFileName(blurHash);
        string targetFile = GetPathForImage(base64, ArtworkKind.Poster, targetSize.Height) ?? string.Empty;
        if (!fileSystem.File.Exists(targetFile))
        {
            string folder = Path.GetDirectoryName(targetFile);
            localFileSystem.EnsureFolderExists(folder);

            // ReSharper disable once ConvertToUsingDeclaration
            using (FileSystemStream fs = fileSystem.File.OpenWrite(targetFile))
            {
                using (SKBitmap image = Blurhasher.Decode(blurHash, targetSize.Width, targetSize.Height))
                {
                    using (SKData data = image.Encode(SKEncodedImageFormat.Png, 100))
                    {
                        data.SaveTo(fs);
                    }
                }
            }
        }

        return Task.FromResult(targetFile);
    }
}
