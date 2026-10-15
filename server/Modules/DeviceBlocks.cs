using System.Globalization;
using Bureau.Infra;
using Microsoft.Extensions.Options;

namespace Bureau.Modules;

/// <summary>
/// Bloqueio de internet por aparelho: manual ("bloquear agora") e/ou por horário (ex.: 23:00–07:00, seg–sex).
/// As regras ficam em DataDir/blocks.json; a cada 30 s o estado desejado é aplicado no Pi-hole.
/// </summary>
public sealed class DeviceBlocks
{
    readonly EventHub _hub;
    readonly PiHoleModule _pihole;
    readonly string _path;
    readonly Lock _lock = new();
    readonly SemaphoreSlim _apply = new(1, 1); // Upsert/Remove aplicam em paralelo ao coletor
    List<BlockRule> _rules;
    readonly Dictionary<string, bool> _applied = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string?> _errors = new(StringComparer.OrdinalIgnoreCase);

    public DeviceBlocks(EventHub hub, PiHoleModule pihole, IOptions<BureauOptions> o)
    {
        _hub = hub;
        _pihole = pihole;
        _path = Path.Combine(o.Value.DataDir, "blocks.json");
        _rules = [.. JsonFile.Load(_path, AppJson.Default.BlockRuleArray) ?? []];
    }

    public OkResponse Upsert(BlockRule r)
    {
        var mac = (r.Mac ?? "").Trim().ToLowerInvariant();
        if (mac.Length != 17) return new(false, "MAC inválido");
        if ((r.Start is null) != (r.End is null) || (r.Start is not null && (!IsTime(r.Start) || !IsTime(r.End!))))
            return new(false, "Horário inválido (use HH:mm)");
        var days = (r.Days ?? []).Where(d => d is >= 0 and <= 6).Distinct().Order().ToArray();
        var rule = new BlockRule(mac, r.Label?.Trim(), r.Manual, days, r.Start, r.End);
        lock (_lock)
        {
            _rules.RemoveAll(x => x.Mac == mac);
            if (rule.Manual || rule.Start is not null) _rules.Add(rule); // regra vazia = remover
            Save();
        }
        _ = ApplyAsync();
        return new(true, null);
    }

    public OkResponse Remove(string mac)
    {
        lock (_lock)
        {
            _rules.RemoveAll(x => x.Mac == mac.ToLowerInvariant());
            Save();
        }
        _ = ApplyAsync();
        return new(true, "Bloqueio removido");
    }

    void Save() => JsonFile.Save(_path, _rules.ToArray(), AppJson.Default.BlockRuleArray);

    static bool IsTime(string s) => TimeOnly.TryParseExact(s, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    /// <summary>Janela ativa agora? Suporta faixas que atravessam a meia-noite (23:00–07:00).</summary>
    public static bool InWindow(BlockRule r, DateTime local)
    {
        if (r.Start is null || r.End is null) return false;
        var start = TimeOnly.ParseExact(r.Start, "HH:mm", CultureInfo.InvariantCulture);
        var end = TimeOnly.ParseExact(r.End, "HH:mm", CultureInfo.InvariantCulture);
        var now = TimeOnly.FromDateTime(local);
        var days = r.Days.Length == 0 ? [0, 1, 2, 3, 4, 5, 6] : r.Days;
        if (start <= end) return days.Contains((int)local.DayOfWeek) && now >= start && now < end;
        // Atravessa a meia-noite: o "dia" da regra é o dia em que a janela começou.
        return (now >= start && days.Contains((int)local.DayOfWeek))
            || (now < end && days.Contains((int)local.AddDays(-1).DayOfWeek));
    }

    public async Task ApplyAsync(CancellationToken ct = default)
    {
        await _apply.WaitAsync(ct);
        try { await ApplyCoreAsync(); }
        finally { _apply.Release(); }
    }

    async Task ApplyCoreAsync()
    {
        BlockRule[] rules;
        lock (_lock) rules = [.. _rules];
        var now = DateTime.Now;

        var desired = rules.ToDictionary(r => r.Mac, r => r.Manual || InWindow(r, now), StringComparer.OrdinalIgnoreCase);
        // Aparelhos que saíram da lista: libera uma última vez.
        foreach (var mac in _applied.Keys.Where(k => !desired.ContainsKey(k)).ToArray()) desired[mac] = false;

        if (_pihole.Available)
        {
            foreach (var (mac, want) in desired)
            {
                if (_applied.TryGetValue(mac, out var cur) && cur == want && _errors.GetValueOrDefault(mac) is null) continue;
                var err = await _pihole.SetClientBlockedAsync(mac, want);
                _errors[mac] = err;
                if (err is null) _applied[mac] = want;
                if (!want && !rules.Any(r => r.Mac == mac) && err is null) { _applied.Remove(mac); _errors.Remove(mac); }
            }
        }

        _hub.Publish("blocks", [.. rules.Select(r => new BlockState(r.Mac, r.Label, r.Manual, r.Days, r.Start, r.End,
            desired.GetValueOrDefault(r.Mac),
            _pihole.Available ? _errors.GetValueOrDefault(r.Mac) : "Requer o Pi-hole configurado"))], AppJson.Default.BlockStateArray);
    }
}
