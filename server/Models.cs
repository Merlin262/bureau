using System.Text.Json.Serialization;

namespace Bureau;

// ── Sistema ──────────────────────────────────────────────
public sealed record SystemSnapshot(
    long Ts, double Cpu, double[] Cores, double[] Load,
    long MemTotal, long MemUsed, long SwapTotal, long SwapUsed,
    double? TempC, BatteryInfo? Battery, double RxRate, double TxRate, double Uptime);

public sealed record BatteryInfo(int Percent, string Status);

public sealed record DiskInfo(string Mount, string Fs, long Total, long Used);

public sealed record HostInfo(
    string Hostname, string Os, string Kernel, string CpuModel, int Cores,
    string[] Addresses, DiskInfo[] Disks, string Version);

public sealed record MetricsHistory(double[] Cpu, double[] Mem, double[] Rx, double[] Tx);

// ── Serviços / Docker ────────────────────────────────────
public sealed record ServiceInfo(string Unit, string Description, string Active, string Sub, int Pid, long Memory, long Since);
public sealed record UnitEntry(string Unit, string Description, string Active);

public sealed record ContainerInfo(string Id, string Name, string Image, string State, string Status, string Ports, long Created);
public sealed record ContainersState(bool Available, string? Error, ContainerInfo[] Items);

// ── Pi-hole / Rede ───────────────────────────────────────
public sealed record TopItem(string Name, long Count);
public sealed record HistPoint(long T, int Total, int Blocked);
public sealed record PiHoleState(
    bool Configured, bool Online, string? Error, bool Blocking, double? Timer,
    long Total, long Blocked, double Percent, long Gravity, int Clients,
    TopItem[] TopBlocked, HistPoint[] History);

public sealed record PiDevice(string Mac, string? Ip, string? Name, string? Vendor, long LastQuery);
public sealed record DeviceInfo(string Ip, string Mac, string? Name, string? Vendor, bool Online, string? Iface,
    string? Alias = null, bool RandomMac = false);

// ── API ──────────────────────────────────────────────────
public sealed record LoginRequest(string? Password);
public sealed record WatchRequest(string[] Units);
public sealed record BlockingRequest(bool Enabled, int? Timer);
public sealed record WolRequest(string Mac);
public sealed record AliasRequest(string Mac, string? Name);
public sealed record OkResponse(bool Ok, string? Message);
public sealed record LogsResponse(string Text);
public sealed record MeResponse(bool Authenticated, string? Hostname, string Version, bool AllowPower);

// ── Alertas / Push / Aparelhos ───────────────────────────
public sealed record AlertEvent(long Ts, string Kind, string Level, string Title, string Body, string View);
public sealed record PushDevice(string Id, string Kind, string Name, string? Token, string? Endpoint, string? P256dh, string? Auth, string? Owner, long Created);
public sealed record DeviceToken(string Id, string Name, string Hash, long Created);
public sealed record TokenRequest(string? Password, string? Name);
public sealed record TokenResponse(string Token, string Id);
public sealed record PushRegisterRequest(string Kind, string? Name, string? Token, string? Endpoint, string? P256dh, string? Auth);
public sealed record PushConfig(string WebPushKey, bool Fcm, bool Ntfy, string? DeviceId);
public sealed record DeviceSummary(string Id, string Name, string Kind, long Created, bool Current);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SystemSnapshot))]
[JsonSerializable(typeof(HostInfo))]
[JsonSerializable(typeof(MetricsHistory))]
[JsonSerializable(typeof(ServiceInfo[]))]
[JsonSerializable(typeof(UnitEntry[]))]
[JsonSerializable(typeof(ContainersState))]
[JsonSerializable(typeof(PiHoleState))]
[JsonSerializable(typeof(DeviceInfo[]))]
[JsonSerializable(typeof(LoginRequest))]
[JsonSerializable(typeof(WatchRequest))]
[JsonSerializable(typeof(BlockingRequest))]
[JsonSerializable(typeof(WolRequest))]
[JsonSerializable(typeof(AliasRequest))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(OkResponse))]
[JsonSerializable(typeof(LogsResponse))]
[JsonSerializable(typeof(MeResponse))]
[JsonSerializable(typeof(AlertEvent))]
[JsonSerializable(typeof(AlertEvent[]))]
[JsonSerializable(typeof(PushDevice[]))]
[JsonSerializable(typeof(DeviceToken[]))]
[JsonSerializable(typeof(TokenRequest))]
[JsonSerializable(typeof(TokenResponse))]
[JsonSerializable(typeof(PushRegisterRequest))]
[JsonSerializable(typeof(PushConfig))]
[JsonSerializable(typeof(DeviceSummary[]))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(string[]))]
internal partial class AppJson : JsonSerializerContext;
