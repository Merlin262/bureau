using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Bureau.Modules;

/// <summary>Lê métricas direto de /proc e /sys — sem processos externos, custo de microssegundos.</summary>
public sealed class LinuxProbe : ISystemProbe
{
    static readonly HashSet<string> RealFs =
        ["ext2", "ext3", "ext4", "xfs", "btrfs", "zfs", "f2fs", "ntfs", "ntfs3", "fuseblk", "exfat", "vfat", "jfs", "bcachefs"];

    ulong[] _prevTotal = [], _prevIdle = [];
    long _prevRx, _prevTx, _prevTs;
    readonly Dictionary<string, bool> _physical = [];
    readonly string? _tempPath = FindTemp();
    readonly string? _batteryPath = FindBattery();

    public SystemSnapshot Sample()
    {
        var (cpu, cores) = ReadCpu();
        var mem = ReadKeyValues("/proc/meminfo");
        var memTotal = mem.GetValueOrDefault("MemTotal") * 1024;
        var memUsed = memTotal - mem.GetValueOrDefault("MemAvailable") * 1024;
        var swapTotal = mem.GetValueOrDefault("SwapTotal") * 1024;
        var swapUsed = swapTotal - mem.GetValueOrDefault("SwapFree") * 1024;

        var load = File.ReadAllText("/proc/loadavg").Split(' ');
        var uptime = double.Parse(File.ReadAllText("/proc/uptime").Split(' ')[0], CultureInfo.InvariantCulture);

        var (rx, tx) = ReadNet();
        var now = Stopwatch.GetTimestamp();
        double rxRate = 0, txRate = 0;
        if (_prevTs != 0)
        {
            var dt = Stopwatch.GetElapsedTime(_prevTs, now).TotalSeconds;
            if (dt > 0)
            {
                rxRate = Math.Max(0, (rx - _prevRx) / dt);
                txRate = Math.Max(0, (tx - _prevTx) / dt);
            }
        }
        (_prevRx, _prevTx, _prevTs) = (rx, tx, now);

        return new SystemSnapshot(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), cpu, cores,
            [Parse(load[0]), Parse(load[1]), Parse(load[2])],
            memTotal, memUsed, swapTotal, swapUsed,
            ReadTemp(), ReadBattery(), Math.Round(rxRate), Math.Round(txRate), uptime);
    }

    public HostInfo ReadHost()
    {
        var os = "Linux";
        foreach (var line in SafeLines("/etc/os-release"))
            if (line.StartsWith("PRETTY_NAME=")) os = line[12..].Trim('"');

        var cpuModel = SafeLines("/proc/cpuinfo").FirstOrDefault(l => l.StartsWith("model name"))?.Split(':', 2)[1].Trim() ?? "CPU";

        var addresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                        && !n.Name.StartsWith("docker") && !n.Name.StartsWith("veth") && !n.Name.StartsWith("br-"))
            .SelectMany(n => n.GetIPProperties().UnicastAddresses
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => $"{n.Name} {a.Address}"))
            .ToArray();

        var disks = DriveInfo.GetDrives()
            .Where(d =>
            {
                try
                {
                    return d.IsReady && RealFs.Contains(d.DriveFormat) && d.TotalSize > 1_000_000_000
                           && !d.Name.StartsWith("/snap") && !d.Name.StartsWith("/boot") && !d.Name.StartsWith("/var/lib/docker");
                }
                catch { return false; }
            })
            .Select(d => new DiskInfo(d.Name, d.DriveFormat, d.TotalSize, d.TotalSize - d.AvailableFreeSpace))
            .DistinctBy(d => (d.Total, d.Used))
            .ToArray();

        return new HostInfo(Environment.MachineName, os, SafeRead("/proc/sys/kernel/osrelease") ?? "",
            cpuModel, Environment.ProcessorCount, addresses, disks, Api.Version);
    }

    (double Total, double[] Cores) ReadCpu()
    {
        var totals = new List<ulong>(16);
        var idles = new List<ulong>(16);
        foreach (var line in File.ReadLines("/proc/stat"))
        {
            if (!line.StartsWith("cpu")) break;
            var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            ulong total = 0;
            for (var k = 1; k < f.Length && k <= 8; k++) total += ulong.Parse(f[k]);
            totals.Add(total);
            idles.Add(ulong.Parse(f[4]) + (f.Length > 5 ? ulong.Parse(f[5]) : 0)); // idle + iowait
        }

        var pct = new double[totals.Count];
        if (_prevTotal.Length == totals.Count)
        {
            for (var i = 0; i < pct.Length; i++)
            {
                var dt = totals[i] - _prevTotal[i];
                var di = idles[i] - _prevIdle[i];
                pct[i] = dt == 0 ? 0 : Math.Round(100.0 * (dt - di) / dt, 1);
            }
        }
        _prevTotal = [.. totals];
        _prevIdle = [.. idles];
        return (pct[0], pct[1..]);
    }

    (long Rx, long Tx) ReadNet()
    {
        long rx = 0, tx = 0;
        if (_physical.Count > 256) _physical.Clear();
        foreach (var line in File.ReadLines("/proc/net/dev").Skip(2))
        {
            var c = line.IndexOf(':');
            if (c < 0) continue;
            var name = line[..c].Trim();
            if (!_physical.TryGetValue(name, out var phys))
                _physical[name] = phys = Directory.Exists($"/sys/class/net/{name}/device");
            if (!phys) continue;
            var f = line[(c + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            rx += long.Parse(f[0]);
            tx += long.Parse(f[8]);
        }
        return (rx, tx);
    }

    double? ReadTemp()
    {
        if (_tempPath is null || SafeRead(_tempPath) is not { } raw || !long.TryParse(raw, out var milli)) return null;
        return Math.Round(milli / 1000.0, 1);
    }

    BatteryInfo? ReadBattery()
    {
        if (_batteryPath is null || !int.TryParse(SafeRead(_batteryPath + "/capacity"), out var pct)) return null;
        return new BatteryInfo(pct, SafeRead(_batteryPath + "/status") ?? "Unknown");
    }

    static string? FindTemp()
    {
        try
        {
            string? any = null;
            foreach (var z in Directory.EnumerateDirectories("/sys/class/thermal", "thermal_zone*"))
            {
                if (SafeRead(z + "/type") == "x86_pkg_temp") return z + "/temp";
                if (any is null && File.Exists(z + "/temp")) any = z + "/temp";
            }
            foreach (var h in Directory.EnumerateDirectories("/sys/class/hwmon"))
                if (SafeRead(h + "/name") is "coretemp" or "k10temp" or "zenpower" && File.Exists(h + "/temp1_input"))
                    return h + "/temp1_input";
            return any;
        }
        catch { return null; }
    }

    static string? FindBattery()
    {
        try
        {
            return Directory.EnumerateDirectories("/sys/class/power_supply")
                .FirstOrDefault(d => SafeRead(d + "/type") == "Battery" && File.Exists(d + "/capacity"));
        }
        catch { return null; }
    }

    static Dictionary<string, long> ReadKeyValues(string path)
    {
        var d = new Dictionary<string, long>();
        foreach (var line in File.ReadLines(path))
        {
            var c = line.IndexOf(':');
            if (c < 0) continue;
            var v = line[(c + 1)..].Trim().Split(' ')[0];
            if (long.TryParse(v, out var n)) d[line[..c]] = n;
        }
        return d;
    }

    static double Parse(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    static string? SafeRead(string path)
    {
        try { return File.ReadAllText(path).Trim(); } catch { return null; }
    }

    static IEnumerable<string> SafeLines(string path)
    {
        try { return File.ReadAllLines(path); } catch { return []; }
    }
}
