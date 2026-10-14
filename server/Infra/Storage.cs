using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Bureau.Infra;

/// <summary>Persistência simples em arquivos JSON no DataDir (escrita atômica).</summary>
public static class JsonFile
{
    public static T? Load<T>(string path, JsonTypeInfo<T> type)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path), type) : default; }
        catch { return default; }
    }

    public static void Save<T>(string path, T value, JsonTypeInfo<T> type)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, type));
        File.Move(tmp, path, overwrite: true);
    }
}

public static class B64Url
{
    public static string Encode(ReadOnlySpan<byte> data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Decode(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}
