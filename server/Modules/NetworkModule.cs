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
    readonly AlertEngine _alerts;
    readonly string _aliasPath, _knownPath;
    readonly ConcurrentDictionary<string, long> _firstSeen;
    readonly ConcurrentDictionary<string, string> _lastIp = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, long> _lastSeen = new(StringComparer.OrdinalIgnoreCase);
    readonly string _seenPath;
    readonly ConcurrentDictionary<string, (string? Name, DateTime At)> _dns = new();
    readonly ConcurrentDictionary<string, string> _aliases;
    DeviceInfo[] _last = [];

    public NetworkModule(EventHub hub, PiHoleModule pihole, AlertEngine alerts, IOptions<BureauOptions> o)
    {
        _hub = hub;
        _pihole = pihole;
        _alerts = alerts;
        _aliasPath = Path.Combine(o.Value.DataDir, "aliases.json");
        _knownPath = Path.Combine(o.Value.DataDir, "known-devices.json");
        _seenPath = Path.Combine(o.Value.DataDir, "last-seen.json");
        foreach (var (mac, v) in JsonFile.Load(_seenPath, AppJson.Default.DictionaryStringString) ?? [])
        {
            var parts = v.Split('|');
            if (parts.Length == 2 && long.TryParse(parts[1], out var ts)) { _lastIp[mac] = parts[0]; _lastSeen[mac] = ts; }
        }
        _firstSeen = new(JsonFile.Load(_knownPath, AppJson.Default.DictionaryStringInt64) ?? [], StringComparer.OrdinalIgnoreCase);
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
        TrackNew(raw);
        _last = raw;
        _hub.Publish("network", [.. raw.Select(Decorate)], AppJson.Default.DeviceInfoArray);
    }

    DateTime _seenSavedAt = DateTime.MinValue;

    void RememberSeen(IEnumerable<DeviceInfo> online)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var d in online.Where(d => d.Online))
        {
            var mac = d.Mac.ToLowerInvariant();
            _lastIp[mac] = d.Ip;
            _lastSeen[mac] = now;
        }
        if (DateTime.UtcNow - _seenSavedAt < TimeSpan.FromMinutes(10)) return; // grava no máximo a cada 10 min
        _seenSavedAt = DateTime.UtcNow;
        JsonFile.Save(_seenPath, _lastSeen.ToDictionary(kv => kv.Key, kv => $"{_lastIp.GetValueOrDefault(kv.Key)}|{kv.Value}"),
            AppJson.Default.DictionaryStringString);
    }

    /// <summary>Aparelho nunca visto antes → alerta. Na primeira execução só aprende os que já existem.</summary>
    void TrackNew(DeviceInfo[] raw)
    {
        var learning = _firstSeen.IsEmpty;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var added = false;
        foreach (var d in raw.Where(d => d.Online))
        {
            if (!_firstSeen.TryAdd(d.Mac.ToLowerInvariant(), learning ? 0 : now)) continue;
            added = true;
            if (!learning) _alerts.OnNewDevice(Decorate(d));
        }
        if (added) JsonFile.Save(_knownPath, new Dictionary<string, long>(_firstSeen), AppJson.Default.DictionaryStringInt64);
    }

    DeviceInfo Decorate(DeviceInfo d) => d with
    {
        FirstSeen = _firstSeen.GetValueOrDefault(d.Mac.ToLowerInvariant()),
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
        RememberSeen(byMac.Values);
        // Aparelhos vistos nos últimos 7 dias continuam na lista (offline) — essencial para o Wake-on-LAN.
        foreach (var (seenMac, ts) in _lastSeen)
            if (ts > cutoff && !byMac.ContainsKey(seenMac) && _lastIp.TryGetValue(seenMac, out var lastIp))
                byMac[seenMac] = new DeviceInfo(lastIp, seenMac, null, null, false, null);

        foreach (var d in await _pihole.GetDevicesAsync(ct))
        {
            if (d.Mac.StartsWith("ip-") || d.Mac.Length != 17) continue;
            if (byMac.TryGetValue(d.Mac, out var cur))
                byMac[d.Mac] = cur with { Name = d.Name, Vendor = d.Vendor };
            else if (d.Ip is not null && d.LastQuery > cutoff)
                byMac[d.Mac] = new DeviceInfo(d.Ip, d.Mac, d.Name, d.Vendor, false, null);
        }

        var activity = await _pihole.ActivityAsync(ct);
        var devices = await Task.WhenAll(byMac.Values.Select(async d =>
            (d.Name is null ? d with { Name = await ResolveAsync(d.Ip) } : d) with { DnsQueries = activity.GetValueOrDefault(d.Ip) }));

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

        // Envia para o broadcast geral e para o broadcast de cada sub-rede (alguns roteadores só repassam este),
        // nas portas 9 e 7 — as duas usadas por placas de rede para o "pacote mágico".
        var targets = new HashSet<IPAddress> { IPAddress.Broadcast };
        foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up ||
                nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask is null) continue;
                var ip = ua.Address.GetAddressBytes();
                var mask = ua.IPv4Mask.GetAddressBytes();
                targets.Add(new IPAddress([.. ip.Select((b, i) => (byte)(b | ~mask[i]))]));
            }
        }
        using var udp = new UdpClient { EnableBroadcast = true };
        foreach (var t in targets)
            foreach (var port in new[] { 9, 7 })
                try { await udp.SendAsync(packet, new IPEndPoint(t, port)); } catch { /* interface sem rota */ }
        return true;
    }
}

/// <summary>
/// Atualiza cada módulo periodicamente: rápido enquanto alguém olha a interface,
/// devagar quando ninguém está conectado (o suficiente para os alertas de push).
/// </summary>
public sealed class Collectors(EventHub hub, ServicesModule services, DockerModule docker,
    PiHoleModule pihole, NetworkModule network, AptModule apt, BackupsModule backups,
    HomeAssistantModule ha, JellyfinModule jellyfin, MonitorsModule monitors, DeviceBlocks blocks, ILogger<Collectors> log) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken ct) => Task.WhenAll(
        Loop("services", services.RefreshAsync, 4, 15, ct),
        Loop("containers", docker.RefreshAsync, 4, 15, ct),
        Loop("pihole", pihole.RefreshAsync, 5, 30, ct),
        Loop("network", network.RefreshAsync, 20, 300, ct),
        Loop("apt", apt.RefreshAsync, 10, 120, ct),          // lista do apt só a cada 6 h (interno)
        Loop("backups", backups.RefreshAsync, 15, 120, ct),
        Loop("homeassistant", ha.RefreshAsync, 5, 60, ct),
        Loop("jellyfin", jellyfin.RefreshAsync, 5, 60, ct),
        Loop("monitors", monitors.RunAsync, 60, 60, ct),
        Loop("blocks", blocks.ApplyAsync, 30, 30, ct));

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
