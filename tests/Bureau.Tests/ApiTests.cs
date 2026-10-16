using Microsoft.AspNetCore.Hosting;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Bureau.Infra;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Bureau.Tests;

/// <summary>
/// O BUREAU inteiro em memória (TestServer), com um DataDir temporário e uma configuração própria.
/// Os coletores em segundo plano ficam desligados para os testes serem determinísticos.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public const string Password = "senha-de-teste";
    public TempDir Dir { get; } = new();
    readonly string? _oldConfig;

    public ApiFactory()
    {
        var config = Dir.File("bureau.json");
        File.WriteAllText(config, JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["DataDir"] = Dir.File("data"),
            ["PasswordHash"] = PasswordHasher.Hash(Password),
            ["AllowPower"] = false,
            ["SystemUpdates"] = false,
            ["Services"] = new[] { "cron" },
            ["Docker"] = new Dictionary<string, object> { ["Enabled"] = false },
            ["Terminal"] = new Dictionary<string, object> { ["Enabled"] = true, ["User"] = "bureau-teste" },
        }, JsonOptions));
        _oldConfig = Environment.GetEnvironmentVariable("BUREAU_CONFIG");
        Environment.SetEnvironmentVariable("BUREAU_CONFIG", config);

        // Interface mínima ao lado do binário de teste (o Program usa esse wwwroot quando existe).
        var www = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        Directory.CreateDirectory(Path.Combine(www, "assets"));
        if (!File.Exists(Path.Combine(www, "index.html"))) File.WriteAllText(Path.Combine(www, "index.html"), "<!doctype html><title>BUREAU</title>");
        File.WriteAllText(Path.Combine(www, "assets", "teste.js"), "console.log(1)");
        File.WriteAllBytes(Path.Combine(www, "assets", "teste.js.br"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(www, "assets", "teste.js.gz"), [4, 5]);
    }

    static readonly JsonSerializerOptions JsonOptions = new() { TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver() };

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureTestServices(s =>
        {
            foreach (var d in s.Where(d => d.ServiceType == typeof(IHostedService)).ToArray()) s.Remove(d);
        });
    }

    public HttpClient Anon()
    {
        var c = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        c.DefaultRequestHeaders.Add("X-Bureau", "1");
        return c;
    }

    public async Task<HttpClient> LoggedIn()
    {
        var c = Anon();
        var r = await c.PostAsJsonAsync("/api/auth/login", new { password = Password });
        r.EnsureSuccessStatusCode();
        return c;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Environment.SetEnvironmentVariable("BUREAU_CONFIG", _oldConfig);
        Dir.Dispose();
    }
}

[Collection("env")]
public class ApiTests : IClassFixture<ApiFixture>
{
    readonly ApiFixture _f;
    public ApiTests(ApiFixture f) => _f = f;

    ApiFactory App => _f.App;
    static int PiHoleDohTotal => Bureau.Modules.PiHoleModule.DohDomains.Length;
    HttpClient C => _f.Client;
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode} {await r.Content.ReadAsStringAsync()}");
        return JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    async Task<JsonElement> Get(string url) => await Json(await C.GetAsync(url, Ct));
    async Task<JsonElement> Send(HttpMethod m, string url, object? body = null)
    {
        using var req = new HttpRequestMessage(m, url) { Content = body is null ? null : JsonContent.Create(body) };
        return await Json(await C.SendAsync(req, Ct));
    }

    // ── Autenticação ──────────────────────────────────────
    [Fact]
    public async Task Sem_login_a_api_responde_401()
    {
        using var anon = App.Anon();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/services/available", Ct)).StatusCode);
        var me = await Json(await anon.GetAsync("/api/auth/me", Ct));
        Assert.False(me.GetProperty("authenticated").GetBoolean());
        Assert.True(!me.TryGetProperty("hostname", out var host) || host.ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task Senha_errada_e_negada_e_registrada()
    {
        using var anon = App.Anon();
        var r = await anon.PostAsJsonAsync("/api/auth/login", new { password = "errada" }, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Equal("ACESSO NEGADO", (await r.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("message").GetString());

        var t = await anon.PostAsJsonAsync("/api/auth/token", new { password = "errada", name = "x" }, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, t.StatusCode);

        var log = await Get("/api/auth/access");
        Assert.Contains(log.EnumerateArray(), e => !e.GetProperty("ok").GetBoolean() && e.GetProperty("method").GetString() == "app");
    }

    [Fact]
    public async Task Login_por_cookie_me_e_logout()
    {
        using var c = await App.LoggedIn();
        var me = await Json(await c.GetAsync("/api/auth/me", Ct));
        Assert.True(me.GetProperty("authenticated").GetBoolean());
        Assert.Equal(Environment.MachineName, me.GetProperty("hostname").GetString());
        Assert.False(me.GetProperty("allowPower").GetBoolean());
        Assert.True(me.GetProperty("terminal").GetBoolean());

        await Json(await c.PostAsync("/api/auth/logout", null, Ct));
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/monitors", Ct)).StatusCode);
    }

    [Fact]
    public async Task Token_do_app_autentica_e_pode_ser_revogado()
    {
        using var anon = App.Anon();
        var t = await Json(await anon.PostAsJsonAsync("/api/auth/token", new { password = ApiFactory.Password, name = "Pixel" }, Ct));
        var token = t.GetProperty("token").GetString()!;
        var id = t.GetProperty("id").GetString()!;

        using var app = App.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        app.DefaultRequestHeaders.Add("X-Bureau", "1");
        app.DefaultRequestHeaders.Authorization = new("Bearer", token);
        var list = await Json(await app.GetAsync("/api/auth/tokens", Ct));
        Assert.Contains(list.EnumerateArray(), d => d.GetProperty("id").GetString() == id && d.GetProperty("current").GetBoolean());

        // Push registrado pelo app sai junto quando o aparelho é revogado.
        await Json(await app.PostAsJsonAsync("/api/push/register", new { kind = "fcm", token = "fcm-token", name = "" }, Ct));
        var cfg = await Json(await app.GetAsync("/api/push/config", Ct));
        Assert.Equal(id, cfg.GetProperty("deviceId").GetString());

        var del = await Json(await app.DeleteAsync("/api/auth/tokens/self", Ct));
        Assert.True(del.GetProperty("ok").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.GetAsync("/api/auth/tokens", Ct)).StatusCode);
        Assert.DoesNotContain((await Get("/api/push/devices")).EnumerateArray(), d => d.GetProperty("name").GetString() == "Android");
        Assert.False((await Send(HttpMethod.Delete, "/api/auth/tokens/nao-existe")).GetProperty("ok").GetBoolean());

        app.DefaultRequestHeaders.Authorization = new("Bearer", "invalido");
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.GetAsync("/api/auth/tokens", Ct)).StatusCode);
    }

    // ── Segurança ─────────────────────────────────────────
    [Fact]
    public async Task Cabecalhos_de_seguranca_e_csrf()
    {
        var r = await C.GetAsync("/api/auth/me", Ct);
        Assert.Equal("nosniff", r.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", r.Headers.GetValues("X-Frame-Options").Single());
        Assert.Contains("frame-ancestors 'none'", r.Headers.GetValues("Content-Security-Policy").Single());

        using var noHeader = App.CreateClient();
        var post = await noHeader.PostAsJsonAsync("/api/auth/login", new { password = ApiFactory.Password }, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode); // sem X-Bureau: possível CSRF
    }

    [Fact]
    public async Task Cors_so_para_o_app()
    {
        using var pre = new HttpRequestMessage(HttpMethod.Options, "/api/auth/token");
        pre.Headers.Add("Origin", "capacitor://localhost");
        var r = await C.SendAsync(pre, Ct);
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.Equal("capacitor://localhost", r.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Contains("X-Bureau", r.Headers.GetValues("Access-Control-Allow-Headers").Single());

        using var get = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        get.Headers.Add("Origin", "https://localhost");
        Assert.Equal("https://localhost", (await C.SendAsync(get, Ct)).Headers.GetValues("Access-Control-Allow-Origin").Single());

        using var evil = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        evil.Headers.Add("Origin", "https://site-malicioso.com");
        Assert.False((await C.SendAsync(evil, Ct)).Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Rota_desconhecida_da_api_e_404_e_o_resto_e_a_interface()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await C.GetAsync("/api/nao-existe", Ct)).StatusCode);

        var page = await C.GetAsync("/qualquer/rota", Ct);
        Assert.Equal("text/html", page.Content.Headers.ContentType!.MediaType);
        Assert.Equal("no-cache", page.Headers.CacheControl!.ToString());

        var asset = await C.GetAsync("/assets/teste.js", Ct);
        Assert.Equal("console.log(1)", await asset.Content.ReadAsStringAsync(Ct));
        Assert.True(asset.Headers.CacheControl!.Public);

        using var br = new HttpRequestMessage(HttpMethod.Get, "/assets/teste.js");
        br.Headers.Add("Accept-Encoding", "br, gzip");
        var rb = await C.SendAsync(br, Ct);
        Assert.Equal("br", rb.Content.Headers.ContentEncoding.Single());
        using var gz = new HttpRequestMessage(HttpMethod.Get, "/assets/teste.js");
        gz.Headers.Add("Accept-Encoding", "gzip");
        Assert.Equal("gzip", (await C.SendAsync(gz, Ct)).Content.Headers.ContentEncoding.Single());

        // Fora do wwwroot ou inexistente: nunca serve arquivo (e "arquivos" não caem na SPA).
        Assert.Equal(HttpStatusCode.NotFound, (await C.GetAsync("/..%2fbureau.json", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await C.GetAsync("/nao-existe.js", Ct)).StatusCode);
    }

    // ── Rotas ─────────────────────────────────────────────
    [Fact]
    public async Task Servicos()
    {
        Assert.Equal(JsonValueKind.Array, (await Get("/api/services/available")).ValueKind);
        Assert.True((await Send(HttpMethod.Put, "/api/services/watch", new { units = new[] { "cron", "ssh" } })).GetProperty("ok").GetBoolean());
        var bad = await Send(HttpMethod.Post, "/api/services/nao-monitorado.service/restart");
        Assert.Equal("Serviço não monitorado", bad.GetProperty("message").GetString());
        Assert.Equal("Serviço não monitorado.", (await Get("/api/services/nao-monitorado.service/logs")).GetProperty("text").GetString());
    }

    [Fact]
    public async Task Conteineres()
    {
        Assert.Equal("ID inválido", (await Send(HttpMethod.Post, "/api/containers/$$$/restart")).GetProperty("message").GetString());
        Assert.Equal("ID inválido.", (await Get("/api/containers/$$$/logs")).GetProperty("text").GetString());
        Assert.Equal("Ação inválida", (await Send(HttpMethod.Post, "/api/containers/abc/kill")).GetProperty("message").GetString());
    }

    [Fact]
    public async Task PiHole_e_bloqueios()
    {
        await Send(HttpMethod.Post, "/api/pihole/blocking", new { enabled = true, timer = (int?)null });
        Assert.Equal(JsonValueKind.Array, (await Get("/api/pihole/queries")).ValueKind);
        Assert.Equal(JsonValueKind.Array, (await Get("/api/pihole/queries?client=192.168.0.10")).ValueKind);
        Assert.Equal(JsonValueKind.Array, (await Get("/api/pihole/queries?client=lixo")).ValueKind);
        Assert.Equal(JsonValueKind.Array, (await Get("/api/pihole/queries?client=192.168.0.10&hours=48")).ValueKind);
        Assert.Equal(PiHoleDohTotal, (await Get("/api/pihole/doh")).GetProperty("total").GetInt32());
        Assert.True((await Send(HttpMethod.Put, "/api/pihole/doh", new { enabled = false })).TryGetProperty("ok", out _));
        Assert.Equal("Domínio inválido", (await Send(HttpMethod.Post, "/api/pihole/domain", new { domain = "x", list = "deny" })).GetProperty("message").GetString());

        Assert.True((await Send(HttpMethod.Put, "/api/network/block", new { mac = "aa:bb:cc:dd:ee:ff", manual = true, days = Array.Empty<int>() })).GetProperty("ok").GetBoolean());
        Assert.True((await Send(HttpMethod.Delete, "/api/network/block/aa:bb:cc:dd:ee:ff")).GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Rede_e_wake_on_lan()
    {
        Assert.False((await Send(HttpMethod.Put, "/api/network/alias", new { mac = "x", name = "y" })).GetProperty("ok").GetBoolean());
        Assert.True((await Send(HttpMethod.Put, "/api/network/alias", new { mac = "aa:bb:cc:dd:ee:ff", name = "TV" })).GetProperty("ok").GetBoolean());

        Assert.True((await Send(HttpMethod.Post, "/api/network/wol", new { mac = "AA-BB-CC-DD-EE-01", name = "PC" })).GetProperty("ok").GetBoolean());
        var state = await Get("/api/network/wol");
        Assert.Equal("aa:bb:cc:dd:ee:01", state.GetProperty("targets")[0].GetProperty("mac").GetString());
        Assert.True((await Send(HttpMethod.Put, "/api/network/wol/aa:bb:cc:dd:ee:01", new { name = "PC", user = "joao" })).GetProperty("ok").GetBoolean());
        Assert.Equal("Aparelho desligado ou fora da rede",
            (await Send(HttpMethod.Post, "/api/network/wol/aa:bb:cc:dd:ee:01/shutdown")).GetProperty("message").GetString());
        Assert.True((await Send(HttpMethod.Delete, "/api/network/wol/aa:bb:cc:dd:ee:01")).GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Pacotes_backups_atualizacoes_e_integracoes()
    {
        Assert.Equal(JsonValueKind.String, (await Get("/api/apt/logs")).GetProperty("text").ValueKind);
        Assert.True((await Send(HttpMethod.Post, "/api/apt/check")).GetProperty("ok").GetBoolean());
        Assert.Equal(JsonValueKind.Object, (await Get("/api/update")).ValueKind);
        Assert.False((await Send(HttpMethod.Post, "/api/update/run")).GetProperty("ok").GetBoolean());
        Assert.Equal(JsonValueKind.String, (await Get("/api/backups/x.service/logs")).GetProperty("text").ValueKind);
        Assert.Equal("Entidade inválida", (await Send(HttpMethod.Post, "/api/homeassistant/toggle", new { entityId = "!!" })).GetProperty("message").GetString());
    }

    [Fact]
    public async Task Upgrade_e_backup_sob_demanda()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "No Windows/macOS são simulados");
        Assert.False((await Send(HttpMethod.Post, "/api/backups/x.service/run")).GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Monitores_e_metricas()
    {
        Assert.True((await Send(HttpMethod.Put, "/api/monitors", new[] { new { id = "", name = "Local", target = "127.0.0.1:1" } })).GetProperty("ok").GetBoolean());
        Assert.Equal("Local", (await Get("/api/monitors"))[0].GetProperty("name").GetString());
        foreach (var range in new[] { "1h", "24h", "90d" })
            Assert.Equal(range, (await Get($"/api/metrics?range={range}")).GetProperty("range").GetString());
        Assert.Equal("24h", (await Get("/api/metrics")).GetProperty("range").GetString());
    }

    [Fact]
    public async Task Push()
    {
        var cfg = await Get("/api/push/config");
        Assert.False(string.IsNullOrEmpty(cfg.GetProperty("webPushKey").GetString()));
        Assert.False((await Send(HttpMethod.Post, "/api/push/register", new { kind = "webpush", endpoint = "http://inseguro" })).GetProperty("ok").GetBoolean());
        Assert.False((await Send(HttpMethod.Post, "/api/push/register", new { kind = "apns", token = "xyz" })).GetProperty("ok").GetBoolean());
        Assert.False((await Send(HttpMethod.Post, "/api/push/register", new { kind = "sms" })).GetProperty("ok").GetBoolean());

        var reg = await Send(HttpMethod.Post, "/api/push/register", new { kind = "apns", token = new string('a', 64), name = "  Meu iPhone  " });
        var id = reg.GetProperty("message").GetString()!;
        var web = await Send(HttpMethod.Post, "/api/push/register", new { kind = "webpush", endpoint = "https://push.example/x", p256dh = "k", auth = "a" });
        Assert.True(web.GetProperty("ok").GetBoolean());
        Assert.Contains((await Get("/api/push/devices")).EnumerateArray(), d => d.GetProperty("name").GetString() == "Meu iPhone");

        Assert.True((await Send(HttpMethod.Delete, $"/api/push/{id}")).GetProperty("ok").GetBoolean());
        Assert.True((await Send(HttpMethod.Delete, $"/api/push/{web.GetProperty("message").GetString()}")).GetProperty("ok").GetBoolean());
        Assert.False((await Send(HttpMethod.Post, "/api/push/test")).GetProperty("ok").GetBoolean()); // ninguém inscrito
        Assert.True((await Send(HttpMethod.Post, "/api/dev/simulate/disk")).GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Energia_desativada_na_configuracao()
    {
        Assert.Equal("Desativado na configuração", (await Send(HttpMethod.Post, "/api/power/reboot")).GetProperty("message").GetString());
    }

    [Fact]
    public async Task Backup_exporta_e_importa()
    {
        await Send(HttpMethod.Put, "/api/network/alias", new { mac = "aa:bb:cc:dd:ee:02", name = "Backup" });
        var r = await C.GetAsync("/api/backup/export", Ct);
        Assert.Contains("bureau-backup-", r.Content.Headers.ContentDisposition?.ToString() ?? r.Headers.GetValues("Content-Disposition").Single());
        var bundle = await Json(r);
        Assert.Equal("bureau-backup", bundle.GetProperty("kind").GetString());
        Assert.Contains("Backup", bundle.GetProperty("files").GetProperty("aliases.json").GetString());

        var bad = await Send(HttpMethod.Post, "/api/backup/import", new { kind = "outro", version = 1, created = 0, host = "", appVersion = "", files = new Dictionary<string, string>() });
        Assert.False(bad.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Stream_envia_o_estado_inicial()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/stream");
        using var r = await C.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.Equal("text/event-stream", r.Content.Headers.ContentType!.MediaType);
        using var reader = new StreamReader(await r.Content.ReadAsStreamAsync(cts.Token));
        Assert.Equal("retry: 1500", await reader.ReadLineAsync(cts.Token));
        string? line;
        while ((line = await reader.ReadLineAsync(cts.Token)) is not null && !line.StartsWith("event: ")) { }
        Assert.StartsWith("event: ", line);
    }

    [Fact]
    public async Task Terminal_exige_ticket_de_uso_unico()
    {
        var ticket = (await Send(HttpMethod.Post, "/api/terminal/ticket")).GetProperty("message").GetString()!;
        Assert.Equal(HttpStatusCode.Forbidden, (await C.GetAsync($"/api/terminal/ws?ticket={ticket}", Ct)).StatusCode); // não é WebSocket

        var ws = App.Server.CreateWebSocketClient();
        await Assert.ThrowsAnyAsync<Exception>(() => ws.ConnectAsync(new Uri(App.Server.BaseAddress, "api/terminal/ws?ticket=falso"), Ct));

        ticket = (await Send(HttpMethod.Post, "/api/terminal/ticket")).GetProperty("message").GetString()!;
        using var socket = await ws.ConnectAsync(new Uri(App.Server.BaseAddress, $"api/terminal/ws?ticket={ticket}"), Ct);
        var buf = new byte[4096];
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        var first = await socket.ReceiveAsync(buf, cts.Token);
        Assert.True(first.Count > 0 || first.MessageType == WebSocketMessageType.Close);

        if (!OperatingSystem.IsLinux())
        {
            Assert.Contains("terminal simulado", Encoding.UTF8.GetString(buf, 0, first.Count));
            await socket.SendAsync("""{"cols":100,"rows":30}"""u8.ToArray(), WebSocketMessageType.Text, true, Ct);
            await socket.SendAsync("ls\x7fs\r"u8.ToArray(), WebSocketMessageType.Binary, true, Ct);
            var echo = new StringBuilder();
            while (!echo.ToString().Contains("(simulado) ls"))
            {
                var r = await socket.ReceiveAsync(buf, cts.Token);
                echo.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
            }
            await socket.SendAsync("lixo"u8.ToArray(), WebSocketMessageType.Text, true, Ct); // mensagem de controle inválida: ignorada
        }
        if (socket.State == WebSocketState.Open)
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "fim", Ct);
    }
}

/// <summary>Uma instância do app (e um login) para todos os testes de API.</summary>
public sealed class ApiFixture : IAsyncLifetime
{
    public ApiFactory App { get; } = new();
    public HttpClient Client { get; private set; } = null!;

    public async ValueTask InitializeAsync() => Client = await App.LoggedIn();

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await App.DisposeAsync();
    }
}
