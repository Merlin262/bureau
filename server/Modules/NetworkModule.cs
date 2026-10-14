using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Bureau.Infra;
using Microsoft.Extensions.Options;

namespace Bureau.Modules;

/// <summary>
/// Dispositivos da rede: tabela ARP do kernel + nomes do Pi-hole + DNS reverso,
/// fabricante pelo MAC (base do IEEE) e apelidos definidos pelo usuário (DataDir/aliases.json).
/// </summary>
public sealed class NetworkModule
{
    readonly EventHub _hub;
    readonly PiHoleModule _pihole;
    readonly string _aliasPath;
    readonly ConcurrentDictionary<string, (string? Name, DateTime At)> _dns = new();
    readonly ConcurrentDictionary<string, string> _aliases;
    DeviceInfo[] _last = [];

    public NetworkModule(EventHub hub, PiHoleModule pihole, IOptions<BureauOptions> o)
    {
        _hub = hub;
        _pihole = pihole;
        _aliasPath = Path.Combine(o.Value.DataDir, "aliases.json");
        _aliases = new(JsonFile.Load(_aliasPath, AppJson.Default.DictionaryStringString) ?? [], StringComparer.OrdinalIgnoreCase);
    }

    public bool SetAlias(string mac, string? name)
    {
        mac = (mac ?? "").Trim().ToLowerInvariant();
        if (mac.Length != 17 || mac.Count(c => c == ':') != 5) return false;
        name = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (name.Length == 0) _aliases.TryRemove(mac, out _);
        else _aliases[mac] = name[..Math.Min(name.Length, 40)];
        JsonFile.Save(_aliasPath, new Dictionary<string, string>(_aliases), AppJson.Default.DictionaryStringString);
        Publish(_last);
        return true;
    }

    void Publish(DeviceInfo[] raw)
    {
        _last = raw;
        _hub.Publish("network", [.. raw.Select(Decorate)], AppJson.Default.DeviceInfoArray);
    }

    DeviceInfo Decorate(DeviceInfo d) => d with
    {
        Alias = _aliases.GetValueOrDefault(d.Mac.ToLowerInvariant()),
        Vendor = d.Vendor ?? MacVendors.Lookup(d.Mac),
        RandomMac = MacVendors.IsRandom(d.Mac),
        Name = d.Name == "_gateway" ? "Roteador" : d.Name, // nome sintético do systemd-resolved
    };

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsLinux())
        {
            Publish(DevFakes.Devices());
            return;
        }

        var byMac = new Dictionary<string, DeviceInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadLines("/proc/net/arp").Skip(1))
        {
            // IP  HWtype  Flags  HWaddress  Mask  Device
            var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 6 || f[2] != "0x2" || f[3] == "00:00:00:00:00:00") continue;
            byMac[f[3]] = new DeviceInfo(f[0], f[3], null, null, true, f[5]);
        }

        var cutoff = DateTimeOffset.UtcNow.AddDays(-7).ToUnixTimeSeconds();
        foreach (var d in await _pihole.GetDevicesAsync(ct))
        {
            if (d.Mac.StartsWith("ip-") || d.Mac.Length != 17) continue;
            if (byMac.TryGetValue(d.Mac, out var cur))
                byMac[d.Mac] = cur with { Name = d.Name, Vendor = d.Vendor };
            else if (d.Ip is not null && d.LastQuery > cutoff)
                byMac[d.Mac] = new DeviceInfo(d.Ip, d.Mac, d.Name, d.Vendor, false, null);
        }

        var devices = await Task.WhenAll(byMac.Values.Select(async d =>
            d.Name is null ? d with { Name = await ResolveAsync(d.Ip) } : d));

        Publish([.. devices.OrderByDescending(d => d.Online).ThenBy(d => IpKey(d.Ip))]);
    }

    async Task<string?> ResolveAsync(string ip)
    {
        if (_dns.TryGetValue(ip, out var c) && c.At > DateTime.UtcNow.AddMinutes(-10)) return c.Name;
        string? name = null;
        try
        {
            using var cts = new CancellationTokenSource(800);
            var entry = await Dns.GetHostEntryAsync(ip, cts.Token);
            if (entry.HostName != ip) name = entry.HostName;
        }
        catch { /* sem PTR */ }
        _dns[ip] = (name, DateTime.UtcNow);
        return name;
    }

    static uint IpKey(string ip) =>
        IPAddress.TryParse(ip, out var a) && a.AddressFamily == AddressFamily.InterNetwork
            ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(a.GetAddressBytes())
            : uint.MaxValue;

    public static async Task<bool> WakeAsync(string mac)
    {
        var hex = (mac ?? "").Replace(":", "").Replace("-", "").Trim();
        if (hex.Length != 12 || !hex.All(Uri.IsHexDigit)) return false;
        var m = Convert.FromHexString(hex);

        var packet = new byte[6 + 16 * 6];
        Array.Fill(packet, (byte)0xFF, 0, 6);
        for (var i = 0; i < 16; i++) m.CopyTo(packet, 6 + i * 6);

        using var udp = new UdpClient { EnableBroadcast = true };
        await udp.SendAsync(packet, new IPEndPoint(IPAddress.Broadcast, 9));
        return true;
    }
}

/// <summary>
/// Atualiza cada módulo periodicamente: rápido enquanto alguém olha a interface,
/// devagar quando ninguém está conectado (o suficiente para os alertas de push).
/// </summary>
public sealed class Collectors(EventHub hub, ServicesModule services, DockerModule docker,
    PiHoleModule pihole, NetworkModule network, ILogger<Collectors> log) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken ct) => Task.WhenAll(
        Loop("services", services.RefreshAsync, 4, 15, ct),
        Loop("containers", docker.RefreshAsync, 4, 15, ct),
        Loop("pihole", pihole.RefreshAsync, 5, 30, ct),
        Loop("network", network.RefreshAsync, 20, 300, ct));

    async Task Loop(string name, Func<CancellationToken, Task> refresh, int activeSec, int idleSec, CancellationToken ct)
    {
        await Task.Yield();
        while (!ct.IsCancellationRequested)
        {
            var seconds = activeSec;
            try
            {
                await refresh(ct);
                seconds = hub.Count > 0 ? activeSec : idleSec;
                // Acorda antes do intervalo se um novo cliente conectar.
                await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(seconds), ct), hub.NextJoin);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e)
            {
                log.LogWarning(e, "Coletor {Name} falhou", name);
                await Task.Delay(TimeSpan.FromSeconds(seconds), ct).ContinueWith(_ => { });
            }
        }
    }
}
