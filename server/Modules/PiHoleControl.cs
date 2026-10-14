using System.Text.Json;
using System.Text.RegularExpressions;
using Bureau.Infra;

namespace Bureau.Modules;

/// <summary>Recursos interativos do Pi-hole v6: consultas ao vivo, listas de domínios, atividade e bloqueio de aparelhos.</summary>
public sealed partial class PiHoleModule
{
    const string BlockGroup = "BUREAU-bloqueio";
    const string BlockRegex = ".*";
    int? _blockGroupId;
    Dictionary<string, int> _activity = [];
    DateTime _activityAt = DateTime.MinValue;

    public bool Available => O.Enabled || Fake;

    // ── Consultas ao vivo ────────────────────────────────
    /// <summary>Últimas consultas; com client, só as daquele aparelho (filtro do Pi-hole + conferência local).</summary>
    public async Task<DnsQuery[]> QueriesAsync(int length = 150, string? client = null)
    {
        if (Fake) return [.. DevFakes.Queries().Where(q => client is null || q.Client == client)];
        if (!O.Enabled) return [];
        var clientFilter = client;
        var filter = client is null ? "" : $"&client_ip={Uri.EscapeDataString(client)}";
        using var doc = await GetJsonAsync($"/api/queries?length={Math.Clamp(length, 10, 1000)}{filter}", CancellationToken.None);
        return [.. doc.RootElement.GetProperty("queries").EnumerateArray().Select(q =>
        {
            var client = q.TryGetProperty("client", out var c) ? c : default;
            var status = Str(q, "status") ?? "?";
            return new DnsQuery((long)q.GetProperty("time").GetDouble(), Str(q, "domain") ?? "", Str(q, "type") ?? "",
                Str(client, "ip") ?? "", Str(client, "name"), status, IsBlocked(status));
        }).Where(q => clientFilter is null || q.Client == clientFilter)];
    }

    static bool IsBlocked(string status) =>
        status.Contains("GRAVITY") || status.Contains("REGEX") || status.Contains("DENYLIST") || status.Contains("BLOCKED") || status == "SPECIAL_DOMAIN";

    // ── Bloquear / liberar domínio ───────────────────────
    public async Task<OkResponse> SetDomainAsync(string domain, string list)
    {
        domain = (domain ?? "").Trim().ToLowerInvariant();
        if (!DomainName().IsMatch(domain)) return new(false, "Domínio inválido");
        if (list is not ("deny" or "allow")) return new(false, "Lista inválida");
        if (Fake) return new(true, list == "deny" ? $"{domain} bloqueado" : $"{domain} liberado");
        if (!O.Enabled) return new(false, "Pi-hole não configurado");

        var body = $"{{\"domain\":{J(domain)},\"comment\":\"BUREAU\",\"groups\":[0],\"enabled\":true}}";
        using var res = await SendAsync(HttpMethod.Post, $"/api/domains/{list}/exact", body, CancellationToken.None);
        if (res.IsSuccessStatusCode) return new(true, list == "deny" ? $"{domain} bloqueado" : $"{domain} liberado");
        var err = await res.Content.ReadAsStringAsync();
        return new(false, err.Contains("UNIQUE") ? "Domínio já está em uma das listas do Pi-hole" : $"Pi-hole respondeu {(int)res.StatusCode}");
    }

    // ── Atividade por aparelho (consultas DNS em 24 h) ──
    public async Task<Dictionary<string, int>> ActivityAsync(CancellationToken ct)
    {
        if (!O.Enabled || DateTime.UtcNow < _activityAt) return _activity;
        try
        {
            using var doc = await GetJsonAsync("/api/stats/top_clients?count=100", ct);
            _activity = doc.RootElement.GetProperty("clients").EnumerateArray()
                .Where(c => Str(c, "ip") is not null)
                .GroupBy(c => Str(c, "ip")!)
                .ToDictionary(g => g.Key, g => g.Sum(c => c.GetProperty("count").GetInt32()));
        }
        catch (Exception e) { log.LogDebug(e, "Falha ao ler top_clients"); }
        _activityAt = DateTime.UtcNow.AddSeconds(60);
        return _activity;
    }

    // ── Bloqueio de aparelhos (grupo com regex ".*") ─────
    /// <summary>
    /// Coloca (ou tira) o aparelho no grupo "BUREAU-bloqueio", que tem uma regra que nega todos os domínios.
    /// O aparelho continua no grupo padrão; só funciona se ele usar o Pi-hole como DNS.
    /// </summary>
    public async Task<string?> SetClientBlockedAsync(string mac, bool blocked)
    {
        if (Fake) return null;
        if (!O.Enabled) return "Pi-hole não configurado";
        try
        {
            var gid = await EnsureBlockGroupAsync();
            var client = mac.ToUpperInvariant();
            var body = $"{{\"client\":{J(client)},\"comment\":\"BUREAU\",\"groups\":[{(blocked ? $"0,{gid}" : "0")}]}}";
            using var put = await SendAsync(HttpMethod.Put, $"/api/clients/{Uri.EscapeDataString(client)}", body, CancellationToken.None);
            if (put.IsSuccessStatusCode) return null;
            using var post = await SendAsync(HttpMethod.Post, "/api/clients", body, CancellationToken.None);
            return post.IsSuccessStatusCode ? null : $"Pi-hole respondeu {(int)post.StatusCode}";
        }
        catch (Exception e) { return e.Message; }
    }

    async Task<int> EnsureBlockGroupAsync()
    {
        if (_blockGroupId is { } cached) return cached;

        int? gid = null;
        using (var groups = await GetJsonAsync("/api/groups", CancellationToken.None))
            foreach (var g in groups.RootElement.GetProperty("groups").EnumerateArray())
                if (Str(g, "name") == BlockGroup) gid = g.GetProperty("id").GetInt32();

        if (gid is null)
        {
            using var res = await SendAsync(HttpMethod.Post, "/api/groups",
                $"{{\"name\":\"{BlockGroup}\",\"comment\":\"Aparelhos bloqueados pelo BUREAU\",\"enabled\":true}}", CancellationToken.None);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsByteArrayAsync());
            foreach (var g in doc.RootElement.GetProperty("groups").EnumerateArray())
                if (Str(g, "name") == BlockGroup) gid = g.GetProperty("id").GetInt32();
        }
        if (gid is null) throw new InvalidOperationException("Não foi possível criar o grupo de bloqueio no Pi-hole");

        // Regra que nega tudo, válida só para esse grupo.
        var rule = $"{{\"domain\":{J(BlockRegex)},\"comment\":\"BUREAU: bloqueio de aparelhos\",\"groups\":[{gid}],\"enabled\":true}}";
        using (var put = await SendAsync(HttpMethod.Put, $"/api/domains/deny/regex/{Uri.EscapeDataString(BlockRegex)}", rule, CancellationToken.None))
            if (!put.IsSuccessStatusCode)
                using (await SendAsync(HttpMethod.Post, "/api/domains/deny/regex", rule, CancellationToken.None)) { }

        _blockGroupId = gid;
        return gid.Value;
    }

    static string? Str(JsonElement e, string n) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static string J(string s) => JsonSerializer.Serialize(s, AppJson.Default.String);

    [GeneratedRegex(@"^(?=.{1,253}$)([a-z0-9_]([a-z0-9_-]{0,61}[a-z0-9_])?\.)+[a-z0-9-]{2,63}$")]
    private static partial Regex DomainName();
}
