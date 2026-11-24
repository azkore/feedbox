using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

internal sealed class FeedIcons
{
    private readonly string folder;
    private readonly SemaphoreSlim slots = new SemaphoreSlim(2);
    private readonly ConcurrentDictionary<string, byte> attempted = new ConcurrentDictionary<string, byte>();

    public FeedIcons(string folder) { this.folder = folder; }

    private string PathFor(string key) { return Path.Combine(folder, key + ".png"); }

    public HashSet<string> Available()
    {
        try { return new HashSet<string>(Directory.EnumerateFiles(folder, "*.png").Select(Path.GetFileNameWithoutExtension)); }
        catch (IOException) { return new HashSet<string>(); }
        catch (UnauthorizedAccessException) { return new HashSet<string>(); }
    }

    public byte[] Read(string key)
    {
        try { return File.ReadAllBytes(PathFor(key)); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public void Discover(string key, Uri advertised, Uri site, Func<bool> stillSubscribed)
    {
        // Queue optional work without delaying articles; a missing icon remains a fallback for this run.
        if ((advertised == null && site == null) || File.Exists(PathFor(key)) || !attempted.TryAdd(key, 0)) return;
        Task.Run(async () =>
        {
            await slots.WaitAsync();
            try
            {
                if (!stillSubscribed() || File.Exists(PathFor(key))) return;
                if (advertised != null && await SaveFrom(key, advertised, stillSubscribed)) return;
                if (site == null) return;
                var declared = await PublisherPreview.FindSiteIcon(site);
                if (declared != null && declared != advertised && await SaveFrom(key, declared, stillSubscribed)) return;
                var fallback = new Uri(site, "/favicon.ico");
                if (fallback != advertised && fallback != declared) await SaveFrom(key, fallback, stillSubscribed);
            }
            catch (Exception) { /* Optional metadata must not interrupt collection. */ }
            finally { slots.Release(); }
        });
    }

    private async Task<bool> SaveFrom(string key, Uri uri, Func<bool> stillSubscribed)
    {
        var bytes = await PublisherPreview.DownloadIcon(uri);
        if (bytes == null || !stillSubscribed()) return false;
        // Decode and rewrite as a small PNG. Never serve downloaded markup or arbitrary response bytes.
        using (var icon = Rasterize(bytes))
        {
            if (icon == null) return false;
            Directory.CreateDirectory(folder);
            var path = PathFor(key);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                icon.Save(temporary, ImageFormat.Png);
                if (!File.Exists(path)) File.Move(temporary, path);
                return true;
            }
            finally { File.Delete(temporary); }
        }
    }

    internal static Bitmap Rasterize(byte[] bytes)
    {
        // Only give the raster decoder formats the sidebar accepts, never vector or HTML documents.
        if (bytes.Length < 8 || !((bytes[0] == 0x89 && bytes[1] == 'P' && bytes[2] == 'N' && bytes[3] == 'G')
            || (bytes[0] == 0xff && bytes[1] == 0xd8) || (bytes[0] == 'G' && bytes[1] == 'I' && bytes[2] == 'F')
            || (bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 1 && bytes[3] == 0))) return null;
        try
        {
            using (var data = new MemoryStream(bytes, false))
            using (var image = Image.FromStream(data, true, true))
            {
                if (image.Width < 2 || image.Height < 2 || image.Width > 2048 || image.Height > 2048) return null;
                if (image.RawFormat.Guid != ImageFormat.Png.Guid && image.RawFormat.Guid != ImageFormat.Jpeg.Guid
                    && image.RawFormat.Guid != ImageFormat.Gif.Guid && image.RawFormat.Guid != ImageFormat.Icon.Guid) return null;
                var scale = Math.Min(1.0, 64.0 / Math.Max(image.Width, image.Height));
                var icon = new Bitmap(Math.Max(1, (int)(image.Width * scale)), Math.Max(1, (int)(image.Height * scale)));
                using (var graphics = Graphics.FromImage(icon)) graphics.DrawImage(image, 0, 0, icon.Width, icon.Height);
                return icon;
            }
        }
        catch (ArgumentException) { return null; }
    }
}
