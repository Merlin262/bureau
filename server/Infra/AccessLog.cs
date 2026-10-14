using Microsoft.Extensions.Options;

namespace Bureau.Infra;

/// <summary>
/// Registro de acessos: logins (web e app) com IP real, país (via Cloudflare), aparelho e resultado.
/// Guarda os 200 últimos em DataDir/access.json e avisa em logins de IPs nunca vistos e em rajadas de falhas.
/// </summary>
public sealed class AccessLog
{
    const int Keep = 200;
    readonly string _path;
    readonly Lock _lock = new();
    readonly List<AccessEntry> _items;

    public AccessLog(IOptions<BureauOptions> o)
    {
        _path = Path.Combine(o.Value.DataDir, "access.json");
        _items = [.. JsonFile.Load(_path, AppJson.Default.AccessEntryArray) ?? []];
    }

    public AccessEntry[] Recent { get { lock (_lock) return [.. _items.AsEnumerable().Reverse()]; } }

    /// <summary>Registra a tentativa. Retorna se o IP já tinha um login bem-sucedido antes (para o alerta de "novo acesso").</summary>
    public (bool KnownIp, int RecentFailures) Record(HttpContext ctx, string method, bool ok, string? device = null)
    {
        var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "?";
        if (ip.StartsWith("::ffff:")) ip = ip[7..];
        var ua = ctx.Request.Headers.UserAgent.ToString();
        var entry = new AccessEntry(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ip,
            ctx.Request.Headers["CF-IPCountry"].ToString() is { Length: 2 } c ? c : null,
            method, ok, device ?? Describe(ua));

        lock (_lock)
        {
            var known = _items.Any(e => e.Ok && e.Ip == ip);
            var since = entry.Ts - 600;
            var failures = _items.Count(e => !e.Ok && e.Ts >= since) + (ok ? 0 : 1);
            _items.Add(entry);
            if (_items.Count > Keep) _items.RemoveRange(0, _items.Count - Keep);
            try { JsonFile.Save(_path, _items.ToArray(), AppJson.Default.AccessEntryArray); } catch { /* segue */ }
            return (known, failures);
        }
    }

    public static bool IsLocal(string ip) =>
        ip is "127.0.0.1" or "::1" || ip.StartsWith("192.168.") || ip.StartsWith("10.") || ip.StartsWith("100.") ||
        (ip.StartsWith("172.") && int.TryParse(ip.Split('.')[1], out var b) && b is >= 16 and <= 31) || ip.StartsWith("fe80:") || ip.StartsWith("fd");

    static string Describe(string ua)
    {
        if (ua.Length == 0) return "Desconhecido";
        var browser = ua.Contains("Edg/") ? "Edge" : ua.Contains("Firefox/") ? "Firefox" : ua.Contains("Chrome/") ? "Chrome"
            : ua.Contains("Safari/") ? "Safari" : ua.Contains("curl/") ? "curl" : "Navegador";
        var os = ua.Contains("Android") ? "Android" : ua.Contains("iPhone") || ua.Contains("iPad") ? "iOS" : ua.Contains("Windows") ? "Windows"
            : ua.Contains("Mac OS") ? "macOS" : ua.Contains("Linux") ? "Linux" : "";
        return os.Length > 0 ? $"{browser} · {os}" : browser;
    }
}
