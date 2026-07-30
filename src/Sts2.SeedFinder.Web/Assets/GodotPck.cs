using System.Buffers.Binary;

namespace Sts2.SeedFinder.Web.Assets;

/// <summary>
/// Minimal reader for a Godot 4 .pck archive — just enough to pull a texture out by path.
///
/// Verified against Slay the Spire 2 v0.109.0 (pack format 3, Godot 4.5.1). Format 3 differs
/// from the widely documented format 2 in two ways that will silently produce garbage if
/// missed: the file table lives at an offset stored in the header rather than immediately
/// after it, and entry offsets are relative to <c>filesBase</c> when the REL_FILEBASE flag
/// is set.
///
/// Header layout:
///   0  "GDPC"      16 flags (u32)
///   4  format ver  20 ...
///   8  major       24 filesBase (u64)
///   12 minor       32 directoryOffset (u64)
///                  40 reserved
/// </summary>
public sealed class GodotPck
{
    private const uint Magic = 0x43504447; // "GDPC" little-endian
    private const uint FlagRelativeFileBase = 1 << 1;

    public readonly record struct Entry(string Path, long Offset, long Size);

    private readonly string _pckPath;
    private readonly Dictionary<string, Entry> _entries;

    public IReadOnlyDictionary<string, Entry> Entries => _entries;
    public int FormatVersion { get; }

    private GodotPck(string pckPath, Dictionary<string, Entry> entries, int formatVersion)
    {
        _pckPath = pckPath;
        _entries = entries;
        FormatVersion = formatVersion;
    }

    public static GodotPck? TryOpen(string pckPath)
    {
        if (!File.Exists(pckPath)) return null;

        using var fs = File.OpenRead(pckPath);
        using var r = new BinaryReader(fs);

        if (r.ReadUInt32() != Magic) return null;

        int formatVersion = r.ReadInt32();
        r.ReadInt32(); r.ReadInt32(); r.ReadInt32();  // engine major/minor/patch

        long filesBase = 0, directoryOffset = 0;
        uint flags = 0;
        if (formatVersion >= 2)
        {
            flags = r.ReadUInt32();
            filesBase = r.ReadInt64();
        }
        if (formatVersion >= 3)
        {
            directoryOffset = r.ReadInt64();
            fs.Seek(directoryOffset, SeekOrigin.Begin);
        }
        else
        {
            fs.Seek(16 * 4, SeekOrigin.Current);  // reserved block
        }

        // An encrypted directory is not something we attempt; the caller degrades to no art.
        if ((flags & 1) != 0) return null;

        long adjust = (formatVersion >= 3 || (flags & FlagRelativeFileBase) != 0) ? filesBase : 0;

        int count = r.ReadInt32();
        if (count is <= 0 or > 2_000_000) return null;

        var entries = new Dictionary<string, Entry>(count, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < count; i++)
        {
            int pathLen = r.ReadInt32();
            if (pathLen is <= 0 or > 4096) return null;

            var path = System.Text.Encoding.UTF8.GetString(r.ReadBytes(pathLen)).TrimEnd('\0');
            long offset = r.ReadInt64();
            long size = r.ReadInt64();
            r.ReadBytes(16);                              // md5, unused
            if (formatVersion >= 2) r.ReadUInt32();       // per-entry flags

            entries[path] = new Entry(path, offset + adjust, size);
        }

        return new GodotPck(pckPath, entries, formatVersion);
    }

    public byte[]? Read(string path)
    {
        if (!_entries.TryGetValue(path, out var e)) return null;
        using var fs = File.OpenRead(_pckPath);
        fs.Seek(e.Offset, SeekOrigin.Begin);
        var buf = new byte[e.Size];
        return fs.ReadAtLeast(buf, buf.Length, throwOnEndOfStream: false) == buf.Length ? buf : null;
    }
}

/// <summary>
/// Godot's CompressedTexture2D (.ctex) container. A 36-byte header, then one image record.
/// The record's data format decides everything: WEBP and PNG payloads can be handed to a
/// browser untouched, while RAW means GPU block data that has to be decoded first.
/// </summary>
public static class CompressedTexture
{
    private enum DataFormat { Raw = 0, Png = 1, WebP = 2, BasisUniversal = 3 }

    // Image::Format values we care about. StS2's relic art is uniformly BPTC_RGBA.
    private const int FormatRgba8 = 5;
    private const int FormatDxt1 = 17;
    private const int FormatDxt5 = 19;
    private const int FormatBptcRgba = 22;

    public readonly record struct Texture(int Width, int Height, byte[] Data, string Kind);

    /// <summary>Kind is "webp", "png", "rgba8", "bc7", "dxt1", "dxt5", or "" when unsupported.</summary>
    public static Texture? Parse(byte[] ctex)
    {
        if (ctex.Length < 56) return null;
        if (ctex[0] != 'G' || ctex[1] != 'S' || ctex[2] != 'T' || ctex[3] != '2') return null;

        var span = ctex.AsSpan();
        int dataFormat = BinaryPrimitives.ReadInt32LittleEndian(span[36..]);
        int width = BinaryPrimitives.ReadUInt16LittleEndian(span[40..]);
        int height = BinaryPrimitives.ReadUInt16LittleEndian(span[42..]);

        switch ((DataFormat)dataFormat)
        {
            case DataFormat.WebP:
            case DataFormat.Png:
            {
                // format (4 bytes at 48), then a length-prefixed payload.
                int size = BinaryPrimitives.ReadInt32LittleEndian(span[52..]);
                if (size <= 0 || 56 + size > ctex.Length) return null;
                var payload = ctex[56..(56 + size)];
                return new Texture(width, height, payload,
                    (DataFormat)dataFormat == DataFormat.WebP ? "webp" : "png");
            }

            case DataFormat.Raw:
            {
                int pixelFormat = BinaryPrimitives.ReadInt32LittleEndian(span[48..]);
                var payload = ctex[52..];
                return pixelFormat switch
                {
                    FormatBptcRgba => new Texture(width, height, payload, "bc7"),
                    FormatRgba8 => new Texture(width, height, payload, "rgba8"),
                    FormatDxt1 => new Texture(width, height, payload, "dxt1"),
                    FormatDxt5 => new Texture(width, height, payload, "dxt5"),
                    _ => new Texture(width, height, payload, ""),
                };
            }

            default:
                return null;
        }
    }
}
