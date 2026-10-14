using System.Text.Json;
using System.Text.RegularExpressions;
using Bureau.Infra;
using Microsoft.Extensions.Options;

namespace Bureau.Modules;

public sealed partial class ServicesModule(EventHub hub, WatchStore watch, AlertEngine alerts)
{
    const string Props = "Id,Description,LoadState,ActiveState,SubState,MainPID,MemoryCurrent,ActiveEnterTimestamp";

    // Nomes reais (Id) dos serviços monitorados. Apelidos como "sshd" → "ssh.service" no Ubuntu.
    volatile string[] _resolved = [];

    bool Allowed(string unit) => watch.Contains(unit) || Array.IndexOf(_resolved, unit) >= 0;

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        ServiceInfo[] items;
        if (!OperatingSystem.IsLinux()) items = DevFakes.Services(watch.Units);
        else if (watch.Units.Length == 0) items = [];
        else
        {
            // Uma única chamada ao systemctl para todos os serviços monitorados.
            var args = new List<string> { "show", "--no-pager", "--timestamp=unix", "--property=" + Props };
            args.AddRange(watch.Units);
            var r = await Proc.RunAsync("systemctl", args, ct: ct);
            if (!r.Ok && r.Err.Contains("timestamp"))
            {
                args.RemoveAt(2); // systemd antigo (< 248)
                r = await Proc.RunAsync("systemctl", args, ct: ct);
            }
            // Apelidos resolvem para o mesmo serviço: mostra cada um só uma vez.
            items = [.. Parse(r.Out).DistinctBy(i => i.Unit)];
            _resolved = [.. items.Select(i => i.Unit)];
        }
        hub.Publish("services", items, AppJson.Default.ServiceInfoArray);
        alerts.OnServices(items);
    }

    public async Task<OkResponse> ActionAsync(string unit, string action)
    {
        if (!Allowed(unit)) return new(false, "Serviço não monitorado");
        if (action is not ("start" or "stop" or "restart")) return new(false, "Ação inválida");
        alerts.Expect($"svc:{unit}");

        if (!OperatingSystem.IsLinux())
        {
            await Task.Delay(400);
            DevFakes.ServiceAction(unit, action);
            await RefreshAsync();
            return new(true, null);
        }
        var r = await Proc.RunAsync("systemctl", ["--no-ask-password", action, unit], 90_000);
        await RefreshAsync();
        return r.Ok ? new(true, null) : new(false, r.Message);
    }

    public async Task<string> LogsAsync(string unit)
    {
        if (!Allowed(unit)) return "Serviço não monitorado.";
        if (!OperatingSystem.IsLinux())
            return string.Join('\n', Enumerable.Range(0, 40).Select(i =>
                $"{DateTime.Now.AddSeconds(i * 37 - 1500):yyyy-MM-ddTHH:mm:ss} velho-thinkpad {unit}[812]: linha de log simulada #{i}"));
        var r = await Proc.RunAsync("journalctl", ["--no-pager", "-o", "short-iso", "-n", "300", "-u", unit]);
        return r.Ok ? r.Out : r.Message;
    }

    public async Task<UnitEntry[]> ListAvailableAsync()
    {
        if (!OperatingSystem.IsLinux()) return DevFakes.AvailableUnits();
        var r = await Proc.RunAsync("systemctl", ["list-units", "--type=service", "--all", "--plain", "--no-legend", "--no-pager"]);
        return r.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => UnitLine().Match(l.TrimStart('●', ' ')))
            .Where(m => m.Success)
            .Select(m => new UnitEntry(m.Groups[1].Value, m.Groups[5].Value.Trim(), m.Groups[3].Value))
            .OrderBy(u => u.Unit)
            .ToArray();
    }

    static ServiceInfo[] Parse(string output)
    {
        var list = new List<ServiceInfo>();
        foreach (var block in output.Replace("\r", "").Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var p = new Dictionary<string, string>();
            foreach (var line in block.Split('\n'))
            {
                var eq = line.IndexOf('=');
                if (eq > 0) p[line[..eq]] = line[(eq + 1)..];
            }
            if (!p.TryGetValue("Id", out var id)) continue;

            var active = p.GetValueOrDefault("LoadState") == "not-found" ? "not-found" : p.GetValueOrDefault("ActiveState", "unknown");
            long.TryParse(p.GetValueOrDefault("MemoryCurrent"), out var mem);
            if (mem < 0 || (ulong)mem == ulong.MaxValue) mem = 0;
            var ts = p.GetValueOrDefault("ActiveEnterTimestamp", "");
            long since = ts.StartsWith('@') && long.TryParse(ts[1..], out var unix) ? unix : 0;

            list.Add(new ServiceInfo(id, p.GetValueOrDefault("Description", ""), active,
                p.GetValueOrDefault("SubState", ""), int.TryParse(p.GetValueOrDefault("MainPID"), out var pid) ? pid : 0,
                mem, active == "active" ? since : 0));
        }
        return [.. list];
    }

    // unit load active sub description
    [GeneratedRegex(@"^(\S+)\s+(\S+)\s+(\S+)\s+(\S+)\s*(.*)$")]
    private static partial Regex UnitLine();
}

/// <summary>Lista de serviços monitorados, persistida em DataDir/watch.json.</summary>
public sealed partial class WatchStore
{
    readonly string _path;
    volatile string[] _units;

    public WatchStore(IOptions<BureauOptions> o)
    {
        _path = Path.Combine(o.Value.DataDir, "watch.json");
        string[] units;
        try
        {
            units = File.Exists(_path)
                ? JsonSerializer.Deserialize(File.ReadAllText(_path), AppJson.Default.StringArray) ?? []
                : o.Value.Services;
        }
        catch { units = o.Value.Services; }
        _units = Normalize(units);
    }

    public string[] Units => _units;
    public bool Contains(string unit) => Array.IndexOf(_units, unit) >= 0;

    public void Set(string[] units)
    {
        _units = Normalize(units);
        File.WriteAllText(_path, JsonSerializer.Serialize(_units, AppJson.Default.StringArray));
    }

    static string[] Normalize(string[] units) => units
        .Select(u => u.Trim())
        .Where(u => u.Length is > 0 and <= 256 && UnitName().IsMatch(u))
        .Select(u => u.Contains('.') ? u : u + ".service")
        .Distinct()
        .Take(64)
        .ToArray();

    [GeneratedRegex(@"^[A-Za-z0-9@._:\\-]+$")]
    private static partial Regex UnitName();
}
