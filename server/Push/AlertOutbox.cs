using System.Diagnostics;
using Bureau.Infra;
using Microsoft.Extensions.Options;

namespace Bureau.Push;

/// <summary>Alerta aguardando entrega (DataDir/outbox.json).</summary>
/// <param name="Delivered">Destinos que já receberam (Id do aparelho ou "ntfy"): o retry não repete neles.</param>
/// <param name="TraceParent">Contexto W3C de quem disparou o alerta, para o trace atravessar a fila.</param>
/// <param name="Failing">Canais que falharam na última tentativa (mostrado na interface).</param>
public sealed record OutboxItem(string Id, AlertEvent Alert, long CreatedAt, int Attempts, long NextAttemptAt,
    string[] Delivered, string? TraceParent, string[]? Failing = null);

/// <summary>
/// Outbox de alertas: o motor de alertas só grava o alerta aqui (rápido e durável) e este worker
/// entrega em segundo plano, com retry e backoff exponencial. Assim um "internet caiu" não se perde
/// porque o FCM estava fora do ar justamente naquela hora, nem se o processo reiniciar no meio.
/// </summary>
public sealed class AlertOutbox : BackgroundService
{
    public const int MaxAttempts = 10;
    public const int Capacity = 500;
    static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(15);
    static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(30);

    readonly Notifier _notifier;
    readonly EventHub _hub;
    readonly ILogger<AlertOutbox> _log;
    readonly TimeProvider _time;
    readonly string _path;
    readonly Lock _lock = new();
    readonly List<OutboxItem> _items;
    TaskCompletionSource _wake = NewTcs();

    public AlertOutbox(Notifier notifier, EventHub hub, IOptions<BureauOptions> o, ILogger<AlertOutbox> log, TimeProvider? time = null)
    {
        _notifier = notifier;
        _hub = hub;
        _log = log;
        _time = time ?? TimeProvider.System;
        _path = Path.Combine(o.Value.DataDir, "outbox.json");
        _items = [.. JsonFile.Load(_path, AppJson.Default.OutboxItemArray) ?? []];
        Telemetry.OutboxPending = () => Pending.Length;
        Publish();
    }

    public OutboxItem[] Pending { get { lock (_lock) return [.. _items]; } }

    long Now => _time.GetUtcNow().ToUnixTimeMilliseconds();

    /// <summary>Grava o alerta para entrega e acorda o worker. Não faz rede: pode ser chamado dentro de locks.</summary>
    public OutboxItem Enqueue(AlertEvent alert)
    {
        using var span = Telemetry.Source.StartActivity("alert.enqueue", ActivityKind.Producer);
        span?.SetTag("alert.kind", alert.Kind);
        span?.SetTag("alert.level", alert.Level);

        var item = new OutboxItem(Guid.NewGuid().ToString("N"), alert, Now, 0, Now, [], Activity.Current?.Id);
        lock (_lock)
        {
            _items.Add(item);
            // Fila limitada: se a entrega ficar parada muito tempo, descarta os mais antigos (como o EventHub).
            if (_items.Count > Capacity)
            {
                var dropped = _items.Count - Capacity;
                _items.RemoveRange(0, dropped);
                Telemetry.OutboxDeadLetters.Add(dropped, new KeyValuePair<string, object?>("reason", "capacity"));
                _log.LogWarning("Outbox cheia: {Count} alerta(s) antigo(s) descartado(s)", dropped);
            }
            Save();
            Publish();
        }
        Interlocked.Exchange(ref _wake, NewTcs()).TrySetResult();
        return item;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Yield();
        while (!ct.IsCancellationRequested)
        {
            // Pega o sinal antes de processar: um Enqueue durante o processamento não se perde.
            var wake = Volatile.Read(ref _wake).Task;
            try { await ProcessDueAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e) { _log.LogError(e, "Falha no processamento da outbox"); }

            var next = NextDue();
            var delay = next is null ? MaxDelay : TimeSpan.FromMilliseconds(Math.Clamp(next.Value - Now, 0, MaxDelay.TotalMilliseconds));
            try { await Task.WhenAny(Task.Delay(delay, _time, ct), wake); }
            catch (OperationCanceledException) { break; }
        }
    }

    long? NextDue()
    {
        lock (_lock) return _items.Count == 0 ? null : _items.Min(i => i.NextAttemptAt);
    }

    /// <summary>Tenta entregar os alertas vencidos. Devolve quantos foram concluídos (entregues em todos os destinos).</summary>
    internal async Task<int> ProcessDueAsync(CancellationToken ct = default)
    {
        var now = Now;
        OutboxItem[] due;
        lock (_lock) due = [.. _items.Where(i => i.NextAttemptAt <= now)];

        var done = 0;
        foreach (var item in due)
        {
            ct.ThrowIfCancellationRequested();
            var updated = await DeliverAsync(item, ct);
            lock (_lock)
            {
                var idx = _items.FindIndex(i => i.Id == item.Id);
                if (idx < 0) continue;
                if (updated is null) { _items.RemoveAt(idx); done++; }
                else _items[idx] = updated;
                Save();
                Publish();
            }
        }
        return done;
    }

    /// <summary>Uma tentativa de entrega. Devolve null quando o item sai da fila (entregue ou desistido).</summary>
    async Task<OutboxItem?> DeliverAsync(OutboxItem item, CancellationToken ct)
    {
        // Continua o trace de quem disparou o alerta (como um consumidor Kafka lendo o header traceparent).
        ActivityContext.TryParse(item.TraceParent, null, out var parent);
        using var span = Telemetry.Source.StartActivity("alert.deliver", ActivityKind.Consumer, parent);
        span?.SetTag("alert.kind", item.Alert.Kind);
        span?.SetTag("outbox.attempt", item.Attempts + 1);

        DeliveryResult[] results;
        try { results = await _notifier.DeliverAsync(item.Alert, item.Delivered, ct: ct); }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.LogWarning(e, "Falha ao entregar alerta {Title}", item.Alert.Title);
            results = [new DeliveryResult("?", "interno", PushResult.Failed)];
        }

        // Gone: inscrição expirou e já foi removida, não adianta tentar de novo.
        var delivered = item.Delivered
            .Concat(results.Where(r => r.Result != PushResult.Failed && r.Target != "?").Select(r => r.Target))
            .Distinct().ToArray();
        var failed = results.Count(r => r.Result == PushResult.Failed);

        if (failed == 0)
        {
            Telemetry.OutboxLag.Record((Now - item.CreatedAt) / 1000.0);
            return null;
        }

        var attempts = item.Attempts + 1;
        span?.SetStatus(ActivityStatusCode.Error, $"{failed} destino(s) falharam");
        if (attempts >= MaxAttempts)
        {
            Telemetry.OutboxDeadLetters.Add(1, new KeyValuePair<string, object?>("reason", "max_attempts"));
            _log.LogError("Alerta {Title} descartado após {Attempts} tentativas ({Failed} destino(s) sem entrega)",
                item.Alert.Title, attempts, failed);
            return null;
        }

        var delay = Backoff(attempts);
        Telemetry.OutboxRetries.Add(1);
        _log.LogInformation("Alerta {Title}: {Failed} destino(s) falharam, nova tentativa em {Delay}",
            item.Alert.Title, failed, delay);
        var failing = results.Where(r => r.Result == PushResult.Failed).Select(r => r.Channel).Distinct().ToArray();
        return item with { Attempts = attempts, NextAttemptAt = Now + (long)delay.TotalMilliseconds, Delivered = delivered, Failing = failing };
    }

    /// <summary>15 s, 30 s, 1 min… até 30 min, com ±20% de jitter para não sincronizar tentativas.</summary>
    internal static TimeSpan Backoff(int attempt)
    {
        var seconds = Math.Min(BaseDelay.TotalSeconds * Math.Pow(2, attempt - 1), MaxDelay.TotalSeconds);
        return TimeSpan.FromSeconds(seconds * (0.8 + Random.Shared.NextDouble() * 0.4));
    }

    /// <summary>Estado da fila para a interface (tópico "outbox" do stream). Chamado com o lock.</summary>
    void Publish() => _hub.Publish("outbox",
        _items.Select(i => new OutboxEntry(i.Alert.Title, i.Alert.Level, i.Attempts, i.NextAttemptAt, i.Failing ?? [])).ToArray(),
        AppJson.Default.OutboxEntryArray);

    void Save()
    {
        try { JsonFile.Save(_path, _items.ToArray(), AppJson.Default.OutboxItemArray); }
        catch (Exception e) { _log.LogWarning(e, "Não foi possível gravar a outbox"); }
    }

    static TaskCompletionSource NewTcs() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
