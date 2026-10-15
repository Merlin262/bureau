using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bureau.Infra;
using Bureau.Push;
using Microsoft.AspNetCore.Http;

namespace Bureau.Tests;

public class WebPushTests
{
    /// <summary>Lado do navegador (RFC 8291): decifra o corpo "aes128gcm" com a chave privada do aparelho.</summary>
    static byte[] Decrypt(byte[] body, ECDiffieHellman ua, byte[] auth)
    {
        var salt = body[..16];
        var rs = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(16));
        Assert.Equal(4096u, rs);
        var keyLen = body[20];
        var asPublic = body[21..(21 + keyLen)];
        using var server = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = asPublic[1..33], Y = asPublic[33..65] },
        });
        var p = ua.ExportParameters(false).Q;
        byte[] uaPublic = [0x04, .. p.X!, .. p.Y!];
        var shared = ua.DeriveRawSecretAgreement(server.PublicKey);
        var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, auth, [.. "WebPush: info\0"u8, .. uaPublic, .. asPublic]);
        var cek = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 16, salt, "Content-Encoding: aes128gcm\0"u8.ToArray());
        var nonce = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 12, salt, "Content-Encoding: nonce\0"u8.ToArray());

        var cipher = body[(21 + keyLen)..];
        var plain = new byte[cipher.Length - 16];
        using var gcm = new AesGcm(cek, 16);
        gcm.Decrypt(nonce, cipher[..^16], cipher[^16..], plain);
        Assert.Equal(2, plain[^1]); // delimitador do último registro
        return plain[..^1];
    }

    static (ECDiffieHellman Ua, byte[] Public, byte[] Auth) Browser()
    {
        var ua = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var q = ua.ExportParameters(false).Q;
        return (ua, [0x04, .. q.X!, .. q.Y!], RandomNumberGenerator.GetBytes(16));
    }

    [Fact]
    public void Criptografia_rfc8291_ida_e_volta()
    {
        var (ua, pub, auth) = Browser();
        using var _ = ua;
        var body = WebPushSender.Encrypt("olá, servidor"u8.ToArray(), pub, auth);
        Assert.Equal("olá, servidor", Encoding.UTF8.GetString(Decrypt(body, ua, auth)));
    }

    [Fact]
    public void Vetor_de_teste_do_rfc8291()
    {
        // RFC 8291, apêndice A.
        static ECParameters Key(string priv, string pub)
        {
            var q = B64Url.Decode(pub);
            return new ECParameters { Curve = ECCurve.NamedCurves.nistP256, D = B64Url.Decode(priv), Q = new ECPoint { X = q[1..33], Y = q[33..65] } };
        }
        using var asKey = ECDiffieHellman.Create(Key("yfWPiYE-n46HLnH0KqZOF1fJJU3MYrct3AELtAQ-oRw",
            "BP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8"));
        var body = WebPushSender.Encrypt("When I grow up, I want to be a watermelon"u8.ToArray(),
            B64Url.Decode("BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4"),
            B64Url.Decode("BTBZMqHH6r4Tts7J_aSIgg"), asKey, B64Url.Decode("DGv6ra1nlYgDCS1FRnbzlw"));
        Assert.Equal("DGv6ra1nlYgDCS1FRnbzlwAAEABBBP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A_yl95bQpu6cVPTpK4Mqgkf1CXztLVBSt2Ks3oZwbuwXPXLWyouBWLVWGNWQexSgSxsj_Qulcy4a-fN",
            B64Url.Encode(body));
    }

    [Fact]
    public async Task Envio_com_vapid_e_tratamento_das_respostas()
    {
        await using var http = await FakeHttp.StartAsync();
        http.Status("POST /push/ok", 201).Status("POST /push/gone", 410).Status("POST /push/old", 404).Status("POST /push/bad", 400);
        using var fx = new Fx(o => o.Push.WebPushSubject = "mailto:eu@exemplo.com");
        var (ua, pub, auth) = Browser();
        using var _ = ua;
        PushDevice Dev(string path) => new("id", "webpush", "n", null, http.Url + path, B64Url.Encode(pub), B64Url.Encode(auth), null, 0);
        var alert = new AlertEvent(1, "power", "crit", "Queda de energia", "Na bateria", "painel");

        Assert.Equal(PushResult.Ok, await fx.Web.SendAsync(Dev("/push/ok"), alert, TestContext.Current.CancellationToken));
        Assert.Equal(PushResult.Gone, await fx.Web.SendAsync(Dev("/push/gone"), alert, TestContext.Current.CancellationToken));
        Assert.Equal(PushResult.Gone, await fx.Web.SendAsync(Dev("/push/old"), alert, TestContext.Current.CancellationToken));
        Assert.Equal(PushResult.Failed, await fx.Web.SendAsync(Dev("/push/bad"), alert, TestContext.Current.CancellationToken));
        Assert.Equal(PushResult.Gone, await fx.Web.SendAsync(Dev("/x") with { P256dh = null }, alert, TestContext.Current.CancellationToken));

        var req = http.Requests.First();
        Assert.Equal("high", req.Headers["Urgency"]);
        Assert.Equal("aes128gcm", req.Headers["Content-Encoding"]);
        var vapid = req.Headers["Authorization"];
        Assert.EndsWith($", k={fx.Web.PublicKey}", vapid);

        // JWT assinado com a chave VAPID, para a origem do serviço de push.
        var jwt = vapid["vapid t=".Length..vapid.IndexOf(',')].Split('.');
        using var claims = JsonDocument.Parse(B64Url.Decode(jwt[1]));
        Assert.Equal(http.Url, claims.RootElement.GetProperty("aud").GetString());
        Assert.Equal("mailto:eu@exemplo.com", claims.RootElement.GetProperty("sub").GetString());
        var q = B64Url.Decode(fx.Web.PublicKey);
        using var ec = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = q[1..33], Y = q[33..65] } });
        Assert.True(ec.VerifyData(Encoding.ASCII.GetBytes($"{jwt[0]}.{jwt[1]}"), B64Url.Decode(jwt[2]), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

        // As chaves VAPID são persistidas (as inscrições dos navegadores dependem delas).
        Assert.Equal(fx.Web.PublicKey, new WebPushSender(fx.Monitor, Fx.Log<WebPushSender>()).PublicKey);
    }
}

public class PushStoreTests
{
    [Fact]
    public void Mesmo_token_ou_endpoint_substitui_em_vez_de_duplicar()
    {
        using var fx = new Fx();
        var a = fx.Push.Upsert(new("1", "fcm", "Pixel", "tok", null, null, null, "dev1", 0));
        var b = fx.Push.Upsert(new("2", "fcm", "Pixel novo", "tok", null, null, null, "dev1", 1));
        Assert.Equal(a.Id, b.Id);
        fx.Push.Upsert(new("3", "webpush", "Chrome", null, "https://push/x", "k", "a", null, 0));
        fx.Push.Upsert(new("4", "webpush", "Chrome", null, "https://push/x", "k2", "a", null, 0));
        Assert.Equal(2, fx.Push.All.Length);
        Assert.Equal("Pixel novo", fx.Push.All.Single(d => d.Id == "1").Name);

        Assert.Equal(2, new PushStore(fx.Opt).All.Length);
        Assert.True(fx.Push.Remove("3"));
        Assert.False(fx.Push.Remove("3"));
        Assert.Equal(1, fx.Push.RemoveWhere(d => d.Owner == "dev1"));
        Assert.Equal(0, fx.Push.RemoveWhere(d => true));
    }
}

public class FcmTests
{
    static string Account(TempDir dir, string tokenUri)
    {
        using var rsa = RSA.Create(2048);
        var json = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["project_id"] = "proj",
            ["client_email"] = "bureau@proj.iam.gserviceaccount.com",
            ["private_key"] = rsa.ExportPkcs8PrivateKeyPem(),
            ["token_uri"] = tokenUri,
        }, AppJson.Default.DictionaryStringString);
        var path = dir.File("firebase.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public async Task Envia_pela_api_v1_com_token_oauth()
    {
        await using var http = await FakeHttp.StartAsync();
        http.Json("POST /token", """{"access_token":"AT1","expires_in":3600}""");
        http.Json("POST /v1/projects/proj/messages:send", """{"name":"x"}""");
        using var fx = new Fx();
        fx.Options.Push.FcmServiceAccount = Account(fx.Dir, http.Url + "/token");
        fx.Fcm.BaseUrl = http.Url;
        Assert.True(fx.Fcm.Configured);

        var dev = new PushDevice("1", "fcm", "Pixel", "TOKEN", null, null, null, null, 0);
        var alert = new AlertEvent(1, "disk", "ok", "Disco", "ok", "sistema");
        Assert.Equal(PushResult.Ok, await fx.Fcm.SendAsync(dev, alert, TestContext.Current.CancellationToken));
        Assert.Equal(PushResult.Ok, await fx.Fcm.SendAsync(dev, alert, TestContext.Current.CancellationToken));
        Assert.Equal(1, http.Count("POST", "/token")); // token reaproveitado

        var send = http.Requests.First(r => r.PathAndQuery.StartsWith("/v1/"));
        Assert.Equal("Bearer AT1", send.Headers["Authorization"]);
        using var msg = JsonDocument.Parse(send.Body);
        var m = msg.RootElement.GetProperty("message");
        Assert.Equal("TOKEN", m.GetProperty("token").GetString());
        Assert.Equal("sistema", m.GetProperty("data").GetProperty("view").GetString());
        Assert.Equal("#ECEBE6", m.GetProperty("android").GetProperty("notification").GetProperty("color").GetString());

        var assertion = http.Requests.First(r => r.PathAndQuery == "/token").Body;
        Assert.Contains("grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Ajwt-bearer", assertion);

        // Erros do FCM.
        http.Status("POST /v1/projects/proj/messages:send", 404);
        Assert.Equal(PushResult.Gone, await fx.Fcm.SendAsync(dev, alert, TestContext.Current.CancellationToken));
        http.Json("POST /v1/projects/proj/messages:send", """{"error":{"status":"UNREGISTERED"}}""", 400);
        Assert.Equal(PushResult.Gone, await fx.Fcm.SendAsync(dev, alert, TestContext.Current.CancellationToken));
        http.Status("POST /v1/projects/proj/messages:send", 401);
        Assert.Equal(PushResult.Failed, await fx.Fcm.SendAsync(dev, alert, TestContext.Current.CancellationToken));
        Assert.Equal(PushResult.Failed, await fx.Fcm.SendAsync(dev, alert, TestContext.Current.CancellationToken));
        Assert.Equal(2, http.Count("POST", "/token")); // 401 descarta o token
        Assert.Equal(PushResult.Failed, await fx.Fcm.SendAsync(dev with { Token = null }, alert, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Conta_recusada_ou_invalida()
    {
        await using var http = await FakeHttp.StartAsync();
        http.Json("POST /token", """{"error":"invalid_grant"}""", 400);
        using var fx = new Fx();
        fx.Options.Push.FcmServiceAccount = Account(fx.Dir, http.Url + "/token");
        var dev = new PushDevice("1", "fcm", "Pixel", "TOKEN", null, null, null, null, 0);
        var alert = new AlertEvent(1, "disk", "crit", "Disco", "cheio", "sistema");
        await Assert.ThrowsAsync<InvalidOperationException>(() => fx.Fcm.SendAsync(dev, alert, TestContext.Current.CancellationToken));

        File.WriteAllText(fx.Dir.File("ruim.json"), "{}");
        fx.Options.Push.FcmServiceAccount = fx.Dir.File("ruim.json");
        Assert.False(fx.Fcm.Configured);
        fx.Options.Push.FcmServiceAccount = fx.Dir.File("nao-existe.json");
        Assert.False(fx.Fcm.Configured);
        Assert.Equal(PushResult.Failed, await fx.Fcm.SendAsync(dev, alert, TestContext.Current.CancellationToken));
    }
}

public class ApnsTests
{
    static async Task<(Fx Fx, FakeHttp Http)> Setup(bool sandbox = true)
    {
        var http = await FakeHttp.StartAsync(http2: true);
        var fx = new Fx();
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        File.WriteAllText(fx.Dir.File("AuthKey.p8"), ec.ExportPkcs8PrivateKeyPem());
        fx.Options.Push.Apns = new ApnsOptions { KeyFile = fx.Dir.File("AuthKey.p8"), KeyId = "KEY123", TeamId = "TEAM42", BundleId = "dev.bureau.app", Sandbox = sandbox };
        fx.Apns.SandboxUrl = http.Url + "/sb";
        fx.Apns.ProductionUrl = http.Url + "/prod";
        return (fx, http);
    }

    static readonly PushDevice Dev = new("1", "apns", "iPhone", "abcdef", null, null, null, null, 0);
    static readonly AlertEvent Alert = new(1, "service", "ok", "nginx voltou", "ativo", "servicos");

    [Fact]
    public async Task Envia_por_http2_com_jwt_es256()
    {
        var (fx, http) = await Setup();
        await using var _ = http;
        using var __ = fx;
        http.Json("POST /sb/3/device/abcdef", "{}");
        Assert.True(fx.Apns.Configured);
        Assert.Equal(PushResult.Ok, await fx.Apns.SendAsync(Dev, Alert, TestContext.Current.CancellationToken));
        Assert.Equal(PushResult.Ok, await fx.Apns.SendAsync(Dev, Alert, TestContext.Current.CancellationToken));

        var req = http.Requests.First();
        Assert.Equal("dev.bureau.app", req.Headers["apns-topic"]);
        Assert.Equal("5", req.Headers["apns-priority"]);
        var jwt = req.Headers["authorization"]["bearer ".Length..].Split('.');
        Assert.Equal(jwt[0], http.Requests.Last().Headers["authorization"]["bearer ".Length..].Split('.')[0]); // JWT reaproveitado
        using var header = JsonDocument.Parse(B64Url.Decode(jwt[0]));
        Assert.Equal("KEY123", header.RootElement.GetProperty("kid").GetString());
        using var claims = JsonDocument.Parse(B64Url.Decode(jwt[1]));
        Assert.Equal("TEAM42", claims.RootElement.GetProperty("iss").GetString());
        using var body = JsonDocument.Parse(req.Body);
        Assert.Equal("nginx voltou", body.RootElement.GetProperty("aps").GetProperty("alert").GetProperty("title").GetString());
    }

    [Fact]
    public async Task Token_de_outro_ambiente_tenta_o_segundo()
    {
        var (fx, http) = await Setup(sandbox: false);
        await using var _ = http;
        using var __ = fx;
        http.Json("POST /prod/3/device/abcdef", """{"reason":"BadDeviceToken"}""", 400);
        http.Json("POST /sb/3/device/abcdef", "{}");
        Assert.Equal(PushResult.Ok, await fx.Apns.SendAsync(Dev, Alert, TestContext.Current.CancellationToken));

        http.Json("POST /sb/3/device/abcdef", """{"reason":"BadDeviceToken"}""", 400);
        Assert.Equal(PushResult.Gone, await fx.Apns.SendAsync(Dev, Alert, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Respostas_de_erro()
    {
        var (fx, http) = await Setup();
        await using var _ = http;
        using var __ = fx;
        http.Status("POST /sb/3/device/abcdef", 410);
        Assert.Equal(PushResult.Gone, await fx.Apns.SendAsync(Dev, Alert, TestContext.Current.CancellationToken));
        http.Json("POST /sb/3/device/abcdef", """{"reason":"Unregistered"}""", 400);
        Assert.Equal(PushResult.Gone, await fx.Apns.SendAsync(Dev, Alert, TestContext.Current.CancellationToken));
        http.Json("POST /sb/3/device/abcdef", """{"reason":"ExpiredProviderToken"}""", 403);
        Assert.Equal(PushResult.Failed, await fx.Apns.SendAsync(Dev, Alert, TestContext.Current.CancellationToken));
        http.Json("POST /sb/3/device/abcdef", """{"reason":"TooManyRequests"}""", 429);
        Assert.Equal(PushResult.Failed, await fx.Apns.SendAsync(Dev, Alert, TestContext.Current.CancellationToken));
        Assert.Equal(PushResult.Failed, await fx.Apns.SendAsync(Dev with { Token = null }, Alert, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Sem_chave_valida_nao_esta_configurado()
    {
        var (fx, http) = await Setup();
        await using var _ = http;
        using var __ = fx;
        File.WriteAllText(fx.Dir.File("ruim.p8"), "não é uma chave");
        fx.Options.Push.Apns.KeyFile = fx.Dir.File("ruim.p8");
        Assert.False(fx.Apns.Configured);
        fx.Options.Push.Apns.KeyFile = null;
        Assert.False(fx.Apns.Configured);
        Assert.Equal(PushResult.Failed, await fx.Apns.SendAsync(Dev, Alert, TestContext.Current.CancellationToken));
    }
}

public class NotifierTests
{
    [Fact]
    public async Task Entrega_em_todos_os_canais_e_remove_inscricoes_expiradas()
    {
        await using var http = await FakeHttp.StartAsync();
        http.Status("POST /push/ok", 201).Status("POST /push/gone", 410).Json("POST /", "{}");
        using var fx = new Fx(o => { o.Push.NtfyUrl = "PLACEHOLDER"; o.Push.NtfyToken = "tk"; });
        fx.Options.Push.NtfyUrl = http.Url + "/meu-topico/";

        var ua = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var q = ua.ExportParameters(false).Q;
        var pub = B64Url.Encode([0x04, .. q.X!, .. q.Y!]);
        var auth = B64Url.Encode(RandomNumberGenerator.GetBytes(16));
        fx.Push.Upsert(new("w1", "webpush", "Chrome", null, http.Url + "/push/ok", pub, auth, null, 0));
        fx.Push.Upsert(new("w2", "webpush", "Velho", null, http.Url + "/push/gone", pub, auth, null, 0));
        fx.Push.Upsert(new("w3", "webpush", "Quebrado", null, http.Url + "/push/x", "AAAA", auth, null, 0)); // chave inválida → exceção tratada
        fx.Push.Upsert(new("f1", "fcm", "Android", "tok", null, null, null, null, 0));   // FCM não configurado
        fx.Push.Upsert(new("a1", "apns", "iPhone", "abc", null, null, null, null, 0));  // APNs não configurado
        fx.Push.Upsert(new("x1", "outro", "?", "t2", null, null, null, null, 0));        // tipo desconhecido → removido

        var alert = new AlertEvent(1, "power", "warn", "Queda de energia", "Na bateria", "painel");
        Assert.True(fx.Notifier.NtfyConfigured);
        Assert.Equal(2, await fx.Notifier.SendAsync(alert)); // web push + ntfy
        Assert.Equal(["a1", "f1", "w1", "w3"], fx.Push.All.Select(d => d.Id).Order());

        var ntfy = http.Requests.Single(r => r.PathAndQuery == "/");
        Assert.Equal("Bearer tk", ntfy.Headers["Authorization"]);
        using var doc = JsonDocument.Parse(ntfy.Body);
        Assert.Equal("meu-topico", doc.RootElement.GetProperty("topic").GetString());
        Assert.Equal("Queda de energia", doc.RootElement.GetProperty("title").GetString());
        Assert.Equal(4, doc.RootElement.GetProperty("priority").GetInt32());

        // Teste para um aparelho só: sem ntfy.
        Assert.Equal(1, await fx.Notifier.SendAsync(alert, "w1"));
        Assert.Single(http.Requests, r => r.PathAndQuery == "/");
    }

    [Fact]
    public async Task Ntfy_com_erro_ou_fora_do_ar_nao_conta()
    {
        await using var http = await FakeHttp.StartAsync();
        http.Status("POST /", 500);
        using var fx = new Fx(o => o.Push.NtfyUrl = http.Url + "/t");
        var alert = new AlertEvent(1, "x", "ok", "t", "b", "v");
        Assert.Equal(0, await fx.Notifier.SendAsync(alert));

        fx.Options.Push.NtfyUrl = "http://127.0.0.1:1/t";
        Assert.Equal(0, await fx.Notifier.SendAsync(alert));
        fx.Options.Push.NtfyUrl = null;
        Assert.False(fx.Notifier.NtfyConfigured);
    }
}
