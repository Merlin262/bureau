using System.Collections.Concurrent;
using System.Diagnostics;
using Bureau.Infra;
using Bureau.Push;
using Microsoft.Extensions.Options;

namespace Bureau.Modules;

/// <summary>
/// Observa o estado publicado pelos módulos e dispara alertas nas transições
/// (ex.: ativo → falha), com histerese e intervalo mínimo entre repetições.
/// </summary>
public sealed class AlertEngine
{
    const int Keep = 50;
    static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(5);

    readonly EventHub _hub;
    readonly Notifier _notifier;
    readonly AlertOutbox _outbox;
    readonly IOptionsMonitor<BureauOptions> _opts;
    readonly ILogger<AlertEngine> _log;
    readonly string _path;
    readonly Lock _lock = new();
    readonly List<AlertEvent> _recent;

    readonly ConcurrentDictionary<string, DateTime> _expected = new();
    readonly ConcurrentDictionary<string, DateTime> _lastSent = new();
    readonly Dictionary<string, string> _services = [];
    readonly HashSet<string> _servicesDown = [];
    readonly Dictionary<string, string> _containers = [];
    readonly HashSet<string> _containersDown = [];
    readonly HashSet<string> _disksFull = [];
    readonly Dictionary<string, string> _backups = [];
    readonly Dictionary<string, int> _offline = [];
    readonly HashSet<string> _down = [];
    int _securityUpdates = -1;
    bool? _rebootRequired; // null = ainda não observado (não alerta no início)
    DateTime? _dischargeSince, _hotSince;
    bool _outage, _lowBattery, _hot, _piDown;
    int _piFailures;

    readonly TimeProvider _time;

    public AlertEngine(EventHub hub, Notifier notifier, AlertOutbox outbox, IOptionsMonitor<BureauOptions> opts, ILogger<AlertEngine> log,
        TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _hub = hub;
        _notifier = notifier;
        _outbox = outbox;
        _opts = opts;
        _log = log;
        _path = Path.Combine(opts.CurrentValue.DataDir, "alerts.json");
        _recent = [.. JsonFile.Load(_path, AppJson.Default.AlertEventArray) ?? []];
        PublishRecent();
    }

    AlertOptions O => _opts.CurrentValue.Alerts;

    DateTime Now => _time.GetUtcNow().UtcDateTime;

    /// <summary>Marca uma mudança como intencional (ação feita pela interface) para não gerar alerta.</summary>
    public void Expect(string key, TimeSpan? window = null) => _expected[key] = Now + (window ?? TimeSpan.FromSeconds(90));

    bool IsExpected(string key) => _expected.TryGetValue(key, out var until) && until > Now;

    // ── Serviços ─────────────────────────────────────────
    public void OnServices(ServiceInfo[] items)
    {
        lock (_lock)
        {
            foreach (var s in items)
            {
                var name = s.Unit.Replace(".service", "");
                var down = s.Active is "failed" or "inactive" || (s.Active == "activating" && s.Sub == "auto-restart");
                var had = _services.TryGetValue(s.Unit, out var prev);
                _services[s.Unit] = s.Active;
                if (!had || IsExpected($"svc:{s.Unit}")) { if (!down) _servicesDown.Remove(s.Unit); continue; }

                if (down && prev == "active" && _servicesDown.Add(s.Unit))
                    Raise($"svc:{s.Unit}", "service", "crit", $"{name} caiu",
                        s.Active == "failed" ? $"O serviço {name} falhou." : $"O serviço {name} parou ({s.Sub}).", "servicos");
                else if (s.Active == "active" && _servicesDown.Remove(s.Unit))
                    Raise($"svc-ok:{s.Unit}", "service", "ok", $"{name} voltou", $"O serviço {name} está ativo novamente.", "servicos");
            }
        }
    }

    // ── Contêineres ──────────────────────────────────────
    public void OnContainers(ContainersState state)
    {
        if (!state.Available) return;
        lock (_lock)
        {
            foreach (var c in state.Items)
            {
                var had = _containers.TryGetValue(c.Id, out var prev);
                _containers[c.Id] = c.State;
                var down = c.State is "exited" or "dead" or "restarting";
                if (!had || IsExpected($"ctr:{c.Id}")) { if (!down) _containersDown.Remove(c.Id); continue; }

                if (down && prev == "running" && _containersDown.Add(c.Id))
                    Raise($"ctr:{c.Id}", "container", "crit", $"{c.Name} parou", $"Contêiner {c.Name}: {c.Status}.", "docker");
                else if (c.State == "running" && _containersDown.Remove(c.Id))
                    Raise($"ctr-ok:{c.Id}", "container", "ok", $"{c.Name} voltou", $"O contêiner {c.Name} está rodando novamente.", "docker");
            }
        }
    }

    // ── Energia e temperatura (a cada segundo) ───────────
    public void OnSystem(SystemSnapshot s)
    {
        var now = Now;
        lock (_lock)
        {
            if (s.Battery is { } b)
            {
                if (b.Status == "Discharging")
                {
                    _dischargeSince ??= now;
                    if (!_outage && now - _dischargeSince >= TimeSpan.FromSeconds(15))
                    {
                        _outage = true;
                        Raise("power", "power", "crit", "Queda de energia", $"O servidor está funcionando na bateria ({b.Percent}%).", "painel");
                    }
                    if (_outage && !_lowBattery && b.Percent <= O.BatteryCritical)
                    {
                        _lowBattery = true;
                        Raise("battery", "battery", "crit", "Bateria crítica", $"Restam {b.Percent}%. Considere desligar o servidor com segurança.", "sistema");
                    }
                }
                else
                {
                    if (_outage) Raise("power-ok", "power", "ok", "Energia restabelecida", $"Alimentação externa de volta. Bateria em {b.Percent}%.", "painel");
                    _dischargeSince = null;
                    _outage = _lowBattery = false;
                }
            }

            if (s.TempC is { } t)
            {
                if (t >= O.TempCelsius)
                {
                    _hotSince ??= now;
                    if (!_hot && now - _hotSince >= TimeSpan.FromSeconds(60))
                    {
                        _hot = true;
                        Raise("temp", "temperature", "warn", "Temperatura alta", $"Processador a {t:0}°C há mais de um minuto.", "painel");
                    }
                }
                else if (t < O.TempCelsius - 5)
                {
                    _hotSince = null;
                    _hot = false;
                }
            }
        }
    }

    // ── Discos (a cada 30 s) ─────────────────────────────
    public void OnHost(HostInfo h)
    {
        lock (_lock)
        {
            foreach (var d in h.Disks)
            {
                if (d.Total <= 0) continue;
                var pct = 100.0 * d.Used / d.Total;
                if (pct >= O.DiskPercent && _disksFull.Add(d.Mount))
                    Raise($"disk:{d.Mount}", "disk", pct >= 97 ? "crit" : "warn", $"Disco {d.Mount} quase cheio",
                        $"{pct:0}% em uso — restam {Human(d.Total - d.Used)}.", "sistema");
                else if (pct < O.DiskPercent - 3)
                    _disksFull.Remove(d.Mount);
            }
        }
    }

    // ── Pi-hole ──────────────────────────────────────────
    public void OnPiHole(PiHoleState p)
    {
        if (!p.Configured) return;
        lock (_lock)
        {
            if (!p.Online)
            {
                if (++_piFailures == 3 && !_piDown)
                {
                    _piDown = true;
                    Raise("pihole", "pihole", "crit", "Pi-hole fora do ar", "O DNS da rede pode parar de responder.", "pihole");
                }
            }
            else
            {
                if (_piDown) Raise("pihole-ok", "pihole", "ok", "Pi-hole voltou", "O DNS está respondendo novamente.", "pihole");
                _piFailures = 0;
                _piDown = false;
            }
        }
    }

    /// <summary>Somente em desenvolvimento: dispara um alerta de exemplo pelo fluxo real (registro + push).</summary>
    public void Simulate(string kind)
    {
        lock (_lock)
        {
            var key = $"sim:{kind}:{Now.Ticks}";
            switch (kind)
            {
                case "power": Raise(key, "power", "crit", "Queda de energia", "O servidor está funcionando na bateria (87%).", "painel"); break;
                case "disk": Raise(key, "disk", "warn", "Disco / quase cheio", "92% em uso — restam 18 GB.", "sistema"); break;
                case "ok": Raise(key, "service", "ok", "cups voltou", "O serviço cups está ativo novamente.", "servicos"); break;
                default: Raise(key, "service", "crit", "cups caiu", "O serviço cups falhou.", "servicos"); break;
            }
        }
    }

    // ── Monitores de disponibilidade ─────────────────────
    public void OnMonitor(bool internet, string name, bool up, long downSeconds, string? error)
    {
        lock (_lock)
        {
            var dur = downSeconds < 120 ? $"{downSeconds} s" : downSeconds < 7200 ? $"{downSeconds / 60} min" : $"{downSeconds / 3600.0:0.#} h";
            if (internet)
            {
                // Com a internet fora o push provavelmente não sai; o aviso de volta informa a duração.
                if (!up) Raise("internet", "internet", "crit", "Internet caiu", "O servidor perdeu a conexão com a internet.", "rede");
                else Raise("internet-ok", "internet", "ok", "Internet voltou", $"A conexão ficou fora por {dur}.", "rede");
            }
            else if (!up) Raise($"mon:{name}", "monitor", "crit", $"{name} fora do ar", error ?? "Sem resposta.", "rede");
            else Raise($"mon-ok:{name}", "monitor", "ok", $"{name} voltou", $"Ficou fora por {dur}.", "rede");
        }
    }

    // ── Rede ─────────────────────────────────────────────
    public void OnNewDevice(DeviceInfo d)
    {
        lock (_lock)
        {
            var what = d.Vendor ?? (d.RandomMac ? "celular/tablet (MAC aleatório)" : "fabricante desconhecido");
            Raise($"newdev:{d.Mac}", "network", "warn", "Aparelho novo na rede", $"{d.Name ?? what} · {d.Ip} · {d.Mac}", "rede");
        }
    }

    // ── Acessos ao BUREAU ────────────────────────────────
    public void OnLogin(AccessEntry e, bool knownIp, int recentFailures)
    {
        lock (_lock)
        {
            var where = e.Country is null ? e.Ip : $"{e.Ip} ({e.Country})";
            if (e.Ok && !knownIp && !AccessLog.IsLocal(e.Ip))
                Raise($"login-new:{e.Ip}", "access", "warn", "Novo acesso ao BUREAU",
                    $"Login de um IP nunca visto: {where} · {e.Device}.", "sistema");
            if (!e.Ok && recentFailures == 5)
                Raise("login-fail", "access", "crit", "Tentativas de login falhas",
                    $"5 senhas erradas em 10 min (última de {where}).", "sistema");
        }
    }

    // ── Integrações (Home Assistant, Jellyfin…) ──────────
    public void OnOnline(string key, string name, bool configured, bool online, string view)
    {
        if (!configured) return;
        lock (_lock)
        {
            if (!online)
            {
                var n = _offline[key] = _offline.GetValueOrDefault(key) + 1;
                if (n == 3 && _down.Add(key))
                    Raise(key, key, "crit", $"{name} fora do ar", $"O {name} não está respondendo.", view);
            }
            else
            {
                _offline[key] = 0;
                if (_down.Remove(key)) Raise($"{key}-ok", key, "ok", $"{name} voltou", $"O {name} está respondendo novamente.", view);
            }
        }
    }

    // ── Backups ──────────────────────────────────────────
    public void OnBackups(BackupInfo[] items)
    {
        lock (_lock)
        {
            foreach (var b in items)
            {
                var had = _backups.TryGetValue(b.Unit, out var prev);
                _backups[b.Unit] = b.Status;
                if (!had || prev == b.Status) continue;
                if (b.Status == "failed")
                    Raise($"bk:{b.Unit}", "backup", "crit", $"Backup {b.Name} falhou", b.Message ?? "A última execução terminou com erro.", "sistema");
                else if (b.Status == "stale")
                    Raise($"bk-old:{b.Unit}", "backup", "warn", $"Backup {b.Name} atrasado", b.Message ?? "Sem execução bem-sucedida recente.", "sistema");
                else if (b.Status == "ok" && prev is "failed" or "stale")
                    Raise($"bk-ok:{b.Unit}", "backup", "ok", $"Backup {b.Name} em dia", "Execução concluída com sucesso.", "sistema");
            }
        }
    }

    // ── Pacotes do sistema ───────────────────────────────
    public void OnApt(AptState s)
    {
        if (!s.Supported || s.CheckedAt == 0) return;
        lock (_lock)
        {
            if (_securityUpdates == 0 && s.Security > 0)
                Raise("apt-sec", "updates", "warn", "Atualizações de segurança",
                    $"{s.Security} pacote(s) com correções de segurança aguardando instalação.", "sistema");
            _securityUpdates = s.Security;

            if (s.RebootRequired && _rebootRequired == false)
                Raise("reboot", "updates", "warn", "Reinicialização necessária",
                    "Pacotes atualizados (ex.: kernel) só valem após reiniciar o servidor.", "sistema");
            _rebootRequired = s.RebootRequired;
        }
    }

    public Task<int> TestAsync(string? deviceId) => _notifier.SendAsync(
        new AlertEvent(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "test", "ok", "Teste do BUREAU",
            "As notificações estão funcionando.", "sistema"), deviceId);

    void Raise(string key, string kind, string level, string title, string body, string view)
    {
        var now = Now;
        if (_lastSent.TryGetValue(key, out var last) && now - last < Cooldown) return;
        _lastSent[key] = now;

        var alert = new AlertEvent(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), kind, level, title, body, view);
        _recent.Add(alert);
        if (_recent.Count > Keep) _recent.RemoveRange(0, _recent.Count - Keep);
        try { JsonFile.Save(_path, _recent.ToArray(), AppJson.Default.AlertEventArray); } catch { /* disco cheio, p.ex. */ }
        PublishRecent();

        _log.LogWarning("ALERTA [{Level}] {Title} — {Body}", level, title, body);
        Telemetry.AlertsRaised.Add(1, new TagList { { "kind", kind }, { "level", level } });
        // Entrega fica com a outbox (durável, com retry): aqui só registra, sem rede dentro do lock.
        if (O.Enabled) _outbox.Enqueue(alert);
    }

    void PublishRecent() => _hub.Publish("alerts", _recent.AsEnumerable().Reverse().ToArray(), AppJson.Default.AlertEventArray);

    static string Human(long bytes)
    {
        string[] u = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        var i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return $"{v:0.#} {u[i]}";
    }
}
