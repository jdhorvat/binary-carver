namespace BinaryCarver.Models;

/// <summary>Complete result of carving analysis on one file.</summary>
public class CarveResult
{
    public string   FilePath      { get; set; } = "";
    public string   FileName      { get; set; } = "";
    public long     FileSize      { get; set; }
    public string   FileSizeHuman => FileSize < 1_048_576
        ? $"{FileSize / 1024.0:F1} KB"
        : $"{FileSize / 1_048_576.0:F2} MB";

    public List<CarvedRegion>  Regions     { get; set; } = [];
    public OverlayInfo?        Overlay     { get; set; }
    public List<string>        Errors      { get; set; } = [];

    /// <summary>Block-level entropy values for heatmap (one per block).</summary>
    public double[]  EntropyMap       { get; set; } = [];   // Byte-level (base-256), max 8.0
    public double[]  NibbleEntropyMap { get; set; } = [];   // Nibble-level (base-16), max 4.0
    public double[]  BitEntropyMap    { get; set; } = [];   // Bit-level (base-2), max 1.0
    public double[]  BigramEntropyMap { get; set; } = [];   // Byte-pair (base-65536), max 16.0
    public double[]  DivergenceMap    { get; set; } = [];   // Cross-base divergence (0–1, boundary indicator)
    public double[]  DeltaEntropyMap  { get; set; } = [];   // Rate of change of byte entropy between blocks
    public int       EntropyBlockSize { get; set; } = 1024;

    /// <summary>Gap regions — uncarved space between detected regions, classified by BFD.</summary>
    public List<GapRegion> Gaps { get; set; } = [];

    public int EmbeddedFileCount => Regions.Count;
    public bool HasOverlay       => Overlay != null;
}

/// <summary>Classification of data in gaps between carved regions.</summary>
public enum GapClassification
{
    Unknown,
    Padding,         // Low entropy, few unique bytes (null fill, 0xFF fill, alignment)
    Text,            // Medium entropy, printable ASCII dominant
    Code,            // Medium-high entropy, structured BFD with instruction-like patterns
    Compressed,      // High entropy (>7.5), near-uniform BFD
    Structured,      // Medium entropy, repetitive patterns (tables, headers, metadata)
}

/// <summary>An uncarved gap between detected regions, with byte frequency classification.</summary>
public class GapRegion
{
    public long   Offset       { get; set; }
    public long   Size         { get; set; }
    public double Entropy      { get; set; }
    public GapClassification Classification { get; set; }
    public string ClassificationDisplay => Classification.ToString();
    public string OffsetHex    => $"0x{Offset:X8}";
    public string SizeDisplay  => Size < 1024
        ? $"{Size} B"
        : Size < 1_048_576
            ? $"{Size / 1024.0:F1} KB"
            : $"{Size / 1_048_576.0:F2} MB";
}

/// <summary>A single carved region (embedded file) found in the binary.</summary>
public class CarvedRegion
{
    public int    Index       { get; set; }
    public long   Offset      { get; set; }
    public long   Size        { get; set; }
    public string FileType    { get; set; } = "";       // "PE/EXE", "ZIP", "PNG", etc.
    public string Signature   { get; set; } = "";       // The magic bytes matched
    public string Description { get; set; } = "";       // Human-readable description
    public byte[] Preview     { get; set; } = [];       // First N bytes for hex view
    public double Entropy     { get; set; }

    // ── Confidence (from format validator) ───────────────────────────────
    public string ConfidenceLevel { get; set; } = "Low";     // "Low", "Medium", "High"
    public string ConfidenceDisplay => ConfidenceLevel;

    // ── Sizing method (how this region's size was determined) ────────────
    public string SizingMethod { get; set; } = "Fallback";   // "Header", "Footer", "Divergence", "Fallback"

    // ── Recursive carving ───────────────────────────────────────────────
    public int    Depth       { get; set; }              // 0 = top-level
    public int    ParentIndex { get; set; } = -1;        // -1 = no parent
    public List<CarvedRegion> Children { get; set; } = [];

    // ── Display properties ──────────────────────────────────────────────
    public string OffsetHex     => $"0x{Offset:X8}";
    public string SizeDisplay   => Size < 1024
        ? $"{Size} B"
        : Size < 1_048_576
            ? $"{Size / 1024.0:F1} KB"
            : $"{Size / 1_048_576.0:F2} MB";
    public string EntropyDisplay => $"{Entropy:F2}";
    public string PreviewHex    => Preview.Length > 0
        ? string.Join(" ", Preview.Take(16).Select(b => $"{b:X2}"))
        : "";
    public string DepthPrefix   => Depth > 0 ? new string(' ', Depth * 2) + "└ " : "";
    public string DisplayType   => $"{DepthPrefix}{FileType}";

    public string Icon => FileType switch
    {
        "PE/EXE" or "PE/DLL" => "🔧",
        "ZIP" or "GZIP" or "7Z" or "RAR" or "CAB" => "📦",
        "PNG" or "JPEG" or "GIF" or "BMP" or "ICO" or "TIFF" => "🖼",
        "PDF" => "📄",
        "XML" or "HTML" => "📝",
        "SQLite" => "🗄",
        "Overlay" => "📎",
        _ => "📁"
    };
}

/// <summary>PE overlay data (appended after the last section).</summary>
public class OverlayInfo
{
    public long   Offset      { get; set; }
    public long   Size        { get; set; }
    public double Entropy     { get; set; }
    public byte[] Preview     { get; set; } = [];
    public string Description { get; set; } = "";  // e.g. "NSIS installer data"

    public string OffsetHex     => $"0x{Offset:X8}";
    public string SizeDisplay   => Size < 1024
        ? $"{Size} B"
        : Size < 1_048_576
            ? $"{Size / 1024.0:F1} KB"
            : $"{Size / 1_048_576.0:F2} MB";
    public string EntropyDisplay => $"{Entropy:F2}";
    public string PreviewHex    => Preview.Length > 0
        ? string.Join(" ", Preview.Take(32).Select(b => $"{b:X2}"))
        : "";
}
