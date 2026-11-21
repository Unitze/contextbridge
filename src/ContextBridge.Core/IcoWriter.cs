namespace ContextBridge.Core;

/// <summary>
/// Wraps PNG images into an .ico container (PNG-compressed entries, supported since Vista).
/// The shell's icon-resource strings accept .ico/.exe/.dll but not .png, so images picked
/// in the UI are converted to %LOCALAPPDATA%\ContextBridge\icons\*.ico.
/// </summary>
public static class IcoWriter
{
    public static byte[] FromPngs(IReadOnlyList<byte[]> pngs)
    {
        if (pngs.Count == 0) throw new ArgumentException("no images", nameof(pngs));
        if (pngs.Count > ushort.MaxValue) throw new ArgumentException("too many images", nameof(pngs));
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((ushort)0);           // reserved
        w.Write((ushort)1);           // type: icon
        w.Write((ushort)pngs.Count);
        int offset = 6 + 16 * pngs.Count;
        foreach (var png in pngs)
        {
            var (width, height) = PngSize(png);
            if (width <= 0 || height <= 0 || width > 256 || height > 256)
                throw new ArgumentException("PNG entries must be between 1x1 and 256x256");
            w.Write((byte)(width == 256 ? 0 : width));
            w.Write((byte)(height == 256 ? 0 : height));
            w.Write((byte)0);         // palette
            w.Write((byte)0);         // reserved
            w.Write((ushort)1);       // planes
            w.Write((ushort)32);      // bpp
            w.Write(png.Length);
            w.Write(offset);
            offset += png.Length;
        }
        foreach (var png in pngs) w.Write(png);
        w.Flush();
        return ms.ToArray();
    }

    public static (int Width, int Height) PngSize(byte[] png)
    {
        ReadOnlySpan<byte> sig = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        ReadOnlySpan<byte> ihdr = new byte[] { (byte)'I', (byte)'H', (byte)'D', (byte)'R' };
        if (png.Length < 24 || !png.AsSpan(0, 8).SequenceEqual(sig) || !png.AsSpan(12, 4).SequenceEqual(ihdr))
            throw new FormatException("not a valid PNG file");
        int Be(int o) => (png[o] << 24) | (png[o + 1] << 16) | (png[o + 2] << 8) | png[o + 3];
        int w = Be(16), h = Be(20);
        if (w <= 0 || h <= 0) throw new FormatException("invalid PNG dimensions");
        return (w, h);
    }
}
