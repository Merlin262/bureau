namespace Bureau.Modules;

/// <summary>
/// Bloqueio de "DNS seguro" (DNS-over-HTTPS/TLS). Navegadores, celulares e TVs que usam um DNS próprio
/// criptografado passam por fora do Pi-hole — e somem dos "Acessos". Negar os nomes desses serviços
/// faz o aparelho voltar ao DNS da rede. (Serviços acessados direto por IP não são afetados.)
/// </summary>
public sealed partial class PiHoleModule
{
    const string DohComment = "BUREAU: DNS seguro";

    /// <summary>Provedores públicos de DoH/DoT mais usados (Chrome, Firefox, Android "DNS privado", Windows, iOS).</summary>
    internal static readonly string[] DohDomains =
    [
        "dns.google", "dns.google.com", "8888.google", "dns64.dns.google",
        "cloudflare-dns.com", "mozilla.cloudflare-dns.com", "chrome.cloudflare-dns.com", "one.one.one.one",
        "1dot1dot1dot1.cloudflare-dns.com", "security.cloudflare-dns.com", "family.cloudflare-dns.com",
        "dns.quad9.net", "dns9.quad9.net", "dns10.quad9.net", "dns11.quad9.net",
        "doh.opendns.com", "doh.familyshield.opendns.com", "dns.umbrella.com",
        "dns.adguard.com", "dns.adguard-dns.com", "dns-unfiltered.adguard.com", "family.adguard-dns.com",
        "dns.nextdns.io", "doh.cleanbrowsing.org", "doh.dns.sb", "dns.alidns.com", "doh.pub", "dns.controld.com",
        "doh.mullvad.net", "dns.mullvad.net", "freedns.controld.com", "dns.switch.ch", "doh.libredns.gr",
    ];

    bool _fakeDoh;

    /// <summary>O domínio é de um provedor de DNS seguro? (para avisar na tela de "Acessos")</summary>
    internal static bool IsDoh(string domain) => Array.IndexOf(DohDomains, domain.ToLowerInvariant().TrimEnd('.')) >= 0;

    public async Task<DohState> DohStateAsync()
    {
        if (Fake) return new(true, _fakeDoh, _fakeDoh ? DohDomains.Length : 0, DohDomains.Length, DohDomains);
        if (!O.Enabled) return new(false, false, 0, DohDomains.Length, DohDomains);
        try
        {
            var n = (await DeniedAsync()).Count(d => IsDoh(d));
            return new(true, n == DohDomains.Length, n, DohDomains.Length, DohDomains);
        }
        catch { return new(false, false, 0, DohDomains.Length, DohDomains); }
    }

    public async Task<OkResponse> SetDohAsync(bool enabled)
    {
        if (Fake) { _fakeDoh = enabled; return new(true, enabled ? "DNS seguro bloqueado" : "DNS seguro liberado"); }
        if (!O.Enabled) return new(false, "Pi-hole não configurado");
        try
        {
            var denied = (await DeniedAsync()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var failed = 0;
            foreach (var d in DohDomains)
            {
                if (enabled && !denied.Contains(d))
                {
                    var body = $"{{\"domain\":{J(d)},\"comment\":{J(DohComment)},\"groups\":[0],\"enabled\":true}}";
                    using var res = await SendAsync(HttpMethod.Post, "/api/domains/deny/exact", body, CancellationToken.None);
                    if (!res.IsSuccessStatusCode) failed++;
                }
                else if (!enabled && denied.Contains(d))
                {
                    using var res = await SendAsync(HttpMethod.Delete, $"/api/domains/deny/exact/{Uri.EscapeDataString(d)}", null, CancellationToken.None);
                    if (!res.IsSuccessStatusCode) failed++;
                }
            }
            if (failed > 0) return new(false, $"O Pi-hole recusou {failed} domínio(s)");
            return new(true, enabled ? "DNS seguro bloqueado: os aparelhos voltam a usar o Pi-hole" : "DNS seguro liberado");
        }
        catch (Exception e) { return new(false, e.Message); }
    }

    async Task<string[]> DeniedAsync()
    {
        using var doc = await GetJsonAsync("/api/domains/deny/exact", CancellationToken.None);
        return [.. doc.RootElement.GetProperty("domains").EnumerateArray().Select(d => Str(d, "domain") ?? "").Where(d => d.Length > 0)];
    }
}
