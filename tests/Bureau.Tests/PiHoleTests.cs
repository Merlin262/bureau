using Microsoft.AspNetCore.Http;
using Bureau.Modules;

namespace Bureau.Tests;

/// <summary>Pi-hole v6 simulado por um servidor HTTP local.</summary>
public sealed class FakePiHole : IAsyncDisposable
{
    public FakeHttp Http { get; private set; } = null!;
    public Fx Fx { get; private set; } = null!;

    public static async Task<FakePiHole> StartAsync()
    {
        var p = new FakePiHole { Http = await FakeHttp.StartAsync() };
        p.Fx = new Fx(o => { o.PiHole.Enabled = true; o.PiHole.Url = p.Http.Url + "/"; o.PiHole.Password = "pw\"x"; });
        p.Http
            .Json("POST /api/auth", """{"session":{"valid":true,"sid":"SID1"}}""")
            .Json("GET /api/stats/summary", """{"queries":{"total":100,"blocked":25,"percent_blocked":25.04},"gravity":{"domains_being_blocked":5000},"clients":{"active":7}}""")
            .Json("GET /api/dns/blocking", """{"blocking":"enabled","timer":12.6}""")
            .Json("POST /api/dns/blocking", """{"blocking":"disabled"}""")
            .Json("GET /api/stats/top_domains", """{"domains":[{"domain":"ads.example","count":10}]}""")
            .Json("GET /api/history", """{"history":[{"timestamp":1700000000.0,"total":10,"blocked":2}]}""")
            .Json("GET /api/network/devices", """{"devices":[{"hwaddr":"aa:bb:cc:dd:ee:ff","macVendor":"Acme","lastQuery":1700000000.7,"ips":[{"ip":"192.168.0.5","name":"pc"}]},{"hwaddr":"","ips":[]}]}""")
            .Json("GET /api/queries", """{"queries":[{"time":1700000001.5,"domain":"ads.example","type":"A","status":"GRAVITY","client":{"ip":"192.168.0.5","name":"pc"}},{"time":1700000000,"domain":"ok.example","type":"AAAA","status":"FORWARDED","client":{"ip":"192.168.0.6"}}]}""")
            .Json("GET /api/stats/top_clients", """{"clients":[{"ip":"192.168.0.5","count":10},{"ip":"192.168.0.5","count":5},{"name":"x","count":1}]}""")
            .Json("POST /api/domains/deny/exact", "{}")
            .Json("POST /api/domains/allow/exact", """{"error":"UNIQUE constraint failed"}""", 400)
            .Json("GET /api/groups", """{"groups":[{"name":"Default","id":0}]}""")
            .Json("POST /api/groups", """{"groups":[{"name":"BUREAU-bloqueio","id":7}]}""")
            .Status("PUT /api/domains/deny/regex/.*", 404)
            .Json("POST /api/domains/deny/regex", "{}")
            .Status("PUT /api/clients/AA:BB:CC:DD:EE:FF", 404)
            .Json("POST /api/clients", "{}")
            .Json("PUT /api/clients/11:22:33:44:55:66", "{}")
            .Status("DELETE /api/auth", 204);
        return p;
    }

    public async ValueTask DisposeAsync()
    {
        Fx.Dispose();
        await Http.DisposeAsync();
    }
}

public class PiHoleTests
{
    [Fact]
    public async Task Atualiza_e_publica_o_resumo()
    {
        await using var p = await FakePiHole.StartAsync();
        await p.Fx.PiHole.RefreshAsync(TestContext.Current.CancellationToken);
        await p.Fx.PiHole.RefreshAsync(TestContext.Current.CancellationToken); // top/histórico ficam em cache por 1 min

        var s = p.Fx.Hub.Require("pihole");
        Assert.True(s.GetProperty("online").GetBoolean());
        Assert.True(s.GetProperty("blocking").GetBoolean());
        Assert.Equal(13, s.GetProperty("timer").GetDouble());
        Assert.Equal(25, s.GetProperty("percent").GetDouble());
        Assert.Equal(5000, s.GetProperty("gravity").GetInt64());
        Assert.Equal(7, s.GetProperty("clients").GetInt32());
        Assert.Equal("ads.example", s.GetProperty("topBlocked")[0].GetProperty("name").GetString());
        Assert.Equal(1700000000, s.GetProperty("history")[0].GetProperty("t").GetInt64());

        Assert.Equal(1, p.Http.Count("POST", "/api/auth")); // sessão reaproveitada
        Assert.Equal(1, p.Http.Count("GET", "/api/history"));
        Assert.All(p.Http.Requests.Where(r => r.PathAndQuery != "/api/auth"), r => Assert.Equal("SID1", r.Headers["X-FTL-SID"]));
        Assert.Contains("\"pw\\u0022x\"", p.Http.Requests.First().Body); // senha serializada com escape
    }

    [Fact]
    public async Task Sessao_expirada_reautentica_uma_vez()
    {
        await using var p = await FakePiHole.StartAsync();
        var calls = 0;
        p.Http.Routes["GET /api/stats/summary"] = async ctx =>
        {
            if (calls++ == 0) { ctx.Response.StatusCode = 401; return; }
            await ctx.Response.WriteAsync("""{"queries":{"total":1,"blocked":0,"percent_blocked":0},"gravity":{"domains_being_blocked":1},"clients":{"active":1}}""");
        };
        await p.Fx.PiHole.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.True(p.Fx.Hub.Require("pihole").GetProperty("online").GetBoolean());
        Assert.Equal(2, p.Http.Count("POST", "/api/auth"));
    }

    [Fact]
    public async Task Senha_errada_publica_offline_e_alerta_depois_de_3()
    {
        await using var p = await FakePiHole.StartAsync();
        p.Http.Json("POST /api/auth", """{"session":{"valid":false}}""");
        for (var i = 0; i < 3; i++) await p.Fx.PiHole.RefreshAsync(TestContext.Current.CancellationToken);

        var s = p.Fx.Hub.Require("pihole");
        Assert.False(s.GetProperty("online").GetBoolean());
        Assert.Equal("Senha do Pi-hole inválida", s.GetProperty("error").GetString());
        Assert.Equal("Pi-hole fora do ar", p.Fx.Hub.Require("alerts")[0].GetProperty("title").GetString());
    }

    [Fact]
    public async Task Desativado_publica_nao_configurado()
    {
        using var fx = new Fx();
        await fx.PiHole.RefreshAsync(TestContext.Current.CancellationToken);
        var s = fx.Hub.Require("pihole");
        if (OperatingSystem.IsLinux())
        {
            Assert.False(s.GetProperty("configured").GetBoolean());
            Assert.False(fx.PiHole.Available);
            Assert.False((await fx.PiHole.SetBlockingAsync(true, null)).Ok);
            Assert.Empty(await fx.PiHole.QueriesAsync());
            Assert.Equal("Pi-hole não configurado", (await fx.PiHole.SetDomainAsync("a.com", "deny")).Message);
            Assert.Equal("Pi-hole não configurado", await fx.PiHole.SetClientBlockedAsync("aa:bb:cc:dd:ee:ff", true));
        }
        else Assert.True(s.GetProperty("configured").GetBoolean()); // dados simulados no Windows/macOS
        Assert.Empty(await fx.PiHole.GetDevicesAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await fx.PiHole.ActivityAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Pausar_bloqueio_envia_timer()
    {
        await using var p = await FakePiHole.StartAsync();
        Assert.True((await p.Fx.PiHole.SetBlockingAsync(false, 300)).Ok);
        Assert.True((await p.Fx.PiHole.SetBlockingAsync(true, null)).Ok);
        var bodies = p.Http.Requests.Where(r => r.Method == "POST" && r.PathAndQuery == "/api/dns/blocking").Select(r => r.Body).ToArray();
        Assert.Equal(["""{"blocking":false,"timer":300}""", """{"blocking":true,"timer":null}"""], bodies);

        p.Http.Status("POST /api/dns/blocking", 500);
        Assert.Equal("Pi-hole respondeu 500", (await p.Fx.PiHole.SetBlockingAsync(true, null)).Message);
    }

    [Fact]
    public async Task Servidor_fora_do_ar_retorna_erro()
    {
        using var fx = new Fx(o => { o.PiHole.Enabled = true; o.PiHole.Url = "http://127.0.0.1:1"; });
        Assert.False((await fx.PiHole.SetBlockingAsync(true, null)).Ok);
        Assert.NotNull(await fx.PiHole.SetClientBlockedAsync("aa:bb:cc:dd:ee:ff", true));
        Assert.Empty(await fx.PiHole.GetDevicesAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await fx.PiHole.ActivityAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Aparelhos_e_atividade()
    {
        await using var p = await FakePiHole.StartAsync();
        var devs = await p.Fx.PiHole.GetDevicesAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new PiDevice("aa:bb:cc:dd:ee:ff", "192.168.0.5", "pc", "Acme", 1700000000), devs[0]);
        Assert.Equal(new PiDevice("", null, null, null, 0), devs[1]);
        await p.Fx.PiHole.GetDevicesAsync(TestContext.Current.CancellationToken); // cache
        Assert.Equal(1, p.Http.Count("GET", "/api/network/devices"));

        var act = await p.Fx.PiHole.ActivityAsync(TestContext.Current.CancellationToken);
        Assert.Equal(15, act["192.168.0.5"]);
        Assert.Single(act);
    }

    [Fact]
    public async Task Consultas_ao_vivo_com_e_sem_filtro()
    {
        await using var p = await FakePiHole.StartAsync();
        var all = await p.Fx.PiHole.QueriesAsync(5000);
        Assert.Equal(2, all.Length);
        Assert.True(all[0].Blocked);
        Assert.False(all[1].Blocked);
        Assert.Null(all[1].ClientName);
        Assert.Contains(p.Http.Requests, r => r.PathAndQuery == "/api/queries?length=5000");
        await p.Fx.PiHole.QueriesAsync(50_000);
        Assert.Contains(p.Http.Requests, r => r.PathAndQuery == "/api/queries?length=10000"); // limite

        var one = await p.Fx.PiHole.QueriesAsync(150, "192.168.0.6");
        Assert.Single(one);
        Assert.Contains(p.Http.Requests, r => r.PathAndQuery == "/api/queries?length=150&client_ip=192.168.0.6");
    }

    [Theory]
    [InlineData("GRAVITY", true)]
    [InlineData("REGEX", true)]
    [InlineData("DENYLIST", true)]
    [InlineData("EXTERNAL_BLOCKED_IP", true)]
    [InlineData("SPECIAL_DOMAIN", true)]
    [InlineData("FORWARDED", false)]
    [InlineData("CACHE", false)]
    public void Status_bloqueado(string status, bool blocked) => Assert.Equal(blocked, PiHoleModule.IsBlocked(status));

    [Fact]
    public async Task Bloquear_e_liberar_dominio()
    {
        await using var p = await FakePiHole.StartAsync();
        Assert.Equal("Domínio inválido", (await p.Fx.PiHole.SetDomainAsync("não é domínio", "deny")).Message);
        Assert.Equal("Lista inválida", (await p.Fx.PiHole.SetDomainAsync("a.com", "outra")).Message);

        var ok = await p.Fx.PiHole.SetDomainAsync(" Ads.Example ", "deny");
        Assert.Equal((true, "ads.example bloqueado"), (ok.Ok, ok.Message));
        Assert.Contains("\"domain\":\"ads.example\"", p.Http.Requests.Last().Body);

        var dup = await p.Fx.PiHole.SetDomainAsync("ads.example", "allow");
        Assert.Equal("Domínio já está em uma das listas do Pi-hole", dup.Message);
        p.Http.Status("POST /api/domains/allow/exact", 500);
        Assert.Equal("Pi-hole respondeu 500", (await p.Fx.PiHole.SetDomainAsync("ads.example", "allow")).Message);
    }

    [Fact]
    public async Task Bloqueio_de_aparelho_cria_grupo_regra_e_cliente()
    {
        await using var p = await FakePiHole.StartAsync();
        Assert.Null(await p.Fx.PiHole.SetClientBlockedAsync("aa:bb:cc:dd:ee:ff", true));
        Assert.Contains(p.Http.Requests, r => r.Method == "POST" && r.PathAndQuery == "/api/clients" && r.Body.Contains("\"groups\":[0,7]"));
        Assert.Contains(p.Http.Requests, r => r.Method == "POST" && r.PathAndQuery == "/api/domains/deny/regex" && r.Body.Contains("\"groups\":[7]"));

        // Grupo em cache; cliente já existente é atualizado com PUT.
        Assert.Null(await p.Fx.PiHole.SetClientBlockedAsync("11:22:33:44:55:66", false));
        Assert.Equal(1, p.Http.Count("GET", "/api/groups"));
        Assert.Contains(p.Http.Requests, r => r.Method == "PUT" && r.PathAndQuery == "/api/clients/11:22:33:44:55:66" && r.Body.Contains("\"groups\":[0]"));

        p.Http.Status("POST /api/clients", 500);
        Assert.Equal("Pi-hole respondeu 500", await p.Fx.PiHole.SetClientBlockedAsync("aa:bb:cc:dd:ee:ff", false));
    }

    [Fact]
    public async Task Grupo_existente_e_reaproveitado_e_grupo_impossivel_falha()
    {
        await using var p = await FakePiHole.StartAsync();
        p.Http.Json("GET /api/groups", """{"groups":[{"name":"BUREAU-bloqueio","id":3}]}""");
        p.Http.Json("PUT /api/domains/deny/regex/.*", "{}");
        Assert.Null(await p.Fx.PiHole.SetClientBlockedAsync("11:22:33:44:55:66", true));
        Assert.Equal(0, p.Http.Count("POST", "/api/groups"));

        await using var q = await FakePiHole.StartAsync();
        q.Http.Json("POST /api/groups", """{"groups":[]}""");
        Assert.Equal("Não foi possível criar o grupo de bloqueio no Pi-hole", await q.Fx.PiHole.SetClientBlockedAsync("11:22:33:44:55:66", true));
    }

    [Fact]
    public async Task Dispose_encerra_a_sessao()
    {
        await using var p = await FakePiHole.StartAsync();
        await p.Fx.PiHole.RefreshAsync(TestContext.Current.CancellationToken);
        p.Fx.PiHole.Dispose();
        Assert.Equal(1, p.Http.Count("DELETE", "/api/auth"));
    }
}
