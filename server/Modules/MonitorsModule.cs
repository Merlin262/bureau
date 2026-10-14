using System.Diagnostics;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Bureau.Infra;
using Microsoft.Extensions.Options;

namespace Bureau.Modules;

/// <summary>
/// Monitor de disponibilidade: testa sites (HTTP/HTTPS) e portas (host:porta) a cada minuto, mede latência e
/// calcula o uptime das últimas 24 h. Inclui o monitor "Internet" (Cloudflare + Google) para saber quando a
/// conexão de casa caiu — o aviso de volta informa quanto tempo ficou fora.
/// </summary>
public sealed partial class MonitorsModule
{
    const int Window = 24 * 60;          // 24 h de resultados (1 por minuto)
    const string InternetId = "internet";

    readonly EventHub _hub;
    readonly AlertEngine _alerts;
    readonly string _path;
    readonly HttpClient _http = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(8),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        AllowAutoRedirect = true,
    })
    { Timeout = TimeSpan.FromSeconds(10) };

    readonly Lock _lock = new();
    MonitorDef[] _defs;
    readonly Dictionary<string, Track> _tracks = [];

    sealed class Track
    {
        public readonly Queue<int> Results = new(); // latência em ms; -1 = falhou
        public bool Up = true;
        public int Fails;
        public long Since = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        public string? Error;
        public int? Latency;
    }

    public MonitorsModule(EventHub hub, AlertEngine alerts, IOptions<BureauOptions> o)
    {
        _hub = hub;
        _alerts = alerts;
        _path = Path.Combine(o.Value.DataDir, "monitors.json");
        _defs = JsonFile.Load(_path, AppJson.Default.MonitorDefArray) ?? [];
    }

    public MonitorDef[] Definitions => _defs;

    public OkResponse Save(MonitorDef[] defs)
    {
        var clean = new List<MonitorDef>();
        foreach (var d in defs.Take(30))
        {
            var target = (d.Target ?? "").Trim();
            var name = (d.Name ?? "").Trim();
            if (!IsValidTarget(target)) return new(false, $"Alvo inválido: {target} (use https://site ou host:porta)");
            if (name.Length == 0) name = target.Replace("https://", "").Replace("http://", "").TrimEnd('/');
            clean.Add(new MonitorDef(string.IsNullOrWhiteSpace(d.Id) ? Guid.NewGuid().ToString("N")[..8] : d.Id, name[..Math.Min(name.Length, 40)], target));
        }
        lock (_lock) _defs = [.. clean];
        JsonFile.Save(_path, _defs, AppJson.Default.MonitorDefArray);
        _ = RunAsync();
        return new(true, "Monitores salvos");
    }

    static bool IsValidTarget(string t) =>
        (Uri.TryCreate(t, UriKind.Absolute, out var u) && u.Scheme is "http" or "https") || HostPort().IsMatch(t);

    public async Task RunAsync(CancellationToken ct = default)
    {
        MonitorDef[] defs;
        lock (_lock) defs = [new MonitorDef(InternetId, "Internet", "cloudflare.com + google.com"), .. _defs];

        var results = await Task.WhenAll(defs.Select(async d =>
            (Def: d, Res: d.Id == InternetId ? await InternetAsync(ct) : await CheckAsync(d.Target, ct))));

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var states = new List<MonitorState>();
        lock (_lock)
        {
            foreach (var key in _tracks.Keys.Except(defs.Select(d => d.Id)).ToArray()) _tracks.Remove(key);
            foreach (var (d, (ok, ms, err)) in results)
            {
                if (!_tracks.TryGetValue(d.Id, out var t)) _tracks[d.Id] = t = new Track();
                t.Results.Enqueue(ok ? ms : -1);
                while (t.Results.Count > Window) t.Results.Dequeue();
                t.Latency = ok ? ms : null;
                t.Error = ok ? null : err;
                t.Fails = ok ? 0 : t.Fails + 1;

                // Muda de estado após 2 falhas seguidas (evita alarme por um pacote perdido).
                if (t.Up && t.Fails >= 2)
                {
                    t.Up = false;
                    t.Since = now;
                    _alerts.OnMonitor(d.Id == InternetId, d.Name, false, 0, err);
                }
                else if (!t.Up && ok)
                {
                    var downFor = now - t.Since;
                    t.Up = true;
                    t.Since = now;
                    _alerts.OnMonitor(d.Id == InternetId, d.Name, true, downFor, null);
                }

                var total = t.Results.Count;
                var up = t.Results.Count(r => r >= 0);
                states.Add(new MonitorState(d.Id, d.Name, d.Target, t.Up, t.Latency, t.Error,
                    total == 0 ? 100 : Math.Round(100.0 * up / total, 2), t.Since,
                    [.. t.Results.Skip(Math.Max(0, total - 60))], d.Id == InternetId));
            }
        }
        _hub.Publish("monitors", [.. states], AppJson.Default.MonitorStateArray);
    }

    async Task<(bool Ok, int Ms, string? Error)> InternetAsync(CancellationToken ct)
    {
        // Basta um responder: um site fora do ar não deve ser confundido com a internet fora.
        var a = CheckAsync("https://1.1.1.1/cdn-cgi/trace", ct);
        var b = CheckAsync("https://www.google.com/generate_204", ct);
        var r = await Task.WhenAll(a, b);
        var ok = r.Where(x => x.Ok).OrderBy(x => x.Ms).ToArray();
        return ok.Length > 0 ? ok[0] : (false, 0, "Sem resposta de Cloudflare nem Google");
    }

    async Task<(bool Ok, int Ms, string? Error)> CheckAsync(string target, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            if (target.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                using var res = await _http.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, ct);
                var code = (int)res.StatusCode;
                return code < 500 ? (true, (int)sw.ElapsedMilliseconds, null) : (false, 0, $"HTTP {code}");
            }
            var m = HostPort().Match(target);
            using var sock = new Socket(SocketType.Stream, ProtocolType.Tcp);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8));
            await sock.ConnectAsync(m.Groups[1].Value, int.Parse(m.Groups[2].Value), cts.Token);
            return (true, (int)sw.ElapsedMilliseconds, null);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            return (false, 0, e switch
            {
                TaskCanceledException or OperationCanceledException => "Tempo esgotado",
                HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError } => "Nome não resolvido (DNS)",
                HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError } => "Erro de certificado/TLS",
                HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError } => "Conexão recusada",
                SocketException s => s.SocketErrorCode == SocketError.ConnectionRefused ? "Porta fechada" : s.SocketErrorCode.ToString(),
                _ => e.Message,
            });
        }
    }

    [GeneratedRegex(@"^([A-Za-z0-9.\-]+|\[[0-9a-fA-F:]+\]):(\d{1,5})$")]
    private static partial Regex HostPort();
}
