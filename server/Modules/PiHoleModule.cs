using System.Net;
using System.Text;
using System.Text.Json;
using Bureau.Infra;
using Microsoft.Extensions.Options;

namespace Bureau.Modules;

/// <summary>Integração com a API REST do Pi-hole v6 (sessão reaproveitada via X-FTL-SID).</summary>
public sealed partial class PiHoleModule(EventHub hub, AlertEngine alerts, IOptionsMonitor<BureauOptions> opts, ILogger<PiHoleModule> log) : IDisposable
{
    readonly HttpClient _http = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    {
        Timeout = TimeSpan.FromSeconds(8),
    };
    readonly SemaphoreSlim _authLock = new(1, 1);
    string? _sid;
    bool _authed;
    TopItem[] _top = [];
    HistPoint[] _hist = [];
    DateTime _slowRefreshAt = DateTime.MinValue;
    PiDevice[] _devices = [];
    DateTime _devicesAt = DateTime.MinValue;

    PiHoleOptions O => opts.CurrentValue.PiHole;
    bool Fake => !OperatingSystem.IsLinux() && !O.Enabled;

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (Fake) { Publish(DevFakes.PiHole()); return; }
        if (!O.Enabled) { Publish(new PiHoleState(false, false, null, false, null, 0, 0, 0, 0, 0, [], [])); return; }

        try
        {
            using var sum = await GetJsonAsync("/api/stats/summary", ct);
            using var blk = await GetJsonAsync("/api/dns/blocking", ct);

            // Top domínios e histórico mudam devagar: 1x por minuto basta.
            if (DateTime.UtcNow >= _slowRefreshAt)
            {
                using (var top = await GetJsonAsync("/api/stats/top_domains?blocked=true&count=8", ct))
                    _top = [.. top.RootElement.GetProperty("domains").EnumerateArray()
                        .Select(d => new TopItem(d.GetProperty("domain").GetString() ?? "", d.GetProperty("count").GetInt64()))];
                using (var hist = await GetJsonAsync("/api/history", ct))
                    _hist = [.. hist.RootElement.GetProperty("history").EnumerateArray()
                        .Select(h => new HistPoint((long)h.GetProperty("timestamp").GetDouble(), h.GetProperty("total").GetInt32(), h.GetProperty("blocked").GetInt32()))];
                _slowRefreshAt = DateTime.UtcNow.AddSeconds(60);
            }

            var q = sum.RootElement.GetProperty("queries");
            var b = blk.RootElement;
            Publish(new PiHoleState(
                true, true, null,
                b.GetProperty("blocking").GetString() == "enabled",
                b.TryGetProperty("timer", out var t) && t.ValueKind == JsonValueKind.Number ? Math.Round(t.GetDouble()) : null,
                q.GetProperty("total").GetInt64(), q.GetProperty("blocked").GetInt64(),
                Math.Round(q.GetProperty("percent_blocked").GetDouble(), 1),
                sum.RootElement.GetProperty("gravity").GetProperty("domains_being_blocked").GetInt64(),
                sum.RootElement.GetProperty("clients").GetProperty("active").GetInt32(),
                _top, _hist));
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            log.LogDebug(e, "Pi-hole indisponível");
            Publish(new PiHoleState(true, false, e.Message, false, null, 0, 0, 0, 0, 0, _top, _hist));
        }
    }

    public async Task<OkResponse> SetBlockingAsync(bool enabled, int? timer)
    {
        if (Fake)
        {
            DevFakes.SetBlocking(enabled);
            await RefreshAsync();
            return new(true, null);
        }
        if (!O.Enabled) return new(false, "Pi-hole não configurado");
        try
        {
            var body = $"{{\"blocking\":{(enabled ? "true" : "false")},\"timer\":{(timer is > 0 ? timer.Value.ToString() : "null")}}}";
            using var res = await SendAsync(HttpMethod.Post, "/api/dns/blocking", body, CancellationToken.None);
            await RefreshAsync();
            return res.IsSuccessStatusCode ? new(true, null) : new(false, $"Pi-hole respondeu {(int)res.StatusCode}");
        }
        catch (Exception e) { return new(false, e.Message); }
    }

    /// <summary>Dispositivos conhecidos pelo Pi-hole (nomes, fabricante) — usado pelo módulo de rede.</summary>
    public async Task<PiDevice[]> GetDevicesAsync(CancellationToken ct)
    {
        if (!O.Enabled || DateTime.UtcNow < _devicesAt) return _devices;
        try
        {
            using var doc = await GetJsonAsync("/api/network/devices?max_devices=100&max_addresses=3", ct);
            _devices = [.. doc.RootElement.GetProperty("devices").EnumerateArray().Select(d =>
            {
                var ip = d.GetProperty("ips").EnumerateArray().FirstOrDefault();
                string? Str(JsonElement e, string n) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;
                return new PiDevice(Str(d, "hwaddr") ?? "", Str(ip, "ip"), Str(ip, "name"), Str(d, "macVendor"),
                    d.TryGetProperty("lastQuery", out var lq) && lq.ValueKind == JsonValueKind.Number ? (long)lq.GetDouble() : 0);
            })];
        }
        catch (Exception e) { log.LogDebug(e, "Falha ao ler dispositivos do Pi-hole"); }
        _devicesAt = DateTime.UtcNow.AddSeconds(60);
        return _devices;
    }

    void Publish(PiHoleState s)
    {
        hub.Publish("pihole", s, AppJson.Default.PiHoleState);
        alerts.OnPiHole(s);
    }

    async Task<JsonDocument> GetJsonAsync(string path, CancellationToken ct)
    {
        using var res = await SendAsync(HttpMethod.Get, path, null, ct);
        res.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await res.Content.ReadAsByteArrayAsync(ct));
    }

    async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? body, CancellationToken ct, bool retry = true)
    {
        if (!_authed) await AuthAsync(ct);
        using var req = new HttpRequestMessage(method, O.Url.TrimEnd('/') + path);
        if (_sid is not null) req.Headers.Add("X-FTL-SID", _sid);
        if (body is not null) req.Content = new StringContent(body, Encoding.UTF8, "application/json");

        var res = await _http.SendAsync(req, ct);
        if (res.StatusCode == HttpStatusCode.Unauthorized && retry)
        {
            res.Dispose();
            _authed = false;
            return await SendAsync(method, path, body, ct, retry: false);
        }
        return res;
    }

    async Task AuthAsync(CancellationToken ct)
    {
        await _authLock.WaitAsync(ct);
        try
        {
            if (_authed) return;
            var body = "{\"password\":" + JsonSerializer.Serialize(O.Password ?? "", AppJson.Default.String) + "}";
            using var res = await _http.PostAsync(O.Url.TrimEnd('/') + "/api/auth",
                new StringContent(body, Encoding.UTF8, "application/json"), ct);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsByteArrayAsync(ct));
            if (!doc.RootElement.TryGetProperty("session", out var s) || !s.GetProperty("valid").GetBoolean())
                throw new InvalidOperationException("Senha do Pi-hole inválida");
            _sid = s.TryGetProperty("sid", out var sid) && sid.ValueKind == JsonValueKind.String ? sid.GetString() : null;
            _authed = true;
        }
        finally { _authLock.Release(); }
    }

    public void Dispose()
    {
        // Libera a sessão (o Pi-hole tem número limitado de sessões simultâneas).
        if (_sid is not null)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Delete, O.Url.TrimEnd('/') + "/api/auth");
                req.Headers.Add("X-FTL-SID", _sid);
                _http.Send(req).Dispose();
            }
            catch { /* desligando */ }
        }
        _http.Dispose();
    }
}
