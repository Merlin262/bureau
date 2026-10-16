using Bureau.Modules;

namespace Bureau.Tests;

public class DohTests
{
    static string DenyList(IEnumerable<string> domains) =>
        $$"""{"domains":[{{string.Join(",", domains.Select(d => $$"""{"domain":"{{d}}","comment":"x"}"""))}}]}""";

    [Fact]
    public async Task Estado_bloquear_e_liberar_os_provedores_de_dns_seguro()
    {
        await using var p = await FakePiHole.StartAsync();
        var all = PiHoleModule.DohDomains;
        p.Http.Json("GET /api/domains/deny/exact", DenyList(["dns.google", "cloudflare-dns.com", "outro.exemplo"]));
        p.Http.Json("POST /api/domains/deny/exact", "{}");
        foreach (var d in all) p.Http.Status($"DELETE /api/domains/deny/exact/{d}", 204);

        var s = await p.Fx.PiHole.DohStateAsync();
        Assert.Equal((true, false, 2, all.Length), (s.Available, s.Enabled, s.Blocked, s.Total));
        Assert.Equal(all, s.Domains);

        var on = await p.Fx.PiHole.SetDohAsync(true);
        Assert.True(on.Ok);
        Assert.Equal(all.Length - 2, p.Http.Count("POST", "/api/domains/deny/exact")); // só os que faltavam
        Assert.Contains(p.Http.Requests, r => r.Method == "POST" && r.Body.Contains("\"comment\":\"BUREAU: DNS seguro\""));

        p.Http.Json("GET /api/domains/deny/exact", DenyList(all));
        Assert.True((await p.Fx.PiHole.DohStateAsync()).Enabled);
        Assert.True((await p.Fx.PiHole.SetDohAsync(false)).Ok);
        Assert.Equal(all.Length, p.Http.Count("DELETE", "/api/domains/deny/exact/"));
    }

    [Fact]
    public async Task Pihole_recusando_ou_fora_do_ar()
    {
        await using var p = await FakePiHole.StartAsync();
        p.Http.Json("GET /api/domains/deny/exact", DenyList([]));
        p.Http.Status("POST /api/domains/deny/exact", 400);
        Assert.Equal($"O Pi-hole recusou {PiHoleModule.DohDomains.Length} domínio(s)", (await p.Fx.PiHole.SetDohAsync(true)).Message);

        p.Http.Status("GET /api/domains/deny/exact", 500);
        Assert.False((await p.Fx.PiHole.DohStateAsync()).Available);
        Assert.False((await p.Fx.PiHole.SetDohAsync(true)).Ok);
    }

    [Fact]
    public async Task Sem_pihole_configurado()
    {
        using var fx = new Fx();
        var s = await fx.PiHole.DohStateAsync();
        if (OperatingSystem.IsLinux())
        {
            Assert.False(s.Available);
            Assert.Equal("Pi-hole não configurado", (await fx.PiHole.SetDohAsync(true)).Message);
        }
        else
        {
            // Windows/macOS: simulado em memória para a interface.
            Assert.True((await fx.PiHole.SetDohAsync(true)).Ok);
            Assert.True((await fx.PiHole.DohStateAsync()).Enabled);
        }
    }

    [Theory]
    [InlineData("dns.google", true)]
    [InlineData("DNS.Google.", true)]
    [InlineData("chrome.cloudflare-dns.com", true)]
    [InlineData("www.google.com", false)]
    public void Identifica_provedores(string domain, bool doh) => Assert.Equal(doh, PiHoleModule.IsDoh(domain));

    [Fact]
    public async Task Historico_de_24h_pede_a_partir_de_um_horario()
    {
        await using var p = await FakePiHole.StartAsync();
        var from = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 86400;
        await p.Fx.PiHole.QueriesAsync(10_000, "192.168.0.5", from);
        Assert.Contains(p.Http.Requests, r => r.PathAndQuery == $"/api/queries?length=10000&client_ip=192.168.0.5&from={from}");
    }
}
