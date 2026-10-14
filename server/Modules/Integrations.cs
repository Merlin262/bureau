using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bureau.Infra;
using Microsoft.Extensions.Options;

namespace Bureau.Modules;

/// <summary>Home Assistant (API REST com token de longa duração): resumo, entidades e liga/desliga.</summary>
public sealed partial class HomeAssistantModule(EventHub hub, AlertEngine alerts, IOptionsMonitor<BureauOptions> opts, ILogger<HomeAssistantModule> log)
{
    static readonly HashSet<string> Toggleable = ["light", "switch", "fan", "input_boolean", "automation", "siren"];
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    HaState? _last;

    HomeAssistantOptions O => opts.CurrentValue.HomeAssistant;

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        HaState state;
        if (!O.Enabled) state = OperatingSystem.IsLinux() ? new(false, false, null, null, null, 0, 0, [], []) : DevFakes.HomeAssistant();
        else
        {
            try
            {
                using var cfg = await GetAsync("/api/config", ct);
                using var states = await GetAsync("/api/states", ct);
                var all = states.RootElement.EnumerateArray().Select(e =>
                {
                    var id = e.GetProperty("entity_id").GetString() ?? "";
                    var attr = e.TryGetProperty("attributes", out var a) ? a : default;
                    return new HaEntity(id, Str(attr, "friendly_name") ?? id, id.Split('.')[0],
                        e.GetProperty("state").GetString() ?? "", Str(attr, "unit_of_measurement"));
                }).ToArray();

                var wanted = O.Entities.Length > 0
                    ? all.Where(e => O.Entities.Contains(e.Id)).OrderBy(e => Array.IndexOf(O.Entities, e.Id))
                    : all.Where(e => e.Domain is "light" or "switch" or "fan").OrderBy(e => e.Domain).ThenBy(e => e.Name);

                state = new HaState(true, true, null,
                    Str(cfg.RootElement, "version"), Str(cfg.RootElement, "location_name"),
                    all.Length, all.Count(e => e.State is "unavailable" or "unknown"),
                    [.. all.GroupBy(e => e.Domain).OrderByDescending(g => g.Count()).Take(8).Select(g => new NamedCount(g.Key, g.Count()))],
                    [.. wanted.Take(40)]);
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                log.LogDebug(e, "Home Assistant indisponível");
                state = new HaState(true, false, Friendly(e), _last?.Version, _last?.Location, 0, 0, [], []);
            }
        }
        _last = state;
        hub.Publish("homeassistant", state, AppJson.Default.HaState);
        alerts.OnOnline("homeassistant", "Home Assistant", state.Configured, state.Online, "apps");
    }

    public async Task<OkResponse> ToggleAsync(string entityId)
    {
        if (!EntityId().IsMatch(entityId)) return new(false, "Entidade inválida");
        var domain = entityId.Split('.')[0];
        var allowed = O.Entities.Contains(entityId) || Toggleable.Contains(domain);
        if (!allowed) return new(false, "Essa entidade não pode ser alternada");
        if (!O.Enabled)
        {
            DevFakes.HaToggle(entityId);
            await RefreshAsync();
            return new(true, null);
        }
        try
        {
            using var req = Request(HttpMethod.Post, "/api/services/homeassistant/toggle");
            req.Content = new StringContent($"{{\"entity_id\":{JsonSerializer.Serialize(entityId, AppJson.Default.String)}}}", Encoding.UTF8, "application/json");
            using var res = await _http.SendAsync(req);
            await RefreshAsync();
            return res.IsSuccessStatusCode ? new(true, null) : new(false, $"Home Assistant respondeu {(int)res.StatusCode}");
        }
        catch (Exception e) { return new(false, Friendly(e)); }
    }

    HttpRequestMessage Request(HttpMethod m, string path)
    {
        var req = new HttpRequestMessage(m, O.Url.TrimEnd('/') + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", O.Token ?? "");
        return req;
    }

    async Task<JsonDocument> GetAsync(string path, CancellationToken ct)
    {
        using var req = Request(HttpMethod.Get, path);
        using var res = await _http.SendAsync(req, ct);
        if (res.StatusCode == System.Net.HttpStatusCode.Unauthorized) throw new InvalidOperationException("Token do Home Assistant inválido");
        res.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await res.Content.ReadAsByteArrayAsync(ct));
    }

    internal static string Friendly(Exception e) => e switch
    {
        HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError } => "Sem conexão com o serviço",
        TaskCanceledException => "Tempo esgotado",
        _ => e.Message,
    };

    internal static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    [GeneratedRegex(@"^[a-z_]+\.[a-z0-9_]+$")]
    private static partial Regex EntityId();
}

/// <summary>Jellyfin (chave de API): servidor, biblioteca e o que está sendo assistido agora.</summary>
public sealed class JellyfinModule(EventHub hub, AlertEngine alerts, IOptionsMonitor<BureauOptions> opts, ILogger<JellyfinModule> log)
{
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    JfState? _last;

    JellyfinOptions O => opts.CurrentValue.Jellyfin;

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        JfState state;
        if (!O.Enabled) state = OperatingSystem.IsLinux() ? new(false, false, null, null, null, [], []) : DevFakes.Jellyfin();
        else
        {
            try
            {
                using var info = await GetAsync("/System/Info", ct);
                using var counts = await GetAsync("/Items/Counts", ct);
                using var sessions = await GetAsync("/Sessions?activeWithinSeconds=960", ct);

                NamedCount[] library = [.. new[] { ("Filmes", "MovieCount"), ("Séries", "SeriesCount"), ("Episódios", "EpisodeCount"), ("Músicas", "SongCount") }
                    .Select(x => new NamedCount(x.Item1, counts.RootElement.TryGetProperty(x.Item2, out var c) && c.TryGetInt32(out var n) ? n : 0))
                    .Where(x => x.Count > 0)];

                var playing = sessions.RootElement.EnumerateArray()
                    .Where(s => s.TryGetProperty("NowPlayingItem", out var i) && i.ValueKind == JsonValueKind.Object)
                    .Select(s =>
                    {
                        var item = s.GetProperty("NowPlayingItem");
                        var play = s.TryGetProperty("PlayState", out var p) ? p : default;
                        var title = HomeAssistantModule.Str(item, "Name") ?? "?";
                        if (HomeAssistantModule.Str(item, "Type") == "Episode")
                            title = $"{HomeAssistantModule.Str(item, "SeriesName")} · T{Num(item, "ParentIndexNumber")}E{Num(item, "IndexNumber")} {title}";
                        var pos = play.ValueKind == JsonValueKind.Object && play.TryGetProperty("PositionTicks", out var pt) && pt.TryGetInt64(out var ptv) ? ptv : 0;
                        var len = item.TryGetProperty("RunTimeTicks", out var rt) && rt.TryGetInt64(out var rtv) ? rtv : 0;
                        return new JfSession(
                            HomeAssistantModule.Str(s, "UserName") ?? "?", title,
                            HomeAssistantModule.Str(s, "Client"), HomeAssistantModule.Str(s, "DeviceName"),
                            play.ValueKind == JsonValueKind.Object && play.TryGetProperty("IsPaused", out var ip) && ip.ValueKind == JsonValueKind.True,
                            len > 0 ? Math.Round(100.0 * pos / len, 1) : 0,
                            play.ValueKind == JsonValueKind.Object && HomeAssistantModule.Str(play, "PlayMethod") == "Transcode");
                    }).ToArray();

                state = new JfState(true, true, null, HomeAssistantModule.Str(info.RootElement, "ServerName"),
                    HomeAssistantModule.Str(info.RootElement, "Version"), library, playing);
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                log.LogDebug(e, "Jellyfin indisponível");
                state = new JfState(true, false, HomeAssistantModule.Friendly(e), _last?.Name, _last?.Version, _last?.Library ?? [], []);
            }
        }
        _last = state;
        hub.Publish("jellyfin", state, AppJson.Default.JfState);
        alerts.OnOnline("jellyfin", "Jellyfin", state.Configured, state.Online, "apps");
    }

    async Task<JsonDocument> GetAsync(string path, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, O.Url.TrimEnd('/') + path);
        req.Headers.TryAddWithoutValidation("Authorization", $"MediaBrowser Token=\"{O.ApiKey}\"");
        req.Headers.TryAddWithoutValidation("X-Emby-Token", O.ApiKey ?? "");
        using var res = await _http.SendAsync(req, ct);
        if (res.StatusCode == System.Net.HttpStatusCode.Unauthorized) throw new InvalidOperationException("Chave de API do Jellyfin inválida");
        res.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await res.Content.ReadAsByteArrayAsync(ct));
    }

    static string Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n.ToString("00") : "?";
}
