using System.Diagnostics.CodeAnalysis;

namespace Bureau.Modules;

/// <summary>
/// Dados simulados usados quando o backend roda fora do Linux (desenvolvimento no Windows/macOS),
/// para que a interface possa ser desenvolvida sem um servidor real.
/// </summary>
[ExcludeFromCodeCoverage]
public static class DevFakes
{
    static readonly Dictionary<string, string> ServiceStates = new()
    {
        ["pihole-FTL.service"] = "active", ["docker.service"] = "active", ["ssh.service"] = "active",
        ["tailscaled.service"] = "active", ["cups.service"] = "failed",
    };
    static readonly Dictionary<string, string> ContainerStates = new()
    {
        ["homeassistant"] = "running", ["jellyfin"] = "running", ["uptime-kuma"] = "exited",
    };

    public static ServiceInfo[] Services(string[] units)
    {
        var since = DateTimeOffset.UtcNow.AddHours(-31).ToUnixTimeSeconds();
        return (units.Length > 0 ? units : [.. ServiceStates.Keys]).Select(u =>
        {
            var st = ServiceStates.GetValueOrDefault(u, "inactive");
            return new ServiceInfo(u, u.Replace(".service", "") + " daemon", st,
                st == "active" ? "running" : st == "failed" ? "failed" : "dead",
                st == "active" ? Random.Shared.Next(300, 9000) : 0,
                st == "active" ? Random.Shared.NextInt64(20, 300) * 1024 * 1024 : 0, since);
        }).ToArray();
    }

    public static UnitEntry[] AvailableUnits() =>
        [.. ServiceStates.Keys.Concat(["cron.service", "nginx.service", "smbd.service", "cloudflared.service"])
            .Select(u => new UnitEntry(u, u.Replace(".service", "") + " daemon", ServiceStates.GetValueOrDefault(u, "inactive")))];

    public static void ServiceAction(string unit, string action) =>
        ServiceStates[unit] = action == "stop" ? "inactive" : "active";

    public static ContainerInfo[] Containers()
    {
        var created = DateTimeOffset.UtcNow.AddDays(-12).ToUnixTimeSeconds();
        return ContainerStates.Select((kv, i) => new ContainerInfo(
            $"a1b2c3d4e5f{i}", kv.Key, kv.Key switch
            {
                "homeassistant" => "ghcr.io/home-assistant/home-assistant:stable",
                "jellyfin" => "jellyfin/jellyfin:latest",
                _ => "louislam/uptime-kuma:1",
            },
            kv.Value, kv.Value == "running" ? "Up 3 days" : "Exited (0) 2 hours ago",
            kv.Key switch { "jellyfin" => "8096→8096/tcp", "uptime-kuma" => "3001→3001/tcp", _ => "" },
            created)).ToArray();
    }

    public static void ContainerAction(string id, string action)
    {
        var name = ContainerStates.Keys.ElementAtOrDefault(id[^1] - '0');
        if (name is not null) ContainerStates[name] = action == "stop" ? "exited" : "running";
    }

    static bool _blocking = true;
    public static void SetBlocking(bool enabled) => _blocking = enabled;

    public static PiHoleState PiHole()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 600 * 600;
        var hist = Enumerable.Range(0, 144).Select(i =>
        {
            var hour = (i / 6 + 10) % 24;
            var total = (int)(40 + 260 * Math.Max(0, Math.Sin((hour - 6) / 24.0 * Math.PI * 2)) + Random.Shared.Next(0, 60));
            return new HistPoint(now - (143 - i) * 600, total, (int)(total * (0.12 + Random.Shared.NextDouble() * 0.15)));
        }).ToArray();
        var totalQ = hist.Sum(h => (long)h.Total);
        var blocked = hist.Sum(h => (long)h.Blocked);
        return new PiHoleState(true, true, null, _blocking, null, totalQ, blocked, Math.Round(100.0 * blocked / totalQ, 1),
            187_455, 9,
            [
                new("telemetry.microsoft.com", 812), new("graph.facebook.com", 604), new("ads.youtube.com", 455),
                new("app-measurement.com", 390), new("doubleclick.net", 288), new("tracking.tiktok.com", 201),
            ], hist);
    }

    public static AptState Apt() => new(true, 7, 2,
        ["base-files", "curl", "libcurl4t64", "linux-firmware", "openssl", "libssl3t64", "tzdata"],
        true, ["linux-image-6.12.48-amd64"], DateTimeOffset.UtcNow.AddMinutes(-42).ToUnixTimeSeconds(), false, "success",
        DateTimeOffset.UtcNow.AddDays(-3).ToUnixTimeSeconds(), 5);

    public static BackupInfo[] Backups()
    {
        var now = DateTimeOffset.UtcNow;
        return
        [
            new("Fotos (restic)", "restic-fotos.service", "ok", now.AddHours(-5).ToUnixTimeSeconds(), now.AddHours(-5).ToUnixTimeSeconds(), now.AddHours(19).ToUnixTimeSeconds(), "Em dia"),
            new("Configurações", "backup-config.service", "failed", now.AddHours(-2).ToUnixTimeSeconds(), now.AddDays(-2).ToUnixTimeSeconds(), now.AddHours(22).ToUnixTimeSeconds(), "Última execução falhou (exit-code)"),
        ];
    }

    static readonly Dictionary<string, string> HaStates = new()
    {
        ["light.sala"] = "on", ["light.quarto"] = "off", ["light.varanda"] = "off", ["switch.cafeteira"] = "off", ["fan.escritorio"] = "on",
    };

    public static void HaToggle(string id)
    {
        if (HaStates.TryGetValue(id, out var s)) HaStates[id] = s == "on" ? "off" : "on";
    }

    public static HaState HomeAssistant() => new(true, true, null, "2026.10.2", "Casa", 214, 3,
        [new("sensor", 96), new("binary_sensor", 31), new("light", 12), new("switch", 9), new("automation", 18)],
        [.. HaStates.Select(kv => new HaEntity(kv.Key, kv.Key.Split('.')[1] switch
            {
                "sala" => "Luz da sala", "quarto" => "Luz do quarto", "varanda" => "Luz da varanda",
                "cafeteira" => "Cafeteira", _ => "Ventilador do escritório",
            }, kv.Key.Split('.')[0], kv.Value, null))]);

    public static JfState Jellyfin() => new(true, true, null, "jellyfin", "10.11.2",
        [new("Filmes", 412), new("Séries", 57), new("Episódios", 2318)],
        [
            new("ana", "Ruptura · T02E04 Woe's Hollow", "Jellyfin Android TV", "TV da sala", false, 37.5, false),
            new("convidado", "Arrival", "Jellyfin Web", "Chrome", true, 81.2, true),
        ]);

    public static DnsQuery[] Queries()
    {
        string[] domains = ["www.google.com", "graph.facebook.com", "api.spotify.com", "ads.youtube.com", "telemetry.microsoft.com",
            "i.ytimg.com", "doubleclick.net", "github.com", "app-measurement.com", "connectivitycheck.gstatic.com", "netflix.com", "tracking.tiktok.com"];
        (string Ip, string Name)[] clients = [("192.168.0.10", "desktop-sala"), ("192.168.0.23", "celular"), ("192.168.0.31", "tv-sala")];
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return [.. Enumerable.Range(0, 60).Select(i =>
        {
            var d = domains[Random.Shared.Next(domains.Length)];
            var c = clients[Random.Shared.Next(clients.Length)];
            var blocked = d.Contains("ads") || d.Contains("telemetry") || d.Contains("doubleclick") || d.Contains("tracking") || d.Contains("measurement");
            return new DnsQuery(now - i * Random.Shared.Next(1, 9), d, i % 5 == 0 ? "AAAA" : "A", c.Ip, c.Name, blocked ? "GRAVITY" : i % 3 == 0 ? "CACHE" : "FORWARDED", blocked);
        })];
    }

    public static DeviceInfo[] Devices() =>
    [
        new("192.168.0.1", "a0:f4:79:11:22:33", "_gateway", null, true, "enp2s0", DnsQueries: 4210),
        new("192.168.0.10", "d8:bb:c1:44:55:66", "desktop-sala", null, true, "enp2s0", DnsQueries: 1880),
        new("192.168.0.23", "26:e5:db:77:88:99", null, null, true, "enp2s0", DnsQueries: 960),
        new("192.168.0.31", "50:13:95:aa:bb:cc", null, null, true, "enp2s0", DnsQueries: 12750),
        new("192.168.0.42", "b8:27:eb:3c:4d:5e", "raspberrypi", null, false, "enp2s0", DnsQueries: 0),
    ];
}

[ExcludeFromCodeCoverage]
public sealed class FakeProbe : ISystemProbe
{
    double _cpu = 18, _mem = 0.42, _rx = 120_000, _tx = 40_000, _bat = 87;
    readonly long _start = Environment.TickCount64 - 3_600_000L * 52;

    public SystemSnapshot Sample()
    {
        _cpu = Math.Clamp(_cpu + (Random.Shared.NextDouble() - 0.5) * 12, 2, 97);
        _mem = Math.Clamp(_mem + (Random.Shared.NextDouble() - 0.5) * 0.02, 0.2, 0.9);
        _rx = Math.Clamp(_rx * (0.75 + Random.Shared.NextDouble() * 0.5), 2_000, 8_000_000);
        _tx = Math.Clamp(_tx * (0.75 + Random.Shared.NextDouble() * 0.5), 1_000, 2_000_000);
        _bat = Math.Max(5, _bat - 0.001);
        const long memTotal = 8L * 1024 * 1024 * 1024;
        var cores = Enumerable.Range(0, 4).Select(_ => Math.Round(Math.Clamp(_cpu + (Random.Shared.NextDouble() - 0.5) * 30, 0, 100), 1)).ToArray();
        return new SystemSnapshot(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Math.Round(_cpu, 1), cores,
            [Math.Round(_cpu / 25, 2), 0.82, 0.64], memTotal, (long)(memTotal * _mem), 2L * 1024 * 1024 * 1024, 180L * 1024 * 1024,
            Math.Round(42 + _cpu / 4, 1), new BatteryInfo((int)_bat, "Full"), Math.Round(_rx), Math.Round(_tx),
            (Environment.TickCount64 - _start) / 1000.0);
    }

    public HostInfo ReadHost() => new("velho-thinkpad", "Debian GNU/Linux 13 (trixie)", "6.12.48-amd64",
        "Intel(R) Core(TM) i5-4300U CPU @ 1.90GHz", 4, ["enp2s0 192.168.0.5", "tailscale0 100.101.12.7"],
        [new("/", "ext4", 240L * 1000 * 1000 * 1000, 96L * 1000 * 1000 * 1000), new("/mnt/dados", "ext4", 1000L * 1000 * 1000 * 1000, 712L * 1000 * 1000 * 1000)],
        Api.Version);
}
