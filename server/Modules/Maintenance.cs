using Bureau.Infra;
using Microsoft.Extensions.Options;

namespace Bureau.Modules;

/// <summary>
/// Atualizações de pacotes (apt): lista o que há para atualizar (a cada 6 h), aponta as de segurança e
/// "reinicialização necessária". A atualização roda como root no serviço bureau-apt-upgrade (criado pelo
/// instalador), que o BUREAU apenas dispara — o próprio BUREAU nunca roda como root.
/// </summary>
public sealed class AptModule(EventHub hub, AlertEngine alerts, IOptionsMonitor<BureauOptions> opts)
{
    public const string Unit = "bureau-apt-upgrade.service";

    string[] _packages = [];
    int _security;
    int _phased;
    long _checkedAt;
    bool _wasUpgrading;
    DateTime _nextList = DateTime.MinValue;

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        AptState state;
        if (!opts.CurrentValue.SystemUpdates) state = new(false, 0, 0, [], false, [], 0, false, null, 0);
        else if (!OperatingSystem.IsLinux()) state = DevFakes.Apt();
        else if (!File.Exists("/usr/bin/apt")) state = new(false, 0, 0, [], false, [], 0, false, "Sistema sem apt", 0);
        else
        {
            var (upgrading, result, lastRun) = await UnitStateAsync(ct);
            // Relista a cada 6 h e logo após terminar uma atualização.
            if (!upgrading && (DateTime.UtcNow >= _nextList || _wasUpgrading))
            {
                await ListAsync(ct);
                _nextList = DateTime.UtcNow.AddHours(6);
            }
            _wasUpgrading = upgrading;

            string[] rebootPkgs = [];
            var reboot = File.Exists("/var/run/reboot-required");
            if (reboot)
            {
                try { rebootPkgs = [.. File.ReadAllLines("/var/run/reboot-required.pkgs").Where(l => l.Length > 0).Distinct()]; }
                catch { /* lista opcional */ }
            }
            state = new AptState(true, _packages.Length, _security, _packages, reboot, rebootPkgs, _checkedAt, upgrading, result, lastRun, _phased);
        }
        hub.Publish("apt", state, AppJson.Default.AptState);
        alerts.OnApt(state);
    }

    async Task ListAsync(CancellationToken ct)
    {
        var r = await Proc.RunAsync("apt", ["list", "--upgradable"], 60_000, ct);
        var (names, security) = ParseUpgradable(r.Out);
        // Atualizações em liberação gradual do Ubuntu ("phased"): o apt as segura de propósito.
        var phased = await PhasedAsync(names, ct);
        _packages = [.. names.Where(n => !phased.Contains(n)).Order()];
        _phased = names.Count(phased.Contains);
        _security = security;
        _checkedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    /// <summary>Saída do "apt list --upgradable": nomes dos pacotes e quantos vêm do repositório de segurança.</summary>
    internal static (List<string> Names, int Security) ParseUpgradable(string output)
    {
        var names = new List<string>();
        var security = 0;
        foreach (var line in output.Split('\n'))
        {
            var slash = line.IndexOf('/');
            if (slash <= 0 || !line.Contains("upgradable")) continue;
            names.Add(line[..slash]);
            var space = line.IndexOf(' ', slash);
            if (space > slash && line[slash..space].Contains("-security")) security++;
        }
        return (names, security);
    }

    /// <summary>
    /// Pacotes em liberação gradual: o "apt-cache policy" marca a versão nova com "(phased N%)".
    /// Não depende do texto de saída do apt-get (que muda entre versões do apt).
    /// </summary>
    static async Task<HashSet<string>> PhasedAsync(IReadOnlyCollection<string> packages, CancellationToken ct)
    {
        if (packages.Count == 0) return [];
        var r = await Proc.RunAsync("apt-cache", ["policy", .. packages], 60_000, ct);
        return ParsePhased(r.Out);
    }

    internal static HashSet<string> ParsePhased(string output)
    {
        var set = new HashSet<string>();
        string? current = null;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]) && line.EndsWith(':')) current = line[..^1].Split(':')[0];
            else if (current is not null && line.Contains("(phased")) set.Add(current);
        }
        return set;
    }

    async Task<(bool Upgrading, string? Result, long LastRun)> UnitStateAsync(CancellationToken ct)
    {
        var r = await Proc.RunAsync("systemctl", ["show", Unit, "--timestamp=unix",
            "--property=LoadState,ActiveState,Result,ExecMainExitTimestamp"], 10_000, ct);
        var p = Props(r.Out);
        if (p.GetValueOrDefault("LoadState") != "loaded") return (false, "não instalado", 0);
        var upgrading = p.GetValueOrDefault("ActiveState") is "activating" or "active" or "reloading";
        return (upgrading, p.GetValueOrDefault("Result"), Unix(p.GetValueOrDefault("ExecMainExitTimestamp")));
    }

    public async Task<OkResponse> UpgradeAsync()
    {
        if (!OperatingSystem.IsLinux()) return new(true, "Simulado: atualização de pacotes iniciada");
        var r = await Proc.RunAsync("systemctl", ["--no-ask-password", "start", "--no-block", Unit], 10_000);
        await RefreshAsync();
        return r.Ok ? new(true, "Atualização de pacotes iniciada") : new(false, r.Message);
    }

    public async Task<string> LogsAsync()
    {
        if (!OperatingSystem.IsLinux()) return "Get:1 http://archive.ubuntu.com noble-updates InRelease\n(simulado)";
        var r = await Proc.RunAsync("journalctl", ["--no-pager", "-o", "short-iso", "-n", "400", "-u", Unit]);
        return r.Ok ? r.Out : r.Message;
    }

    public void Recheck() => _nextList = DateTime.MinValue;

    internal static Dictionary<string, string> Props(string output)
    {
        var d = new Dictionary<string, string>();
        foreach (var line in output.Split('\n'))
        {
            var eq = line.IndexOf('=');
            if (eq > 0) d[line[..eq]] = line[(eq + 1)..].Trim();
        }
        return d;
    }

    internal static long Unix(string? ts) => ts is ['@', ..] && long.TryParse(ts[1..], out var u) ? u : 0;
}

/// <summary>
/// Acompanha jobs de backup (serviços systemd): última execução, último sucesso, próxima execução (.timer)
/// e alerta se falhar ou ficar atrasado. O último sucesso é guardado em DataDir/backups.json.
/// </summary>
public sealed class BackupsModule
{
    readonly EventHub _hub;
    readonly AlertEngine _alerts;
    readonly IOptionsMonitor<BureauOptions> _opts;
    readonly string _path;
    readonly Dictionary<string, long> _lastSuccess;

    public BackupsModule(EventHub hub, AlertEngine alerts, IOptionsMonitor<BureauOptions> opts)
    {
        _hub = hub;
        _alerts = alerts;
        _opts = opts;
        _path = Path.Combine(opts.CurrentValue.DataDir, "backups.json");
        _lastSuccess = JsonFile.Load(_path, AppJson.Default.DictionaryStringInt64) ?? [];
    }

    static string Normalize(string unit) => unit.Contains('.') ? unit : unit + ".service";

    BackupJob[] Jobs => [.. _opts.CurrentValue.Backups.Where(j => !string.IsNullOrWhiteSpace(j.Unit))];

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsLinux() && Jobs.Length == 0)
        {
            _hub.Publish("backups", DevFakes.Backups(), AppJson.Default.BackupInfoArray);
            return;
        }

        var list = new List<BackupInfo>();
        var changed = false;
        foreach (var job in Jobs)
        {
            var unit = Normalize(job.Unit);
            var name = string.IsNullOrWhiteSpace(job.Name) ? unit.Replace(".service", "") : job.Name;
            if (!OperatingSystem.IsLinux())
            {
                list.Add(new BackupInfo(name, unit, "unknown", 0, 0, 0, "Disponível só no servidor Linux"));
                continue;
            }

            var p = AptModule.Props((await Proc.RunAsync("systemctl", ["show", unit, "--timestamp=unix",
                "--property=LoadState,ActiveState,Result,ExecMainStartTimestamp,ExecMainExitTimestamp"], 10_000, ct)).Out);
            var timer = AptModule.Props((await Proc.RunAsync("systemctl", ["show", unit.Replace(".service", ".timer"),
                "--timestamp=unix", "--property=LoadState,NextElapseUSecRealtime"], 10_000, ct)).Out);

            var lastRun = AptModule.Unix(p.GetValueOrDefault("ExecMainStartTimestamp"));
            var exited = AptModule.Unix(p.GetValueOrDefault("ExecMainExitTimestamp"));
            var result = p.GetValueOrDefault("Result");
            if (result == "success" && exited > _lastSuccess.GetValueOrDefault(unit))
            {
                _lastSuccess[unit] = exited;
                changed = true;
            }
            var lastOk = _lastSuccess.GetValueOrDefault(unit);
            var next = timer.GetValueOrDefault("LoadState") == "loaded" ? AptModule.Unix(timer.GetValueOrDefault("NextElapseUSecRealtime")) : 0;
            var age = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - lastOk;

            var (status, msg) = p.GetValueOrDefault("LoadState") != "loaded" ? ("unknown", "Serviço não encontrado")
                : p.GetValueOrDefault("ActiveState") is "activating" or "active" ? ("running", "Em execução")
                : lastRun > 0 && result is not (null or "success") ? ("failed", $"Última execução falhou ({result})")
                : lastOk == 0 ? ("unknown", "Nenhum sucesso registrado ainda")
                : age > job.MaxAgeHours * 3600L ? ("stale", $"Sem sucesso há mais de {job.MaxAgeHours} h")
                : ("ok", "Em dia");
            list.Add(new BackupInfo(name, unit, status, lastRun, lastOk, next, msg));
        }

        if (changed) JsonFile.Save(_path, _lastSuccess, AppJson.Default.DictionaryStringInt64);
        var items = list.ToArray();
        _hub.Publish("backups", items, AppJson.Default.BackupInfoArray);
        _alerts.OnBackups(items);
    }

    bool Allowed(string unit) => Jobs.Any(j => Normalize(j.Unit) == unit);

    public async Task<OkResponse> RunAsync(string unit)
    {
        if (!OperatingSystem.IsLinux()) return new(true, "Simulado: backup iniciado");
        if (!Allowed(unit)) return new(false, "Backup não configurado");
        var r = await Proc.RunAsync("systemctl", ["--no-ask-password", "start", "--no-block", unit], 10_000);
        await RefreshAsync();
        return r.Ok ? new(true, "Backup iniciado") : new(false, r.Message);
    }

    public async Task<string> LogsAsync(string unit)
    {
        if (!OperatingSystem.IsLinux()) return "restic: snapshot 4f2a9c1e saved\n(simulado)";
        if (!Allowed(unit)) return "Backup não configurado.";
        var r = await Proc.RunAsync("journalctl", ["--no-pager", "-o", "short-iso", "-n", "300", "-u", unit]);
        return r.Ok ? r.Out : r.Message;
    }
}
