using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Bureau.Infra;
using Microsoft.Extensions.Options;

namespace Bureau.Push;

/// <summary>Aparelhos inscritos para receber push (DataDir/push.json).</summary>
public sealed class PushStore
{
    readonly string _path;
    readonly Lock _lock = new();
    List<PushDevice> _items;

    public PushStore(IOptions<BureauOptions> o)
    {
        _path = Path.Combine(o.Value.DataDir, "push.json");
        _items = [.. JsonFile.Load(_path, AppJson.Default.PushDeviceArray) ?? []];
    }

    public PushDevice[] All { get { lock (_lock) return [.. _items]; } }

    public PushDevice Upsert(PushDevice d)
    {
        lock (_lock)
        {
            // Mesmo token/endpoint = mesmo aparelho: substitui em vez de duplicar.
            var existing = _items.FindIndex(x => (d.Token != null && x.Token == d.Token) || (d.Endpoint != null && x.Endpoint == d.Endpoint));
            if (existing >= 0) d = d with { Id = _items[existing].Id };
            _items.RemoveAll(x => x.Id == d.Id);
            _items.Add(d);
            Save();
            return d;
        }
    }

    public bool Remove(string id) => RemoveWhere(x => x.Id == id) > 0;

    public int RemoveWhere(Predicate<PushDevice> match)
    {
        lock (_lock)
        {
            var n = _items.RemoveAll(match);
            if (n > 0) Save();
            return n;
        }
    }

    void Save() => JsonFile.Save(_path, _items.ToArray(), AppJson.Default.PushDeviceArray);
}

/// <summary>Entrega um alerta em todos os canais configurados.</summary>
public sealed class Notifier(PushStore store, WebPushSender web, FcmSender fcm, ApnsSender apns,
    IOptionsMonitor<BureauOptions> opts, ILogger<Notifier> log)
{
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public bool NtfyConfigured => !string.IsNullOrWhiteSpace(opts.CurrentValue.Push.NtfyUrl);

    /// <summary>Envia agora (teste pela interface) e devolve quantos canais aceitaram.</summary>
    public async Task<int> SendAsync(AlertEvent a, string? onlyDeviceId = null) =>
        (await DeliverAsync(a, [], onlyDeviceId)).Count(r => r.Result == PushResult.Ok);

    /// <summary>
    /// Entrega em cada destino (aparelhos e ntfy), pulando os que já receberam este alerta
    /// numa tentativa anterior — assim um retry não duplica a notificação em quem já recebeu.
    /// </summary>
    public async Task<DeliveryResult[]> DeliverAsync(AlertEvent a, IReadOnlyCollection<string> skip,
        string? onlyDeviceId = null, CancellationToken ct = default)
    {
        var targets = store.All.Where(d => (onlyDeviceId is null || d.Id == onlyDeviceId) && !skip.Contains(d.Id))
            .Select(d => (Target: d.Id, Channel: d.Kind, Send: (Func<Task<PushResult>>)(() => SendDeviceAsync(d, a, ct))))
            .ToList();
        if (onlyDeviceId is null && NtfyConfigured && !skip.Contains(NtfyTarget))
            targets.Add((NtfyTarget, "ntfy", async () => await SendNtfyAsync(a) ? PushResult.Ok : PushResult.Failed));

        return await Task.WhenAll(targets.Select(async t =>
        {
            using var span = Telemetry.Source.StartActivity("push.send", ActivityKind.Client);
            span?.SetTag("push.channel", t.Channel);
            var start = Stopwatch.GetTimestamp();
            PushResult r;
            try { r = await t.Send(); }
            catch (Exception e)
            {
                log.LogWarning(e, "Falha ao enviar push para {Target}", t.Target);
                span?.AddException(e);
                r = PushResult.Failed;
            }
            var tags = new TagList { { "channel", t.Channel }, { "result", r.ToString().ToLowerInvariant() } };
            Telemetry.PushDeliveries.Add(1, tags);
            Telemetry.PushDuration.Record(Stopwatch.GetElapsedTime(start).TotalSeconds, tags);
            span?.SetTag("push.result", r.ToString());
            if (r == PushResult.Failed) span?.SetStatus(ActivityStatusCode.Error);
            return new DeliveryResult(t.Target, t.Channel, r);
        }));
    }

    public const string NtfyTarget = "ntfy";

    async Task<PushResult> SendDeviceAsync(PushDevice d, AlertEvent a, CancellationToken ct)
    {
        var r = d.Kind switch
        {
            "webpush" => await web.SendAsync(d, a, ct),
            "fcm" => await fcm.SendAsync(d, a, ct),
            "apns" => await apns.SendAsync(d, a, ct),
            _ => PushResult.Gone,
        };
        if (r == PushResult.Gone)
        {
            store.Remove(d.Id);
            log.LogInformation("Inscrição de push expirada removida: {Name}", d.Name);
        }
        return r;
    }

    async Task<bool> SendNtfyAsync(AlertEvent a)
    {
        var p = opts.CurrentValue.Push;
        if (string.IsNullOrWhiteSpace(p.NtfyUrl)) return false;
        try
        {
            // Publica via JSON na raiz do servidor (suporta acentos no título).
            var url = new Uri(p.NtfyUrl.TrimEnd('/'));
            var topic = url.Segments[^1];
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                w.WriteString("topic", topic);
                w.WriteString("title", a.Title);
                w.WriteString("message", a.Body);
                w.WriteNumber("priority", a.Level == "crit" ? 5 : a.Level == "warn" ? 4 : 3);
                w.WriteStartArray("tags");
                w.WriteStringValue(a.Level == "ok" ? "white_check_mark" : "rotating_light");
                w.WriteEndArray();
                w.WriteEndObject();
            }
            using var req = new HttpRequestMessage(HttpMethod.Post, url.GetLeftPart(UriPartial.Authority))
            {
                Content = new ByteArrayContent(ms.ToArray()),
            };
            if (!string.IsNullOrWhiteSpace(p.NtfyToken)) req.Headers.Authorization = new("Bearer", p.NtfyToken);
            using var res = await _http.SendAsync(req);
            if (!res.IsSuccessStatusCode) log.LogWarning("ntfy {Status}: {Body}", (int)res.StatusCode, await res.Content.ReadAsStringAsync());
            return res.IsSuccessStatusCode;
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Falha ao publicar no ntfy");
            return false;
        }
    }
}

/// <summary>Resultado da entrega para um destino (Id do aparelho ou "ntfy").</summary>
public sealed record DeliveryResult(string Target, string Channel, PushResult Result);
