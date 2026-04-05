namespace BinaryCarver.Analysis;

/// <summary>How to use the footer pattern when determining file boundaries.</summary>
public enum FooterMode
{
    /// <summary>No footer — size comes from header parsing or heuristic.</summary>
    None,

    /// <summary>Find first footer after header, include footer bytes in carved region.</summary>
    Forward,

    /// <summary>Find first footer after header, exclude footer from carved region.</summary>
    ForwardNext,

    /// <summary>Find the furthest footer within MaxSize (e.g. PDF with multiple %%EOF).</summary>
    Reverse,
}

/// <summary>Category for color-coding and grouping.</summary>
public enum SignatureCategory
{
    Executable,
    Archive,
    Image,
    Document,
    Audio,
    Database,
    Crypto,
    Other,
}

/// <summary>
/// Enhanced signature definition combining magic bytes with footer patterns,
/// size limits, search modes, and category metadata. Derived from analysis of
/// binwalk, scalpel, and academic carving research.
/// </summary>
public sealed class SignatureDefinition
{
    public required string            FileType    { get; init; }
    public required string            Description { get; init; }
    public required byte[]            Magic       { get; init; }
    public          int               SearchOffset { get; init; }
    public          byte[]?           Footer      { get; init; }
    public          FooterMode        FooterMode  { get; init; } = FooterMode.None;
    public          long              MinSize     { get; init; } = 4;
    public          long              MaxSize     { get; init; } = 500_000_000;  // 500 MB default
    public          string            DefaultExt  { get; init; } = ".bin";
    public          SignatureCategory Category    { get; init; } = SignatureCategory.Other;

    // ── Text-based signatures ───────────────────────────────────────
    public          bool              IsTextBased { get; init; }
    public          string?           TextPrefix  { get; init; }
}

/// <summary>
/// Central registry of all built-in file signatures, enhanced with footer patterns,
/// size limits, and footer search modes from scalpel and binwalk research.
/// </summary>
public static class SignatureDatabase
{
    public static IReadOnlyList<SignatureDefinition> GetAll() => AllSignatures;

    private static readonly SignatureDefinition[] AllSignatures =
    [
        // ═══════════════════════════════════════════════════════════════════
        // EXECUTABLES
        // ═══════════════════════════════════════════════════════════════════
        new()
        {
            FileType = "PE/EXE", Description = "MZ header (PE executable)",
            Magic = [0x4D, 0x5A], MinSize = 512, MaxSize = 500_000_000,
            DefaultExt = ".exe", Category = SignatureCategory.Executable,
            // PE size determined by section table — no footer
        },
        new()
        {
            FileType = "ELF", Description = "ELF executable",
            Magic = [0x7F, 0x45, 0x4C, 0x46], MinSize = 52, MaxSize = 500_000_000,
            DefaultExt = ".elf", Category = SignatureCategory.Executable,
        },
        new()
        {
            FileType = "Mach-O", Description = "Mach-O binary (32-bit)",
            Magic = [0xFE, 0xED, 0xFA, 0xCE], MinSize = 28, MaxSize = 500_000_000,
            DefaultExt = ".macho", Category = SignatureCategory.Executable,
        },
        new()
        {
            FileType = "Mach-O", Description = "Mach-O binary (64-bit)",
            Magic = [0xFE, 0xED, 0xFA, 0xCF], MinSize = 32, MaxSize = 500_000_000,
            DefaultExt = ".macho", Category = SignatureCategory.Executable,
        },
        new()
        {
            FileType = "Mach-O", Description = "Mach-O universal binary",
            Magic = [0xCA, 0xFE, 0xBA, 0xBE], MinSize = 32, MaxSize = 500_000_000,
            DefaultExt = ".macho", Category = SignatureCategory.Executable,
        },

        // ═══════════════════════════════════════════════════════════════════
        // ARCHIVES — with footer patterns and search modes from scalpel
        // ═══════════════════════════════════════════════════════════════════
        new()
        {
            FileType = "ZIP", Description = "ZIP/DOCX/XLSX/JAR archive",
            Magic = [0x50, 0x4B, 0x03, 0x04],
            Footer = [0x50, 0x4B, 0x05, 0x06], // End Of Central Directory
            FooterMode = FooterMode.Reverse,     // EOCD can appear multiple times
            MinSize = 22, MaxSize = 4_000_000_000,
            DefaultExt = ".zip", Category = SignatureCategory.Archive,
        },
        new()
        {
            FileType = "GZIP", Description = "GZIP compressed",
            Magic = [0x1F, 0x8B], MinSize = 18, MaxSize = 1_000_000_000,
            DefaultExt = ".gz", Category = SignatureCategory.Archive,
        },
        new()
        {
            FileType = "7Z", Description = "7-Zip archive",
            Magic = [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C],
            MinSize = 32, MaxSize = 4_000_000_000,
            DefaultExt = ".7z", Category = SignatureCategory.Archive,
        },
        new()
        {
            FileType = "RAR", Description = "RAR archive v5",
            Magic = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00],
            MinSize = 20, MaxSize = 4_000_000_000,
            DefaultExt = ".rar", Category = SignatureCategory.Archive,
        },
        new()
        {
            FileType = "RAR", Description = "RAR archive v4",
            Magic = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00],
            MinSize = 20, MaxSize = 4_000_000_000,
            DefaultExt = ".rar", Category = SignatureCategory.Archive,
        },
        new()
        {
            FileType = "CAB", Description = "Microsoft Cabinet",
            Magic = [0x4D, 0x53, 0x43, 0x46], MinSize = 60, MaxSize = 2_000_000_000,
            DefaultExt = ".cab", Category = SignatureCategory.Archive,
        },
        new()
        {
            FileType = "XZ", Description = "XZ compressed",
            Magic = [0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00],
            MinSize = 32, MaxSize = 1_000_000_000,
            DefaultExt = ".xz", Category = SignatureCategory.Archive,
        },
        new()
        {
            FileType = "BZIP2", Description = "BZIP2 compressed",
            Magic = [0x42, 0x5A, 0x68], MinSize = 14, MaxSize = 1_000_000_000,
            DefaultExt = ".bz2", Category = SignatureCategory.Archive,
        },
        new()
        {
            FileType = "ZSTD", Description = "Zstandard compressed",
            Magic = [0x28, 0xB5, 0x2F, 0xFD], MinSize = 8, MaxSize = 1_000_000_000,
            DefaultExt = ".zst", Category = SignatureCategory.Archive,
        },
        new()
        {
            FileType = "TAR", Description = "TAR archive (ustar)",
            Magic = [0x75, 0x73, 0x74, 0x61, 0x72], // "ustar" at offset 257
            SearchOffset = 257,
            MinSize = 512, MaxSize = 4_000_000_000,
            DefaultExt = ".tar", Category = SignatureCategory.Archive,
        },
        new()
        {
            FileType = "CPIO", Description = "CPIO archive",
            Magic = [0x30, 0x37, 0x30, 0x37, 0x30, 0x31], // "070701"
            MinSize = 76, MaxSize = 2_000_000_000,
            DefaultExt = ".cpio", Category = SignatureCategory.Archive,
        },
        new()
        {
            FileType = "LZ4", Description = "LZ4 frame",
            Magic = [0x04, 0x22, 0x4D, 0x18], MinSize = 7, MaxSize = 1_000_000_000,
            DefaultExt = ".lz4", Category = SignatureCategory.Archive,
        },
        new()
        {
            FileType = "LZMA", Description = "LZMA compressed",
            Magic = [0x5D, 0x00, 0x00], MinSize = 13, MaxSize = 1_000_000_000,
            DefaultExt = ".lzma", Category = SignatureCategory.Archive,
        },

        // ═══════════════════════════════════════════════════════════════════
        // IMAGES — with footer patterns from scalpel + binwalk
        // ═══════════════════════════════════════════════════════════════════
        new()
        {
            FileType = "PNG", Description = "PNG image",
            Magic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A],
            Footer = [0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82], // IEND + CRC
            FooterMode = FooterMode.Forward,
            MinSize = 67, MaxSize = 100_000_000,
            DefaultExt = ".png", Category = SignatureCategory.Image,
        },
        new()
        {
            FileType = "JPEG", Description = "JPEG image",
            Magic = [0xFF, 0xD8, 0xFF],
            Footer = [0xFF, 0xD9],
            FooterMode = FooterMode.Forward,
            MinSize = 107, MaxSize = 100_000_000,
            DefaultExt = ".jpg", Category = SignatureCategory.Image,
        },
        new()
        {
            FileType = "GIF", Description = "GIF image (87a)",
            Magic = [0x47, 0x49, 0x46, 0x38, 0x37, 0x61],
            Footer = [0x3B],
            FooterMode = FooterMode.Forward,
            MinSize = 13, MaxSize = 50_000_000,
            DefaultExt = ".gif", Category = SignatureCategory.Image,
        },
        new()
        {
            FileType = "GIF", Description = "GIF image (89a)",
            Magic = [0x47, 0x49, 0x46, 0x38, 0x39, 0x61],
            Footer = [0x3B],
            FooterMode = FooterMode.Forward,
            MinSize = 13, MaxSize = 50_000_000,
            DefaultExt = ".gif", Category = SignatureCategory.Image,
        },
        new()
        {
            FileType = "BMP", Description = "BMP image",
            Magic = [0x42, 0x4D], MinSize = 26, MaxSize = 100_000_000,
            DefaultExt = ".bmp", Category = SignatureCategory.Image,
            // Size is in the BMP header at offset +2 (4 bytes LE)
        },
        new()
        {
            FileType = "ICO", Description = "Windows icon",
            Magic = [0x00, 0x00, 0x01, 0x00], MinSize = 22, MaxSize = 10_000_000,
            DefaultExt = ".ico", Category = SignatureCategory.Image,
        },
        new()
        {
            FileType = "TIFF", Description = "TIFF image (LE)",
            Magic = [0x49, 0x49, 0x2A, 0x00], MinSize = 8, MaxSize = 500_000_000,
            DefaultExt = ".tiff", Category = SignatureCategory.Image,
        },
        new()
        {
            FileType = "TIFF", Description = "TIFF image (BE)",
            Magic = [0x4D, 0x4D, 0x00, 0x2A], MinSize = 8, MaxSize = 500_000_000,
            DefaultExt = ".tiff", Category = SignatureCategory.Image,
        },
        new()
        {
            FileType = "WEBP", Description = "WebP image",
            Magic = [0x52, 0x49, 0x46, 0x46], MinSize = 12, MaxSize = 100_000_000,
            DefaultExt = ".webp", Category = SignatureCategory.Image,
            // RIFF container — size at offset +4
        },

        // ═══════════════════════════════════════════════════════════════════
        // DOCUMENTS
        // ═══════════════════════════════════════════════════════════════════
        new()
        {
            FileType = "PDF", Description = "PDF document",
            Magic = [0x25, 0x50, 0x44, 0x46], // %PDF
            Footer = [0x25, 0x25, 0x45, 0x4F, 0x46], // %%EOF
            FooterMode = FooterMode.Reverse,  // PDFs can have multiple %%EOF
            MinSize = 67, MaxSize = 1_000_000_000,
            DefaultExt = ".pdf", Category = SignatureCategory.Document,
        },
        new()
        {
            FileType = "OLE/DOC", Description = "OLE2 Compound (DOC/XLS/PPT)",
            Magic = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1],
            MinSize = 512, MaxSize = 500_000_000,
            DefaultExt = ".doc", Category = SignatureCategory.Document,
        },

        // ═══════════════════════════════════════════════════════════════════
        // AUDIO / VIDEO
        // ═══════════════════════════════════════════════════════════════════
        new()
        {
            FileType = "OGG", Description = "OGG container",
            Magic = [0x4F, 0x67, 0x67, 0x53], MinSize = 27, MaxSize = 500_000_000,
            DefaultExt = ".ogg", Category = SignatureCategory.Audio,
        },
        new()
        {
            FileType = "FLAC", Description = "FLAC audio",
            Magic = [0x66, 0x4C, 0x61, 0x43], MinSize = 42, MaxSize = 1_000_000_000,
            DefaultExt = ".flac", Category = SignatureCategory.Audio,
        },
        new()
        {
            FileType = "MP3", Description = "MP3 audio (ID3)",
            Magic = [0x49, 0x44, 0x33], MinSize = 128, MaxSize = 500_000_000,
            DefaultExt = ".mp3", Category = SignatureCategory.Audio,
        },
        new()
        {
            FileType = "MP4", Description = "MP4/M4A container (ftyp)",
            Magic = [0x66, 0x74, 0x79, 0x70], // "ftyp" at offset 4
            SearchOffset = 4,
            MinSize = 8, MaxSize = 4_000_000_000,
            DefaultExt = ".mp4", Category = SignatureCategory.Audio,
        },

        // ═══════════════════════════════════════════════════════════════════
        // DATABASE
        // ═══════════════════════════════════════════════════════════════════
        new()
        {
            FileType = "SQLite", Description = "SQLite database",
            Magic = [0x53, 0x51, 0x4C, 0x69, 0x74, 0x65], // "SQLite"
            MinSize = 512, MaxSize = 1_000_000_000,
            DefaultExt = ".sqlite", Category = SignatureCategory.Database,
        },

        // ═══════════════════════════════════════════════════════════════════
        // CRYPTO / CERTIFICATES
        // ═══════════════════════════════════════════════════════════════════
        new()
        {
            FileType = "DER/CER", Description = "DER certificate",
            Magic = [0x30, 0x82], MinSize = 4, MaxSize = 10_000_000,
            DefaultExt = ".cer", Category = SignatureCategory.Crypto,
        },

        // ═══════════════════════════════════════════════════════════════════
        // JAVA / ANDROID
        // ═══════════════════════════════════════════════════════════════════
        new()
        {
            FileType = "CLASS", Description = "Java class file",
            Magic = [0xCA, 0xFE, 0xBA, 0xBE], MinSize = 32, MaxSize = 50_000_000,
            DefaultExt = ".class", Category = SignatureCategory.Executable,
        },
        new()
        {
            FileType = "DEX", Description = "Android DEX",
            Magic = [0x64, 0x65, 0x78, 0x0A], MinSize = 112, MaxSize = 100_000_000,
            DefaultExt = ".dex", Category = SignatureCategory.Executable,
        },

        // ═══════════════════════════════════════════════════════════════════
        // FIRMWARE / FILESYSTEM (new from binwalk research)
        // ═══════════════════════════════════════════════════════════════════
        new()
        {
            FileType = "SquashFS", Description = "SquashFS filesystem (LE)",
            Magic = [0x68, 0x73, 0x71, 0x73], // "hsqs"
            MinSize = 96, MaxSize = 4_000_000_000,
            DefaultExt = ".sqsh", Category = SignatureCategory.Archive,
        },
        new()
        {
            FileType = "SquashFS", Description = "SquashFS filesystem (BE)",
            Magic = [0x73, 0x71, 0x73, 0x68], // "sqsh"
            MinSize = 96, MaxSize = 4_000_000_000,
            DefaultExt = ".sqsh", Category = SignatureCategory.Archive,
        },
        new()
        {
            FileType = "CramFS", Description = "CramFS filesystem",
            Magic = [0x45, 0x3D, 0xCD, 0x28],
            MinSize = 76, MaxSize = 256_000_000,
            DefaultExt = ".cramfs", Category = SignatureCategory.Archive,
        },
        new()
        {
            FileType = "JFFS2", Description = "JFFS2 filesystem",
            Magic = [0x85, 0x19], MinSize = 12, MaxSize = 256_000_000,
            DefaultExt = ".jffs2", Category = SignatureCategory.Archive,
        },
        new()
        {
            FileType = "UBI", Description = "UBI erase count header",
            Magic = [0x55, 0x42, 0x49, 0x23], // "UBI#"
            MinSize = 64, MaxSize = 1_000_000_000,
            DefaultExt = ".ubi", Category = SignatureCategory.Archive,
        },
        new()
        {
            FileType = "uImage", Description = "U-Boot legacy image",
            Magic = [0x27, 0x05, 0x19, 0x56],
            MinSize = 64, MaxSize = 100_000_000,
            DefaultExt = ".uimage", Category = SignatureCategory.Executable,
        },
        new()
        {
            FileType = "FIT", Description = "Flattened Image Tree",
            Magic = [0xD0, 0x0D, 0xFE, 0xED],
            MinSize = 40, MaxSize = 500_000_000,
            DefaultExt = ".fit", Category = SignatureCategory.Executable,
        },
        new()
        {
            FileType = "AndroidBoot", Description = "Android boot image",
            Magic = [0x41, 0x4E, 0x44, 0x52, 0x4F, 0x49, 0x44, 0x21], // "ANDROID!"
            MinSize = 608, MaxSize = 500_000_000,
            DefaultExt = ".img", Category = SignatureCategory.Executable,
        },
        new()
        {
            FileType = "EXT", Description = "EXT2/3/4 filesystem",
            Magic = [0x53, 0xEF], SearchOffset = 0x438,
            MinSize = 1024, MaxSize = 4_000_000_000,
            DefaultExt = ".ext", Category = SignatureCategory.Archive,
        },

        // ═══════════════════════════════════════════════════════════════════
        // TEXT-BASED SIGNATURES
        // ═══════════════════════════════════════════════════════════════════
        new()
        {
            FileType = "XML", Description = "XML document",
            Magic = System.Text.Encoding.ASCII.GetBytes("<?xml"),
            IsTextBased = true, TextPrefix = "<?xml",
            MinSize = 10, MaxSize = 100_000_000,
            DefaultExt = ".xml", Category = SignatureCategory.Document,
        },
        new()
        {
            FileType = "HTML", Description = "HTML document",
            Magic = System.Text.Encoding.ASCII.GetBytes("<html"),
            IsTextBased = true, TextPrefix = "<html",
            MinSize = 10, MaxSize = 100_000_000,
            DefaultExt = ".html", Category = SignatureCategory.Document,
        },
        new()
        {
            FileType = "HTML", Description = "HTML document (DOCTYPE)",
            Magic = System.Text.Encoding.ASCII.GetBytes("<!DOCTYPE html"),
            IsTextBased = true, TextPrefix = "<!DOCTYPE html",
            MinSize = 15, MaxSize = 100_000_000,
            DefaultExt = ".html", Category = SignatureCategory.Document,
        },
        new()
        {
            FileType = "RTF", Description = "Rich Text Format",
            Magic = System.Text.Encoding.ASCII.GetBytes("{\\rtf"),
            IsTextBased = true, TextPrefix = "{\\rtf",
            Footer = System.Text.Encoding.ASCII.GetBytes("}"),
            FooterMode = FooterMode.Reverse, // RTF ends with last }
            MinSize = 6, MaxSize = 100_000_000,
            DefaultExt = ".rtf", Category = SignatureCategory.Document,
        },
        new()
        {
            FileType = "PEM", Description = "PEM certificate/key",
            Magic = System.Text.Encoding.ASCII.GetBytes("-----BEGIN"),
            IsTextBased = true, TextPrefix = "-----BEGIN",
            Footer = System.Text.Encoding.ASCII.GetBytes("-----END"),
            FooterMode = FooterMode.Forward,
            MinSize = 20, MaxSize = 100_000,
            DefaultExt = ".pem", Category = SignatureCategory.Crypto,
        },
        new()
        {
            FileType = "SVG", Description = "SVG image",
            Magic = System.Text.Encoding.ASCII.GetBytes("<svg"),
            IsTextBased = true, TextPrefix = "<svg",
            Footer = System.Text.Encoding.ASCII.GetBytes("</svg>"),
            FooterMode = FooterMode.Reverse,
            MinSize = 20, MaxSize = 50_000_000,
            DefaultExt = ".svg", Category = SignatureCategory.Image,
        },
        new()
        {
            FileType = "JSON", Description = "JSON document",
            Magic = System.Text.Encoding.ASCII.GetBytes("{\""),
            IsTextBased = true, TextPrefix = "{\"",
            MinSize = 2, MaxSize = 100_000_000,
            DefaultExt = ".json", Category = SignatureCategory.Document,
        },
    ];
}
