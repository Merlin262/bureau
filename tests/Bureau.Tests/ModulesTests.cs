using System.Text;
using System.Text.Json;
using Bureau.Modules;
using Microsoft.Extensions.Hosting;

namespace Bureau.Tests;

public class ServicesTests
{
    const string Show = """
        Id=nginx.service
        Description=A high performance web server
        LoadState=loaded
        ActiveState=active
        SubState=running
        MainPID=1234
        MemoryCurrent=10485760
        ActiveEnterTimestamp=@1700000000

        Id=ghost.service
        LoadState=not-found
        ActiveState=inactive
        MemoryCurrent=[not set]
        ActiveEnterTimestamp=

        Description=sem id

        Id=old.service
        ActiveState=failed
        SubState=failed
        MemoryCurrent=18446744073709551615
        ActiveEnterTimestamp=@1700000000
        """;

    [Fact]
    public void Interpreta_systemctl_show()
    {
        var items = ServicesModule.Parse(Show.Replace("\n", "\r\n"));
        Assert.Equal(3, items.Length);
        Assert.Equal(new ServiceInfo("nginx.service", "A high performance web server", "active", "running", 1234, 10485760, 1700000000), items[0]);
        Assert.Equal(("not-found", 0L, 0L), (items[1].Active, items[1].Memory, items[1].Since));
        Assert.Equal(("failed", 0L, 0L), (items[2].Active, items[2].Memory, items[2].Since)); // desde = 0 se não está ativo
    }

    [Fact]
    public void Interpreta_list_units()
    {
        var units = ServicesModule.ParseUnits("● nginx.service loaded failed failed A web server\ncron.service loaded active running Regular jobs\nlixo\n");
        Assert.Equal([new UnitEntry("cron.service", "Regular jobs", "active"), new UnitEntry("nginx.service", "A web server", "failed")], units);
    }

    [Fact]
    public void Lista_monitorada_normalizada_e_persistida()
    {
        using var fx = new Fx(o => o.Services = ["sshd", "docker.socket", "a b", "sshd.service", ""]);
        var w = new WatchStore(fx.Opt);
        Assert.Equal(["sshd.service", "docker.socket"], w.Units);
        Assert.True(w.Contains("sshd.service"));

        w.Set(["cron", "../etc", new string('x', 300)]);
        Assert.Equal(["cron.service"], new WatchStore(fx.Opt).Units);

        File.WriteAllText(fx.Dir.File("watch.json"), "{lixo");
        Assert.Equal(["sshd.service", "docker.socket"], new WatchStore(fx.Opt).Units); // arquivo corrompido: volta à configuração
    }

    [Fact]
    public async Task Atualiza_acoes_e_logs()
    {
        using var fx = new Fx(o => o.Services = ["cron"]);
        var svc = new ServicesModule(fx.Hub, new WatchStore(fx.Opt), fx.Alerts);
        await svc.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Equal(JsonValueKind.Array, fx.Hub.Require("services").ValueKind);

        Assert.Equal("Serviço não monitorado", (await svc.ActionAsync("nginx.service", "restart")).Message);
        Assert.Equal("Ação inválida", (await svc.ActionAsync("cron.service", "mask")).Message);
        Assert.Equal("Serviço não monitorado.", await svc.LogsAsync("nginx.service"));
        Assert.False(string.IsNullOrEmpty(await svc.LogsAsync("cron.service")));
        Assert.NotNull(await svc.ListAvailableAsync());
    }

    [Fact]
    public async Task Sem_servicos_monitorados_publica_lista_vazia()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "No Windows/macOS a lista é simulada");
        using var fx = new Fx();
        await new ServicesModule(fx.Hub, new WatchStore(fx.Opt), fx.Alerts).RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, fx.Hub.Require("services").GetArrayLength());
    }
}

public class DockerTests
{
    [Fact]
    public void Interpreta_conteiner_da_api()
    {
        using var doc = JsonDocument.Parse("""
            {"Id":"0123456789abcdef","Names":["/jellyfin"],"Image":"jellyfin/jellyfin","State":"running","Status":"Up 2 hours","Created":1700000000,
             "Ports":[{"PrivatePort":8096,"PublicPort":8096,"Type":"tcp"},{"PrivatePort":1900,"Type":"udp"},{"PrivatePort":8920}]}
            """);
        var c = DockerModule.ParseContainer(doc.RootElement);
        Assert.Equal(new ContainerInfo("0123456789ab", "jellyfin", "jellyfin/jellyfin", "running", "Up 2 hours",
            "1900/udp, 8096→8096/tcp, 8920/tcp", 1700000000), c);
    }

    [Fact]
    public void Separa_logs_multiplexados()
    {
        static byte[] Frame(byte stream, string text)
        {
            var b = Encoding.UTF8.GetBytes(text);
            return [stream, 0, 0, 0, (byte)(b.Length >> 24), (byte)(b.Length >> 16), (byte)(b.Length >> 8), (byte)b.Length, .. b];
        }
        Assert.Equal("olá\nerro\n", DockerModule.Demux([.. Frame(1, "olá\n"), .. Frame(2, "erro\n")]));
        Assert.Equal("texto puro com TTY", DockerModule.Demux(Encoding.UTF8.GetBytes("texto puro com TTY")));
        Assert.Equal("abc", DockerModule.Demux([1, 0, 0, 0, 0, 0, 0, 99, .. "abc"u8])); // tamanho maior que o resto
        Assert.Equal("", DockerModule.Demux([]));
    }

    [Fact]
    public async Task Validacoes_e_estado()
    {
        using var fx = new Fx();
        using var docker = new DockerModule(fx.Hub, fx.Alerts, fx.Opt);
        Assert.Equal("ID inválido", (await docker.ActionAsync("../x", "start")).Message);
        Assert.Equal("Ação inválida", (await docker.ActionAsync("abc", "rm")).Message);
        Assert.Equal("ID inválido.", await docker.LogsAsync("$"));
        await docker.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Equal(JsonValueKind.Object, fx.Hub.Require("containers").ValueKind);
    }

    [Fact]
    public async Task Desativado_ou_sem_socket()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Docker por socket unix só no Linux");
        using var off = new Fx(o => o.Docker.Enabled = false);
        using (var d = new DockerModule(off.Hub, off.Alerts, off.Opt))
        {
            await d.RefreshAsync(TestContext.Current.CancellationToken);
            Assert.Equal("Docker desativado na configuração", off.Hub.Require("containers").GetProperty("error").GetString());
            Assert.Equal("Docker indisponível", (await d.ActionAsync("abc", "start")).Message);
            Assert.Equal("Docker indisponível.", await d.LogsAsync("abc"));
        }

        using var missing = new Fx(o => o.Docker.Socket = "/tmp/nao-existe.sock");
        using var d2 = new DockerModule(missing.Hub, missing.Alerts, missing.Opt);
        await d2.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Equal("Docker não encontrado", missing.Hub.Require("containers").GetProperty("error").GetString());
    }
}

public class AptTests
{
    [Fact]
    public void Interpreta_lista_de_atualizacoes()
    {
        var (names, security) = AptModule.ParseUpgradable("""
            Listing... Done
            libssl3/jammy-updates,jammy-security 3.0.2-0ubuntu1.18 amd64 [upgradable from: 3.0.2-0ubuntu1.17]
            curl/jammy-updates 7.81.0-1ubuntu1.20 amd64 [upgradable from: 7.81.0-1ubuntu1.19]
            /estranho
            """);
        Assert.Equal(["libssl3", "curl"], names);
        Assert.Equal(1, security);
    }

    [Fact]
    public void Identifica_liberacao_gradual()
    {
        var phased = AptModule.ParsePhased("""
            curl:
              Installed: 7.81.0-1ubuntu1.19
              Candidate: 7.81.0-1ubuntu1.20
              Version table:
                 7.81.0-1ubuntu1.20 500 (phased 40%)
            libssl3:amd64:
              Installed: 1
              Candidate: 2
            """);
        Assert.Equal(["curl"], phased);
    }

    [Fact]
    public void Propriedades_e_timestamps_do_systemctl()
    {
        var p = AptModule.Props("A=1\nB= x \nlixo\n=y");
        Assert.Equal("1", p["A"]);
        Assert.Equal("x", p["B"]);
        Assert.Equal(2, p.Count);
        Assert.Equal(1700000000, AptModule.Unix("@1700000000"));
        Assert.Equal(0, AptModule.Unix("n/a"));
        Assert.Equal(0, AptModule.Unix(null));
        Assert.Equal(0, AptModule.Unix("@x"));
    }

    [Fact]
    public async Task Desativado_na_configuracao()
    {
        using var fx = new Fx(o => o.SystemUpdates = false);
        await new AptModule(fx.Hub, fx.Alerts, fx.Monitor).RefreshAsync(TestContext.Current.CancellationToken);
        Assert.False(fx.Hub.Require("apt").GetProperty("supported").GetBoolean());
    }

    [Fact]
    public async Task Lista_atualiza_e_logs()
    {
        using var fx = new Fx();
        var apt = new AptModule(fx.Hub, fx.Alerts, fx.Monitor);
        await apt.RefreshAsync(TestContext.Current.CancellationToken);
        apt.Recheck();
        await apt.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Equal(JsonValueKind.Object, fx.Hub.Require("apt").ValueKind);
        Assert.NotNull(await apt.LogsAsync());
        var r = await apt.UpgradeAsync();
        if (!OperatingSystem.IsLinux()) Assert.True(r.Ok);
    }
}

public class BackupsTests
{
    [Fact]
    public async Task Jobs_configurados()
    {
        using var fx = new Fx(o => o.Backups = [new BackupJob { Name = "", Unit = "bureau-teste-backup" }, new BackupJob { Unit = " " }]);
        var b = new BackupsModule(fx.Hub, fx.Alerts, fx.Monitor);
        await b.RefreshAsync(TestContext.Current.CancellationToken);
        var items = fx.Hub.Require("backups");
        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal("bureau-teste-backup", items[0].GetProperty("name").GetString());
        Assert.Equal("bureau-teste-backup.service", items[0].GetProperty("unit").GetString());
        Assert.Equal("unknown", items[0].GetProperty("status").GetString());

        if (OperatingSystem.IsLinux())
        {
            Assert.Equal("Serviço não encontrado", items[0].GetProperty("message").GetString());
            Assert.Equal("Backup não configurado", (await b.RunAsync("outro.service")).Message);
            Assert.Equal("Backup não configurado.", await b.LogsAsync("outro.service"));
            Assert.False((await b.RunAsync("bureau-teste-backup.service")).Ok);
            Assert.NotNull(await b.LogsAsync("bureau-teste-backup.service"));
        }
        else
        {
            Assert.True((await b.RunAsync("x")).Ok);
            Assert.NotEmpty(await b.LogsAsync("x"));
        }
    }

    [Fact]
    public async Task Sem_jobs()
    {
        using var fx = new Fx();
        await new BackupsModule(fx.Hub, fx.Alerts, fx.Monitor).RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Equal(JsonValueKind.Array, fx.Hub.Require("backups").ValueKind);
    }
}

public class UpdateTests
{
    [Fact]
    public async Task Estado_da_atualizacao_automatica()
    {
        using var fx = new Fx();
        File.WriteAllText(fx.Dir.File("update.json"), """{"latest":"v9.9.9","status":"erro","message":"falhou","checkedAt":1700000000}""");
        var u = new UpdateModule(fx.Opt);
        var s = await u.GetAsync();
        Assert.StartsWith("v", s.Current);
        if (OperatingSystem.IsLinux())
        {
            Assert.Equal(("v9.9.9", "erro", "falhou", 1700000000L), (s.Latest, s.Status, s.Message, s.CheckedAt));
            Assert.False(s.Supported); // sem update.sh ao lado do binário de teste
            Assert.Equal("Atualizador não instalado — rode o install.sh novamente", (await u.RunAsync()).Message);

            File.WriteAllText(fx.Dir.File("update.json"), "{lixo");
            Assert.Equal("nunca", (await u.GetAsync()).Status);
        }
        else
        {
            Assert.Equal("atualizado", s.Status);
            Assert.False((await u.RunAsync()).Ok);
        }
    }
}

public class MetricsTests
{
    [Fact]
    public void Grava_por_minuto_consolida_por_hora_e_consulta()
    {
        using var dir = new TempDir();
        var m = new MetricsStore(dir.Path);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var start = (now - 3 * 3600) / 3600 * 3600; // início de uma hora, ~3 h atrás
        for (var t = start; t < now; t += 30)
            m.Add(Snap.System(cpu: 40, memTotal: 1000, memUsed: 250, temp: null, battery: new(90, "Full"), rx: 1000, tx: 10), t);
        m.Add(Snap.System(memTotal: 0), now + 60); // fecha o último minuto

        var h1 = m.Query("1h");
        Assert.Equal("1h", h1.Range);
        Assert.Equal(60, h1.Step);
        Assert.InRange(h1.T.Length, 55, 62);
        Assert.All(h1.Cpu, v => Assert.Equal(40, v));
        Assert.All(h1.Mem, v => Assert.Equal(25, v));
        Assert.All(h1.Temp, Assert.Null); // sem sensor
        Assert.All(h1.Battery, v => Assert.Equal(90, v));
        Assert.All(h1.Rx, v => Assert.Equal(1000, v));

        var d7 = m.Query("7d");
        Assert.Equal(3600, d7.Step);
        Assert.InRange(d7.T.Length, 2, 3);
        Assert.All(d7.Cpu, v => Assert.Equal(40, v));

        Assert.Equal(86400 / 360, m.Query("qualquer").Step);
        Assert.Equal(60, m.Query("6h").Step);
        Assert.Equal(30 * 86400 / 360, m.Query("30d").Step);
        Assert.Equal(90 * 86400 / 360, m.Query("90d").Step);

        // Novo processo (reinício) lê o histórico gravado.
        Assert.Equal(h1.T.Length, new MetricsStore(dir.Path).Query("1h").T.Length);
    }

    [Fact]
    public void Diretorio_inexistente_nao_quebra()
    {
        var m = new MetricsStore(Path.Combine(Path.GetTempPath(), "nao", "existe", Guid.NewGuid().ToString()));
        m.Add(Snap.System(), 1000);
        m.Add(Snap.System(), 5000);
        Assert.Empty(m.Query("24h").T);
    }
}

sealed class FakeLifetime : IHostApplicationLifetime
{
    public bool Stopped;
    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => CancellationToken.None;
    public CancellationToken ApplicationStopped => CancellationToken.None;
    public void StopApplication() => Stopped = true;
}

public class ConfigBackupTests
{
    [Fact]
    public async Task Exporta_e_restaura_so_arquivos_conhecidos()
    {
        using var fx = new Fx();
        File.WriteAllText(fx.Dir.File("aliases.json"), """{"aa:bb:cc:dd:ee:ff":"TV"}""");
        File.WriteAllText(fx.Dir.File("segredo.txt"), "não exporta");
        var life = new FakeLifetime();
        var b = new ConfigBackup(fx.Opt, life, Fx.Log<ConfigBackup>());

        var bundle = b.Export();
        Assert.Equal(("bureau-backup", 1), (bundle.Kind, bundle.Version));
        Assert.Equal(["aliases.json"], bundle.Files.Keys);

        using var target = new Fx();
        var b2 = new ConfigBackup(target.Opt, life, Fx.Log<ConfigBackup>());
        var files = new Dictionary<string, string>(bundle.Files) { ["../../etc/passwd"] = "{}", ["monitors.json"] = "[]" };
        var r = b2.Import(bundle with { Files = files, Config = """{"Port":8420}""" });
        Assert.True(r.Ok);
        Assert.StartsWith("2 arquivo(s) restaurado(s)", r.Message);
        Assert.Contains("bureau.json.restaurado", r.Message);
        Assert.Equal("""{"aa:bb:cc:dd:ee:ff":"TV"}""", File.ReadAllText(target.Dir.File("aliases.json")));
        Assert.True(File.Exists(target.Dir.File("bureau.json.restaurado")));
        Assert.False(File.Exists(Path.Combine(target.Dir.Path, "..", "..", "etc", "passwd")));

        await Task.Delay(1800, TestContext.Current.CancellationToken);
        Assert.True(life.Stopped); // reinicia para os módulos lerem os arquivos
    }

    [Fact]
    public void Rejeita_arquivo_errado_ou_corrompido()
    {
        using var fx = new Fx();
        var b = new ConfigBackup(fx.Opt, new FakeLifetime(), Fx.Log<ConfigBackup>());
        var ok = new BackupBundle("bureau-backup", 1, 0, "h", "1", [], null);
        Assert.Equal("Arquivo não é um backup do BUREAU", b.Import(ok with { Kind = "x" }).Message);
        Assert.Equal("Arquivo não é um backup do BUREAU", b.Import(ok with { Version = 2 }).Message);
        Assert.Equal("blocks.json está corrompido no backup", b.Import(ok with { Files = new() { ["blocks.json"] = "{" } }).Message);
        Assert.Equal("0 arquivo(s) restaurado(s). Reiniciando o BUREAU…", b.Import(ok with { Files = null! }).Message);
    }
}

public class HomeAssistantTests
{
    const string States = """
        [
          {"entity_id":"light.sala","state":"on","attributes":{"friendly_name":"Luz da sala"}},
          {"entity_id":"switch.tv","state":"off","attributes":{}},
          {"entity_id":"sensor.temp","state":"22.5","attributes":{"friendly_name":"Temperatura","unit_of_measurement":"°C"}},
          {"entity_id":"sensor.x","state":"unavailable"},
          {"entity_id":"fan.quarto","state":"unknown","attributes":{"friendly_name":"Ventilador"}}
        ]
        """;

    static async Task<(Fx, FakeHttp, HomeAssistantModule)> Setup(params string[] entities)
    {
        var http = await FakeHttp.StartAsync();
        http.Json("GET /api/config", """{"version":"2026.10.2","location_name":"Casa"}""").Json("GET /api/states", States)
            .Json("POST /api/services/homeassistant/toggle", "[]");
        var fx = new Fx(o => { o.HomeAssistant.Enabled = true; o.HomeAssistant.Url = http.Url + "/"; o.HomeAssistant.Token = "tok"; o.HomeAssistant.Entities = entities; });
        return (fx, http, new HomeAssistantModule(fx.Hub, fx.Alerts, fx.Monitor, Fx.Log<HomeAssistantModule>()));
    }

    [Fact]
    public async Task Resumo_e_entidades_padrao()
    {
        var (fx, http, ha) = await Setup();
        await using var _ = http;
        using var __ = fx;
        await ha.RefreshAsync(TestContext.Current.CancellationToken);
        var s = fx.Hub.Require("homeassistant");
        Assert.True(s.GetProperty("online").GetBoolean());
        Assert.Equal(("2026.10.2", "Casa"), (s.GetProperty("version").GetString(), s.GetProperty("location").GetString()));
        Assert.Equal((5, 2), (s.GetProperty("total").GetInt32(), s.GetProperty("unavailable").GetInt32()));
        Assert.Equal("sensor", s.GetProperty("domains")[0].GetProperty("name").GetString());
        Assert.Equal(["fan.quarto", "light.sala", "switch.tv"], s.GetProperty("entities").EnumerateArray().Select(e => e.GetProperty("id").GetString()));
        Assert.Equal("Bearer tok", http.Requests.First().Headers["Authorization"]);
    }

    [Fact]
    public async Task Entidades_escolhidas_e_alternar()
    {
        var (fx, http, ha) = await Setup("sensor.temp", "light.sala");
        await using var _ = http;
        using var __ = fx;
        await ha.RefreshAsync(TestContext.Current.CancellationToken);
        var ents = fx.Hub.Require("homeassistant").GetProperty("entities");
        Assert.Equal(["sensor.temp", "light.sala"], ents.EnumerateArray().Select(e => e.GetProperty("id").GetString()));
        Assert.Equal("°C", ents[0].GetProperty("unit").GetString());

        Assert.Equal("Entidade inválida", (await ha.ToggleAsync("Light.Sala!")).Message);
        Assert.Equal("Essa entidade não pode ser alternada", (await ha.ToggleAsync("sensor.x")).Message);
        Assert.True((await ha.ToggleAsync("sensor.temp")).Ok); // escolhida na configuração: liberada
        Assert.True((await ha.ToggleAsync("switch.tv")).Ok);
        Assert.Contains(http.Requests, r => r.PathAndQuery == "/api/services/homeassistant/toggle" && r.Body == """{"entity_id":"switch.tv"}""");

        http.Status("POST /api/services/homeassistant/toggle", 500);
        Assert.Equal("Home Assistant respondeu 500", (await ha.ToggleAsync("switch.tv")).Message);
    }

    [Fact]
    public async Task Token_invalido_e_servidor_fora()
    {
        var (fx, http, ha) = await Setup();
        await using var _ = http;
        using var __ = fx;
        await ha.RefreshAsync(TestContext.Current.CancellationToken);
        http.Status("GET /api/config", 401);
        await ha.RefreshAsync(TestContext.Current.CancellationToken);
        var s = fx.Hub.Require("homeassistant");
        Assert.False(s.GetProperty("online").GetBoolean());
        Assert.Equal("Token do Home Assistant inválido", s.GetProperty("error").GetString());
        Assert.Equal("2026.10.2", s.GetProperty("version").GetString()); // mantém o último conhecido

        fx.Options.HomeAssistant.Url = "http://127.0.0.1:1";
        await ha.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Equal("Sem conexão com o serviço", fx.Hub.Require("homeassistant").GetProperty("error").GetString());
        Assert.Equal("Sem conexão com o serviço", (await ha.ToggleAsync("light.sala")).Message);
    }

    [Fact]
    public void Mensagens_amigaveis()
    {
        Assert.Equal("Tempo esgotado", HomeAssistantModule.Friendly(new TaskCanceledException()));
        Assert.Equal("x", HomeAssistantModule.Friendly(new Exception("x")));
    }

    [Fact]
    public async Task Desativado()
    {
        using var fx = new Fx();
        var ha = new HomeAssistantModule(fx.Hub, fx.Alerts, fx.Monitor, Fx.Log<HomeAssistantModule>());
        await ha.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Equal(!OperatingSystem.IsLinux(), fx.Hub.Require("homeassistant").GetProperty("configured").GetBoolean());
        Assert.True((await ha.ToggleAsync("light.sala")).Ok);
    }
}

public class JellyfinTests
{
    const string Sessions = """
        [
          {"UserName":"joao","Client":"Android TV","DeviceName":"Sala",
           "NowPlayingItem":{"Name":"Piloto","Type":"Episode","SeriesName":"Dark","ParentIndexNumber":1,"IndexNumber":1,"RunTimeTicks":1000},
           "PlayState":{"PositionTicks":250,"IsPaused":true,"PlayMethod":"Transcode"}},
          {"UserName":"ana","NowPlayingItem":{"Name":"Filme","Type":"Movie"}},
          {"UserName":"ocioso"}
        ]
        """;

    [Fact]
    public async Task Servidor_biblioteca_e_sessoes()
    {
        await using var http = await FakeHttp.StartAsync();
        http.Json("GET /System/Info", """{"ServerName":"Bureau","Version":"10.11.2"}""")
            .Json("GET /Items/Counts", """{"MovieCount":12,"SeriesCount":3,"EpisodeCount":0}""")
            .Json("GET /Sessions", Sessions);
        using var fx = new Fx(o => { o.Jellyfin.Enabled = true; o.Jellyfin.Url = http.Url; o.Jellyfin.ApiKey = " KEY "; });
        var jf = new JellyfinModule(fx.Hub, fx.Alerts, fx.Monitor, Fx.Log<JellyfinModule>());
        await jf.RefreshAsync(TestContext.Current.CancellationToken);

        var s = fx.Hub.Require("jellyfin");
        Assert.Equal(("Bureau", "10.11.2"), (s.GetProperty("name").GetString(), s.GetProperty("version").GetString()));
        Assert.Equal(["Filmes", "Séries"], s.GetProperty("library").EnumerateArray().Select(x => x.GetProperty("name").GetString()));
        var sess = s.GetProperty("sessions");
        Assert.Equal(2, sess.GetArrayLength());
        Assert.Equal("Dark · T01E01 Piloto", sess[0].GetProperty("title").GetString());
        Assert.True(sess[0].GetProperty("paused").GetBoolean());
        Assert.True(sess[0].GetProperty("transcoding").GetBoolean());
        Assert.Equal(25, sess[0].GetProperty("progress").GetDouble());
        Assert.Equal(0, sess[1].GetProperty("progress").GetDouble());
        Assert.Equal("MediaBrowser Token=\"KEY\"", http.Requests.First().Headers["Authorization"]);

        http.Status("GET /System/Info", 401);
        await jf.RefreshAsync(TestContext.Current.CancellationToken);
        s = fx.Hub.Require("jellyfin");
        Assert.Equal("Chave de API do Jellyfin inválida", s.GetProperty("error").GetString());
        Assert.Equal(2, s.GetProperty("library").GetArrayLength()); // mantém a última biblioteca
    }

    [Fact]
    public async Task Desativado()
    {
        using var fx = new Fx();
        await new JellyfinModule(fx.Hub, fx.Alerts, fx.Monitor, Fx.Log<JellyfinModule>()).RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Equal(!OperatingSystem.IsLinux(), fx.Hub.Require("jellyfin").GetProperty("configured").GetBoolean());
    }
}

public class TerminalTests
{
    [Fact]
    public void Desligado_por_padrao_e_exige_usuario_no_linux()
    {
        using var fx = new Fx();
        var term = new TerminalModule(fx.Monitor, Fx.Log<TerminalModule>());
        Assert.False(term.Enabled);
        Assert.Null(term.IssueTicket());

        fx.Options.Terminal.Enabled = true;
        Assert.Equal(!OperatingSystem.IsLinux(), term.Enabled);
        fx.Options.Terminal.User = "joao";
        Assert.True(term.Enabled);
        Assert.NotEqual(term.IssueTicket(), term.IssueTicket());
    }
}

public class SystemMonitorTests
{
    [Fact]
    public async Task Publica_amostras_host_e_historico()
    {
        using var fx = new Fx();
        var mon = new SystemMonitor(fx.Hub, fx.Alerts, new MetricsStore(fx.Dir.Path), Fx.Log<SystemMonitor>());
        await mon.StartAsync(TestContext.Current.CancellationToken);
        for (var i = 0; i < 40 && fx.Hub.Last("sys") is null; i++) await Task.Delay(100, TestContext.Current.CancellationToken);
        await Task.Delay(1200, TestContext.Current.CancellationToken); // 2ª amostra (CPU por diferença)
        await mon.StopAsync(TestContext.Current.CancellationToken);

        var sys = fx.Hub.Require("sys");
        Assert.True(sys.GetProperty("memTotal").GetInt64() > 0);
        Assert.False(string.IsNullOrEmpty(fx.Hub.Require("host").GetProperty("hostname").GetString()));
        var hist = fx.Hub.Require("history");
        Assert.True(hist.GetProperty("cpu").GetArrayLength() >= 1);
    }

    [Fact]
    public void Leitura_direta_do_proc_e_sys()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Lê /proc e /sys");
        var probe = new LinuxProbe();
        var a = probe.Sample();
        Thread.Sleep(200);
        var b = probe.Sample();
        Assert.True(b.MemTotal > 0 && b.MemUsed > 0);
        Assert.Equal(Environment.ProcessorCount, b.Cores.Length);
        Assert.InRange(b.Cpu, 0, 100);
        Assert.Equal(3, a.Load.Length);
        Assert.True(b.Uptime > 0);

        var h = probe.ReadHost();
        Assert.Equal(Environment.ProcessorCount, h.Cores);
        Assert.False(string.IsNullOrEmpty(h.Kernel));
        Assert.NotEmpty(h.Disks);
    }
}

public class CollectorsTests
{
    [Fact]
    public async Task Atualiza_todos_os_modulos_em_segundo_plano()
    {
        using var fx = new Fx(o => { o.SystemUpdates = false; o.Docker.Enabled = false; });
        var watch = new WatchStore(fx.Opt);
        var collectors = new Collectors(fx.Hub,
            new ServicesModule(fx.Hub, watch, fx.Alerts),
            new DockerModule(fx.Hub, fx.Alerts, fx.Opt),
            fx.PiHole, fx.Network,
            new AptModule(fx.Hub, fx.Alerts, fx.Monitor),
            new BackupsModule(fx.Hub, fx.Alerts, fx.Monitor),
            new HomeAssistantModule(fx.Hub, fx.Alerts, fx.Monitor, Fx.Log<HomeAssistantModule>()),
            new JellyfinModule(fx.Hub, fx.Alerts, fx.Monitor, Fx.Log<JellyfinModule>()),
            new MonitorsModule(fx.Hub, fx.Alerts, fx.Opt),
            new DeviceBlocks(fx.Hub, fx.PiHole, fx.Opt),
            Fx.Log<Collectors>());

        await collectors.StartAsync(TestContext.Current.CancellationToken);
        string[] topics = ["services", "containers", "pihole", "network", "apt", "backups", "homeassistant", "jellyfin", "blocks"];
        for (var i = 0; i < 100 && topics.Any(t => fx.Hub.Last(t) is null); i++) await Task.Delay(100, TestContext.Current.CancellationToken);
        using (fx.Hub.Subscribe()) await Task.Delay(100, TestContext.Current.CancellationToken); // cliente novo acorda os coletores
        await collectors.StopAsync(TestContext.Current.CancellationToken);

        Assert.All(topics, t => Assert.NotNull(fx.Hub.Last(t)));
    }
}
