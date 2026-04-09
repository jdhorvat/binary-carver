using System.IO;
using System.Text.Json;

namespace BinaryCarver.Models;

/// <summary>User-defined magic-byte signature for carving.</summary>
public class CustomSignature
{
    public string   FileType      { get; set; } = "";
    public string   Description   { get; set; } = "";
    public string   MagicHex      { get; set; } = "";      // e.g. "89504E47" for PNG
    public int      SearchOffset  { get; set; }             // 0 = match at region start
    public bool     IsTextBased   { get; set; }             // true = match as ASCII prefix
    public string   TextPrefix    { get; set; } = "";       // only used if IsTextBased
    public string   DefaultExt    { get; set; } = ".bin";   // suggested extraction extension

    /// <summary>Parse MagicHex string into byte array.</summary>
    public byte[] GetMagicBytes()
    {
        if (string.IsNullOrWhiteSpace(MagicHex)) return [];
        string hex = MagicHex.Replace(" ", "").Replace("-", "");
        byte[] bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return bytes;
    }

    /// <summary>Validate this signature has minimum required fields.</summary>
    public bool IsValid => !string.IsNullOrWhiteSpace(FileType) &&
        (IsTextBased ? !string.IsNullOrWhiteSpace(TextPrefix) : MagicHex.Replace(" ", "").Length >= 2);
}

/// <summary>Manages loading/saving custom signatures to a JSON file next to the app.</summary>
public static class CustomSignatureStore
{
    private static readonly string StorePath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "custom_signatures.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static List<CustomSignature> Load()
    {
        try
        {
            if (!File.Exists(StorePath)) return [];
            string json = File.ReadAllText(StorePath);
            return JsonSerializer.Deserialize<List<CustomSignature>>(json, JsonOpts) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public static void Save(List<CustomSignature> sigs)
    {
        try
        {
            string json = JsonSerializer.Serialize(sigs, JsonOpts);
            File.WriteAllText(StorePath, json);
        }
        catch
        {
            // silent fail — non-critical
        }
    }
}
