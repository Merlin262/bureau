using System.Collections.Concurrent;
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
    DateTime? _dischargeSince, _hotSince;
    bool _outage, _lowBattery, _hot, _piDown;
    int _piFailures;

    public AlertEngine(EventHub hub, Notifier notifier, IOptionsMonitor<BureauOptions> opts, ILogger<AlertEngine> log)
    {
        _hub = hub;
        _notifier = notifier;
        _opts = opts;
        _log = log;
        _path = Path.Combine(opts.CurrentValue.DataDir, "alerts.json");
        _recent = [.. JsonFile.Load(_path, AppJson.Default.AlertEventArray) ?? []];
        PublishRecent();
    }

    AlertOptions O => _opts.CurrentValue.Alerts;

    /// <summary>Marca uma mudança como intencional (ação feita pela interface) para não gerar alerta.</summary>
    public void Expect(string key, TimeSpan? window = null) => _expected[key] = DateTime.UtcNow + (window ?? TimeSpan.FromSeconds(90));

    bool IsExpected(string key) => _expected.TryGetValue(key, out var until) && until > DateTime.UtcNow;

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
        var now = DateTime.UtcNow;
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
            var key = $"sim:{kind}:{DateTime.UtcNow.Ticks}";
            switch (kind)
            {
                case "power": Raise(key, "power", "crit", "Queda de energia", "O servidor está funcionando na bateria (87%).", "painel"); break;
                case "disk": Raise(key, "disk", "warn", "Disco / quase cheio", "92% em uso — restam 18 GB.", "sistema"); break;
                case "ok": Raise(key, "service", "ok", "cups voltou", "O serviço cups está ativo novamente.", "servicos"); break;
                default: Raise(key, "service", "crit", "cups caiu", "O serviço cups falhou.", "servicos"); break;
            }
        }
    }

    public Task<int> TestAsync(string? deviceId) => _notifier.SendAsync(
        new AlertEvent(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "test", "ok", "Teste do BUREAU",
            "As notificações estão funcionando.", "sistema"), deviceId);

    void Raise(string key, string kind, string level, string title, string body, string view)
    {
        var now = DateTime.UtcNow;
        if (_lastSent.TryGetValue(key, out var last) && now - last < Cooldown) return;
        _lastSent[key] = now;

        var alert = new AlertEvent(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), kind, level, title, body, view);
        _recent.Add(alert);
        if (_recent.Count > Keep) _recent.RemoveRange(0, _recent.Count - Keep);
        try { JsonFile.Save(_path, _recent.ToArray(), AppJson.Default.AlertEventArray); } catch { /* disco cheio, p.ex. */ }
        PublishRecent();

        _log.LogWarning("ALERTA [{Level}] {Title} — {Body}", level, title, body);
        if (O.Enabled) _ = _notifier.SendAsync(alert);
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
