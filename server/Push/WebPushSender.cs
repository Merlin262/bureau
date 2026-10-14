using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bureau.Infra;
using Microsoft.Extensions.Options;

namespace Bureau.Push;

public enum PushResult { Ok, Gone, Failed }

/// <summary>
/// Web Push padrão (RFC 8030/8291/8292): funciona no Chrome/Android e no Safari/iOS 16.4+ (PWA instalado)
/// sem nenhuma conta — as chaves VAPID são geradas no primeiro uso e ficam no DataDir.
/// </summary>
public sealed class WebPushSender
{
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    readonly IOptionsMonitor<BureauOptions> _opts;
    readonly ILogger<WebPushSender> _log;
    readonly ECParameters _vapid;

    public string PublicKey { get; }

    public WebPushSender(IOptionsMonitor<BureauOptions> opts, ILogger<WebPushSender> log)
    {
        _opts = opts;
        _log = log;
        var path = Path.Combine(opts.CurrentValue.DataDir, "vapid.json");
        var saved = JsonFile.Load(path, AppJson.Default.StringArray);
        if (saved is [var pub, var priv])
        {
            var q = B64Url.Decode(pub);
            _vapid = new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                D = B64Url.Decode(priv),
                Q = new ECPoint { X = q[1..33], Y = q[33..65] },
            };
        }
        else
        {
            using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            _vapid = ec.ExportParameters(includePrivateParameters: true);
            JsonFile.Save(path, [B64Url.Encode(Raw(_vapid.Q)), B64Url.Encode(_vapid.D)], AppJson.Default.StringArray);
        }
        PublicKey = B64Url.Encode(Raw(_vapid.Q));
    }

    public async Task<PushResult> SendAsync(PushDevice d, AlertEvent alert, CancellationToken ct = default)
    {
        if (d.Endpoint is null || d.P256dh is null || d.Auth is null) return PushResult.Gone;

        var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(alert, AppJson.Default.AlertEvent));
        var body = Encrypt(payload, B64Url.Decode(d.P256dh), B64Url.Decode(d.Auth));
        var endpoint = new Uri(d.Endpoint);

        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new ByteArrayContent(body) };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        req.Content.Headers.ContentEncoding.Add("aes128gcm");
        req.Headers.TryAddWithoutValidation("Authorization", $"vapid t={VapidJwt(endpoint.GetLeftPart(UriPartial.Authority))}, k={PublicKey}");
        req.Headers.TryAddWithoutValidation("TTL", "86400");
        req.Headers.TryAddWithoutValidation("Urgency", alert.Level == "crit" ? "high" : "normal");

        using var res = await _http.SendAsync(req, ct);
        if (res.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) return PushResult.Gone;
        if (res.IsSuccessStatusCode) return PushResult.Ok;
        _log.LogWarning("Web Push {Status}: {Body}", (int)res.StatusCode, await res.Content.ReadAsStringAsync(ct));
        return PushResult.Failed;
    }

    string VapidJwt(string audience)
    {
        var exp = DateTimeOffset.UtcNow.AddHours(12).ToUnixTimeSeconds();
        var header = B64Url.Encode("""{"typ":"JWT","alg":"ES256"}"""u8);
        var claims = B64Url.Encode(Encoding.UTF8.GetBytes(
            $"{{\"aud\":{Str(audience)},\"exp\":{exp},\"sub\":{Str(_opts.CurrentValue.Push.WebPushSubject)}}}"));
        using var ec = ECDsa.Create(_vapid);
        var sig = ec.SignData(Encoding.ASCII.GetBytes($"{header}.{claims}"), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return $"{header}.{claims}.{B64Url.Encode(sig)}";
    }

    /// <summary>Criptografia "aes128gcm" do RFC 8291 (um único registro).</summary>
    public static byte[] Encrypt(byte[] plaintext, byte[] uaPublic, byte[] authSecret,
        ECDiffieHellman? serverKey = null, byte[]? salt = null)
    {
        using var ua = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = uaPublic[1..33], Y = uaPublic[33..65] },
        });
        var asKey = serverKey ?? ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        try
        {
            var asPublic = Raw(asKey.ExportParameters(false).Q);
            var shared = asKey.DeriveRawSecretAgreement(ua.PublicKey);

            var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, authSecret,
                [.. "WebPush: info\0"u8, .. uaPublic, .. asPublic]);
            salt ??= RandomNumberGenerator.GetBytes(16);
            var cek = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 16, salt, "Content-Encoding: aes128gcm\0"u8.ToArray());
            var nonce = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 12, salt, "Content-Encoding: nonce\0"u8.ToArray());

            var plain = new byte[plaintext.Length + 1];
            plaintext.CopyTo(plain, 0);
            plain[^1] = 2; // delimitador do último registro

            var output = new byte[16 + 4 + 1 + asPublic.Length + plain.Length + 16];
            salt.CopyTo(output, 0);
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(16), 4096);
            output[20] = (byte)asPublic.Length;
            asPublic.CopyTo(output, 21);
            var at = 21 + asPublic.Length;

            using var gcm = new AesGcm(cek, 16);
            gcm.Encrypt(nonce, plain, output.AsSpan(at, plain.Length), output.AsSpan(at + plain.Length, 16));
            return output;
        }
        finally
        {
            if (serverKey is null) asKey.Dispose();
        }
    }

    static byte[] Raw(ECPoint q) => [0x04, .. q.X!, .. q.Y!];

    static string Str(string s) => JsonSerializer.Serialize(s, AppJson.Default.String);
}
