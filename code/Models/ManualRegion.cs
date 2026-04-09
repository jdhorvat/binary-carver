using System.Text.Json;
using System.Text.Json.Serialization;

namespace BinaryCarver.Models;

/// <summary>
/// A user-defined region built from manually selected 3D page blocks.
/// Blocks are tracked by page index; byte ranges are computed from block size at export time.
/// </summary>
public class ManualRegion
{
    public string Name { get; set; } = "Unnamed";

    /// <summary>Color used in 3D overlay to indicate blocks belong to this region.</summary>
    public string ColorHex { get; set; } = "#FF6F00";

    /// <summary>Set of page indices assigned to this region (unordered — may be non-contiguous).</summary>
    public HashSet<int> PageIndices { get; set; } = [];

    /// <summary>User-provided notes / description.</summary>
    public string Notes { get; set; } = "";

    // ── Display properties ──────────────────────────────────────────────
    [JsonIgnore]
    public int BlockCount => PageIndices.Count;

    [JsonIgnore]
    public string BlockCountDisplay => $"{BlockCount} block{(BlockCount == 1 ? "" : "s")}";

    /// <summary>Compute total byte size given a block size.</summary>
    public long GetByteSize(int blockSize) => (long)PageIndices.Count * blockSize;

    /// <summary>Get contiguous spans (offset, size) from the page indices, sorted by offset.</summary>
    public List<(long Offset, long Size)> GetSpans(int blockSize)
    {
        if (PageIndices.Count == 0) return [];

        var sorted = PageIndices.OrderBy(p => p).ToList();
        var spans = new List<(long, long)>();

        int spanStart = sorted[0];
        int spanEnd = sorted[0];

        for (int i = 1; i < sorted.Count; i++)
        {
            if (sorted[i] == spanEnd + 1)
            {
                spanEnd = sorted[i];
            }
            else
            {
                spans.Add(((long)spanStart * blockSize, (long)(spanEnd - spanStart + 1) * blockSize));
                spanStart = sorted[i];
                spanEnd = sorted[i];
            }
        }
        spans.Add(((long)spanStart * blockSize, (long)(spanEnd - spanStart + 1) * blockSize));
        return spans;
    }
}

/// <summary>Helper for persisting manual regions to JSON alongside the analysis.</summary>
public static class ManualRegionStore
{
    public static string Serialize(List<ManualRegion> regions)
    {
        return JsonSerializer.Serialize(regions, new JsonSerializerOptions { WriteIndented = true });
    }

    public static List<ManualRegion> Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<ManualRegion>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }
}
