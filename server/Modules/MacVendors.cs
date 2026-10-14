using System.IO.Compression;
using System.Globalization;

namespace Bureau.Modules;

/// <summary>
/// Fabricante a partir do prefixo do MAC (registro público do IEEE, embutido no binário — sem internet).
/// Carregado sob demanda; para atualizar a base: node scripts/update-oui.mjs
/// </summary>
public static class MacVendors
{
    static readonly Lazy<(uint[] Keys, string[] Names)> Db = new(Load);

    public static string? Lookup(string mac)
    {
        if (IsRandom(mac) || !TryPrefix(mac, out var prefix)) return null;
        var (keys, names) = Db.Value;
        var i = Array.BinarySearch(keys, prefix);
        return i >= 0 ? names[i] : null;
    }

    /// <summary>MAC "localmente administrado" (bit 0x02 do 1º byte): endereço aleatório de celulares/tablets.</summary>
    public static bool IsRandom(string mac) =>
        mac.Length >= 2 && byte.TryParse(mac.AsSpan(0, 2), NumberStyles.HexNumber, null, out var b) && (b & 0x02) != 0;

    static bool TryPrefix(string mac, out uint prefix)
    {
        prefix = 0;
        var hex = mac.Replace(":", "").Replace("-", "");
        return hex.Length >= 6 && uint.TryParse(hex.AsSpan(0, 6), NumberStyles.HexNumber, null, out prefix);
    }

    static (uint[], string[]) Load()
    {
        var keys = new List<uint>(41_000);
        var names = new List<string>(41_000);
        try
        {
            using var res = typeof(MacVendors).Assembly.GetManifestResourceStream("oui.tsv.gz");
            if (res is null) return ([], []);
            using var reader = new StreamReader(new GZipStream(res, CompressionMode.Decompress));
            while (reader.ReadLine() is { } line)
            {
                var tab = line.IndexOf('\t');
                if (tab != 6 || !uint.TryParse(line.AsSpan(0, 6), NumberStyles.HexNumber, null, out var k)) continue;
                keys.Add(k);
                names.Add(line[(tab + 1)..]);
            }
        }
        catch { /* sem base: só não mostra fabricante */ }
        return ([.. keys], [.. names]); // o arquivo já vem ordenado
    }
}
