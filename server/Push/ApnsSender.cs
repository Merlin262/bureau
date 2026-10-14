using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bureau.Infra;
using Microsoft.Extensions.Options;

namespace Bureau.Push;

/// <summary>
/// Apple Push Notification service (HTTP/2 + JWT ES256) — push nativo do app no iPhone.
/// Requer conta paga de desenvolvedor Apple: chave .p8 (Keys → Apple Push Notifications service).
/// Tokens de builds do Xcode valem no ambiente "sandbox"; TestFlight/App Store, em "production".
/// Se o ambiente configurado recusar o token, tenta o outro automaticamente.
/// </summary>
public sealed class ApnsSender(IOptionsMonitor<BureauOptions> opts, ILogger<ApnsSender> log)
{
    const string Production = "https://api.push.apple.com";
    const string Sandbox = "https://api.sandbox.push.apple.com";

    readonly HttpClient _http = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(30) })
    {
        DefaultRequestVersion = HttpVersion.Version20,
        DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        Timeout = TimeSpan.FromSeconds(15),
    };
    readonly Lock _lock = new();
    string? _keyPath;
    ECParameters? _key;
    string? _jwt;
    DateTime _jwtAt;

    ApnsOptions O => opts.CurrentValue.Push.Apns;

    public bool Configured => !string.IsNullOrWhiteSpace(O.KeyId) && !string.IsNullOrWhiteSpace(O.TeamId) && LoadKey() is not null;

    public async Task<PushResult> SendAsync(PushDevice d, AlertEvent a, CancellationToken ct = default)
    {
        if (d.Token is null || !Configured) return PushResult.Failed;
        var body = Payload(a);
        string[] hosts = O.Sandbox ? [Sandbox, Production] : [Production, Sandbox];
        var badToken = 0;

        foreach (var host in hosts)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{host}/3/device/{d.Token}")
            {
                Content = new ByteArrayContent(body),
            };
            req.Headers.TryAddWithoutValidation("authorization", $"bearer {Jwt()}");
            req.Headers.TryAddWithoutValidation("apns-topic", O.BundleId);
            req.Headers.TryAddWithoutValidation("apns-push-type", "alert");
            req.Headers.TryAddWithoutValidation("apns-priority", a.Level == "ok" ? "5" : "10");

            using var res = await _http.SendAsync(req, ct);
            if (res.IsSuccessStatusCode) return PushResult.Ok;

            var reason = await res.Content.ReadAsStringAsync(ct);
            if (res.StatusCode == HttpStatusCode.Gone || reason.Contains("Unregistered")) return PushResult.Gone;
            if (reason.Contains("ExpiredProviderToken")) lock (_lock) _jwt = null;
            if (reason.Contains("BadDeviceToken")) { badToken++; continue; } // token de outro ambiente
            log.LogWarning("APNs {Status}: {Reason}", (int)res.StatusCode, reason);
            return PushResult.Failed;
        }
        return badToken == hosts.Length ? PushResult.Gone : PushResult.Failed;
    }

    static byte[] Payload(AlertEvent a)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteStartObject("aps");
            w.WriteStartObject("alert");
            w.WriteString("title", a.Title);
            w.WriteString("body", a.Body);
            w.WriteEndObject();
            w.WriteString("sound", "default");
            w.WriteString("thread-id", a.Kind);
            w.WriteEndObject();
            w.WriteString("view", a.View);
            w.WriteString("kind", a.Kind);
            w.WriteString("level", a.Level);
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    /// <summary>JWT do provedor: a Apple aceita por até 1 h e pede para não renovar mais que a cada 20 min.</summary>
    string Jwt()
    {
        lock (_lock)
        {
            if (_jwt is not null && DateTime.UtcNow - _jwtAt < TimeSpan.FromMinutes(40)) return _jwt;
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var header = B64Url.Encode(Encoding.UTF8.GetBytes($"{{\"alg\":\"ES256\",\"kid\":{Str(O.KeyId!)}}}"));
            var claims = B64Url.Encode(Encoding.UTF8.GetBytes($"{{\"iss\":{Str(O.TeamId!)},\"iat\":{now}}}"));
            using var ec = ECDsa.Create(LoadKey()!.Value);
            var sig = ec.SignData(Encoding.ASCII.GetBytes($"{header}.{claims}"), HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            _jwt = $"{header}.{claims}.{B64Url.Encode(sig)}";
            _jwtAt = DateTime.UtcNow;
            return _jwt;
        }
    }

    ECParameters? LoadKey()
    {
        var path = O.KeyFile;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        lock (_lock)
        {
            if (path == _keyPath) return _key;
            try
            {
                using var ec = ECDsa.Create();
                ec.ImportFromPem(File.ReadAllText(path));
                _key = ec.ExportParameters(includePrivateParameters: true);
                _keyPath = path;
                _jwt = null;
            }
            catch (Exception e)
            {
                log.LogWarning(e, "Chave APNs inválida: {Path}", path);
                _key = null;
            }
            return _key;
        }
    }

    static string Str(string s) => JsonSerializer.Serialize(s, AppJson.Default.String);
}
