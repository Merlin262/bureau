using Bureau.Infra;

namespace Bureau.Tests;

public class AlertEngineTests : IDisposable
{
    readonly Fx _fx = new();

    public void Dispose() => _fx.Dispose();

    string[] Titles() => [.. _fx.Hub.Require("alerts").EnumerateArray().Select(a => a.GetProperty("title").GetString()!)];

    static ServiceInfo Svc(string active, string sub = "running") => new("nginx.service", "nginx", active, sub, 1, 0, 0);

    [Fact]
    public void Servico_que_cai_e_volta_gera_dois_alertas()
    {
        var a = _fx.Alerts;
        a.OnServices([Svc("active")]);           // primeira leitura: só aprende
        a.OnServices([Svc("failed", "failed")]);
        a.OnServices([Svc("failed", "failed")]); // repetido: nada novo
        a.OnServices([Svc("active")]);

        Assert.Equal(["nginx voltou", "nginx caiu"], Titles());
        var body = _fx.Hub.Require("alerts")[1].GetProperty("body").GetString();
        Assert.Equal("O serviço nginx falhou.", body);
    }

    [Fact]
    public void Servico_parado_ou_reiniciando_em_loop_tambem_e_queda()
    {
        _fx.Alerts.OnServices([Svc("active")]);
        _fx.Alerts.OnServices([Svc("activating", "auto-restart")]);
        Assert.Equal("O serviço nginx parou (auto-restart).", _fx.Hub.Require("alerts")[0].GetProperty("body").GetString());
    }

    [Fact]
    public void Acao_pela_interface_nao_gera_alerta()
    {
        _fx.Alerts.OnServices([Svc("active")]);
        _fx.Alerts.Expect("svc:nginx.service");
        _fx.Alerts.OnServices([Svc("inactive", "dead")]);
        _fx.Alerts.OnServices([Svc("active")]);
        Assert.Empty(Titles());
    }

    [Fact]
    public void Expectativa_vence_depois_da_janela()
    {
        _fx.Alerts.OnServices([Svc("active")]);
        _fx.Alerts.Expect("svc:nginx.service", TimeSpan.FromSeconds(10));
        _fx.Time.Advance(TimeSpan.FromSeconds(11));
        _fx.Alerts.OnServices([Svc("failed")]);
        Assert.Equal(["nginx caiu"], Titles());
    }

    [Fact]
    public void Mesmo_alerta_respeita_intervalo_de_5_minutos()
    {
        var a = _fx.Alerts;
        a.OnServices([Svc("active")]);
        a.OnServices([Svc("failed")]);
        a.OnServices([Svc("active")]);
        a.OnServices([Svc("failed")]);   // dentro do intervalo: suprimido
        Assert.Equal(2, Titles().Length);

        _fx.Time.Advance(TimeSpan.FromMinutes(6));
        a.OnServices([Svc("active")]);
        a.OnServices([Svc("failed")]);
        Assert.Equal(["nginx caiu", "nginx voltou", "nginx voltou", "nginx caiu"], Titles());
    }

    [Fact]
    public void Conteineres()
    {
        var a = _fx.Alerts;
        a.OnContainers(new ContainersState(false, "sem docker", [])); // indisponível: ignora
        ContainerInfo C(string state) => new("abc", "jellyfin", "img", state, "Exited (1)", "", 0);
        a.OnContainers(new(true, null, [C("running")]));
        a.OnContainers(new(true, null, [C("exited")]));
        a.OnContainers(new(true, null, [C("running")]));
        Assert.Equal(["jellyfin voltou", "jellyfin parou"], Titles());

        _fx.Time.Advance(TimeSpan.FromMinutes(6));
        a.Expect("ctr:abc");
        a.OnContainers(new(true, null, [C("dead")]));
        Assert.Equal(2, Titles().Length);
    }

    [Fact]
    public void Queda_de_energia_bateria_critica_e_retorno()
    {
        var a = _fx.Alerts;
        a.OnSystem(Snap.System(battery: new(80, "Discharging")));
        _fx.Time.Advance(TimeSpan.FromSeconds(10));
        a.OnSystem(Snap.System(battery: new(80, "Discharging")));
        Assert.Empty(Titles()); // menos de 15 s: pode ser só um soluço

        _fx.Time.Advance(TimeSpan.FromSeconds(6));
        a.OnSystem(Snap.System(battery: new(79, "Discharging")));
        a.OnSystem(Snap.System(battery: new(20, "Discharging")));
        a.OnSystem(Snap.System(battery: new(19, "Discharging")));
        a.OnSystem(Snap.System(battery: new(19, "Charging")));
        Assert.Equal(["Energia restabelecida", "Bateria crítica", "Queda de energia"], Titles());
    }

    [Fact]
    public void Temperatura_alta_por_mais_de_um_minuto_com_histerese()
    {
        var a = _fx.Alerts;
        a.OnSystem(Snap.System(temp: 90));
        _fx.Time.Advance(TimeSpan.FromSeconds(61));
        a.OnSystem(Snap.System(temp: 91));
        a.OnSystem(Snap.System(temp: 82)); // abaixo do limite mas dentro da histerese
        a.OnSystem(Snap.System(temp: 95));
        Assert.Equal(["Temperatura alta"], Titles());

        a.OnSystem(Snap.System(temp: 70)); // esfriou de verdade
        _fx.Time.Advance(TimeSpan.FromMinutes(6));
        a.OnSystem(Snap.System(temp: 90));
        _fx.Time.Advance(TimeSpan.FromSeconds(61));
        a.OnSystem(Snap.System(temp: 90));
        Assert.Equal(2, Titles().Length);
        a.OnSystem(Snap.System(temp: null, battery: null));
    }

    [Fact]
    public void Disco_quase_cheio_avisa_uma_vez_ate_baixar()
    {
        var a = _fx.Alerts;
        HostInfo H(long used) => new("h", "os", "k", "cpu", 4, [], [new("/", "ext4", 100L << 30, used << 30), new("/x", "ext4", 0, 0)], "1");
        a.OnHost(H(91));
        a.OnHost(H(92));
        Assert.Equal(["Disco / quase cheio"], Titles());
        Assert.Equal("warn", _fx.Hub.Require("alerts")[0].GetProperty("level").GetString());
        Assert.Contains("restam 9 GB", _fx.Hub.Require("alerts")[0].GetProperty("body").GetString());

        a.OnHost(H(80));
        _fx.Time.Advance(TimeSpan.FromMinutes(6));
        a.OnHost(H(98));
        Assert.Equal("crit", _fx.Hub.Require("alerts")[0].GetProperty("level").GetString());
    }

    [Fact]
    public void PiHole_fora_do_ar_depois_de_3_falhas()
    {
        var a = _fx.Alerts;
        PiHoleState P(bool online, bool configured = true) => new(configured, online, null, true, null, 0, 0, 0, 0, 0, [], []);
        a.OnPiHole(P(false, configured: false));
        a.OnPiHole(P(false));
        a.OnPiHole(P(false));
        Assert.Empty(Titles());
        a.OnPiHole(P(false));
        a.OnPiHole(P(false));
        a.OnPiHole(P(true));
        Assert.Equal(["Pi-hole voltou", "Pi-hole fora do ar"], Titles());
    }

    [Fact]
    public void Monitores_e_internet()
    {
        var a = _fx.Alerts;
        a.OnMonitor(true, "Internet", false, 0, null);
        a.OnMonitor(true, "Internet", true, 90, null);
        a.OnMonitor(false, "Site", false, 0, "HTTP 503");
        a.OnMonitor(false, "Site", true, 600, null);
        a.OnMonitor(false, "Outro", true, 7200, null);
        var all = _fx.Hub.Require("alerts").EnumerateArray().Select(x => (x.GetProperty("title").GetString(), x.GetProperty("body").GetString())).ToArray();
        Assert.Contains(("Internet voltou", "A conexão ficou fora por 90 s."), all);
        Assert.Contains(("Site fora do ar", "HTTP 503"), all);
        Assert.Contains(("Site voltou", "Ficou fora por 10 min."), all);
        Assert.Contains(("Outro voltou", "Ficou fora por 2 h."), all);
        Assert.Contains(("Internet caiu", "O servidor perdeu a conexão com a internet."), all);
    }

    [Fact]
    public void Aparelho_novo()
    {
        _fx.Alerts.OnNewDevice(new DeviceInfo("192.168.0.9", "02:00:00:00:00:01", null, null, true, null, RandomMac: true));
        _fx.Alerts.OnNewDevice(new DeviceInfo("192.168.0.8", "aa:00:00:00:00:01", "tv", "LG", true, null));
        var bodies = _fx.Hub.Require("alerts").EnumerateArray().Select(x => x.GetProperty("body").GetString()).ToArray();
        Assert.Equal(["tv · 192.168.0.8 · aa:00:00:00:00:01", "celular/tablet (MAC aleatório) · 192.168.0.9 · 02:00:00:00:00:01"], bodies);
    }

    [Fact]
    public void Logins_suspeitos()
    {
        var a = _fx.Alerts;
        a.OnLogin(new AccessEntry(0, "192.168.0.5", null, "web", true, "x"), false, 0); // rede local: ok
        a.OnLogin(new AccessEntry(0, "203.0.113.1", "US", "web", true, "Chrome"), true, 0); // IP conhecido: ok
        Assert.Empty(Titles());
        a.OnLogin(new AccessEntry(0, "203.0.113.2", "US", "web", true, "Chrome"), false, 0);
        a.OnLogin(new AccessEntry(0, "203.0.113.3", null, "web", false, "?"), false, 4);
        a.OnLogin(new AccessEntry(0, "203.0.113.3", null, "web", false, "?"), false, 5);
        Assert.Equal(["Tentativas de login falhas", "Novo acesso ao BUREAU"], Titles());
        Assert.Contains("203.0.113.2 (US)", _fx.Hub.Require("alerts")[1].GetProperty("body").GetString());
    }

    [Fact]
    public void Integracoes_fora_do_ar_e_de_volta()
    {
        var a = _fx.Alerts;
        a.OnOnline("jellyfin", "Jellyfin", false, false, "apps");
        for (var i = 0; i < 4; i++) a.OnOnline("jellyfin", "Jellyfin", true, false, "apps");
        a.OnOnline("jellyfin", "Jellyfin", true, true, "apps");
        a.OnOnline("jellyfin", "Jellyfin", true, true, "apps");
        Assert.Equal(["Jellyfin voltou", "Jellyfin fora do ar"], Titles());
    }

    [Fact]
    public void Backups()
    {
        var a = _fx.Alerts;
        BackupInfo B(string status, string? msg = null) => new("Fotos", "restic.service", status, 0, 0, 0, msg);
        a.OnBackups([B("ok")]);
        a.OnBackups([B("failed")]);
        a.OnBackups([B("failed")]);
        a.OnBackups([B("ok")]);
        a.OnBackups([B("stale", "Atrasado!")]);
        a.OnBackups([B("running")]);
        Assert.Equal(["Backup Fotos atrasado", "Backup Fotos em dia", "Backup Fotos falhou"], Titles());
    }

    [Fact]
    public void Pacotes_de_seguranca_e_reinicializacao()
    {
        var a = _fx.Alerts;
        AptState S(int security, bool reboot, long checkedAt = 1, bool supported = true) =>
            new(supported, security, security, [], reboot, [], checkedAt, false, null, 0);
        a.OnApt(S(3, true, supported: false));
        a.OnApt(S(3, true, checkedAt: 0));
        a.OnApt(S(0, false));
        a.OnApt(S(2, false));
        a.OnApt(S(2, true));
        a.OnApt(S(2, true));
        Assert.Equal(["Reinicialização necessária", "Atualizações de segurança"], Titles());
    }

    [Fact]
    public void Simulacoes_e_historico_persistido()
    {
        foreach (var k in new[] { "power", "disk", "ok", "service" }) _fx.Alerts.Simulate(k);
        Assert.Equal(4, Titles().Length);

        // Nova instância (reinício) lê os alertas salvos.
        var hub = new EventHub();
        _ = new Bureau.Modules.AlertEngine(hub, _fx.Notifier, _fx.Monitor, Fx.Log<Bureau.Modules.AlertEngine>());
        Assert.Equal(4, hub.Require("alerts").GetArrayLength());
    }

    [Fact]
    public void Guarda_so_os_50_ultimos()
    {
        for (var i = 0; i < 55; i++) _fx.Alerts.OnMonitor(false, $"m{i}", false, 0, null);
        Assert.Equal(50, Titles().Length);
    }

    [Fact]
    public async Task Teste_de_push_sem_aparelhos_retorna_zero()
    {
        _fx.Options.Alerts.Enabled = false;
        _fx.Alerts.Simulate("disk");
        Assert.Equal(0, await _fx.Alerts.TestAsync(null));
    }
}
