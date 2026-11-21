using IPBlueprint.Store.Data;
using IPBlueprint.Store.Localization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace IPBlueprint.Store.Shop;

/// <summary>Saves uploaded files under random names, after checking type and size.</summary>
public sealed class FileStore(StoragePaths paths, IStringLocalizer<SharedResource> localizer, ILogger<FileStore> logger)
{
    public const long MaxProductImageBytes = 10 * 1024 * 1024;

    /// <summary>Longest side of a saved product photo.</summary>
    public const int ProductImageSize = 1600;
    public const long MaxAttachmentBytes = 25 * 1024 * 1024;

    internal static readonly HashSet<string> ImageExtensions = [".jpg", ".jpeg", ".png", ".webp"];

    public static readonly IReadOnlySet<string> AttachmentExtensions =
        new HashSet<string>([".jpg", ".jpeg", ".png", ".webp", ".pdf", ".stl", ".3mf", ".obj", ".step", ".stp"]);

    public static string AttachmentAcceptList => string.Join(',', AttachmentExtensions);

    public async Task<(string? FileName, string? Error)> SaveProductImageAsync(IFormFile file, CancellationToken ct = default)
    {
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!ImageExtensions.Contains(ext)) return (null, "Please upload a JPG, PNG or WebP image.");
        if (file.Length > MaxProductImageBytes) return (null, "Images can be at most 10 MB.");
        if (!await LooksLikeImageAsync(file, ct)) return (null, "That file doesn't look like an image.");

        using var buffer = new MemoryStream((int)file.Length);
        await file.CopyToAsync(buffer, ct);
        return SaveProductImage(buffer.ToArray()) is { } name
            ? (name, null)
            : (null, "That image couldn't be read. Try saving it again as JPG.");
    }

    /// <summary>
    /// Re-encodes a product photo as WebP, the right way up and without metadata. Phone photos carry the GPS position
    /// of where they were taken, and these are public, so the original file is never kept.
    /// </summary>
    internal string? SaveProductImage(byte[] data)
    {
        try
        {
            using var upright = GalleryPhotos.DecodeUpright(data, ProductImageSize);
            return upright is null ? null
                : GalleryPhotos.WriteWebp(upright, ProductImageSize, paths.ProductImages, $"{Guid.NewGuid():N}.webp").FileName;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not process a product photo");
            return null;
        }
    }

    /// <summary>
    /// Product photos uploaded before they were re-encoded are still the original files, possibly with GPS data.
    /// Converts each one once (at startup) and deletes the original.
    /// </summary>
    public async Task ReencodeOldProductImagesAsync(StoreDbContext db, CancellationToken ct = default)
    {
        var old = await db.Products.Where(p => p.ImageFileName != null && !p.ImageFileName.EndsWith(".webp")).ToListAsync(ct);
        foreach (var product in old)
        {
            var path = SafePath(paths.ProductImages, product.ImageFileName);
            if (path is null || !File.Exists(path)) continue;

            var converted = SaveProductImage(await File.ReadAllBytesAsync(path, ct));
            if (converted is null) continue; // leave it; the admin can upload it again

            product.ImageFileName = converted;
            await db.SaveChangesAsync(ct);
            File.Delete(path);
            logger.LogInformation("Re-encoded the photo of {Product} without its metadata", product.Name);
        }
    }

    public async Task<(string? FileName, string? Error)> SaveAttachmentAsync(IFormFile file, CancellationToken ct = default)
    {
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!AttachmentExtensions.Contains(ext))
            return (null, localizer["Allowed file types: images (JPG, PNG, WebP), PDF and 3D models (STL, 3MF, OBJ, STEP)."]);
        if (file.Length > MaxAttachmentBytes) return (null, localizer["Files can be at most 25 MB."]);

        return (await SaveAsync(file, paths.RequestAttachments, ext, ct), null);
    }

    public string? AttachmentPath(string? fileName) => SafePath(paths.RequestAttachments, fileName);

    /// <summary>Deletes a custom request's attached file, if it has one.</summary>
    public void DeleteAttachment(string? fileName)
    {
        var path = SafePath(paths.RequestAttachments, fileName);
        if (path is not null && File.Exists(path)) File.Delete(path);
    }

    public void DeleteProductImage(string? fileName)
    {
        var path = SafePath(paths.ProductImages, fileName);
        if (path is not null && File.Exists(path)) File.Delete(path);
    }

    private static async Task<string> SaveAsync(IFormFile file, string folder, string ext, CancellationToken ct)
    {
        var name = $"{Guid.NewGuid():N}{ext}";
        await using var target = File.Create(Path.Combine(folder, name));
        await file.CopyToAsync(target, ct);
        return name;
    }

    /// <summary>Only plain file names we generated are accepted, so a stored name can never point outside its folder.</summary>
    private static string? SafePath(string folder, string? fileName)
    {
        if (string.IsNullOrEmpty(fileName) || fileName != Path.GetFileName(fileName)) return null;
        return Path.Combine(folder, fileName);
    }

    internal static async Task<bool> LooksLikeImageAsync(IFormFile file, CancellationToken ct)
    {
        var header = new byte[12];
        await using var s = file.OpenReadStream();
        var read = await s.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, ct);
        if (read < 12) return false;

        var isJpeg = header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF;
        var isPng = header[0] == 0x89 && header[1] == 'P' && header[2] == 'N' && header[3] == 'G';
        var isWebp = header[0] == 'R' && header[1] == 'I' && header[2] == 'F' && header[3] == 'F'
                     && header[8] == 'W' && header[9] == 'E' && header[10] == 'B' && header[11] == 'P';
        return isJpeg || isPng || isWebp;
    }
}
