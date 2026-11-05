using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using IPBlueprint.Store.Data;
using IPBlueprint.Store.Localization;
using IPBlueprint.Store.Shop;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;

namespace IPBlueprint.Store.Tests;

public partial class GalleryTests : IClassFixture<StoreFactory>
{
    private readonly StoreFactory _factory;

    public GalleryTests(StoreFactory factory) => _factory = factory;

    [Fact]
    public async Task Phone_photo_is_turned_upright_resized_and_saved_without_camera_data()
    {
        // 3000x1500, red on the left and blue on the right, stored sideways: "rotate 90° clockwise to view".
        var jpeg = WithExifOrientation(MakeJpeg(3000, 1500), orientation: 6);
        var photos = _factory.Services.GetRequiredService<GalleryPhotos>();

        var (photo, error) = await photos.SaveAsync(FormFile(jpeg, "IMG_1234.JPG"));

        Assert.Null(error);
        Assert.NotNull(photo);
        Assert.Equal((1000, 2000), (photo.Width, photo.Height));
        Assert.Equal((400, 800), (photo.ThumbnailWidth, photo.ThumbnailHeight));

        var bytes = await File.ReadAllBytesAsync(Path.Combine(StoragePaths().GalleryImages, photo.FileName));
        Assert.Equal("RIFF", Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal("WEBP", Encoding.ASCII.GetString(bytes, 8, 4));
        Assert.DoesNotContain("EXIF", Encoding.ASCII.GetString(bytes));

        using var saved = SKBitmap.Decode(bytes);
        var top = saved.GetPixel(500, 100);
        var bottom = saved.GetPixel(500, 1900);
        Assert.True(top.Red > 200 && top.Blue < 60, $"Expected red at the top after rotating, got {top}");
        Assert.True(bottom.Blue > 200 && bottom.Red < 60, $"Expected blue at the bottom after rotating, got {bottom}");
    }

    [Fact]
    public async Task Product_photo_is_saved_as_webp_upright_and_without_camera_data()
    {
        var jpeg = WithExifOrientation(MakeJpeg(3000, 1500), orientation: 6);
        var files = _factory.Services.GetRequiredService<FileStore>();

        var (name, error) = await files.SaveProductImageAsync(FormFile(jpeg, "IMG_5678.JPG"));

        Assert.Null(error);
        Assert.EndsWith(".webp", name);
        var bytes = await File.ReadAllBytesAsync(Path.Combine(StoragePaths().ProductImages, name!));
        Assert.Equal("WEBP", Encoding.ASCII.GetString(bytes, 8, 4));
        Assert.DoesNotContain("Exif", Encoding.ASCII.GetString(bytes));
        using var saved = SKBitmap.Decode(bytes);
        Assert.Equal((FileStore.ProductImageSize / 2, FileStore.ProductImageSize), (saved.Width, saved.Height));
    }

    [Fact]
    public async Task Product_photos_uploaded_before_re_encoding_are_converted_at_startup()
    {
        var product = await _factory.AddProductAsync("Gammal bild", 100m);
        var original = Path.Combine(StoragePaths().ProductImages, $"{Guid.NewGuid():N}.jpg");
        await File.WriteAllBytesAsync(original, WithExifOrientation(MakeJpeg(400, 300), orientation: 1));
        await _factory.WithDbAsync(db => db.Products.Where(p => p.Id == product.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.ImageFileName, Path.GetFileName(original))));

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<FileStore>()
                .ReencodeOldProductImagesAsync(scope.ServiceProvider.GetRequiredService<StoreDbContext>());
        }

        var now = await _factory.WithDbAsync(db => db.Products.Where(p => p.Id == product.Id).Select(p => p.ImageFileName).SingleAsync());
        Assert.EndsWith(".webp", now);
        Assert.True(File.Exists(Path.Combine(StoragePaths().ProductImages, now!)));
        Assert.False(File.Exists(original));
    }

    [Fact]
    public async Task Small_photos_are_not_enlarged()
    {
        var photos = _factory.Services.GetRequiredService<GalleryPhotos>();

        var (photo, _) = await photos.SaveAsync(FormFile(MakePng(640, 480), "small.png"));

        Assert.Equal((640, 480), (photo!.Width, photo.Height));
        Assert.Equal((640, 480), (photo.ThumbnailWidth, photo.ThumbnailHeight));
    }

    [Theory]
    [InlineData("notes.txt", "hello")]
    [InlineData("fake.jpg", "this is not really a jpeg")]
    [InlineData("model.stl", "solid cube")]
    public async Task Files_that_are_not_images_are_refused(string name, string content)
    {
        var photos = _factory.Services.GetRequiredService<GalleryPhotos>();

        var (photo, error) = await photos.SaveAsync(FormFile(Encoding.UTF8.GetBytes(content), name));

        Assert.Null(photo);
        Assert.NotNull(error);
    }

    [Fact]
    public async Task A_broken_image_with_a_valid_header_is_refused()
    {
        var photos = _factory.Services.GetRequiredService<GalleryPhotos>();
        var broken = MakeJpeg(200, 200)[..20];

        var (photo, error) = await photos.SaveAsync(FormFile(broken, "broken.jpg"));

        Assert.Null(photo);
        Assert.NotNull(error);
    }

    [Fact]
    public async Task Admin_uploads_photos_that_show_on_the_gallery_page_in_both_languages()
    {
        var client = await AdminClientAsync();

        var page = await client.GetStringAsync("/Admin/Gallery");
        using var form = new MultipartFormDataContent
        {
            { new StringContent(Token(page)), "__RequestVerificationToken" },
            { new StringContent("Ledad drake i silke-PLA"), "Upload.Caption" },
            { new StringContent("Articulated dragon in silk PLA"), "Upload.CaptionEn" },
            { ImageContent(MakeJpeg(1200, 900), "image/jpeg"), "Photos", "drake.jpg" },
            { ImageContent(MakePng(800, 800), "image/png"), "Photos", "drake2.png" },
        };

        var response = await client.PostAsync("/Admin/Gallery?handler=Upload", form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Added 2 photos to the gallery.", await response.Content.ReadAsStringAsync());

        var images = await _factory.WithDbAsync(db => db.GalleryImages.Where(g => g.Caption == "Ledad drake i silke-PLA").ToListAsync());
        Assert.Equal(2, images.Count);

        var swedish = await _factory.CreateClient().GetStringAsync("/gallery");
        var english = await EnglishClient().GetStringAsync("/gallery");
        Assert.Contains("Ledad drake i silke-PLA", swedish);
        Assert.Contains($"/images/gallery/{images[0].ThumbnailFileName}", swedish);
        Assert.Contains("Articulated dragon in silk PLA", english);
        Assert.DoesNotContain("Ledad drake i silke-PLA", english);

        var photo = await _factory.CreateClient().GetAsync($"/images/gallery/{images[0].FileName}");
        Assert.Equal(HttpStatusCode.OK, photo.StatusCode);
        Assert.Equal("image/webp", photo.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Admin_can_reorder_edit_and_delete_photos()
    {
        var (a, b) = await _factory.WithDbAsync(async db =>
        {
            var first = await db.GalleryImages.MinAsync(g => (int?)g.SortOrder) ?? 0;
            var a = NewImage("A", first - 2);
            var b = NewImage("B", first - 1);
            db.GalleryImages.AddRange(a, b);
            await db.SaveChangesAsync();
            return (a, b);
        });
        var client = await AdminClientAsync();
        var token = Token(await client.GetStringAsync("/Admin/Gallery"));

        await client.PostAsync($"/Admin/Gallery?handler=Move&id={b.Id}&direction=-1", Form(token));
        await client.PostAsync($"/Admin/Gallery?handler=Save&id={a.Id}", Form(token, ("Edit.Caption", "Ny text"), ("Edit.CaptionEn", "")));

        var order = await _factory.WithDbAsync(db => db.GalleryImages.OrderBy(g => g.SortOrder).Select(g => g.Id).Take(2).ToListAsync());
        Assert.Equal([b.Id, a.Id], order);
        var edited = await _factory.WithDbAsync(db => db.GalleryImages.SingleAsync(g => g.Id == a.Id));
        Assert.Equal("Ny text", edited.Caption);
        Assert.Null(edited.CaptionEn);

        var folder = StoragePaths().GalleryImages;
        await File.WriteAllTextAsync(Path.Combine(folder, b.FileName), "x");
        await File.WriteAllTextAsync(Path.Combine(folder, b.ThumbnailFileName), "x");

        await client.PostAsync($"/Admin/Gallery?handler=Delete&id={b.Id}", Form(token));

        Assert.False(await _factory.WithDbAsync(db => db.GalleryImages.AnyAsync(g => g.Id == b.Id)));
        Assert.False(File.Exists(Path.Combine(folder, b.FileName)));
        Assert.False(File.Exists(Path.Combine(folder, b.ThumbnailFileName)));
    }

    [Fact]
    public async Task Gallery_admin_requires_login()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/Admin/Gallery");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/Admin/Login", response.Headers.Location!.PathAndQuery);
    }

    [Fact]
    public async Task Gallery_page_is_linked_from_the_menu()
    {
        var html = await _factory.CreateClient().GetStringAsync("/");

        Assert.Contains("href=\"/gallery\">Galleri</a>", html);
    }

    private static GalleryImage NewImage(string name, int sortOrder) => new()
    {
        FileName = $"{Guid.NewGuid():N}.webp", ThumbnailFileName = $"{Guid.NewGuid():N}-thumb.webp",
        Width = 10, Height = 10, ThumbnailWidth = 10, ThumbnailHeight = 10, Caption = name, SortOrder = sortOrder,
    };

    private StoragePaths StoragePaths() => _factory.Services.GetRequiredService<StoragePaths>();

    private HttpClient EnglishClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"{Languages.CookieName}={Uri.EscapeDataString(Languages.CookieValue("en"))}");
        return client;
    }

    private async Task<HttpClient> AdminClientAsync()
    {
        var client = _factory.CreateClient();
        var token = Token(await client.GetStringAsync("/Admin/Login"));
        var response = await client.PostAsync("/Admin/Login", Form(token, ("password", "correct horse battery staple")));
        response.EnsureSuccessStatusCode();
        return client;
    }

    private static FormUrlEncodedContent Form(string token, params (string Key, string Value)[] fields) =>
        new([new("__RequestVerificationToken", token), .. fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value))]);

    private static string Token(string html) => AntiforgeryToken().Match(html).Groups[1].Value;

    private static ByteArrayContent ImageContent(byte[] data, string mediaType)
    {
        var content = new ByteArrayContent(data);
        content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return content;
    }

    private static FormFile FormFile(byte[] data, string name) =>
        new(new MemoryStream(data), 0, data.Length, "Photos", name);

    private static SKBitmap TwoColours(int width, int height)
    {
        var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.DrawRect(0, 0, width / 2f, height, new SKPaint { Color = SKColors.Red });
        canvas.DrawRect(width / 2f, 0, width / 2f, height, new SKPaint { Color = SKColors.Blue });
        return bitmap;
    }

    private static byte[] MakeJpeg(int width, int height)
    {
        using var bitmap = TwoColours(width, height);
        return bitmap.Encode(SKEncodedImageFormat.Jpeg, 90).ToArray();
    }

    private static byte[] MakePng(int width, int height)
    {
        using var bitmap = TwoColours(width, height);
        return bitmap.Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }

    /// <summary>Adds the smallest possible EXIF block holding just an orientation, like a phone camera would.</summary>
    private static byte[] WithExifOrientation(byte[] jpeg, ushort orientation)
    {
        byte[] tiff =
        [
            (byte)'M', (byte)'M', 0, 42, 0, 0, 0, 8, // big-endian TIFF header, first IFD at offset 8
            0, 1,                                   // one entry
            0x01, 0x12, 0, 3, 0, 0, 0, 1,           // tag 0x0112 Orientation, type SHORT, count 1
            (byte)(orientation >> 8), (byte)orientation, 0, 0,
            0, 0, 0, 0,                             // no next IFD
        ];
        byte[] payload = [.. "Exif\0\0"u8, .. tiff];
        var length = payload.Length + 2;
        byte[] app1 = [0xFF, 0xE1, (byte)(length >> 8), (byte)length, .. payload];
        return [.. jpeg[..2], .. app1, .. jpeg[2..]];
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryToken();
}
