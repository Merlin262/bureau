using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Bureau.Infra;

/// <summary>
/// Fontes de traces e métricas do BUREAU (APIs nativas do .NET). Sem exportador configurado
/// ninguém escuta: StartActivity devolve null e os contadores não custam nada.
/// O OpenTelemetry (Program.cs) só liga a coleta quando há um endpoint OTLP.
/// </summary>
public static class Telemetry
{
    public const string Name = "Bureau";

    public static readonly ActivitySource Source = new(Name);
    public static readonly Meter Meter = new(Name);

    public static readonly Counter<long> AlertsRaised =
        Meter.CreateCounter<long>("bureau.alerts.raised", "{alert}", "Alertas disparados pelo motor de alertas");

    public static readonly Counter<long> PushDeliveries =
        Meter.CreateCounter<long>("bureau.push.deliveries", "{delivery}", "Tentativas de entrega de push por canal e resultado");

    public static readonly Histogram<double> PushDuration =
        Meter.CreateHistogram<double>("bureau.push.duration", "s", "Duração de cada entrega de push");

    public static readonly Counter<long> OutboxRetries =
        Meter.CreateCounter<long>("bureau.outbox.retries", "{retry}", "Alertas reagendados após falha de entrega");

    public static readonly Counter<long> OutboxDeadLetters =
        Meter.CreateCounter<long>("bureau.outbox.dead_letters", "{alert}", "Alertas descartados após esgotar as tentativas");

    /// <summary>Tamanho atual da outbox (definido pela AlertOutbox).</summary>
    public static Func<int> OutboxPending { get; set; } = () => 0;

    static readonly ObservableGauge<int> _outboxPending =
        Meter.CreateObservableGauge("bureau.outbox.pending", () => OutboxPending(), "{alert}", "Alertas aguardando entrega");

    public static readonly Histogram<double> OutboxLag =
        Meter.CreateHistogram<double>("bureau.outbox.lag", "s", "Tempo entre o alerta disparar e ser entregue");
}
