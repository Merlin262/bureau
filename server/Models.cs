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

/// <summary>Histórico persistido: T (unix s) e médias por intervalo; null = sem dado.</summary>
public sealed record MetricsSeries(string Range, long Step, long[] T, double?[] Cpu, double?[] Mem, double?[] Temp,
    double?[] Rx, double?[] Tx, double?[] Battery);

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
    string? Alias = null, bool RandomMac = false, long FirstSeen = 0, int DnsQueries = 0);

// ── API ──────────────────────────────────────────────────
public sealed record LoginRequest(string? Password);
public sealed record WatchRequest(string[] Units);
public sealed record BlockingRequest(bool Enabled, int? Timer);
public sealed record WolRequest(string Mac);
public sealed record AliasRequest(string Mac, string? Name);
public sealed record UpdateStatus(string Current, string? Latest, string Status, string? Message, long CheckedAt, bool Auto, bool Supported);
public sealed record OkResponse(bool Ok, string? Message);
public sealed record LogsResponse(string Text);
public sealed record MeResponse(bool Authenticated, string? Hostname, string Version, bool AllowPower, bool Terminal = false);

// ── Pacotes / Backups / Integrações ──────────────────────
public sealed record AptState(bool Supported, int Total, int Security, string[] Packages, bool RebootRequired,
    string[] RebootPackages, long CheckedAt, bool Upgrading, string? LastResult, long LastRunAt);
public sealed record BackupInfo(string Name, string Unit, string Status, long LastRun, long LastSuccess, long NextRun, string? Message);
public sealed record HaEntity(string Id, string Name, string Domain, string State, string? Unit);
public sealed record NamedCount(string Name, int Count);
public sealed record HaState(bool Configured, bool Online, string? Error, string? Version, string? Location,
    int Total, int Unavailable, NamedCount[] Domains, HaEntity[] Entities);
public sealed record JfSession(string User, string Title, string? Client, string? Device, bool Paused, double Progress, bool Transcoding);
public sealed record JfState(bool Configured, bool Online, string? Error, string? Name, string? Version,
    NamedCount[] Library, JfSession[] Sessions);
public sealed record EntityRequest(string EntityId);
public sealed record DnsQuery(long Ts, string Domain, string Type, string Client, string? ClientName, string Status, bool Blocked);
public sealed record DomainRequest(string Domain, string List);
public sealed record BlockRule(string Mac, string? Label, bool Manual, int[] Days, string? Start, string? End);
public sealed record BlockState(string Mac, string? Label, bool Manual, int[] Days, string? Start, string? End, bool Active, string? Error);
public sealed record BackupBundle(string Kind, int Version, long Created, string Host, string AppVersion,
    Dictionary<string, string> Files, string? Config);
public sealed record MonitorDef(string Id, string Name, string Target);
public sealed record MonitorState(string Id, string Name, string Target, bool Up, int? LatencyMs, string? Error,
    double Uptime24h, long Since, int[] Recent, bool Builtin);
public sealed record AccessEntry(long Ts, string Ip, string? Country, string Method, bool Ok, string Device);

// ── Alertas / Push / Aparelhos ───────────────────────────
public sealed record AlertEvent(long Ts, string Kind, string Level, string Title, string Body, string View);
public sealed record PushDevice(string Id, string Kind, string Name, string? Token, string? Endpoint, string? P256dh, string? Auth, string? Owner, long Created);
public sealed record DeviceToken(string Id, string Name, string Hash, long Created);
public sealed record TokenRequest(string? Password, string? Name);
public sealed record TokenResponse(string Token, string Id);
public sealed record PushRegisterRequest(string Kind, string? Name, string? Token, string? Endpoint, string? P256dh, string? Auth);
public sealed record PushConfig(string WebPushKey, bool Fcm, bool Ntfy, string? DeviceId, bool Apns = false);
public sealed record DeviceSummary(string Id, string Name, string Kind, long Created, bool Current);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SystemSnapshot))]
[JsonSerializable(typeof(HostInfo))]
[JsonSerializable(typeof(MetricsHistory))]
[JsonSerializable(typeof(MetricsSeries))]
[JsonSerializable(typeof(AptState))]
[JsonSerializable(typeof(BackupInfo[]))]
[JsonSerializable(typeof(HaState))]
[JsonSerializable(typeof(JfState))]
[JsonSerializable(typeof(EntityRequest))]
[JsonSerializable(typeof(AccessEntry[]))]
[JsonSerializable(typeof(MonitorDef[]))]
[JsonSerializable(typeof(BackupBundle))]
[JsonSerializable(typeof(DnsQuery[]))]
[JsonSerializable(typeof(DomainRequest))]
[JsonSerializable(typeof(BlockRule))]
[JsonSerializable(typeof(BlockRule[]))]
[JsonSerializable(typeof(BlockState[]))]
[JsonSerializable(typeof(MonitorState[]))]
[JsonSerializable(typeof(Dictionary<string, long>))]
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
[JsonSerializable(typeof(UpdateStatus))]
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
