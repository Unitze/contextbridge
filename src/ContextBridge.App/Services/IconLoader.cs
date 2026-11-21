using ContextBridge.App.Resources;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ContextBridge.Core;

namespace ContextBridge.App.Services;

/// <summary>Loads the same icon the handler will show, for the list and editor previews.</summary>
static class IconLoader
{
    static readonly object Sync = new();
    static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? For(CommandDef c, ProgramLocator locator) => For(c.Icon, c.Action.Program, locator);

    public static ImageSource? For(IconDef? icon, string fallbackProgram, ProgramLocator locator)
    {
        if (icon is null || icon.Source == IconSources.None) return null;
        string? path;
        int index = icon.Index;
        switch (icon.Source)
        {
            case IconSources.File:
                path = ResolveIconFile(icon.Path, locator);
                break;
            default:
                path = locator.Locate(string.IsNullOrWhiteSpace(icon.Path) ? fallbackProgram : icon.Path!);
                break;
        }
        return path is null ? null : Load(path, index);
    }

    static string? ResolveIconFile(string? rawPath, ProgramLocator locator)
    {
        if (string.IsNullOrWhiteSpace(rawPath)) return null;
        string expanded = Template.ExpandProgram(rawPath);
        if (expanded.Length == 0) return null;
        if (expanded.IndexOfAny(new[] { '\\', '/', ':' }) >= 0) return expanded;
        return locator.Locate(expanded);
    }

    public static ImageSource? Load(string path, int index)
    {
        string key = path + "," + index;
        lock (Sync)
        {
            if (Cache.TryGetValue(key, out var cached)) return cached;
        }
        ImageSource? result = null;
        try
        {
            if (File.Exists(path))
            {
                // PrivateExtractIcons supports resource ID -1 (unlike ExtractIconEx which treats -1 as count query)
                // and extracts crisp 32x32 icons for high-DPI displays.
                var icons = new IntPtr[1];
                var ids = new uint[1];
                if (NativeMethods.PrivateExtractIcons(path, index, 32, 32, icons, ids, 1, 0) > 0 && icons[0] != IntPtr.Zero)
                {
                    try
                    {
                        var src = Imaging.CreateBitmapSourceFromHIcon(icons[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        src.Freeze();
                        result = src;
                    }
                    finally
                    {
                        NativeMethods.DestroyIcon(icons[0]);
                    }
                }
            }
        }
        catch
        {
            result = null;
        }
        lock (Sync) Cache[key] = result;
        return result;
    }

    public static void Invalidate(string? path = null)
    {
        lock (Sync)
        {
            if (path is null)
            {
                Cache.Clear();
                return;
            }
            string prefix = path + ",";
            foreach (var k in Cache.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
                Cache.Remove(k);
        }
    }

    /// <summary>Converts any image WPF can decode into an .ico (16–256 px PNG frames).</summary>
    public static string ConvertToIco(string imagePath, string targetDirectory, string baseName)
    {
        BitmapSource frame;
        using (var fs = File.OpenRead(imagePath))
        {
            var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            frame = decoder.Frames[0];
        }
        if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0)
            throw new InvalidOperationException(Strings.Err_ImageSize);

        var pngs = new List<byte[]>();
        foreach (int size in new[] { 16, 24, 32, 48, 256 })
        {
            double scale = Math.Min((double)size / frame.PixelWidth, (double)size / frame.PixelHeight);
            double w = Math.Max(1.0, Math.Round(frame.PixelWidth * scale));
            double h = Math.Max(1.0, Math.Round(frame.PixelHeight * scale));
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
                dc.DrawImage(frame, new Rect((size - w) / 2, (size - h) / 2, w, h));
            }
            var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using var ms = new MemoryStream();
            enc.Save(ms);
            pngs.Add(ms.ToArray());
        }
        Directory.CreateDirectory(targetDirectory);
        string target = Path.Combine(targetDirectory, baseName + ".ico");
        File.WriteAllBytes(target, IcoWriter.FromPngs(pngs));
        Invalidate(target);
        return target;
    }
}
