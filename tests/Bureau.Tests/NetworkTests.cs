using System.Net;
using System.Net.Sockets;
using Bureau.Modules;

namespace Bureau.Tests;

public class MacVendorsTests
{
    [Theory]
    [InlineData("00:15:5D:C4:A6:8F", "Microsoft")]
    [InlineData("b8-27-eb-00-00-01", "Raspberry")]
    public void Encontra_o_fabricante_na_base_do_IEEE(string mac, string vendorPart) =>
        Assert.Contains(vendorPart, MacVendors.Lookup(mac), StringComparison.OrdinalIgnoreCase);

    [Theory]
    [InlineData("02:11:22:33:44:55")] // aleatório
    [InlineData("zz:zz")]             // inválido
    [InlineData("FC:FF:FF:00:00:00")] // prefixo não registrado
    public void Sem_fabricante(string mac) => Assert.Null(MacVendors.Lookup(mac));

    [Theory]
    [InlineData("02:00:00:00:00:00", true)]
    [InlineData("DA:A1:19:00:00:00", true)]
    [InlineData("00:15:5D:00:00:00", false)]
    [InlineData("x", false)]
    [InlineData("zz:00", false)]
    public void Mac_aleatorio(string mac, bool random) => Assert.Equal(random, MacVendors.IsRandom(mac));
}

public class NetworkModuleTests
{
    static DeviceInfo D(string ip, string mac, bool online = true, string? name = null) => new(ip, mac, name, null, online, "eth0");

    [Fact]
    public void Pacote_magico()
    {
        var p = NetworkModule.MagicPacket("30-56-0F-86-A7-DD")!;
        Assert.Equal(102, p.Length);
        Assert.All(p[..6], b => Assert.Equal(0xFF, b));
        for (var i = 0; i < 16; i++) Assert.Equal(new byte[] { 0x30, 0x56, 0x0F, 0x86, 0xA7, 0xDD }, p[(6 + i * 6)..(12 + i * 6)]);

        Assert.Null(NetworkModule.MagicPacket(null));
        Assert.Null(NetworkModule.MagicPacket("30:56:0F"));
        Assert.Null(NetworkModule.MagicPacket("zz:56:0F:86:A7:DD"));
    }

    [Fact]
    public async Task Wake_envia_para_o_broadcast()
    {
        Assert.False(await NetworkModule.WakeAsync("inválido"));
        Assert.True(await NetworkModule.WakeAsync("30:56:0F:86:A7:DD"));
    }

    [Fact]
    public void Filtro_de_sub_rede()
    {
        Assert.Equal(0xC0A80001u, NetworkModule.IpKey("192.168.0.1"));
        Assert.Equal(uint.MaxValue, NetworkModule.IpKey("::1"));
        Assert.Equal(uint.MaxValue, NetworkModule.IpKey("lixo"));

        List<(uint, uint)> lan = [(0xC0A86400, 0xFFFFFF00)]; // 192.168.100.0/24
        Assert.True(NetworkModule.InLan("192.168.100.172", lan));
        Assert.False(NetworkModule.InLan("172.17.0.2", lan)); // contêiner do docker
        Assert.False(NetworkModule.InLan("fe80::1", lan));
        Assert.True(NetworkModule.InLan("172.17.0.2", [])); // sem informação: não filtra
        Assert.NotNull(NetworkModule.LanNetworks());
    }

    [Fact]
    public void Apelidos_fabricante_e_aparelho_novo()
    {
        using var fx = new Fx();
        var net = fx.Network;
        net.Publish([D("192.168.0.1", "00:15:5d:00:00:01", name: "_gateway")]); // 1ª vez: só aprende
        Assert.Empty(fx.Hub.Require("alerts").EnumerateArray());

        net.Publish([D("192.168.0.1", "00:15:5d:00:00:01", name: "_gateway"), D("192.168.0.9", "02:aa:bb:cc:dd:ee")]);
        Assert.Equal("Aparelho novo na rede", fx.Hub.Require("alerts")[0].GetProperty("title").GetString());

        Assert.False(net.SetAlias("xx", "a"));
        Assert.True(net.SetAlias("00:15:5D:00:00:01", " Roteador\n da sala "));
        var devs = fx.Hub.Require("network");
        Assert.Equal("Roteador", devs[0].GetProperty("name").GetString());
        Assert.Equal("Roteador da sala", devs[0].GetProperty("alias").GetString());
        Assert.Contains("Microsoft", devs[0].GetProperty("vendor").GetString());
        Assert.True(devs[1].GetProperty("randomMac").GetBoolean());
        Assert.True(devs[1].GetProperty("firstSeen").GetInt64() > 0);

        Assert.Equal("Roteador da sala", net.Find("00:15:5D:00:00:01")!.Alias);
        Assert.Null(net.Find("aa:aa:aa:aa:aa:aa"));

        Assert.True(net.SetAlias("00:15:5d:00:00:01", ""));
        Assert.Null(net.Find("00:15:5d:00:00:01")!.Alias);

        // Apelidos e aparelhos conhecidos sobrevivem a um reinício.
        net.SetAlias("02:aa:bb:cc:dd:ee", new string('x', 60));
        var again = new NetworkModule(fx.Hub, fx.PiHole, fx.Alerts, fx.Opt);
        again.Publish([D("192.168.0.9", "02:aa:bb:cc:dd:ee")]);
        Assert.Equal(40, again.Find("02:aa:bb:cc:dd:ee")!.Alias!.Length);
        Assert.Single(fx.Hub.Require("alerts").EnumerateArray());
    }

    [Fact]
    public async Task Varredura_publica_a_lista()
    {
        using var fx = new Fx();
        await fx.Network.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Equal(System.Text.Json.JsonValueKind.Array, fx.Hub.Require("network").ValueKind);
    }

    [Fact]
    public async Task Varredura_no_linux_combina_arp_pihole_e_ultimos_vistos()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Lê /proc/net/arp (só Linux)");
        await using var p = await FakePiHole.StartAsync();
        // Um aparelho do Pi-hole na sub-rede de uma placa física, para passar pelo filtro da LAN.
        var lan = NetworkModule.LanNetworks();
        var k = lan.Count == 0 ? 0xC0A80005u : lan[0].Net | 5;
        var ip = $"{k >> 24}.{(k >> 16) & 255}.{(k >> 8) & 255}.{k & 255}";
        p.Http.Json("GET /api/network/devices",
            $$"""{"devices":[{"hwaddr":"aa:bb:cc:dd:ee:01","macVendor":"Acme","lastQuery":{{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}},"ips":[{"ip":"{{ip}}","name":"pc"}]},{"hwaddr":"ip-1.2.3.4","ips":[]}]}""");
        p.Http.Json("GET /api/stats/top_clients", $$"""{"clients":[{"ip":"{{ip}}","count":42}]}""");

        await p.Fx.Network.RefreshAsync(TestContext.Current.CancellationToken);
        var dev = p.Fx.Network.Find("aa:bb:cc:dd:ee:01");
        Assert.NotNull(dev);
        Assert.Equal("pc", dev.Name);
        Assert.Equal(42, dev.DnsQueries);
    }
}

public class WakeTargetsTests
{
    [Theory]
    [InlineData("30-56-0F-86-A7-DD", "30:56:0f:86:a7:dd")]
    [InlineData("3056.0f86.a7dd", "30:56:0f:86:a7:dd")]
    [InlineData(" 305 60F86A7DD ", null)]
    [InlineData("30:56:0f:86:a7", null)]
    [InlineData(null, null)]
    public void Normaliza_mac(string? input, string? expected) => Assert.Equal(expected, WakeTargets.NormalizeMac(input));

    [Fact]
    public async Task Envio_salva_o_endereco_e_mantem_nome_e_usuario()
    {
        using var fx = new Fx();
        fx.Network.Publish([new DeviceInfo("192.168.0.20", "30:56:0f:86:a7:dd", "merlinpc", null, false, null)]);
        var w = new WakeTargets(fx.Hub, fx.Network, fx.Opt);

        Assert.False((await w.WakeAsync("xx", null)).Ok);
        Assert.True((await w.WakeAsync("30-56-0F-86-A7-DD", null)).Ok);
        var t = fx.Hub.Require("wol")[0];
        Assert.Equal("30:56:0f:86:a7:dd", t.GetProperty("mac").GetString());
        Assert.Equal("merlinpc", t.GetProperty("name").GetString()); // nome vindo da lista da rede

        Assert.True(w.Update("30:56:0f:86:a7:dd", new("PC do quarto", "MERLINPC\\joao")).Ok);
        Assert.False(w.Update("30:56:0f:86:a7:dd", new("x", "joao; rm -rf")).Ok);
        Assert.False(w.Update("aa:aa:aa:aa:aa:aa", new("x", null)).Ok);

        await w.WakeAsync("30:56:0f:86:a7:dd", null); // reenviar não apaga a configuração
        await w.WakeAsync("aa:bb:cc:dd:ee:ff", " Outro ");
        var state = await w.StateAsync();
        Assert.Equal(["aa:bb:cc:dd:ee:ff", "30:56:0f:86:a7:dd"], state.Targets.Select(x => x.Mac));
        Assert.Equal("Outro", state.Targets[0].Name);
        Assert.Equal(("PC do quarto", "MERLINPC\\joao"), (state.Targets[1].Name, state.Targets[1].User));
        Assert.StartsWith("ssh-ed25519 ", state.PublicKey);

        // Persistido e restaurado.
        var again = new WakeTargets(new(), fx.Network, fx.Opt);
        Assert.Equal(2, (await again.StateAsync()).Targets.Length);

        Assert.True(w.Remove("AA-BB-CC-DD-EE-FF").Ok);
        Assert.False(w.Remove("AA-BB-CC-DD-EE-FF").Ok);
    }

    [Fact]
    public async Task Desligar_so_vale_para_aparelhos_ligados_pelo_pacote_magico()
    {
        using var fx = new Fx();
        var w = new WakeTargets(fx.Hub, fx.Network, fx.Opt);
        fx.Network.Publish([new DeviceInfo("127.0.0.1", "30:56:0f:86:a7:dd", "pc", null, true, null)]);

        Assert.Equal("Só aparelhos ligados pelo pacote mágico podem ser desligados daqui", (await w.PowerAsync("30:56:0f:86:a7:dd", "shutdown")).Message);
        await w.WakeAsync("30:56:0f:86:a7:dd", null);
        Assert.Equal("Configure o usuário do Windows deste aparelho primeiro", (await w.PowerAsync("30:56:0f:86:a7:dd", "shutdown")).Message);
        w.Update("30:56:0f:86:a7:dd", new("pc", "PC\\joao"));
        Assert.Equal("Ação inválida", (await w.PowerAsync("30:56:0f:86:a7:dd", "format")).Message);

        var r = await w.PowerAsync("30:56:0f:86:a7:dd", "restart");
        if (OperatingSystem.IsLinux()) Assert.False(r.Ok); // sem sshd/chave autorizada no 127.0.0.1 de teste: falha controlada
        else Assert.Equal("(simulado) shutdown /r /f /t 0 em 127.0.0.1", r.Message);

        fx.Network.Publish([new DeviceInfo("127.0.0.1", "30:56:0f:86:a7:dd", "pc", null, false, null)]);
        Assert.Equal("Aparelho desligado ou fora da rede", (await w.PowerAsync("30:56:0f:86:a7:dd", "shutdown")).Message);
    }
}

public class DeviceBlocksTests
{
    static BlockRule R(string? start, string? end, params int[] days) => new("aa:bb:cc:dd:ee:ff", null, false, days, start, end);

    [Theory]
    [InlineData("2026-10-14 23:30", true)]  // quarta, dentro (23:00–07:00)
    [InlineData("2026-10-15 06:59", true)]  // quinta de madrugada, janela começou quarta
    [InlineData("2026-10-15 07:00", false)]
    [InlineData("2026-10-14 22:59", false)]
    [InlineData("2026-10-14 12:00", false)]
    public void Janela_que_atravessa_a_meia_noite(string when, bool active) =>
        Assert.Equal(active, DeviceBlocks.InWindow(R("23:00", "07:00"), DateTime.Parse(when)));

    [Fact]
    public void Dias_da_semana()
    {
        var weekdays = R("23:00", "07:00", 1, 2, 3, 4, 5);
        Assert.False(DeviceBlocks.InWindow(weekdays, DateTime.Parse("2026-10-17 23:30"))); // sábado
        Assert.True(DeviceBlocks.InWindow(weekdays, DateTime.Parse("2026-10-17 06:00")));  // sábado cedo: começou sexta
        Assert.False(DeviceBlocks.InWindow(weekdays, DateTime.Parse("2026-10-19 06:00"))); // segunda cedo: começou domingo

        var day = R("08:00", "12:00", 0);
        Assert.True(DeviceBlocks.InWindow(day, DateTime.Parse("2026-10-18 09:00")));  // domingo
        Assert.False(DeviceBlocks.InWindow(day, DateTime.Parse("2026-10-19 09:00")));
        Assert.False(DeviceBlocks.InWindow(R(null, null), DateTime.Now));
    }

    [Fact]
    public async Task Regras_validadas_salvas_e_aplicadas_no_pihole()
    {
        await using var p = await FakePiHole.StartAsync();
        var blocks = new DeviceBlocks(p.Fx.Hub, p.Fx.PiHole, p.Fx.Opt);

        Assert.Equal("MAC inválido", blocks.Upsert(new("x", null, true, [], null, null)).Message);
        Assert.Equal("Horário inválido (use HH:mm)", blocks.Upsert(new("aa:bb:cc:dd:ee:ff", null, false, [], "25:00", "07:00")).Message);
        Assert.Equal("Horário inválido (use HH:mm)", blocks.Upsert(new("aa:bb:cc:dd:ee:ff", null, false, [], "23:00", null)).Message);

        Assert.True(blocks.Upsert(new("AA:BB:CC:DD:EE:FF", " TV ", true, [9, 1, 1, -1], null, null)).Ok);
        await blocks.ApplyAsync(TestContext.Current.CancellationToken);
        var s = p.Fx.Hub.Require("blocks")[0];
        Assert.True(s.GetProperty("active").GetBoolean());
        Assert.Equal("TV", s.GetProperty("label").GetString());
        Assert.Equal([1], s.GetProperty("days").EnumerateArray().Select(x => x.GetInt32()));
        Assert.Contains(p.Http.Requests, r => r.Method == "POST" && r.PathAndQuery == "/api/clients" && r.Body.Contains("[0,7]"));

        // Sem mudança: não chama o Pi-hole de novo.
        var before = p.Http.Requests.Count;
        await blocks.ApplyAsync(TestContext.Current.CancellationToken);
        Assert.Equal(before, p.Http.Requests.Count);

        // Remover libera o aparelho uma última vez.
        Assert.True(blocks.Remove("AA:BB:CC:DD:EE:FF").Ok);
        await blocks.ApplyAsync(TestContext.Current.CancellationToken);
        Assert.Empty(p.Fx.Hub.Require("blocks").EnumerateArray());
        Assert.Contains(p.Http.Requests, r => r.Method == "POST" && r.PathAndQuery == "/api/clients" && r.Body.Contains("\"groups\":[0]"));

        // Regra vazia equivale a remover; regras persistem.
        blocks.Upsert(new("11:22:33:44:55:66", null, false, [], "08:00", "09:00"));
        blocks.Upsert(new("aa:bb:cc:dd:ee:ff", null, false, [], null, null));
        var hub = new Bureau.Infra.EventHub();
        await new DeviceBlocks(hub, p.Fx.PiHole, p.Fx.Opt).ApplyAsync(TestContext.Current.CancellationToken);
        Assert.Equal("11:22:33:44:55:66", hub.Require("blocks")[0].GetProperty("mac").GetString());
        Assert.Equal(1, hub.Require("blocks").GetArrayLength());
    }

    [Fact]
    public async Task Sem_pihole_informa_o_motivo()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "No Windows/macOS o Pi-hole é simulado");
        using var fx = new Fx();
        var blocks = new DeviceBlocks(fx.Hub, fx.PiHole, fx.Opt);
        blocks.Upsert(new("aa:bb:cc:dd:ee:ff", null, true, [], null, null));
        await blocks.ApplyAsync(TestContext.Current.CancellationToken);
        Assert.Equal("Requer o Pi-hole configurado", fx.Hub.Require("blocks")[0].GetProperty("error").GetString());
    }

    [Fact]
    public async Task Erro_do_pihole_e_reaplicado_na_proxima_rodada()
    {
        await using var p = await FakePiHole.StartAsync();
        p.Http.Status("POST /api/clients", 500);
        var blocks = new DeviceBlocks(p.Fx.Hub, p.Fx.PiHole, p.Fx.Opt);
        blocks.Upsert(new("aa:bb:cc:dd:ee:ff", null, true, [], null, null));
        await blocks.ApplyAsync(TestContext.Current.CancellationToken);
        Assert.Equal("Pi-hole respondeu 500", p.Fx.Hub.Require("blocks")[0].GetProperty("error").GetString());

        p.Http.Json("POST /api/clients", "{}");
        await blocks.ApplyAsync(TestContext.Current.CancellationToken);
        Assert.False(p.Fx.Hub.Require("blocks")[0].TryGetProperty("error", out _));
    }
}

public class MonitorsTests
{
    [Theory]
    [InlineData("https://bureau.merlin.api.br", true)]
    [InlineData("http://192.168.0.1/admin", true)]
    [InlineData("192.168.100.1:80", true)]
    [InlineData("[::1]:8080", true)]
    [InlineData("router.local:443", true)]
    [InlineData("ftp://x", false)]
    [InlineData("sem porta", false)]
    [InlineData("", false)]
    public void Alvos_validos(string target, bool valid) => Assert.Equal(valid, MonitorsModule.IsValidTarget(target));

    [Fact]
    public async Task Salva_definicoes_e_testa_http_e_tcp()
    {
        await using var http = await FakeHttp.StartAsync();
        http.Status("GET /ok", 204).Status("GET /erro", 503);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var fx = new Fx();
        var m = new MonitorsModule(fx.Hub, fx.Alerts, fx.Opt);
        Assert.False(m.Save([new("", "x", "nada")]).Ok);
        Assert.True(m.Save([
            new("", "", http.Url + "/ok"),
            new("b", "Quebrado", http.Url + "/erro"),
            new("c", "Porta", $"127.0.0.1:{port}"),
            new("d", "Fechada", "127.0.0.1:1"),
        ]).Ok);
        Assert.Equal(4, new MonitorsModule(fx.Hub, fx.Alerts, fx.Opt).Definitions.Length); // persistido
        Assert.Equal(http.Url.Replace("http://", "") + "/ok", m.Definitions[0].Name);

        await m.RunAsync(TestContext.Current.CancellationToken);
        await m.RunAsync(TestContext.Current.CancellationToken); // 2ª falha seguida muda o estado
        var states = fx.Hub.Require("monitors").EnumerateArray().ToDictionary(s => s.GetProperty("name").GetString()!);

        Assert.True(states["Internet"].GetProperty("builtin").GetBoolean());
        Assert.True(states[m.Definitions[0].Name].GetProperty("up").GetBoolean());
        Assert.True(states["Porta"].GetProperty("up").GetBoolean());
        Assert.False(states["Quebrado"].GetProperty("up").GetBoolean());
        Assert.Equal("HTTP 503", states["Quebrado"].GetProperty("error").GetString());
        Assert.Equal(0, states["Quebrado"].GetProperty("uptime24h").GetDouble());
        Assert.False(states["Fechada"].GetProperty("up").GetBoolean());
        Assert.Contains(fx.Hub.Require("alerts").EnumerateArray(), a => a.GetProperty("title").GetString() == "Quebrado fora do ar");

        // Voltou: alerta de retorno; monitores removidos somem.
        http.Status("GET /erro", 200);
        m.Save([new("b", "Quebrado", http.Url + "/erro")]);
        await m.RunAsync(TestContext.Current.CancellationToken);
        Assert.Contains(fx.Hub.Require("alerts").EnumerateArray(), a => a.GetProperty("title").GetString() == "Quebrado voltou");
        Assert.Equal(2, fx.Hub.Require("monitors").GetArrayLength());
    }

    [Fact]
    public async Task Nome_que_nao_resolve()
    {
        using var fx = new Fx();
        var m = new MonitorsModule(fx.Hub, fx.Alerts, fx.Opt);
        m.Save([new("x", "Inexistente", "https://nao-existe.invalid")]);
        await m.RunAsync(TestContext.Current.CancellationToken);
        var s = fx.Hub.Require("monitors").EnumerateArray().First(x => x.GetProperty("id").GetString() == "x");
        Assert.Equal("Nome não resolvido (DNS)", s.GetProperty("error").GetString());
    }
}
