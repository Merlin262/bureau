using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bureau.Infra;
using Microsoft.Extensions.Options;

namespace Bureau.Modules;

/// <summary>Fala direto com a API do Docker pelo socket unix (sem CLI, sem dependências).</summary>
public sealed partial class DockerModule : IDisposable
{
    readonly EventHub _hub;
    readonly HttpClient? _http;
    readonly string _socket;
    readonly bool _enabled;

    readonly AlertEngine _alerts;

    public DockerModule(EventHub hub, AlertEngine alerts, IOptions<BureauOptions> o)
    {
        _hub = hub;
        _alerts = alerts;
        _socket = o.Value.Docker.Socket;
        _enabled = o.Value.Docker.Enabled && OperatingSystem.IsLinux();
        if (!_enabled) return;

        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectCallback = async (_, ct) =>
            {
                var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await s.ConnectAsync(new UnixDomainSocketEndPoint(_socket), ct);
                    return new NetworkStream(s, ownsSocket: true);
                }
                catch { s.Dispose(); throw; }
            },
        };
        _http = new HttpClient(handler) { BaseAddress = new Uri("http://docker/"), Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        ContainersState state;
        if (!OperatingSystem.IsLinux()) state = new(true, null, DevFakes.Containers());
        else if (!_enabled || _http is null) state = new(false, "Docker desativado na configuração", []);
        else if (!File.Exists(_socket)) state = new(false, "Docker não encontrado", []);
        else
        {
            try
            {
                using var doc = JsonDocument.Parse(await _http.GetByteArrayAsync("containers/json?all=1", ct));
                state = new(true, null, [.. doc.RootElement.EnumerateArray().Select(ParseContainer).OrderBy(c => c.Name)]);
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                state = new(false, e is HttpRequestException { InnerException: SocketException { SocketErrorCode: SocketError.AccessDenied } }
                    ? "Sem permissão no socket do Docker (grupo docker)"
                    : e.Message, []);
            }
        }
        _hub.Publish("containers", state, AppJson.Default.ContainersState);
        _alerts.OnContainers(state);
    }

    public async Task<OkResponse> ActionAsync(string id, string action)
    {
        if (!ValidId().IsMatch(id)) return new(false, "ID inválido");
        if (action is not ("start" or "stop" or "restart")) return new(false, "Ação inválida");
        _alerts.Expect($"ctr:{id}");

        if (!OperatingSystem.IsLinux())
        {
            await Task.Delay(500);
            DevFakes.ContainerAction(id, action);
            await RefreshAsync();
            return new(true, null);
        }
        if (_http is null) return new(false, "Docker indisponível");

        using var res = await _http.PostAsync($"containers/{id}/{action}{(action == "start" ? "" : "?t=10")}", null);
        await RefreshAsync();
        return res.IsSuccessStatusCode || (int)res.StatusCode == 304
            ? new(true, null)
            : new(false, await res.Content.ReadAsStringAsync());
    }

    public async Task<string> LogsAsync(string id)
    {
        if (!ValidId().IsMatch(id)) return "ID inválido.";
        if (!OperatingSystem.IsLinux())
            return string.Join('\n', Enumerable.Range(0, 30).Select(i => $"[{DateTime.Now.AddSeconds(i * 13 - 400):HH:mm:ss}] INFO  evento simulado {i}"));
        if (_http is null) return "Docker indisponível.";
        var bytes = await _http.GetByteArrayAsync($"containers/{id}/logs?stdout=1&stderr=1&tail=300&timestamps=1");
        return Ansi().Replace(Demux(bytes), "");
    }

    internal static ContainerInfo ParseContainer(JsonElement c)
    {
        var name = c.GetProperty("Names").EnumerateArray().FirstOrDefault().GetString()?.TrimStart('/') ?? "?";
        var ports = new SortedSet<string>();
        if (c.TryGetProperty("Ports", out var ps))
            foreach (var p in ps.EnumerateArray())
            {
                var priv = p.GetProperty("PrivatePort").GetInt32();
                var type = p.TryGetProperty("Type", out var t) ? t.GetString() : "tcp";
                ports.Add(p.TryGetProperty("PublicPort", out var pub) ? $"{pub.GetInt32()}→{priv}/{type}" : $"{priv}/{type}");
            }
        return new ContainerInfo(
            c.GetProperty("Id").GetString()![..12], name, c.GetProperty("Image").GetString() ?? "",
            c.GetProperty("State").GetString() ?? "", c.GetProperty("Status").GetString() ?? "",
            string.Join(", ", ports), c.GetProperty("Created").GetInt64());
    }

    /// <summary>Logs sem TTY vêm multiplexados: cabeçalho de 8 bytes [stream,0,0,0,len(4 BE)] por bloco.</summary>
    internal static string Demux(byte[] b)
    {
        if (b.Length < 8 || b[0] > 2 || b[1] != 0 || b[2] != 0 || b[3] != 0) return Encoding.UTF8.GetString(b);
        var sb = new StringBuilder(b.Length);
        var i = 0;
        while (i + 8 <= b.Length)
        {
            var len = BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(i + 4));
            i += 8;
            if (len < 0 || i + len > b.Length) len = b.Length - i;
            sb.Append(Encoding.UTF8.GetString(b, i, len));
            i += len;
        }
        return sb.ToString();
    }

    public void Dispose() => _http?.Dispose();

    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,127}$")]
    private static partial Regex ValidId();

    [GeneratedRegex(@"\x1B\[[0-9;?]*[A-Za-z]")]
    private static partial Regex Ansi();
}
