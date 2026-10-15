using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bureau.Infra;
using Microsoft.Extensions.Options;

namespace Bureau.Push;

/// <summary>
/// Firebase Cloud Messaging (API HTTP v1) — push nativo do app Android.
/// Autentica com a conta de serviço do Firebase (JWT RS256 → token OAuth2, renovado a cada hora).
/// </summary>
public sealed class FcmSender(IOptionsMonitor<BureauOptions> opts, ILogger<FcmSender> log)
{
    sealed record Account(string ProjectId, string Email, string Key, string TokenUri);

    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    readonly SemaphoreSlim _lock = new(1, 1);
    Account? _account;
    string? _accountPath;
    string? _accessToken;
    DateTime _accessTokenExp;

    public bool Configured => LoadAccount() is not null;

    /// <summary>Endereço da API (substituído nos testes).</summary>
    internal string BaseUrl { get; set; } = "https://fcm.googleapis.com";

    public async Task<PushResult> SendAsync(PushDevice d, AlertEvent a, CancellationToken ct = default)
    {
        if (LoadAccount() is not { } acc || d.Token is null) return PushResult.Failed;
        var token = await AccessTokenAsync(acc, ct);

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v1/projects/{acc.ProjectId}/messages:send")
        {
            Content = new StringContent(Message(d.Token, a), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new("Bearer", token);

        using var res = await _http.SendAsync(req, ct);
        if (res.IsSuccessStatusCode) return PushResult.Ok;

        var body = await res.Content.ReadAsStringAsync(ct);
        if (res.StatusCode == HttpStatusCode.NotFound || body.Contains("UNREGISTERED")) return PushResult.Gone;
        if (res.StatusCode == HttpStatusCode.Unauthorized) _accessToken = null;
        log.LogWarning("FCM {Status}: {Body}", (int)res.StatusCode, body);
        return PushResult.Failed;
    }

    static string Message(string token, AlertEvent a)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteStartObject("message");
            w.WriteString("token", token);
            w.WriteStartObject("notification");
            w.WriteString("title", a.Title);
            w.WriteString("body", a.Body);
            w.WriteEndObject();
            w.WriteStartObject("data");
            w.WriteString("view", a.View);
            w.WriteString("kind", a.Kind);
            w.WriteString("level", a.Level);
            w.WriteEndObject();
            w.WriteStartObject("android");
            w.WriteString("priority", "high");
            w.WriteStartObject("notification");
            w.WriteString("channel_id", "alerts");
            w.WriteString("icon", "ic_stat_bureau");
            w.WriteString("color", a.Level == "ok" ? "#ECEBE6" : "#FF2B2B");
            w.WriteString("tag", a.Kind);
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    async Task<string> AccessTokenAsync(Account acc, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_accessToken is not null && DateTime.UtcNow < _accessTokenExp) return _accessToken;

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var header = B64Url.Encode("""{"alg":"RS256","typ":"JWT"}"""u8);
            var claims = B64Url.Encode(Encoding.UTF8.GetBytes(
                $"{{\"iss\":{Str(acc.Email)},\"scope\":\"https://www.googleapis.com/auth/firebase.messaging\"," +
                $"\"aud\":{Str(acc.TokenUri)},\"iat\":{now},\"exp\":{now + 3600}}}"));
            using var rsa = RSA.Create();
            rsa.ImportFromPem(acc.Key);
            var sig = rsa.SignData(Encoding.ASCII.GetBytes($"{header}.{claims}"), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

            using var res = await _http.PostAsync(acc.TokenUri, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                ["assertion"] = $"{header}.{claims}.{B64Url.Encode(sig)}",
            }), ct);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsByteArrayAsync(ct));
            if (!doc.RootElement.TryGetProperty("access_token", out var tok))
                throw new InvalidOperationException("Firebase recusou a conta de serviço: " + doc.RootElement);
            _accessToken = tok.GetString();
            _accessTokenExp = DateTime.UtcNow.AddMinutes(50);
            return _accessToken!;
        }
        finally { _lock.Release(); }
    }

    Account? LoadAccount()
    {
        var path = opts.CurrentValue.Push.FcmServiceAccount;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        if (path == _accountPath) return _account;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            var r = doc.RootElement;
            _account = new Account(r.GetProperty("project_id").GetString()!, r.GetProperty("client_email").GetString()!,
                r.GetProperty("private_key").GetString()!,
                r.TryGetProperty("token_uri", out var u) ? u.GetString()! : "https://oauth2.googleapis.com/token");
            _accountPath = path;
            _accessToken = null;
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Conta de serviço do Firebase inválida: {Path}", path);
            _account = null;
        }
        return _account;
    }

    static string Str(string s) => JsonSerializer.Serialize(s, AppJson.Default.String);
}
