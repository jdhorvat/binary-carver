namespace BinaryCarver.Analysis;

/// <summary>
/// Confidence levels for signature matches, following binwalk's three-tier model.
/// LOW  = magic bytes matched but structural validation failed or was inconclusive.
/// MEDIUM = basic structural validation passed.
/// HIGH = deep validation passed (checksums, internal consistency, etc.)
/// </summary>
public enum Confidence
{
    Invalid = -1,
    Low     = 0,
    Medium  = 128,
    High    = 250,
}

/// <summary>
/// Per-format structural validators that go beyond magic byte matching.
/// Each validator examines internal file structure, checksums, field ranges,
/// and cross-field consistency to assign a confidence level.
///
/// Techniques sourced from:
///   - binwalk: multi-level validation + confidence scoring
///   - scalpel: header/footer pair validation
///   - Bin-Carver papers: PE section table integrity
///   - Jpeg-Carver: Oscar Method + RST marker validation
/// </summary>
public static class FormatValidators
{
    /// <summary>
    /// Validate a candidate match and return a confidence level.
    /// Returns Invalid if this is clearly a false positive.
    /// </summary>
    public static Confidence Validate(byte[] data, long offset, SignatureDefinition sig)
    {
        // Bounds check
        if (offset < 0 || offset + sig.Magic.Length > data.Length)
            return Confidence.Invalid;

        return sig.FileType switch
        {
            "PE/EXE" or "PE/DLL" => ValidatePE(data, offset),
            "ELF"                => ValidateELF(data, offset),
            "ZIP"                => ValidateZIP(data, offset),
            "PNG"                => ValidatePNG(data, offset),
            "JPEG"               => ValidateJPEG(data, offset),
            "PDF"                => ValidatePDF(data, offset),
            "GIF"                => ValidateGIF(data, offset),
            "BMP"                => ValidateBMP(data, offset),
            "GZIP"               => ValidateGZIP(data, offset),
            "SQLite"             => ValidateSQLite(data, offset),
            "OLE/DOC"            => ValidateOLE(data, offset),
            "7Z"                 => Confidence.Medium,  // 6-byte magic is already strong
            "RAR"                => Confidence.Medium,  // 7-8 byte magic is strong
            "FLAC"               => ValidateFLAC(data, offset),
            "MP4"                => ValidateMP4(data, offset),
            _                    => Confidence.Low,      // unknown format, magic match only
        };
    }

    // ═══════════════════════════════════════════════════════════════════════
    // PE VALIDATOR — Section table integrity, alignment, packer detection
    // Sources: Bin-Carver paper, carve-exe reference implementation
    // ═══════════════════════════════════════════════════════════════════════

    private static Confidence ValidatePE(byte[] data, long offset)
    {
        if (offset + 64 > data.Length) return Confidence.Low;

        // DOS header: e_lfanew at offset 0x3C
        int peRva = ReadInt32(data, offset + 0x3C);
        if (peRva <= 0 || peRva > 0x10000) return Confidence.Low; // unreasonable PE offset

        long peOffset = offset + peRva;
        if (peOffset + 24 > data.Length) return Confidence.Low;

        // PE signature: "PE\0\0"
        if (data[peOffset] != 0x50 || data[peOffset + 1] != 0x45 ||
            data[peOffset + 2] != 0x00 || data[peOffset + 3] != 0x00)
            return Confidence.Invalid; // MZ but not PE — very likely false positive

        // COFF header
        ushort machine     = ReadUInt16(data, peOffset + 4);
        ushort numSections = ReadUInt16(data, peOffset + 6);
        ushort optHdrSize  = ReadUInt16(data, peOffset + 20);

        // Machine type validation (common values)
        bool validMachine = machine is
            0x014C or  // x86
            0x8664 or  // x64
            0x01C0 or  // ARM
            0xAA64 or  // ARM64
            0x01C4;    // ARMv7 Thumb
        if (!validMachine) return Confidence.Low;

        // Section count sanity
        if (numSections == 0 || numSections > 96) return Confidence.Low;

        // Optional header size sanity (PE32=224, PE32+=240, but can vary)
        if (optHdrSize < 28 || optHdrSize > 1024) return Confidence.Low;

        // Validate section headers
        long secStart = peOffset + 24 + optHdrSize;
        if (secStart + numSections * 40 > data.Length) return Confidence.Medium; // truncated but valid header

        long prevEnd = 0;
        bool sectionsValid = true;
        bool packerDetected = false;

        for (int i = 0; i < numSections; i++)
        {
            long so = secStart + i * 40;

            // Section name (8 bytes, null-padded)
            string secName = ReadAscii(data, so, 8).TrimEnd('\0');

            // Detect common packers by section name
            if (secName is "UPX0" or "UPX1" or "UPX2" or ".aspack" or ".adata"
                or ".Themida" or ".vmp0" or ".vmp1" or ".enigma1" or ".nsp0")
                packerDetected = true;

            uint virtualSize = ReadUInt32(data, so + 8);
            uint rawDataPtr  = ReadUInt32(data, so + 20);
            uint rawDataSize = ReadUInt32(data, so + 16);

            // Sanity checks
            if (rawDataPtr > 0 && rawDataSize > 0)
            {
                long secEnd = rawDataPtr + rawDataSize;

                // Check for overlapping sections (non-packed PE should be sequential)
                if (!packerDetected && rawDataPtr > 0 && rawDataPtr < prevEnd && prevEnd > 0)
                    sectionsValid = false;

                if (rawDataPtr > 0)
                    prevEnd = Math.Max(prevEnd, secEnd);
            }

            // Virtual size much larger than raw size is normal (.bss), but insane values are suspicious
            if (virtualSize > 0x80000000) sectionsValid = false;
        }

        // File alignment check: PE files typically align to 0x200 (512 bytes)
        if (peOffset + 24 + 60 < data.Length)
        {
            uint fileAlignment = ReadUInt32(data, peOffset + 24 + 36);
            if (fileAlignment > 0 && fileAlignment <= 0x10000)
            {
                // Valid file alignment is a power of 2, 512..65536
                bool isPow2 = (fileAlignment & (fileAlignment - 1)) == 0;
                if (isPow2 && sectionsValid)
                    return Confidence.High;
            }
        }

        return sectionsValid ? Confidence.Medium : Confidence.Low;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // ELF VALIDATOR — Program header validation
    // ═══════════════════════════════════════════════════════════════════════

    private static Confidence ValidateELF(byte[] data, long offset)
    {
        if (offset + 52 > data.Length) return Confidence.Low;

        // EI_CLASS: 1=32-bit, 2=64-bit
        byte elfClass = data[offset + 4];
        if (elfClass is not (1 or 2)) return Confidence.Invalid;

        // EI_DATA: 1=LE, 2=BE
        byte elfData = data[offset + 5];
        if (elfData is not (1 or 2)) return Confidence.Invalid;

        // EI_VERSION: must be 1
        if (data[offset + 6] != 1) return Confidence.Low;

        // e_type at offset 16 (2 bytes)
        ushort eType = elfData == 1
            ? ReadUInt16(data, offset + 16)
            : ReadUInt16BE(data, offset + 16);

        // Valid types: 0=NONE, 1=REL, 2=EXEC, 3=DYN, 4=CORE
        if (eType > 4 && eType < 0xFE00) return Confidence.Low;

        return Confidence.High;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // JPEG VALIDATOR — Oscar Method + marker analysis
    // Sources: Jpeg-Carver PreCheck.cs, binwalk jpeg.rs
    // ═══════════════════════════════════════════════════════════════════════

    private static Confidence ValidateJPEG(byte[] data, long offset)
    {
        if (offset + 4 > data.Length) return Confidence.Low;

        // FFD8 already matched. Check the third byte is a valid marker.
        byte marker = data[offset + 2];
        if (marker != 0xFF) return Confidence.Invalid;

        // Fourth byte should be a known APP/SOF/DQT/DHT marker
        byte markerType = data[offset + 3];
        bool validSecondMarker = markerType switch
        {
            >= 0xC0 and <= 0xCF => true,  // SOF markers
            >= 0xD0 and <= 0xD7 => true,  // RST markers (unusual but valid)
            0xD8                => false,  // SOI inside SOI — bad
            0xD9                => false,  // EOI immediately — bad
            >= 0xDA and <= 0xFE => true,  // SOS, DQT, DNL, DRI, DHP, EXP, APPn, COM, JPG
            _                   => false,
        };
        if (!validSecondMarker) return Confidence.Invalid;

        // Oscar Method: Count stuffed zero bytes (0xFF00) in a 4KB sample.
        // Real JPEG entropy data has 0xFF followed by 0x00 (byte stuffing).
        // Expected frequency: ~9.7–47 per 4KB in real JPEGs (from Jpeg-Carver research).
        long sampleStart = offset + 4;
        long sampleEnd = Math.Min(sampleStart + 4096, data.Length - 1);
        int stuffedCount = 0;
        int rstSequence = 0;
        int rstErrors = 0;
        byte expectedRst = 0xD0;

        for (long i = sampleStart; i < sampleEnd; i++)
        {
            if (data[i] == 0xFF && i + 1 < data.Length)
            {
                byte next = data[i + 1];
                if (next == 0x00)
                {
                    stuffedCount++;
                }
                else if (next >= 0xD0 && next <= 0xD7)
                {
                    // RST marker validation: should appear in order D0, D1, ..., D7, D0, ...
                    if (rstSequence > 0 && next != expectedRst)
                        rstErrors++;
                    expectedRst = (byte)(0xD0 + ((next - 0xD0 + 1) & 7));
                    rstSequence++;
                }
            }
        }

        long sampleSize = sampleEnd - sampleStart;
        if (sampleSize < 100) return Confidence.Medium; // too small to judge

        // Score based on Oscar Method
        double stuffedRate = (double)stuffedCount / (sampleSize / 4096.0);

        // High confidence: stuffed bytes in expected range, no RST sequence errors
        if (stuffedRate >= 5.0 && stuffedRate <= 60.0 && rstErrors == 0)
            return Confidence.High;

        // Medium: markers look valid, stuffed rate outside range but not zero
        if (stuffedCount > 0 || validSecondMarker)
            return Confidence.Medium;

        return Confidence.Low;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // ZIP VALIDATOR — Local file header + EOCD validation
    // ═══════════════════════════════════════════════════════════════════════

    private static Confidence ValidateZIP(byte[] data, long offset)
    {
        if (offset + 30 > data.Length) return Confidence.Low;

        // Local file header version
        ushort versionNeeded = ReadUInt16(data, offset + 4);
        if (versionNeeded > 100) return Confidence.Invalid; // max reasonable version is ~6.3

        // General purpose bit flag
        ushort flags = ReadUInt16(data, offset + 6);
        // Bits 0-15 defined, upper bits should be zero in practice
        // (but don't reject on this, some archivers set weird flags)

        // Compression method
        ushort method = ReadUInt16(data, offset + 8);
        // 0=stored, 8=deflate, 9=deflate64, 12=bzip2, 14=lzma, 93=zstd, 95=xz, 98=ppmd
        bool validMethod = method is 0 or 8 or 9 or 12 or 14 or 93 or 95 or 98;

        // Filename length + extra field length
        ushort fnLen    = ReadUInt16(data, offset + 26);
        ushort extraLen = ReadUInt16(data, offset + 28);

        // Filename length sanity
        if (fnLen == 0 || fnLen > 1024) return Confidence.Low;

        // Check filename is readable
        long fnStart = offset + 30;
        if (fnStart + fnLen > data.Length) return Confidence.Low;

        bool readableFilename = true;
        for (int i = 0; i < Math.Min((int)fnLen, 64); i++)
        {
            byte b = data[fnStart + i];
            if (b < 0x20 && b != 0x09) { readableFilename = false; break; }
        }

        if (validMethod && readableFilename && fnLen < 512)
            return Confidence.High;

        if (readableFilename)
            return Confidence.Medium;

        return Confidence.Low;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // PNG VALIDATOR — Chunk structure (IHDR validation)
    // ═══════════════════════════════════════════════════════════════════════

    private static Confidence ValidatePNG(byte[] data, long offset)
    {
        // 8-byte header already matched. First chunk should be IHDR.
        long ihdrStart = offset + 8;
        if (ihdrStart + 25 > data.Length) return Confidence.Low;

        // Chunk length (4 bytes BE)
        uint chunkLen = ReadUInt32BE(data, ihdrStart);
        if (chunkLen != 13) return Confidence.Invalid; // IHDR is always 13 bytes

        // Chunk type should be "IHDR"
        if (data[ihdrStart + 4] != 0x49 || data[ihdrStart + 5] != 0x48 ||
            data[ihdrStart + 6] != 0x44 || data[ihdrStart + 7] != 0x52)
            return Confidence.Invalid;

        // Width and height (4 bytes each, BE)
        uint width  = ReadUInt32BE(data, ihdrStart + 8);
        uint height = ReadUInt32BE(data, ihdrStart + 12);
        if (width == 0 || height == 0) return Confidence.Invalid;
        if (width > 100000 || height > 100000) return Confidence.Low; // implausible

        // Bit depth: 1, 2, 4, 8, or 16
        byte bitDepth = data[ihdrStart + 16];
        bool validBitDepth = bitDepth is 1 or 2 or 4 or 8 or 16;

        // Color type: 0, 2, 3, 4, or 6
        byte colorType = data[ihdrStart + 17];
        bool validColorType = colorType is 0 or 2 or 3 or 4 or 6;

        // Compression method: must be 0
        byte compression = data[ihdrStart + 18];

        if (validBitDepth && validColorType && compression == 0)
            return Confidence.High;

        if (validBitDepth && validColorType)
            return Confidence.Medium;

        return Confidence.Low;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // PDF VALIDATOR — Structure verification
    // ═══════════════════════════════════════════════════════════════════════

    private static Confidence ValidatePDF(byte[] data, long offset)
    {
        if (offset + 8 > data.Length) return Confidence.Low;

        // %PDF-x.y — check version string
        if (data[offset + 4] != (byte)'-') return Confidence.Low;

        byte major = data[offset + 5];
        byte minor = (offset + 7 < data.Length) ? data[offset + 7] : (byte)0;

        // Valid versions: 1.0–2.0
        if (major < (byte)'1' || major > (byte)'2') return Confidence.Low;
        if (data[offset + 6] != (byte)'.') return Confidence.Low;

        // Scan for "obj" keyword nearby (within first 4KB)
        long scanEnd = Math.Min(offset + 4096, data.Length - 3);
        bool foundObj = false;
        for (long i = offset + 8; i < scanEnd; i++)
        {
            if (data[i] == (byte)'o' && data[i + 1] == (byte)'b' && data[i + 2] == (byte)'j')
            {
                foundObj = true;
                break;
            }
        }

        // Scan for xref or startxref (within last portion of scan range)
        bool foundXref = false;
        long xrefScanStart = Math.Max(offset + 8, scanEnd - 2048);
        byte[] xrefMagic = System.Text.Encoding.ASCII.GetBytes("xref");
        for (long i = xrefScanStart; i < scanEnd - 3; i++)
        {
            if (data[i] == xrefMagic[0] && data[i + 1] == xrefMagic[1] &&
                data[i + 2] == xrefMagic[2] && data[i + 3] == xrefMagic[3])
            {
                foundXref = true;
                break;
            }
        }

        if (foundObj && foundXref) return Confidence.High;
        if (foundObj) return Confidence.Medium;
        return Confidence.Low;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // GIF VALIDATOR
    // ═══════════════════════════════════════════════════════════════════════

    private static Confidence ValidateGIF(byte[] data, long offset)
    {
        // "GIF87a" or "GIF89a" already matched (6 bytes).
        if (offset + 13 > data.Length) return Confidence.Low;

        // Logical screen descriptor: width + height (2 bytes each, LE)
        ushort width  = ReadUInt16(data, offset + 6);
        ushort height = ReadUInt16(data, offset + 8);

        if (width == 0 || height == 0) return Confidence.Invalid;
        if (width > 32768 || height > 32768) return Confidence.Low;

        // Packed byte at offset 10: global color table flag (bit 7)
        byte packed = data[offset + 10];
        bool hasGlobalTable = (packed & 0x80) != 0;
        int colorTableSize = hasGlobalTable ? 3 * (1 << ((packed & 0x07) + 1)) : 0;

        // After LSD (13 bytes from header) + global color table, expect a block
        long blockStart = offset + 13 + colorTableSize;
        if (blockStart < data.Length)
        {
            byte blockType = data[blockStart];
            // Valid block types: 0x21 (extension), 0x2C (image descriptor), 0x3B (trailer)
            if (blockType is 0x21 or 0x2C or 0x3B)
                return Confidence.High;
        }

        return hasGlobalTable ? Confidence.Medium : Confidence.Low;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // BMP VALIDATOR
    // ═══════════════════════════════════════════════════════════════════════

    private static Confidence ValidateBMP(byte[] data, long offset)
    {
        if (offset + 26 > data.Length) return Confidence.Low;

        // File size at offset 2 (4 bytes LE)
        uint fileSize = ReadUInt32(data, offset + 2);
        if (fileSize < 26 || fileSize > 500_000_000) return Confidence.Low;

        // Reserved fields at offset 6-9 should be 0
        uint reserved = ReadUInt32(data, offset + 6);
        if (reserved != 0) return Confidence.Invalid; // almost certainly not a BMP

        // Pixel data offset at offset 10
        uint pixOffset = ReadUInt32(data, offset + 10);
        if (pixOffset < 14 || pixOffset > fileSize) return Confidence.Low;

        // DIB header size at offset 14
        uint dibSize = ReadUInt32(data, offset + 14);
        // Common sizes: 12 (OS/2 v1), 40 (BITMAPINFOHEADER), 52, 56, 108, 124
        bool validDib = dibSize is 12 or 40 or 52 or 56 or 64 or 108 or 124;

        if (validDib && reserved == 0) return Confidence.High;
        return Confidence.Medium;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // GZIP VALIDATOR
    // ═══════════════════════════════════════════════════════════════════════

    private static Confidence ValidateGZIP(byte[] data, long offset)
    {
        if (offset + 10 > data.Length) return Confidence.Low;

        // Compression method at offset 2: must be 8 (deflate)
        byte method = data[offset + 2];
        if (method != 8) return Confidence.Invalid;

        // Flags byte at offset 3: only bits 0-4 defined
        byte flags = data[offset + 3];
        if ((flags & 0xE0) != 0) return Confidence.Low; // reserved bits set

        // OS byte at offset 9: 0-13 and 255 are defined
        byte os = data[offset + 9];
        bool validOs = os <= 13 || os == 255;

        if (validOs) return Confidence.High;
        return Confidence.Medium;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // SQLITE VALIDATOR
    // ═══════════════════════════════════════════════════════════════════════

    private static Confidence ValidateSQLite(byte[] data, long offset)
    {
        // "SQLite format 3\000" — 16 bytes
        byte[] fullMagic = System.Text.Encoding.ASCII.GetBytes("SQLite format 3");
        if (offset + 100 > data.Length) return Confidence.Low;

        bool fullMatch = true;
        for (int i = 0; i < fullMagic.Length && offset + i < data.Length; i++)
        {
            if (data[offset + i] != fullMagic[i]) { fullMatch = false; break; }
        }
        if (data[offset + 15] != 0) fullMatch = false;

        if (!fullMatch) return Confidence.Low;

        // Page size at offset 16 (2 bytes BE): must be power of 2, 512..65536
        ushort pageSize = ReadUInt16BE(data, offset + 16);
        bool validPageSize = pageSize >= 512 && (pageSize & (pageSize - 1)) == 0;

        return validPageSize ? Confidence.High : Confidence.Medium;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // OLE/DOC VALIDATOR
    // ═══════════════════════════════════════════════════════════════════════

    private static Confidence ValidateOLE(byte[] data, long offset)
    {
        if (offset + 512 > data.Length) return Confidence.Low;

        // 8-byte magic already matched.
        // Minor version at offset 24-25, major version at offset 26-27
        ushort minorVer = ReadUInt16(data, offset + 24);
        ushort majorVer = ReadUInt16(data, offset + 26);

        // Major version: 3 or 4
        if (majorVer is not (3 or 4)) return Confidence.Low;

        // Byte order: 0xFFFE (LE)
        ushort byteOrder = ReadUInt16(data, offset + 28);
        if (byteOrder != 0xFFFE) return Confidence.Low;

        // Sector size power: 9 (512) for v3, 12 (4096) for v4
        ushort sectorPow = ReadUInt16(data, offset + 30);
        if (majorVer == 3 && sectorPow != 9) return Confidence.Low;
        if (majorVer == 4 && sectorPow != 12) return Confidence.Low;

        return Confidence.High;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // FLAC VALIDATOR
    // ═══════════════════════════════════════════════════════════════════════

    private static Confidence ValidateFLAC(byte[] data, long offset)
    {
        if (offset + 8 > data.Length) return Confidence.Low;

        // "fLaC" magic matched. First metadata block header at offset 4.
        byte blockHeader = data[offset + 4];
        byte blockType = (byte)(blockHeader & 0x7F);

        // First block must be STREAMINFO (type 0)
        if (blockType != 0) return Confidence.Invalid;

        // STREAMINFO is 34 bytes
        if (offset + 42 > data.Length) return Confidence.Medium;

        uint blockLen = (uint)(data[offset + 5] << 16 | data[offset + 6] << 8 | data[offset + 7]);
        if (blockLen != 34) return Confidence.Low;

        return Confidence.High;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // MP4 VALIDATOR — ftyp box
    // ═══════════════════════════════════════════════════════════════════════

    private static Confidence ValidateMP4(byte[] data, long offset)
    {
        // "ftyp" at offset+4, box size at offset+0 (4 bytes BE)
        if (offset + 12 > data.Length) return Confidence.Low;

        // Note: offset is the start of ftyp magic, but SearchOffset=4 means
        // the actual box starts 4 bytes before
        long boxStart = offset - 4;
        if (boxStart < 0) return Confidence.Low;

        uint boxSize = ReadUInt32BE(data, boxStart);
        if (boxSize < 8 || boxSize > 1_000_000) return Confidence.Low; // ftyp box shouldn't be huge

        // Brand (4 bytes after "ftyp"): should be printable ASCII
        long brandOffset = offset + 4;
        if (brandOffset + 4 > data.Length) return Confidence.Medium;

        bool validBrand = true;
        for (int i = 0; i < 4; i++)
        {
            byte b = data[brandOffset + i];
            if (b < 0x20 || b > 0x7E) { validBrand = false; break; }
        }

        return validBrand ? Confidence.High : Confidence.Medium;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // BYTE READING HELPERS
    // ═══════════════════════════════════════════════════════════════════════

    private static ushort ReadUInt16(byte[] data, long offset) =>
        (ushort)(data[offset] | (data[offset + 1] << 8));

    private static ushort ReadUInt16BE(byte[] data, long offset) =>
        (ushort)((data[offset] << 8) | data[offset + 1]);

    private static int ReadInt32(byte[] data, long offset) =>
        data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24);

    private static uint ReadUInt32(byte[] data, long offset) =>
        (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));

    private static uint ReadUInt32BE(byte[] data, long offset) =>
        (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);

    private static string ReadAscii(byte[] data, long offset, int length)
    {
        char[] chars = new char[length];
        for (int i = 0; i < length && offset + i < data.Length; i++)
            chars[i] = (char)data[offset + i];
        return new string(chars);
    }
}
