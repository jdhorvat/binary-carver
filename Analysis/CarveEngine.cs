using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Text.Json;
using BinaryCarver.Models;

namespace BinaryCarver.Analysis;

/// <summary>
/// Two-pass binary carving engine with Aho-Corasick multi-pattern matching,
/// per-format structural validation, confidence scoring, footer-based boundary
/// detection, and min/max size filtering.
///
/// Architecture derived from analysis of binwalk, scalpel, Bin-Carver, and
/// Jpeg-Carver research. See CLAUDE.md for full design rationale.
///
/// Pass 1: Aho-Corasick single-pass scan → discover all magic byte matches
///         → validate each with format-specific validator → assign confidence.
/// Pass 2: Resolve conflicts (overlaps, duplicates) → estimate sizes using
///         footer scanning with FORWARD/REVERSE/NEXT modes → apply min/max
///         size filtering → sort and index.
/// </summary>
public static class CarveEngine
{
    private const int MaxRecursionDepth = 4;
    private static List<CustomSignature> _customSignatures = [];

    // ── Overlay detection patterns ──────────────────────────────────────

    private static readonly List<(byte[] Magic, string Description)> OverlayPatterns =
    [
        ([0xEF, 0xBE, 0xAD, 0xDE], "NSIS installer data"),
        ([0x50, 0x4B, 0x03, 0x04], "ZIP archive (appended)"),
        ([0x52, 0x61, 0x72, 0x21], "RAR archive (appended)"),
        ([0x37, 0x7A, 0xBC, 0xAF], "7-Zip data (appended)"),
        ([0xD0, 0xCF, 0x11, 0xE0], "OLE2 data (appended)"),
        ([0x4D, 0x5A],             "Embedded PE (appended)"),
    ];

    /// <summary>Set custom user signatures before calling Analyze.</summary>
    public static void SetCustomSignatures(List<CustomSignature> sigs) =>
        _customSignatures = sigs ?? [];

    // ═══════════════════════════════════════════════════════════════════════
    // MAIN ENTRY POINT
    // ═══════════════════════════════════════════════════════════════════════

    public static CarveResult Analyze(string filePath, bool recursive = false)
    {
        var result = new CarveResult
        {
            FilePath = filePath,
            FileName = Path.GetFileName(filePath),
        };

        byte[] data;
        try
        {
            var fi = new FileInfo(filePath);
            result.FileSize = fi.Length;

            if (fi.Length > int.MaxValue)
            {
                result.Errors.Add($"File too large for in-memory analysis ({fi.Length / 1_073_741_824.0:F1} GB). Max ~2 GB.");
                return result;
            }

            // Use memory-mapped file for large files (>64MB) to reduce peak memory
            // and avoid large contiguous heap allocations. For smaller files,
            // ReadAllBytes is faster due to less overhead.
            if (fi.Length > 64 * 1024 * 1024)
            {
                data = ReadViaMemoryMap(filePath, (int)fi.Length);
            }
            else
            {
                data = File.ReadAllBytes(filePath);
            }
        }
        catch (Exception ex)
        {
            result.Errors.Add($"Failed to read file: {ex.Message}");
            return result;
        }

        // Get all signature definitions
        var allSigs = SignatureDatabase.GetAll().ToList();

        // ── Pass 1: Discover + Validate ────────────────────────────────
        Pass1_DiscoverAndValidate(data, allSigs, result);

        // ── Custom signatures ──────────────────────────────────────────
        ScanCustomSignatures(data, result);

        // ── PE overlay detection ───────────────────────────────────────
        DetectOverlay(data, result);

        // ── Entropy heatmap (computed BEFORE Pass 2 so divergence data ──
        // ── is available for boundary sizing)                           ──
        ComputeEntropyMap(data, result);

        // ── Pass 2: Resolve conflicts + estimate sizes ─────────────────
        Pass2_ResolveAndSize(data, allSigs, result);

        // ── Recursive carving ──────────────────────────────────────────
        if (recursive)
        {
            RecursiveCarve(data, allSigs, result, depth: 0);
            var flat = FlattenRegions(result.Regions);
            result.Regions = flat;
            for (int i = 0; i < result.Regions.Count; i++)
                result.Regions[i].Index = i;
        }

        // ── Gap classification ─────────────────────────────────────────
        ClassifyGaps(data, result);

        return result;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // PUBLIC — Recompute entropy maps with a different block size
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Recompute all entropy maps (byte, nibble, bit, bigram, divergence, delta)
    /// and gap classification using a new block size. Called when the user changes
    /// the block size in the settings bar.
    /// </summary>
    public static void RecomputeEntropy(byte[] data, CarveResult result, int blockSize)
    {
        ComputeEntropyMap(data, result, blockSize);
        ClassifyGaps(data, result);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // PASS 1 — Aho-Corasick scan + format validation
    // ═══════════════════════════════════════════════════════════════════════

    private static void Pass1_DiscoverAndValidate(byte[] data, List<SignatureDefinition> sigs, CarveResult result)
    {
        // Separate signatures into offset-0 search and regular magic patterns
        var regularSigs = new List<(int SigIndex, byte[] Magic)>();
        var offsetSigs  = new List<(int SigIndex, byte[] Magic, int SearchOffset)>();

        for (int i = 0; i < sigs.Count; i++)
        {
            if (sigs[i].SearchOffset > 0)
                offsetSigs.Add((i, sigs[i].Magic, sigs[i].SearchOffset));
            else
                regularSigs.Add((i, sigs[i].Magic));
        }

        // Build Aho-Corasick automaton from all regular magic patterns
        var patterns = regularSigs.Select(s => s.Magic).ToList();
        var ac = new AhoCorasick(patterns);

        // Single-pass scan
        var matches = ac.Search(data);

        // Process matches
        foreach (var (patternIdx, matchOffset) in matches)
        {
            int sigIndex = regularSigs[patternIdx].SigIndex;
            var sig = sigs[sigIndex];

            // Validate with format-specific validator
            Confidence conf = FormatValidators.Validate(data, matchOffset, sig);
            if (conf == Confidence.Invalid) continue;

            AddCandidateRegion(data, result, sig, matchOffset, conf);
        }

        // Handle offset-based signatures (e.g. TAR at offset 257, MP4 ftyp at 4, EXT at 0x438)
        foreach (var (sigIndex, magic, searchOffset) in offsetSigs)
        {
            var sig = sigs[sigIndex];
            int pos = 0;
            while (pos < data.Length)
            {
                int searchStart = pos + searchOffset;
                if (searchStart + magic.Length > data.Length) break;

                int found = FindPattern(data, magic, searchStart, data.Length);
                if (found < 0) break;

                long regionStart = found - searchOffset;
                if (regionStart < 0) regionStart = 0;

                Confidence conf = FormatValidators.Validate(data, regionStart, sig);
                if (conf != Confidence.Invalid)
                    AddCandidateRegion(data, result, sig, regionStart, conf);

                pos = found + 1;
            }
        }
    }

    private static void AddCandidateRegion(byte[] data, CarveResult result,
        SignatureDefinition sig, long offset, Confidence conf)
    {
        int previewLen = (int)Math.Min(64, data.Length - offset);
        byte[] preview = new byte[previewLen];
        Array.Copy(data, offset, preview, 0, previewLen);

        string confStr = conf switch
        {
            Confidence.High   => "High",
            Confidence.Medium => "Medium",
            _                 => "Low",
        };

        result.Regions.Add(new CarvedRegion
        {
            Offset          = offset,
            Size            = 0, // sized in Pass 2
            FileType        = sig.FileType,
            Signature       = sig.Description,
            Description     = $"{sig.FileType} at 0x{offset:X8}",
            Preview         = preview,
            Entropy         = ComputeEntropy(data, offset, Math.Min(4096, data.Length - offset)),
            ConfidenceLevel = confStr,
        });
    }

    // ═══════════════════════════════════════════════════════════════════════
    // PASS 2 — Conflict resolution, footer scanning, size estimation
    // ═══════════════════════════════════════════════════════════════════════

    private static void Pass2_ResolveAndSize(byte[] data, List<SignatureDefinition> sigs, CarveResult result)
    {
        // Sort by offset
        result.Regions = result.Regions.OrderBy(r => r.Offset).ToList();

        // Remove duplicates at the same offset (keep highest confidence)
        var deduped = new List<CarvedRegion>();
        for (int i = 0; i < result.Regions.Count; i++)
        {
            var region = result.Regions[i];

            // Check if there's already a region at this exact offset
            bool isDuplicate = false;
            for (int j = 0; j < deduped.Count; j++)
            {
                if (deduped[j].Offset == region.Offset)
                {
                    // Keep the one with higher confidence
                    int existingConf = ConfidenceToInt(deduped[j].ConfidenceLevel);
                    int newConf = ConfidenceToInt(region.ConfidenceLevel);
                    if (newConf > existingConf)
                        deduped[j] = region;
                    isDuplicate = true;
                    break;
                }
            }
            if (!isDuplicate)
                deduped.Add(region);
        }
        result.Regions = deduped;

        // Build a lookup from FileType → SignatureDefinition for footer/size info
        var sigLookup = new Dictionary<string, SignatureDefinition>();
        foreach (var sig in sigs)
        {
            if (!sigLookup.ContainsKey(sig.FileType + "|" + sig.Description))
                sigLookup[sig.FileType + "|" + sig.Description] = sig;
        }

        // Apply signature specificity weighting to initial confidence
        ApplySpecificityWeighting(result.Regions, sigLookup);

        // Estimate size for each region
        for (int i = 0; i < result.Regions.Count; i++)
        {
            var region = result.Regions[i];
            var sig = sigLookup.GetValueOrDefault(region.FileType + "|" + region.Signature);

            // Find next region's offset (for ScanToNext fallback)
            long nextRegionOffset = (i + 1 < result.Regions.Count)
                ? result.Regions[i + 1].Offset
                : data.Length;

            // Step 1: Try header-based size estimation
            long size = EstimateRegionSize(data, region.Offset, region.FileType);
            string sizingMethod = size > 0 ? "Header" : "Fallback";

            // Step 2: Try footer-based size estimation if signature has a footer
            if (sig?.Footer != null && sig.Footer.Length > 0 && sig.FooterMode != FooterMode.None)
            {
                long footerSize = EstimateByFooter(data, region.Offset, sig.Footer,
                    sig.FooterMode, sig.MaxSize);
                if (footerSize > 0)
                {
                    size = footerSize;
                    sizingMethod = "Footer";
                }
            }

            // Step 3: Try divergence-based boundary detection when header/footer failed
            if (size <= 0 && result.DivergenceMap.Length > 0)
            {
                long divSize = EstimateByDivergence(result, region.Offset, nextRegionOffset);
                if (divSize > 0)
                {
                    size = divSize;
                    sizingMethod = "Divergence";
                }
            }

            // Step 4: Fall back to next-signature boundary
            if (size <= 0)
            {
                size = nextRegionOffset - region.Offset;
                sizingMethod = "Fallback";
            }

            // Step 5: Apply min/max size filtering
            long minSize = sig?.MinSize ?? 4;
            long maxSize = sig?.MaxSize ?? 500_000_000;

            if (size < minSize)
            {
                region.ConfidenceLevel = "Low";
                region.Description += " [undersized]";
            }
            if (size > maxSize)
                size = maxSize;

            // Clamp to file boundary
            if (region.Offset + size > data.Length)
                size = data.Length - region.Offset;

            region.Size = size;
            region.SizingMethod = sizingMethod;
            region.Description = $"{region.FileType} at 0x{region.Offset:X8} ({EstimateSizeString(size)}) [{region.ConfidenceLevel}] via {sizingMethod}";
        }

        // Remove overlapping regions (lower confidence loses)
        result.Regions = ResolveOverlaps(result.Regions);

        // Final sort and indexing
        result.Regions = result.Regions.OrderBy(r => r.Offset).ToList();
        for (int i = 0; i < result.Regions.Count; i++)
            result.Regions[i].Index = i;
    }

    /// <summary>
    /// Resolve overlapping regions by keeping the higher-confidence one.
    /// If equal confidence, keep the one that starts first.
    /// </summary>
    private static List<CarvedRegion> ResolveOverlaps(List<CarvedRegion> regions)
    {
        if (regions.Count < 2) return regions;

        var sorted = regions.OrderBy(r => r.Offset).ToList();
        var resolved = new List<CarvedRegion> { sorted[0] };

        for (int i = 1; i < sorted.Count; i++)
        {
            var prev = resolved[^1];
            var curr = sorted[i];

            // Check for overlap
            if (curr.Offset < prev.Offset + prev.Size)
            {
                // Overlapping — keep higher confidence, or first if tied
                int prevConf = ConfidenceToInt(prev.ConfidenceLevel);
                int currConf = ConfidenceToInt(curr.ConfidenceLevel);

                if (currConf > prevConf)
                {
                    resolved[^1] = curr; // replace prev with curr
                }
                // else keep prev (already in list)
            }
            else
            {
                resolved.Add(curr);
            }
        }

        return resolved;
    }

    private static int ConfidenceToInt(string level) => level switch
    {
        "High"   => 250,
        "Medium" => 128,
        _        => 0,
    };

    // ═══════════════════════════════════════════════════════════════════════
    // FOOTER-BASED SIZE ESTIMATION
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Scan for footer pattern using the specified mode.
    /// Returns estimated size (including footer), or 0 if not found.
    /// </summary>
    private static long EstimateByFooter(byte[] data, long headerOffset, byte[] footer,
        FooterMode mode, long maxSize)
    {
        long searchEnd = Math.Min(headerOffset + maxSize, data.Length);
        long searchStart = headerOffset + 1; // skip past header

        switch (mode)
        {
            case FooterMode.Forward:
            {
                // Find FIRST footer after header, include footer bytes
                int found = FindPattern(data, footer, (int)searchStart, (int)searchEnd);
                if (found >= 0)
                    return found + footer.Length - headerOffset;
                return 0;
            }

            case FooterMode.ForwardNext:
            {
                // Find FIRST footer after header, exclude footer bytes
                int found = FindPattern(data, footer, (int)searchStart, (int)searchEnd);
                if (found >= 0)
                    return found - headerOffset;
                return 0;
            }

            case FooterMode.Reverse:
            {
                // Find LAST (furthest) footer within maxSize — critical for PDFs, RTFs
                long lastFound = -1;
                int pos = (int)searchStart;
                while (pos < (int)searchEnd)
                {
                    int found = FindPattern(data, footer, pos, (int)searchEnd);
                    if (found < 0) break;
                    lastFound = found;
                    pos = found + 1;
                }
                if (lastFound >= 0)
                    return lastFound + footer.Length - headerOffset;
                return 0;
            }

            default:
                return 0;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // DIVERGENCE-BASED BOUNDARY DETECTION
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Find the next significant divergence peak after the region's start offset.
    /// A "significant" peak is one that exceeds mean + 2*stddev of the divergence map.
    /// Returns estimated size, or 0 if no clear boundary found.
    /// </summary>
    private static long EstimateByDivergence(CarveResult result, long regionOffset, long maxOffset)
    {
        var div = result.DivergenceMap;
        int blockSize = result.EntropyBlockSize;
        if (div.Length == 0 || blockSize <= 0) return 0;

        // Compute mean and stddev of divergence
        double sum = 0, sumSq = 0;
        for (int i = 0; i < div.Length; i++)
        {
            sum += div[i];
            sumSq += div[i] * div[i];
        }
        double mean = sum / div.Length;
        double variance = (sumSq / div.Length) - (mean * mean);
        double stddev = Math.Sqrt(Math.Max(0, variance));
        double threshold = mean + 2.0 * stddev;

        // Minimum threshold to avoid noise in uniform files
        if (threshold < 0.05) threshold = 0.05;

        // Start searching from one block past the region header
        int startBlock = (int)(regionOffset / blockSize) + 1;
        int maxBlock = (int)Math.Min((maxOffset - 1) / blockSize, div.Length - 1);

        // Scan for the first divergence peak above threshold
        for (int i = startBlock; i <= maxBlock; i++)
        {
            if (div[i] >= threshold)
            {
                // Found a significant boundary — the region ends at this block's start
                long boundaryOffset = (long)i * blockSize;
                long size = boundaryOffset - regionOffset;
                if (size > 0) return size;
            }
        }

        return 0;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // SIGNATURE SPECIFICITY WEIGHTING
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Adjust initial confidence based on signature length/uniqueness.
    /// Short magic bytes (2 bytes like MZ) are less specific than long ones (16 bytes like SQLite).
    /// </summary>
    private static void ApplySpecificityWeighting(List<CarvedRegion> regions,
        Dictionary<string, SignatureDefinition> sigLookup)
    {
        foreach (var region in regions)
        {
            var sig = sigLookup.GetValueOrDefault(region.FileType + "|" + region.Signature);
            if (sig == null) continue;

            int magicLen = sig.Magic?.Length ?? 0;
            if (sig.IsTextBased && sig.TextPrefix != null)
                magicLen = sig.TextPrefix.Length;

            // Short signatures (≤3 bytes) get downgraded if they were auto-Medium
            if (magicLen <= 3 && region.ConfidenceLevel == "Medium")
                region.ConfidenceLevel = "Low";

            // Long signatures (≥8 bytes) get a boost from Low to Medium
            if (magicLen >= 8 && region.ConfidenceLevel == "Low")
                region.ConfidenceLevel = "Medium";

            // Very long signatures (≥12 bytes) validated as Medium → High
            if (magicLen >= 12 && region.ConfidenceLevel == "Medium")
                region.ConfidenceLevel = "High";
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // GAP CLASSIFICATION (BFD — Byte Frequency Distribution)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Identify gaps between carved regions and classify each by its byte frequency
    /// distribution. Gaps reveal what's filling the space between embedded files:
    /// null padding, text, code, compressed data, or structured metadata.
    /// </summary>
    private static void ClassifyGaps(byte[] data, CarveResult result)
    {
        result.Gaps.Clear();
        if (result.Regions.Count == 0)
        {
            // Entire file is one gap
            if (data.Length > 0)
                result.Gaps.Add(ClassifySingleGap(data, 0, data.Length));
            return;
        }

        var sorted = result.Regions.Where(r => r.Depth == 0).OrderBy(r => r.Offset).ToList();

        // Gap before first region
        if (sorted[0].Offset > 0)
            result.Gaps.Add(ClassifySingleGap(data, 0, sorted[0].Offset));

        // Gaps between regions
        for (int i = 0; i < sorted.Count - 1; i++)
        {
            long gapStart = sorted[i].Offset + sorted[i].Size;
            long gapEnd = sorted[i + 1].Offset;
            if (gapEnd > gapStart && gapStart < data.Length)
                result.Gaps.Add(ClassifySingleGap(data, gapStart, Math.Min(gapEnd, data.Length) - gapStart));
        }

        // Gap after last region
        long lastEnd = sorted[^1].Offset + sorted[^1].Size;
        if (lastEnd < data.Length)
            result.Gaps.Add(ClassifySingleGap(data, lastEnd, data.Length - lastEnd));
    }

    private static GapRegion ClassifySingleGap(byte[] data, long offset, long size)
    {
        // Sample up to 8KB for classification
        int sampleLen = (int)Math.Min(size, 8192);
        long end = Math.Min(offset + sampleLen, data.Length);

        // Build byte frequency distribution
        int[] freq = new int[256];
        int total = 0;
        int printableCount = 0;
        for (long i = offset; i < end; i++)
        {
            byte b = data[i];
            freq[b]++;
            total++;
            if ((b >= 0x20 && b < 0x7F) || b == 0x09 || b == 0x0A || b == 0x0D)
                printableCount++;
        }

        // Compute entropy
        double entropy = 0;
        int uniqueBytes = 0;
        for (int i = 0; i < 256; i++)
        {
            if (freq[i] == 0) continue;
            uniqueBytes++;
            double p = (double)freq[i] / total;
            entropy -= p * Math.Log2(p);
        }

        // Compute dominant byte ratio (how much the top byte dominates)
        int maxFreq = 0;
        for (int i = 0; i < 256; i++)
            if (freq[i] > maxFreq) maxFreq = freq[i];
        double dominance = total > 0 ? (double)maxFreq / total : 0;
        double printableRatio = total > 0 ? (double)printableCount / total : 0;

        // Classify
        GapClassification cls;
        if (entropy < 1.0 && dominance > 0.8)
            cls = GapClassification.Padding;
        else if (entropy < 3.0 && uniqueBytes < 16)
            cls = GapClassification.Padding;
        else if (printableRatio > 0.85 && entropy > 2.0 && entropy < 6.5)
            cls = GapClassification.Text;
        else if (entropy > 7.5 && uniqueBytes > 200)
            cls = GapClassification.Compressed;
        else if (entropy > 5.0 && entropy <= 7.5 && uniqueBytes > 100)
            cls = GapClassification.Code;
        else if (entropy >= 3.0 && entropy <= 5.0)
            cls = GapClassification.Structured;
        else
            cls = GapClassification.Unknown;

        return new GapRegion
        {
            Offset = offset,
            Size = size,
            Entropy = entropy,
            Classification = cls,
        };
    }

    // ═══════════════════════════════════════════════════════════════════════
    // HEADER-BASED SIZE ESTIMATION (per-format)
    // ═══════════════════════════════════════════════════════════════════════

    private static long EstimateRegionSize(byte[] data, long offset, string fileType)
    {
        try
        {
            return fileType switch
            {
                "PE/EXE" or "PE/DLL" => EstimatePeSize(data, offset),
                "BMP"                => EstimateBmpSize(data, offset),
                "WEBP"               => EstimateRiffSize(data, offset),
                "OLE/DOC"            => EstimateOleSize(data, offset),
                "SQLite"             => EstimateSqliteSize(data, offset),
                "ELF"                => EstimateElfSize(data, offset),
                _                    => 0, // will be handled by footer scan or fallback
            };
        }
        catch
        {
            return 0;
        }
    }

    private static long EstimatePeSize(byte[] data, long offset)
    {
        if (offset + 64 > data.Length) return 0;

        int peOff = BitConverter.ToInt32(data, (int)offset + 0x3C);
        int absPeOff = (int)offset + peOff;
        if (absPeOff + 24 > data.Length) return 0;

        if (data[absPeOff] != 0x50 || data[absPeOff + 1] != 0x45) return 0;

        ushort numSec = BitConverter.ToUInt16(data, absPeOff + 6);
        ushort optSize = BitConverter.ToUInt16(data, absPeOff + 20);
        int secStart = absPeOff + 24 + optSize;

        long end = 0;
        for (int i = 0; i < numSec; i++)
        {
            int so = secStart + (i * 40);
            if (so + 40 > data.Length) break;
            uint rawPtr = BitConverter.ToUInt32(data, so + 20);
            uint rawSz  = BitConverter.ToUInt32(data, so + 16);
            long secEnd = offset + rawPtr + rawSz;
            if (secEnd > end) end = secEnd;
        }

        return end > offset ? end - offset : 0;
    }

    private static long EstimateBmpSize(byte[] data, long offset)
    {
        if (offset + 6 > data.Length) return 0;
        uint bmpSize = BitConverter.ToUInt32(data, (int)offset + 2);
        return bmpSize > 0 && bmpSize <= data.Length - offset ? bmpSize : 0;
    }

    private static long EstimateRiffSize(byte[] data, long offset)
    {
        // RIFF container: size at offset +4 (4 bytes LE) + 8 for header
        if (offset + 8 > data.Length) return 0;
        uint size = BitConverter.ToUInt32(data, (int)offset + 4);
        return size > 0 ? size + 8 : 0;
    }

    private static long EstimateOleSize(byte[] data, long offset)
    {
        // OLE2: sector size from header, total sectors from FAT
        // Simplified: use header sector count
        if (offset + 48 > data.Length) return 0;
        ushort sectorPow = BitConverter.ToUInt16(data, (int)offset + 30);
        int sectorSize = 1 << sectorPow;
        uint totalSectors = BitConverter.ToUInt32(data, (int)offset + 44);
        long estimate = (long)(totalSectors + 1) * sectorSize;
        return estimate > 0 ? estimate : 0;
    }

    private static long EstimateSqliteSize(byte[] data, long offset)
    {
        // SQLite: page size at offset 16 (2 bytes BE), page count at offset 28 (4 bytes BE)
        if (offset + 100 > data.Length) return 0;
        ushort pageSize = (ushort)((data[offset + 16] << 8) | data[offset + 17]);
        uint pageCount = (uint)((data[offset + 28] << 24) | (data[offset + 29] << 16) |
                                (data[offset + 30] << 8) | data[offset + 31]);
        if (pageSize == 0 || pageCount == 0) return 0;
        // pageSize==1 means 65536 in SQLite
        long realPageSize = pageSize == 1 ? 65536 : pageSize;
        return realPageSize * pageCount;
    }

    private static long EstimateElfSize(byte[] data, long offset)
    {
        if (offset + 52 > data.Length) return 0;
        byte elfClass = data[offset + 4];
        byte elfData = data[offset + 5]; // 1=LE, 2=BE

        if (elfClass == 1) // 32-bit
        {
            if (offset + 52 > data.Length) return 0;
            // e_shoff (section header table offset) at offset 32 (4 bytes)
            // e_shnum at offset 48 (2 bytes), e_shentsize at offset 46 (2 bytes)
            uint shoff = elfData == 1
                ? BitConverter.ToUInt32(data, (int)offset + 32)
                : ReadUInt32BE(data, offset + 32);
            ushort shnum = elfData == 1
                ? BitConverter.ToUInt16(data, (int)offset + 48)
                : ReadUInt16BE(data, offset + 48);
            ushort shsize = elfData == 1
                ? BitConverter.ToUInt16(data, (int)offset + 46)
                : ReadUInt16BE(data, offset + 46);
            if (shoff > 0 && shnum > 0)
                return shoff + (long)shnum * shsize;
        }
        else if (elfClass == 2) // 64-bit
        {
            if (offset + 64 > data.Length) return 0;
            // e_shoff at offset 40 (8 bytes)
            // e_shnum at offset 60 (2 bytes), e_shentsize at offset 58 (2 bytes)
            long shoff = elfData == 1
                ? BitConverter.ToInt64(data, (int)offset + 40)
                : ReadInt64BE(data, offset + 40);
            ushort shnum = elfData == 1
                ? BitConverter.ToUInt16(data, (int)offset + 60)
                : ReadUInt16BE(data, offset + 60);
            ushort shsize = elfData == 1
                ? BitConverter.ToUInt16(data, (int)offset + 58)
                : ReadUInt16BE(data, offset + 58);
            if (shoff > 0 && shnum > 0)
                return shoff + (long)shnum * shsize;
        }

        return 0;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // PE OVERLAY DETECTION
    // ═══════════════════════════════════════════════════════════════════════

    private static void DetectOverlay(byte[] data, CarveResult result)
    {
        if (data.Length < 64 || data[0] != 0x4D || data[1] != 0x5A) return;

        try
        {
            int peOffset = BitConverter.ToInt32(data, 0x3C);
            if (peOffset < 0 || peOffset + 6 > data.Length) return;
            if (data[peOffset] != 0x50 || data[peOffset + 1] != 0x45 ||
                data[peOffset + 2] != 0x00 || data[peOffset + 3] != 0x00) return;

            ushort numSections = BitConverter.ToUInt16(data, peOffset + 6);
            ushort optHeaderSize = BitConverter.ToUInt16(data, peOffset + 20);
            int sectionStart = peOffset + 24 + optHeaderSize;

            long peImageEnd = 0;
            for (int i = 0; i < numSections; i++)
            {
                int secOff = sectionStart + (i * 40);
                if (secOff + 40 > data.Length) return;
                uint rawDataPtr  = BitConverter.ToUInt32(data, secOff + 20);
                uint rawDataSize = BitConverter.ToUInt32(data, secOff + 16);
                long secEnd = rawDataPtr + rawDataSize;
                if (secEnd > peImageEnd) peImageEnd = secEnd;
            }

            if (peImageEnd > 0 && peImageEnd < data.Length)
            {
                long overlaySize = data.Length - peImageEnd;
                if (overlaySize < 4) return;

                int previewLen = (int)Math.Min(64, overlaySize);
                byte[] preview = new byte[previewLen];
                Array.Copy(data, peImageEnd, preview, 0, previewLen);

                string desc = "Unknown overlay data";
                foreach (var (magic, description) in OverlayPatterns)
                {
                    if (peImageEnd + magic.Length <= data.Length &&
                        MatchesAt(data, (int)peImageEnd, magic))
                    {
                        desc = description;
                        break;
                    }
                }

                result.Overlay = new OverlayInfo
                {
                    Offset = peImageEnd, Size = overlaySize,
                    Preview = preview, Description = desc,
                    Entropy = ComputeEntropy(data, peImageEnd, Math.Min(overlaySize, 8192)),
                };

                result.Regions.Add(new CarvedRegion
                {
                    Offset = peImageEnd, Size = overlaySize,
                    FileType = "Overlay", Signature = "PE overlay",
                    Description = $"Overlay: {desc} ({EstimateSizeString(overlaySize)})",
                    Preview = preview,
                    Entropy = result.Overlay.Entropy,
                    ConfidenceLevel = "High",
                });
            }
        }
        catch { }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // CUSTOM SIGNATURE SCANNING
    // ═══════════════════════════════════════════════════════════════════════

    private static void ScanCustomSignatures(byte[] data, CarveResult result)
    {
        if (_customSignatures.Count == 0) return;

        var foundRanges = result.Regions.Select(r => (r.Offset, End: r.Offset + r.Size)).ToList();

        foreach (var csig in _customSignatures)
        {
            if (!csig.IsValid) continue;

            if (csig.IsTextBased)
            {
                byte[] magic = Encoding.ASCII.GetBytes(csig.TextPrefix);
                int pos = 0;
                while (pos < data.Length)
                {
                    int found = FindPattern(data, magic, pos, data.Length);
                    if (found < 0) break;
                    if (found > 0 && IsOverlapping(foundRanges, found)) { pos = found + 1; continue; }

                    long size = EstimateTextRegionSize(data, found);
                    int previewLen = (int)Math.Min(64, data.Length - found);
                    byte[] preview = new byte[previewLen];
                    Array.Copy(data, found, preview, 0, previewLen);

                    result.Regions.Add(new CarvedRegion
                    {
                        Offset = found, Size = size, FileType = csig.FileType,
                        Signature = csig.Description,
                        Description = $"[Custom] {csig.FileType} at 0x{found:X8}",
                        Preview = preview,
                        Entropy = ComputeEntropy(data, found, Math.Min(size, 4096)),
                        ConfidenceLevel = "Medium",
                    });
                    foundRanges.Add((found, found + size));
                    pos = found + 1;
                }
            }
            else
            {
                byte[] magic = csig.GetMagicBytes();
                if (magic.Length == 0) continue;
                int pos = 0;
                while (pos < data.Length)
                {
                    int searchStart = pos + csig.SearchOffset;
                    if (searchStart + magic.Length > data.Length) break;
                    int found = FindPattern(data, magic, searchStart, data.Length);
                    if (found < 0) break;

                    long regionStart = found - csig.SearchOffset;
                    if (regionStart < 0) regionStart = 0;
                    if (regionStart > 0 && IsOverlapping(foundRanges, regionStart)) { pos = found + 1; continue; }

                    long size = data.Length - regionStart; // fallback size
                    int previewLen = (int)Math.Min(64, data.Length - regionStart);
                    byte[] preview = new byte[previewLen];
                    Array.Copy(data, regionStart, preview, 0, previewLen);

                    result.Regions.Add(new CarvedRegion
                    {
                        Offset = regionStart, Size = size, FileType = csig.FileType,
                        Signature = csig.Description,
                        Description = $"[Custom] {csig.FileType} at 0x{regionStart:X8} ({EstimateSizeString(size)})",
                        Preview = preview,
                        Entropy = ComputeEntropy(data, regionStart, Math.Min(size, 4096)),
                        ConfidenceLevel = "Medium",
                    });
                    foundRanges.Add((regionStart, regionStart + size));
                    pos = found + Math.Max(1, magic.Length);
                }
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // RECURSIVE CARVING
    // ═══════════════════════════════════════════════════════════════════════

    private static void RecursiveCarve(byte[] data, List<SignatureDefinition> sigs,
        CarveResult result, int depth)
    {
        if (depth >= MaxRecursionDepth) return;

        var topLevel = result.Regions.Where(r => r.Depth == depth).ToList();

        foreach (var parent in topLevel)
        {
            if (parent.Size < 16 || parent.FileType == "Overlay") continue;

            long start = parent.Offset;
            long length = Math.Min(parent.Size, data.Length - start);
            if (length < 16) continue;

            byte[] regionData = new byte[length];
            Array.Copy(data, start, regionData, 0, (int)length);

            var subResult = new CarveResult { FileSize = length };
            Pass1_DiscoverAndValidate(regionData, sigs, subResult);

            // Filter: remove match at offset 0 (the parent itself)
            var children = subResult.Regions
                .Where(r => r.Offset > 0)
                .OrderBy(r => r.Offset)
                .ToList();

            // Size the children
            for (int i = 0; i < children.Count; i++)
            {
                var child = children[i];
                long nextOffset = (i + 1 < children.Count) ? children[i + 1].Offset : length;
                if (child.Size <= 0)
                    child.Size = nextOffset - child.Offset;

                child.Offset += start;
                child.Depth = depth + 1;
                child.ParentIndex = parent.Index;
                parent.Children.Add(child);
            }

            if (children.Count > 0)
            {
                result.Regions.AddRange(children);
                RecursiveCarve(data, sigs, result, depth + 1);
            }
        }
    }

    private static List<CarvedRegion> FlattenRegions(List<CarvedRegion> regions)
    {
        var flat = new List<CarvedRegion>();
        var topLevel = regions.Where(r => r.Depth == 0).OrderBy(r => r.Offset).ToList();
        foreach (var r in topLevel)
        {
            flat.Add(r);
            FlattenChildren(r, flat);
        }
        return flat;
    }

    private static void FlattenChildren(CarvedRegion parent, List<CarvedRegion> flat)
    {
        foreach (var child in parent.Children.OrderBy(c => c.Offset))
        {
            flat.Add(child);
            FlattenChildren(child, flat);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // ENTROPY HEATMAP
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Compute entropy in four bases per block and a cross-base divergence metric.
    ///
    /// Byte (base-256):   Standard Shannon entropy on byte values.       Max 8.0 bits.
    /// Nibble (base-16):  Each byte → 2 nibbles, entropy on 16 symbols. Max 4.0 bits.
    ///                    Reveals hex-structured data, UUID strings, ASCII hex dumps.
    /// Bit (base-2):      Ratio of 1-bits to 0-bits per block.          Max 1.0 bit.
    ///                    Reveals bit-bias patterns: padding, fill bytes, alignment.
    /// Bigram (base-64K): Entropy on consecutive byte pairs.             Max 16.0 bits.
    ///                    Captures sequential structure byte entropy misses.
    ///
    /// Divergence:        Measures how much the four bases disagree after normalization
    ///                    to [0,1]. High divergence = structural boundary candidate.
    ///                    Computed as standard deviation of the four normalized values.
    /// </summary>
    private static void ComputeEntropyMap(byte[] data, CarveResult result, int blockSize = 1024)
    {
        if (data.Length == 0) return;
        result.EntropyBlockSize = blockSize;
        int numBlocks = (int)Math.Ceiling((double)data.Length / blockSize);

        result.EntropyMap       = new double[numBlocks];
        result.NibbleEntropyMap = new double[numBlocks];
        result.BitEntropyMap    = new double[numBlocks];
        result.BigramEntropyMap = new double[numBlocks];
        result.DivergenceMap    = new double[numBlocks];
        result.DeltaEntropyMap  = new double[numBlocks];

        for (int i = 0; i < numBlocks; i++)
        {
            long offset = (long)i * blockSize;
            int length = (int)Math.Min(blockSize, data.Length - offset);

            double byteEnt   = ComputeEntropy(data, offset, length);
            double nibbleEnt  = ComputeNibbleEntropy(data, offset, length);
            double bitEnt     = ComputeBitEntropy(data, offset, length);
            double bigramEnt  = ComputeBigramEntropy(data, offset, length);

            result.EntropyMap[i]       = byteEnt;
            result.NibbleEntropyMap[i] = nibbleEnt;
            result.BitEntropyMap[i]    = bitEnt;
            result.BigramEntropyMap[i] = bigramEnt;

            // Normalize each to 0–1 and compute divergence (std dev of the four)
            double nByte   = byteEnt / 8.0;
            double nNibble = nibbleEnt / 4.0;
            double nBit    = bitEnt;  // already 0–1
            double nBigram = bigramEnt / 16.0;

            double mean = (nByte + nNibble + nBit + nBigram) / 4.0;
            double variance = ((nByte - mean) * (nByte - mean) +
                               (nNibble - mean) * (nNibble - mean) +
                               (nBit - mean) * (nBit - mean) +
                               (nBigram - mean) * (nBigram - mean)) / 4.0;
            result.DivergenceMap[i] = Math.Sqrt(variance);
        }

        // Compute delta entropy (rate of change between adjacent blocks)
        result.DeltaEntropyMap[0] = 0;
        for (int i = 1; i < numBlocks; i++)
            result.DeltaEntropyMap[i] = Math.Abs(result.EntropyMap[i] - result.EntropyMap[i - 1]);
    }

    /// <summary>Nibble entropy: split each byte into two 4-bit nibbles, entropy on 16 symbols.</summary>
    private static double ComputeNibbleEntropy(byte[] data, long offset, int length)
    {
        int[] freq = new int[16];
        int total = 0;
        long end = Math.Min(offset + length, data.Length);
        for (long i = offset; i < end; i++)
        {
            freq[data[i] >> 4]++;
            freq[data[i] & 0x0F]++;
            total += 2;
        }
        return ShannonEntropy(freq, total);
    }

    /// <summary>Bit entropy: count 1-bits vs 0-bits, entropy on 2 symbols.</summary>
    private static double ComputeBitEntropy(byte[] data, long offset, int length)
    {
        int ones = 0;
        int total = 0;
        long end = Math.Min(offset + length, data.Length);
        for (long i = offset; i < end; i++)
        {
            byte b = data[i];
            // Popcount
            ones += ((b >> 0) & 1) + ((b >> 1) & 1) + ((b >> 2) & 1) + ((b >> 3) & 1) +
                    ((b >> 4) & 1) + ((b >> 5) & 1) + ((b >> 6) & 1) + ((b >> 7) & 1);
            total += 8;
        }
        if (total == 0) return 0;
        int zeros = total - ones;
        double p1 = (double)ones / total;
        double p0 = (double)zeros / total;
        double entropy = 0;
        if (p1 > 0) entropy -= p1 * Math.Log2(p1);
        if (p0 > 0) entropy -= p0 * Math.Log2(p0);
        return entropy;
    }

    /// <summary>Bigram entropy: entropy on consecutive byte pairs (65536 possible values).</summary>
    private static double ComputeBigramEntropy(byte[] data, long offset, int length)
    {
        if (length < 2) return 0;
        // Use a dictionary since 65536-entry array is wasteful for small blocks
        var freq = new Dictionary<int, int>();
        int total = 0;
        long end = Math.Min(offset + length - 1, data.Length - 1);
        for (long i = offset; i < end; i++)
        {
            int pair = (data[i] << 8) | data[i + 1];
            freq[pair] = freq.GetValueOrDefault(pair) + 1;
            total++;
        }
        if (total == 0) return 0;
        double entropy = 0;
        foreach (int count in freq.Values)
        {
            double p = (double)count / total;
            entropy -= p * Math.Log2(p);
        }
        return entropy;
    }

    /// <summary>Generic Shannon entropy from a frequency array.</summary>
    private static double ShannonEntropy(int[] freq, int total)
    {
        if (total == 0) return 0;
        double entropy = 0;
        for (int i = 0; i < freq.Length; i++)
        {
            if (freq[i] == 0) continue;
            double p = (double)freq[i] / total;
            entropy -= p * Math.Log2(p);
        }
        return entropy;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // EXTRACTION
    // ═══════════════════════════════════════════════════════════════════════

    public static void ExtractRegion(string sourceFile, CarvedRegion region, string outputPath)
    {
        using var fs = File.OpenRead(sourceFile);
        fs.Seek(region.Offset, SeekOrigin.Begin);

        long toRead = region.Size;
        if (region.Offset + toRead > fs.Length)
            toRead = fs.Length - region.Offset;

        using var outFs = File.Create(outputPath);
        byte[] buffer = new byte[81920];
        long remaining = toRead;
        while (remaining > 0)
        {
            int chunk = (int)Math.Min(buffer.Length, remaining);
            int read = fs.Read(buffer, 0, chunk);
            if (read == 0) break;
            outFs.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    public static void ExtractRange(string sourceFile, long offset, long size, string outputPath)
    {
        using var fs = File.OpenRead(sourceFile);
        if (offset >= fs.Length) return;
        fs.Seek(offset, SeekOrigin.Begin);

        long toRead = Math.Min(size, fs.Length - offset);
        using var outFs = File.Create(outputPath);
        byte[] buffer = new byte[81920];
        long remaining = toRead;
        while (remaining > 0)
        {
            int chunk = (int)Math.Min(buffer.Length, remaining);
            int read = fs.Read(buffer, 0, chunk);
            if (read == 0) break;
            outFs.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // EXPORT
    // ═══════════════════════════════════════════════════════════════════════

    public static void ExportJson(CarveResult result, string outputPath)
    {
        var export = new
        {
            result.FileName, result.FilePath, result.FileSize,
            FileSizeHuman = result.FileSizeHuman,
            RegionCount = result.Regions.Count,
            HasOverlay = result.HasOverlay,
            Overlay = result.Overlay == null ? null : new
            {
                result.Overlay.Offset, OffsetHex = result.Overlay.OffsetHex,
                result.Overlay.Size, SizeDisplay = result.Overlay.SizeDisplay,
                result.Overlay.Entropy, result.Overlay.Description,
            },
            Regions = result.Regions.Select(r => new
            {
                r.Index, r.Offset, OffsetHex = r.OffsetHex,
                r.Size, SizeDisplay = r.SizeDisplay,
                r.FileType, r.Signature, r.Description,
                r.Entropy, r.Depth, r.ParentIndex,
                r.ConfidenceLevel, r.SizingMethod,
                PreviewHex = r.PreviewHex,
            }),
        };

        var opts = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        File.WriteAllText(outputPath, JsonSerializer.Serialize(export, opts));
    }

    public static void ExportCsv(CarveResult result, string outputPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Index,Depth,ParentIndex,FileType,Signature,Offset,OffsetHex,Size,SizeDisplay,Entropy,Confidence,SizingMethod,Description");
        foreach (var r in result.Regions)
        {
            string desc = r.Description.Replace("\"", "\"\"");
            string sig = r.Signature.Replace("\"", "\"\"");
            sb.AppendLine($"{r.Index},{r.Depth},{r.ParentIndex},\"{r.FileType}\",\"{sig}\",{r.Offset},{r.OffsetHex},{r.Size},\"{r.SizeDisplay}\",{r.Entropy:F4},{r.ConfidenceLevel},{r.SizingMethod},\"{desc}\"");
        }
        File.WriteAllText(outputPath, sb.ToString());
    }

    // ═══════════════════════════════════════════════════════════════════════
    // RE TOOL EXPORTS
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Export as IDA-compatible MAP file (File → Load File → MAP File).</summary>
    public static void ExportIdaMap(CarveResult result, string outputPath, string? sourceFilePath = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"; BinaryCarver region map for {result.FileName}");
        sb.AppendLine($"; File size: {result.FileSizeHuman} ({result.FileSize} bytes)");
        sb.AppendLine($"; Regions: {result.Regions.Count}");
        sb.AppendLine();
        sb.AppendLine(" Start         Length     Name                   Class");

        foreach (var r in result.Regions.Where(r => r.Depth == 0))
        {
            string name = SanitizeSegName(r.FileType, r.Index);
            string sclass = IsCodeType(r.FileType) ? "CODE" : "DATA";
            sb.AppendLine($" 0001:{r.Offset:X8} {r.Size:X8}H {name,-23} {sclass}");
        }

        // Parsed internal sections (PE .text/.data, ELF segments, etc.)
        if (sourceFilePath != null)
        {
            var sectionMap = ParseAllRegionSections(sourceFilePath, result);
            foreach (var (regionIdx, secs) in sectionMap)
            {
                foreach (var sec in secs)
                {
                    if (sec.Size <= 0) continue;
                    string secName = $"{sec.Name}_{regionIdx}";
                    sb.AppendLine($" 0001:{sec.FileOffset:X8} {sec.Size:X8}H {secName,-23} {sec.Class}");
                }
            }
        }

        // Map gaps as their classification
        foreach (var g in result.Gaps)
        {
            string name = $"gap_{g.Classification}_{g.Offset:X}";
            string sclass = g.Classification == GapClassification.Code ? "CODE" : "DATA";
            sb.AppendLine($" 0001:{g.Offset:X8} {g.Size:X8}H {name,-23} {sclass}");
        }

        sb.AppendLine();
        sb.AppendLine(" Address         Publics by Value");
        sb.AppendLine();

        foreach (var r in result.Regions.Where(r => r.Depth == 0))
        {
            string name = SanitizeSegName(r.FileType, r.Index);
            sb.AppendLine($" 0001:{r.Offset:X8}       {name}");
        }

        File.WriteAllText(outputPath, sb.ToString());
    }

    /// <summary>Export as IDAPython script (.py) for IDA Pro 7.x+.</summary>
    public static void ExportIdaPython(CarveResult result, string outputPath, string? sourceFilePath = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# BinaryCarver region map — IDAPython 7.x+ script");
        sb.AppendLine($"# Source: {result.FileName} ({result.FileSizeHuman})");
        sb.AppendLine("# Usage: File → Script File... (Alt+F7) in IDA Pro");
        sb.AppendLine();
        sb.AppendLine("import idaapi");
        sb.AppendLine("import idc");
        sb.AppendLine();
        sb.AppendLine("def apply_binarycarver_map():");
        sb.AppendLine($"    print('Applying BinaryCarver region map: {result.Regions.Count} regions')");
        sb.AppendLine();

        // Color palette for region types
        sb.AppendLine("    # Color palette (IDA uses BBGGRR)");
        sb.AppendLine("    COLORS = {");
        sb.AppendLine("        'PE':   0x50AF4C, 'EXE':  0x50AF4C, 'ELF': 0x50AF4C, 'MachO': 0x50AF4C,");
        sb.AppendLine("        'ZIP':  0xF39621, 'GZIP': 0xF39621, '7Z':  0xF39621, 'RAR':   0xF39621,");
        sb.AppendLine("        'PNG':  0x0098FF, 'JPEG': 0x0098FF, 'GIF': 0x0098FF, 'BMP':   0x0098FF,");
        sb.AppendLine("        'PDF':  0xBC27AB, 'DOC':  0xBC27AB, 'XML': 0xBC27AB,");
        sb.AppendLine("        'OVL':  0x3644F4,");
        sb.AppendLine("    }");
        sb.AppendLine();

        foreach (var r in result.Regions.Where(r => r.Depth == 0))
        {
            string name = SanitizeSegName(r.FileType, r.Index);
            string sclass = IsCodeType(r.FileType) ? "CODE" : "DATA";
            long end = r.Offset + r.Size;
            string desc = r.Description.Replace("'", "\\'").Replace("\\", "\\\\");
            string sig = r.Signature.Replace("'", "\\'");

            sb.AppendLine($"    # Region {r.Index}: {r.FileType} at 0x{r.Offset:X8}");
            sb.AppendLine($"    idaapi.add_segm(0, 0x{r.Offset:X}, 0x{end:X}, '{name}', '{sclass}')");
            sb.AppendLine($"    idc.set_cmt(0x{r.Offset:X}, 'BinaryCarver: {r.FileType} | {r.SizeDisplay} | entropy={r.Entropy:F3} | {r.ConfidenceLevel} conf | sig={sig}', 1)");

            // Color the region start
            string typeKey = r.FileType.Split('/')[0].Replace("-", "");
            sb.AppendLine($"    _ck = '{typeKey}'");
            sb.AppendLine($"    if _ck in COLORS: idc.set_color(0x{r.Offset:X}, 1, COLORS[_ck])");
            sb.AppendLine();
        }

        // Parsed internal sections (PE .text/.data, ELF segments, etc.)
        if (sourceFilePath != null)
        {
            var sectionMap = ParseAllRegionSections(sourceFilePath, result);
            if (sectionMap.Count > 0)
            {
                sb.AppendLine("    # ── Internal section detail (parsed from format headers) ──");
                foreach (var (regionIdx, secs) in sectionMap)
                {
                    var region = result.Regions.First(r => r.Index == regionIdx);
                    sb.AppendLine($"    # Sub-sections of region {regionIdx} ({region.FileType}):");
                    foreach (var sec in secs)
                    {
                        if (sec.Size <= 0) continue;
                        string secName = $"{SanitizeSegName(region.FileType, regionIdx)}_{sec.Name}";
                        sb.AppendLine($"    idaapi.add_segm(0, 0x{sec.FileOffset:X}, 0x{sec.FileOffset + sec.Size:X}, '{secName}', '{sec.Class}')");
                        sb.AppendLine($"    idc.set_cmt(0x{sec.FileOffset:X}, '{sec.Description} [{sec.Permissions}]', 1)");
                    }
                    sb.AppendLine();
                }
            }
        }

        // Gap annotations
        if (result.Gaps.Count > 0)
        {
            sb.AppendLine("    # Gap regions (uncarved data between detected files)");
            foreach (var g in result.Gaps)
            {
                sb.AppendLine($"    idc.set_cmt(0x{g.Offset:X}, 'BinaryCarver gap: {g.Classification} | {g.SizeDisplay} | entropy={g.Entropy:F3}', 1)");
            }
            sb.AppendLine();
        }

        sb.AppendLine($"    print('Done — {result.Regions.Count} regions + {result.Gaps.Count} gaps mapped')");
        sb.AppendLine();
        sb.AppendLine("apply_binarycarver_map()");

        File.WriteAllText(outputPath, sb.ToString());
    }

    /// <summary>Export as Ghidra Python script (GhidraScript).</summary>
    public static void ExportGhidraScript(CarveResult result, string outputPath, string? sourceFilePath = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# BinaryCarver region map — Ghidra Python script");
        sb.AppendLine($"# Source: {result.FileName} ({result.FileSizeHuman})");
        sb.AppendLine("# Usage: Script Manager → Run Script in Ghidra");
        sb.AppendLine("# @category BinaryCarver");
        sb.AppendLine("# @menupath Tools.BinaryCarver.Apply Region Map");
        sb.AppendLine();
        sb.AppendLine("from ghidra.program.model.listing import CodeUnit");
        sb.AppendLine("from ghidra.program.model.symbol import SourceType");
        sb.AppendLine();

        foreach (var r in result.Regions.Where(r => r.Depth == 0))
        {
            string name = SanitizeSegName(r.FileType, r.Index);
            string desc = r.Description.Replace("'", "\\'");
            string sig = r.Signature.Replace("'", "\\'");
            string bkCat = r.FileType.Split('/')[0];

            sb.AppendLine($"# Region {r.Index}: {r.FileType} at 0x{r.Offset:X8} ({r.SizeDisplay})");
            sb.AppendLine($"addr = toAddr(0x{r.Offset:X})");
            sb.AppendLine($"createBookmark(addr, '{bkCat}', '{r.FileType}: {r.SizeDisplay} entropy={r.Entropy:F3} [{r.ConfidenceLevel}]')");
            sb.AppendLine($"setEOLComment(addr, 'BinaryCarver: {r.FileType} | {r.SizeDisplay} | entropy={r.Entropy:F3} | {r.ConfidenceLevel} conf | sized={r.SizingMethod} | sig={sig}')");
            sb.AppendLine($"createLabel(toAddr(0x{r.Offset:X}), '{name}', True)");
            sb.AppendLine();
        }

        // Parsed internal sections
        if (sourceFilePath != null)
        {
            var sectionMap = ParseAllRegionSections(sourceFilePath, result);
            if (sectionMap.Count > 0)
            {
                sb.AppendLine("# ── Internal section detail (parsed from format headers) ──");
                foreach (var (regionIdx, secs) in sectionMap)
                {
                    var region = result.Regions.First(r => r.Index == regionIdx);
                    sb.AppendLine($"# Sub-sections of region {regionIdx} ({region.FileType}):");
                    foreach (var sec in secs)
                    {
                        if (sec.Size <= 0) continue;
                        string secLabel = $"{SanitizeSegName(region.FileType, regionIdx)}_{sec.Name}";
                        sb.AppendLine($"createLabel(toAddr(0x{sec.FileOffset:X}), '{secLabel}', True)");
                        sb.AppendLine($"setEOLComment(toAddr(0x{sec.FileOffset:X}), '{sec.Description} [{sec.Permissions}] VA=0x{sec.VirtualAddr:X}')");
                        sb.AppendLine($"createBookmark(toAddr(0x{sec.FileOffset:X}), 'Section', '{sec.Name}: {sec.Size} bytes {sec.Class} {sec.Permissions}')");
                    }
                    sb.AppendLine();
                }
            }
        }

        // Gap annotations
        if (result.Gaps.Count > 0)
        {
            sb.AppendLine("# Gap regions");
            foreach (var g in result.Gaps)
            {
                sb.AppendLine($"setEOLComment(toAddr(0x{g.Offset:X}), 'BinaryCarver gap: {g.Classification} | {g.SizeDisplay} | entropy={g.Entropy:F3}')");
                sb.AppendLine($"createBookmark(toAddr(0x{g.Offset:X}), 'Gap', '{g.Classification}: {g.SizeDisplay} entropy={g.Entropy:F3}')");
            }
            sb.AppendLine();
        }

        sb.AppendLine($"print('BinaryCarver: mapped {result.Regions.Count} regions + {result.Gaps.Count} gaps')");

        File.WriteAllText(outputPath, sb.ToString());
    }

    /// <summary>Export as radare2 command script (.r2).</summary>
    public static void ExportR2Script(CarveResult result, string outputPath, string? sourceFilePath = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# BinaryCarver region map — radare2 script");
        sb.AppendLine($"# Source: {result.FileName} ({result.FileSizeHuman})");
        sb.AppendLine($"# Usage: r2 -i {Path.GetFileName(outputPath)} <binary>");
        sb.AppendLine();

        // Sections
        sb.AppendLine("# Define carved regions as sections + flags");
        sb.AppendLine("fs+ binarycarver");
        sb.AppendLine();

        foreach (var r in result.Regions.Where(r => r.Depth == 0))
        {
            string name = SanitizeSegName(r.FileType, r.Index).ToLower().Replace("/", "_");
            string rwx = IsCodeType(r.FileType) ? "r-x" : "r--";

            sb.AppendLine($"# Region {r.Index}: {r.FileType} ({r.SizeDisplay})");
            sb.AppendLine($"S 0x{r.Offset:X} 0x{r.Offset:X} 0x{r.Size:X} 0x{r.Size:X} {name} {rwx}");
            sb.AppendLine($"f region.{name} 0x{r.Size:X} 0x{r.Offset:X}");
            sb.AppendLine($"CCu BinaryCarver: {r.FileType} | {r.SizeDisplay} | entropy={r.Entropy:F3} | {r.ConfidenceLevel} @ 0x{r.Offset:X}");
            sb.AppendLine();
        }

        // Parsed sub-sections
        if (sourceFilePath != null)
        {
            var sectionMap = ParseAllRegionSections(sourceFilePath, result);
            if (sectionMap.Count > 0)
            {
                sb.AppendLine("# Internal section detail");
                foreach (var (regionIdx, secs) in sectionMap)
                {
                    foreach (var sec in secs)
                    {
                        if (sec.Size <= 0) continue;
                        string secName = $"sec.{sec.Name}_{regionIdx}".ToLower().Replace(" ", "_");
                        sb.AppendLine($"f {secName} 0x{sec.Size:X} 0x{sec.FileOffset:X}");
                        sb.AppendLine($"CCu {sec.Description} [{sec.Permissions}] @ 0x{sec.FileOffset:X}");
                    }
                }
                sb.AppendLine();
            }
        }

        // Gaps
        if (result.Gaps.Count > 0)
        {
            sb.AppendLine("# Gap regions");
            foreach (var g in result.Gaps)
            {
                string gapName = $"gap_{g.Classification}_{g.Offset:x}".ToLower();
                sb.AppendLine($"f gap.{gapName} 0x{g.Size:X} 0x{g.Offset:X}");
                sb.AppendLine($"CCu BinaryCarver gap: {g.Classification} | {g.SizeDisplay} | entropy={g.Entropy:F3} @ 0x{g.Offset:X}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("fs-");
        sb.AppendLine($"# {result.Regions.Count} regions + {result.Gaps.Count} gaps mapped");

        File.WriteAllText(outputPath, sb.ToString());
    }

    /// <summary>Export as Binary Ninja Python script.</summary>
    public static void ExportBinaryNinja(CarveResult result, string outputPath, string? sourceFilePath = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# BinaryCarver region map — Binary Ninja Python script");
        sb.AppendLine($"# Source: {result.FileName} ({result.FileSizeHuman})");
        sb.AppendLine("# Usage: Tools → Run Script in Binary Ninja");
        sb.AppendLine();
        sb.AppendLine("from binaryninja import *");
        sb.AppendLine("from binaryninja.enums import SegmentFlag, SectionSemantics");
        sb.AppendLine();
        sb.AppendLine("def apply_binarycarver_map(bv):");
        sb.AppendLine($"    print(f'Applying BinaryCarver map: {result.Regions.Count} regions')");
        sb.AppendLine();

        foreach (var r in result.Regions.Where(r => r.Depth == 0))
        {
            string name = SanitizeSegName(r.FileType, r.Index);
            string desc = r.Description.Replace("'", "\\'");
            bool isCode = IsCodeType(r.FileType);
            string flags = isCode
                ? "SegmentFlag.SegmentReadable | SegmentFlag.SegmentExecutable"
                : "SegmentFlag.SegmentReadable";
            string semantics = isCode
                ? "SectionSemantics.ReadOnlyCodeSectionSemantics"
                : "SectionSemantics.ReadOnlyDataSectionSemantics";

            sb.AppendLine($"    # Region {r.Index}: {r.FileType} ({r.SizeDisplay})");
            sb.AppendLine($"    bv.add_user_segment(0x{r.Offset:X}, 0x{r.Size:X}, 0x{r.Offset:X}, 0x{r.Size:X}, {flags})");
            sb.AppendLine($"    bv.add_user_section('{name}', 0x{r.Offset:X}, 0x{r.Size:X}, {semantics})");
            sb.AppendLine($"    bv.set_comment_at(0x{r.Offset:X}, 'BinaryCarver: {r.FileType} | {r.SizeDisplay} | entropy={r.Entropy:F3} | {r.ConfidenceLevel} conf')");
            sb.AppendLine();
        }

        // Parsed sub-sections
        if (sourceFilePath != null)
        {
            var sectionMap = ParseAllRegionSections(sourceFilePath, result);
            if (sectionMap.Count > 0)
            {
                sb.AppendLine("    # Internal section detail");
                foreach (var (regionIdx, secs) in sectionMap)
                {
                    var region = result.Regions.First(r => r.Index == regionIdx);
                    foreach (var sec in secs)
                    {
                        if (sec.Size <= 0) continue;
                        string secName = $"{SanitizeSegName(region.FileType, regionIdx)}_{sec.Name}";
                        string secFlags = sec.Permissions.Contains('x')
                            ? "SegmentFlag.SegmentReadable | SegmentFlag.SegmentExecutable"
                            : sec.Permissions.Contains('w')
                                ? "SegmentFlag.SegmentReadable | SegmentFlag.SegmentWritable"
                                : "SegmentFlag.SegmentReadable";
                        sb.AppendLine($"    bv.add_user_section('{secName}', 0x{sec.FileOffset:X}, 0x{sec.Size:X})");
                        sb.AppendLine($"    bv.set_comment_at(0x{sec.FileOffset:X}, '{sec.Description} [{sec.Permissions}]')");
                    }
                }
                sb.AppendLine();
            }
        }

        // Gap comments
        if (result.Gaps.Count > 0)
        {
            sb.AppendLine("    # Gap annotations");
            foreach (var g in result.Gaps)
            {
                sb.AppendLine($"    bv.set_comment_at(0x{g.Offset:X}, 'BinaryCarver gap: {g.Classification} | {g.SizeDisplay} | entropy={g.Entropy:F3}')");
            }
            sb.AppendLine();
        }

        sb.AppendLine($"    print('Done — {result.Regions.Count} regions + {result.Gaps.Count} gaps mapped')");
        sb.AppendLine();
        sb.AppendLine("# Auto-run");
        sb.AppendLine("apply_binarycarver_map(bv)");

        File.WriteAllText(outputPath, sb.ToString());
    }

    // ── Helpers for RE exports ──────────────────────────────────────────

    private static string SanitizeSegName(string fileType, int index)
    {
        string clean = fileType.Replace("/", "_").Replace(" ", "_").Replace("-", "_");
        return $"{clean}_{index:D3}";
    }

    private static bool IsCodeType(string fileType)
    {
        return fileType is "PE/EXE" or "PE/DLL" or "ELF" or "Mach-O"
            or "DEX" or "COM" or "NE/EXE";
    }

    /// <summary>
    /// For each top-level region in a recognized format (PE, ELF, Mach-O, ZIP),
    /// parse internal section headers and return them keyed by region index.
    /// Requires the source file path to read region bytes.
    /// </summary>
    public static Dictionary<int, List<FormatSectionParser.ParsedSection>> ParseAllRegionSections(
        string filePath, CarveResult result)
    {
        var map = new Dictionary<int, List<FormatSectionParser.ParsedSection>>();
        if (!File.Exists(filePath)) return map;

        var parsableTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "PE/EXE", "PE/DLL", "ELF", "Mach-O", "ZIP", "JAR", "APK",
            "GZIP", "7Z", "CAB", "DOCX", "XLSX", "PPTX" // Office = ZIP
        };

        using var fs = File.OpenRead(filePath);
        foreach (var r in result.Regions.Where(r => r.Depth == 0))
        {
            if (!parsableTypes.Contains(r.FileType)) continue;
            if (r.Size > 50_000_000 || r.Size < 64) continue; // skip huge/tiny

            fs.Seek(r.Offset, SeekOrigin.Begin);
            byte[] buf = new byte[Math.Min(r.Size, 2_000_000)]; // read up to 2MB for header parsing
            int read = fs.Read(buf, 0, buf.Length);
            if (read < 64) continue;
            if (read < buf.Length) Array.Resize(ref buf, read);

            var sections = FormatSectionParser.Parse(buf, r.Offset);
            if (sections.Count > 0) map[r.Index] = sections;
        }

        return map;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // UTILITY
    // ═══════════════════════════════════════════════════════════════════════

    private static bool IsOverlapping(List<(long Start, long End)> ranges, long offset)
    {
        foreach (var (s, e) in ranges)
            if (offset >= s && offset < e) return true;
        return false;
    }

    private static bool MatchesAt(byte[] data, int offset, byte[] pattern)
    {
        if (offset + pattern.Length > data.Length) return false;
        for (int i = 0; i < pattern.Length; i++)
            if (data[offset + i] != pattern[i]) return false;
        return true;
    }

    private static int FindPattern(byte[] data, byte[] pattern, int start, int end)
    {
        int limit = Math.Min(end, data.Length) - pattern.Length;
        for (int i = start; i <= limit; i++)
        {
            bool match = true;
            for (int j = 0; j < pattern.Length; j++)
            {
                if (data[i + j] != pattern[j]) { match = false; break; }
            }
            if (match) return i;
        }
        return -1;
    }

    private static long EstimateTextRegionSize(byte[] data, long offset)
    {
        long maxScan = Math.Min(data.Length, offset + 10_000_000);
        int nullCount = 0;
        for (long i = offset; i < maxScan; i++)
        {
            if (data[i] == 0) { nullCount++; if (nullCount >= 4) return i - nullCount + 1 - offset; }
            else nullCount = 0;
        }
        return maxScan - offset;
    }

    private static double ComputeEntropy(byte[] data, long offset, long length)
    {
        int[] freq = new int[256];
        int total = 0;
        long end = Math.Min(offset + length, data.Length);
        for (long i = offset; i < end; i++) { freq[data[i]]++; total++; }
        if (total == 0) return 0;

        double entropy = 0;
        for (int i = 0; i < 256; i++)
        {
            if (freq[i] == 0) continue;
            double p = (double)freq[i] / total;
            entropy -= p * Math.Log2(p);
        }
        return entropy;
    }

    private static string EstimateSizeString(long size)
    {
        if (size < 1024) return $"{size} B";
        if (size < 1_048_576) return $"{size / 1024.0:F1} KB";
        return $"{size / 1_048_576.0:F2} MB";
    }

    private static ushort ReadUInt16BE(byte[] data, long offset) =>
        (ushort)((data[offset] << 8) | data[offset + 1]);

    private static uint ReadUInt32BE(byte[] data, long offset) =>
        (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);

    /// <summary>
    /// Read a file using memory-mapped I/O. For large files this avoids
    /// the double-buffering overhead of File.ReadAllBytes and reduces peak
    /// memory by letting the OS page in data on demand during the copy.
    /// </summary>
    private static byte[] ReadViaMemoryMap(string filePath, int length)
    {
        var data = new byte[length];
        using var mmf = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open,
            null, 0, MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, length, MemoryMappedFileAccess.Read);
        accessor.ReadArray(0, data, 0, length);
        return data;
    }

    private static long ReadInt64BE(byte[] data, long offset) =>
        ((long)data[offset] << 56) | ((long)data[offset + 1] << 48) |
        ((long)data[offset + 2] << 40) | ((long)data[offset + 3] << 32) |
        ((long)data[offset + 4] << 24) | ((long)data[offset + 5] << 16) |
        ((long)data[offset + 6] << 8) | data[offset + 7];
}
