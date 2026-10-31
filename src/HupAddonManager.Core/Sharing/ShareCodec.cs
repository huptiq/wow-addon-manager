using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HupAddonManager.Core.Storage;

namespace HupAddonManager.Core;

public sealed class ShareEntry
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("source")] public string Provider { get; set; } = "";
    [JsonPropertyName("id")] public string ProjectId { get; set; } = "";
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("beta")] public bool Prerelease { get; set; }
}

public sealed class ShareFile
{
    [JsonPropertyName("app")] public string App { get; set; } = AppInfo.Name;
    [JsonPropertyName("format")] public int Format { get; set; } = 1;
    [JsonPropertyName("flavor")] public string? Flavor { get; set; }
    [JsonPropertyName("created")] public DateTimeOffset Created { get; set; } = DateTimeOffset.Now;
    [JsonPropertyName("addons")] public List<ShareEntry> Addons { get; set; } = new();
}

public static class ShareCodec
{
    public const string Prefix = "HUP1:";
    public const string FileExtension = ".hupaddons";

    public static string ToJson(ShareFile file) => JsonSerializer.Serialize(file, JsonStore.Options);

    public static string ToCode(ShareFile file)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(file);
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.SmallestSize, leaveOpen: true)) gz.Write(json);
        return Prefix + Convert.ToBase64String(ms.ToArray());
    }

    public static List<ShareEntry> Parse(string text)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) return new();

        if (text.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var bytes = Convert.FromBase64String(new string(text[Prefix.Length..].Where(c => !char.IsWhiteSpace(c)).ToArray()));
                using var gz = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
                return Clean(JsonSerializer.Deserialize<ShareFile>(gz)?.Addons);
            }
            catch (Exception ex) when (ex is FormatException or InvalidDataException or JsonException)
            {
                throw new FormatException("The share code is damaged or incomplete. Copy the whole code (it starts with HUP1:).");
            }
        }

        if (text.StartsWith('{'))
            return Clean(JsonSerializer.Deserialize<ShareFile>(text, JsonStore.Options)?.Addons);
        if (text.StartsWith('['))
            return Clean(JsonSerializer.Deserialize<List<ShareEntry>>(text, JsonStore.Options));

        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !l.StartsWith('#'))
            .Select(l => new ShareEntry { Name = l, Url = l })
            .ToList();
    }

    private static List<ShareEntry> Clean(List<ShareEntry>? list) =>
        (list ?? new()).Where(e => !string.IsNullOrWhiteSpace(e.Url) || (!string.IsNullOrWhiteSpace(e.Provider) && !string.IsNullOrWhiteSpace(e.ProjectId))).ToList();
}
