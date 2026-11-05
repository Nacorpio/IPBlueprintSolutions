using SkiaSharp;

namespace IPBlueprint.Store.Shop;

/// <summary>The two files made from one uploaded gallery photo.</summary>
public sealed record SavedGalleryPhoto(
    string FileName, int Width, int Height,
    string ThumbnailFileName, int ThumbnailWidth, int ThumbnailHeight);

/// <summary>
/// Turns an uploaded photo into a web-sized copy and a thumbnail. Both are re-encoded as WebP, which also
/// drops the camera metadata (phones put the GPS position of where the photo was taken in there).
/// </summary>
public sealed class GalleryPhotos(StoragePaths paths, ILogger<GalleryPhotos> logger)
{
    public const long MaxUploadBytes = 20 * 1024 * 1024;

    /// <summary>The whole upload form, so a few photos can go up at once.</summary>
    public const long MaxRequestBytes = 100 * 1024 * 1024;

    /// <summary>Longest side of the photo shown when a visitor opens it.</summary>
    public const int LargeSize = 2000;

    /// <summary>Longest side of the grid thumbnail (twice the shown size, for sharp phone screens).</summary>
    public const int ThumbnailSize = 800;

    /// <summary>Refuse anything bigger than this before decoding, so a crafted file can't eat all the memory.</summary>
    private const long MaxSourcePixels = 120_000_000;

    private const int WebpQuality = 82;

    public async Task<(SavedGalleryPhoto? Photo, string? Error)> SaveAsync(IFormFile file, CancellationToken ct = default)
    {
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!FileStore.ImageExtensions.Contains(ext)) return (null, "Please upload JPG, PNG or WebP images.");
        if (file.Length > MaxUploadBytes) return (null, "Each photo can be at most 20 MB.");
        if (!await FileStore.LooksLikeImageAsync(file, ct)) return (null, "That file doesn't look like an image.");

        using var buffer = new MemoryStream((int)file.Length);
        await file.CopyToAsync(buffer, ct);

        try
        {
            var photo = Process(buffer.ToArray());
            return photo is null ? (null, "That image couldn't be read. Try saving it again as JPG.") : (photo, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not process gallery upload {FileName}", file.FileName);
            return (null, "That image couldn't be read. Try saving it again as JPG.");
        }
    }

    public void Delete(params string?[] fileNames)
    {
        foreach (var name in fileNames)
        {
            if (string.IsNullOrEmpty(name) || name != Path.GetFileName(name)) continue;
            var path = Path.Combine(paths.GalleryImages, name);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private SavedGalleryPhoto? Process(byte[] data)
    {
        using var oriented = DecodeUpright(data, LargeSize);
        if (oriented is null) return null;

        var id = Guid.NewGuid().ToString("N");
        var (large, lw, lh) = WriteWebp(oriented, LargeSize, paths.GalleryImages, $"{id}.webp");
        var (thumb, tw, th) = WriteWebp(oriented, ThumbnailSize, paths.GalleryImages, $"{id}-thumb.webp");
        return new SavedGalleryPhoto(large, lw, lh, thumb, tw, th);
    }

    /// <summary>
    /// Decodes an uploaded image and turns it the right way up. The result has no metadata at all, so whatever is
    /// written from it carries no camera data or GPS position. Null when the file can't be read or is too big.
    /// </summary>
    internal static SKBitmap? DecodeUpright(byte[] data, int longestSide)
    {
        using var codec = SKCodec.Create(new SKMemoryStream(data));
        if (codec is null) return null;

        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0 || (long)info.Width * info.Height > MaxSourcePixels) return null;

        using var decoded = Decode(codec, longestSide);
        return decoded is null ? null : Orient(decoded, codec.EncodedOrigin);
    }

    /// <summary>
    /// Decodes into sRGB. JPEGs can be decoded straight at a smaller size, which saves a lot of memory for
    /// big phone photos; we only ask for that when the result is still at least as big as we need.
    /// </summary>
    private static SKBitmap? Decode(SKCodec codec, int longestSide)
    {
        var size = codec.Info.Size;
        var longest = Math.Max(size.Width, size.Height);
        if (longest > longestSide)
        {
            var scaled = codec.GetScaledDimensions((float)longestSide / longest);
            if (Math.Max(scaled.Width, scaled.Height) >= longestSide) size = scaled;
        }

        var target = new SKImageInfo(size.Width, size.Height, SKColorType.Rgba8888, SKAlphaType.Premul, SKColorSpace.CreateSrgb());
        var bitmap = new SKBitmap(target);
        var result = codec.GetPixels(target, bitmap.GetPixels());
        if (result is SKCodecResult.Success or SKCodecResult.IncompleteInput) return bitmap;

        bitmap.Dispose();
        return null;
    }

    /// <summary>Phones store photos sideways and note the rotation in the metadata. Apply it, since we drop the metadata.</summary>
    internal static SKBitmap Orient(SKBitmap source, SKEncodedOrigin origin)
    {
        int w = source.Width, h = source.Height;
        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var result = new SKBitmap(new SKImageInfo(swap ? h : w, swap ? w : h, source.ColorType, source.AlphaType, source.ColorSpace));

        using var canvas = new SKCanvas(result);
        switch (origin)
        {
            case SKEncodedOrigin.TopRight: canvas.Translate(w, 0); canvas.Scale(-1, 1); break;
            case SKEncodedOrigin.BottomRight: canvas.Translate(w, h); canvas.RotateDegrees(180); break;
            case SKEncodedOrigin.BottomLeft: canvas.Translate(0, h); canvas.Scale(1, -1); break;
            case SKEncodedOrigin.LeftTop: canvas.RotateDegrees(90); canvas.Scale(1, -1); break;
            case SKEncodedOrigin.RightTop: canvas.Translate(h, 0); canvas.RotateDegrees(90); break;
            case SKEncodedOrigin.RightBottom: canvas.Translate(h, w); canvas.RotateDegrees(90); canvas.Scale(-1, 1); break;
            case SKEncodedOrigin.LeftBottom: canvas.Translate(0, w); canvas.RotateDegrees(-90); break;
        }
        canvas.DrawBitmap(source, 0, 0);
        return result;
    }

    /// <summary>Writes a WebP copy no bigger than <paramref name="longestSide"/> into <paramref name="folder"/>.</summary>
    internal static (string FileName, int Width, int Height) WriteWebp(SKBitmap source, int longestSide, string folder, string fileName)
    {
        var scale = Math.Min(1.0, (double)longestSide / Math.Max(source.Width, source.Height));
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));

        using var resized = scale < 1.0
            ? source.Resize(new SKImageInfo(width, height, source.ColorType, source.AlphaType, source.ColorSpace),
                new SKSamplingOptions(SKCubicResampler.Mitchell))
            : source.Copy();

        using var image = SKImage.FromBitmap(resized);
        using var encoded = image.Encode(SKEncodedImageFormat.Webp, WebpQuality);
        using (var output = File.Create(Path.Combine(folder, fileName)))
        {
            encoded.SaveTo(output);
        }
        return (fileName, width, height);
    }
}
