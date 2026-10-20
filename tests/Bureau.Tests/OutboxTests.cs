using System.Diagnostics;
using System.Security.Cryptography;
using Bureau.Infra;
using Bureau.Push;

namespace Bureau.Tests;

public class OutboxTests
{
    static readonly AlertEvent Alert = new(1, "network", "crit", "Internet caiu", "Sem resposta", "rede");

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static PushDevice WebDevice(string id, string endpoint)
    {
        var ua = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var q = ua.ExportParameters(false).Q;
        return new(id, "webpush", id, null, endpoint, B64Url.Encode([0x04, .. q.X!, .. q.Y!]),
            B64Url.Encode(RandomNumberGenerator.GetBytes(16)), null, 0);
    }

    [Fact]
    public async Task Falha_fica_na_fila_e_entrega_no_retry()
    {
        await using var http = await FakeHttp.StartAsync();
        http.Status("POST /", 503);
        using var fx = new Fx(o => o.Push.NtfyUrl = http.Url + "/t");

        fx.Outbox.Enqueue(Alert);
        Assert.Equal(0, await fx.Outbox.ProcessDueAsync(Ct));
        var item = Assert.Single(fx.Outbox.Pending);
        Assert.Equal(1, item.Attempts);

        // A interface recebe a fila pelo stream: título, tentativas e canal com falha.
        var entry = Assert.Single(fx.Hub.Require("outbox").EnumerateArray());
        Assert.Equal("Internet caiu", entry.GetProperty("title").GetString());
        Assert.Equal(1, entry.GetProperty("attempts").GetInt32());
        Assert.Equal("ntfy", entry.GetProperty("failing")[0].GetString());
        Assert.True(entry.GetProperty("nextAttemptAt").GetInt64() > fx.Time.GetUtcNow().ToUnixTimeMilliseconds());

        // Antes do backoff vencer: não tenta de novo.
        Assert.Equal(0, await fx.Outbox.ProcessDueAsync(Ct));
        Assert.Equal(1, http.Count("POST", "/"));

        http.Json("POST /", "{}");
        fx.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, await fx.Outbox.ProcessDueAsync(Ct));
        Assert.Empty(fx.Outbox.Pending);
        Assert.Equal(0, fx.Hub.Require("outbox").GetArrayLength());
        Assert.Equal(2, http.Count("POST", "/"));
    }

    [Fact]
    public async Task Retry_nao_repete_em_quem_ja_recebeu()
    {
        await using var http = await FakeHttp.StartAsync();
        http.Status("POST /push/ok", 201).Status("POST /", 500);
        using var fx = new Fx(o => o.Push.NtfyUrl = http.Url + "/t");
        fx.Push.Upsert(WebDevice("w1", http.Url + "/push/ok"));

        fx.Outbox.Enqueue(Alert);
        await fx.Outbox.ProcessDueAsync(Ct);
        Assert.Equal(["w1"], Assert.Single(fx.Outbox.Pending).Delivered);

        http.Json("POST /", "{}");
        fx.Time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(1, await fx.Outbox.ProcessDueAsync(Ct));
        Assert.Equal(1, http.Count("POST", "/push/ok")); // celular não recebe o alerta duas vezes
        Assert.Equal(2, http.Requests.Count(r => r.PathAndQuery == "/"));
    }

    [Fact]
    public async Task Inscricao_expirada_nao_trava_a_fila()
    {
        await using var http = await FakeHttp.StartAsync();
        http.Status("POST /push/gone", 410);
        using var fx = new Fx();
        fx.Push.Upsert(WebDevice("w1", http.Url + "/push/gone"));

        fx.Outbox.Enqueue(Alert);
        Assert.Equal(1, await fx.Outbox.ProcessDueAsync(Ct));
        Assert.Empty(fx.Push.All);
    }

    [Fact]
    public async Task Sem_destinos_conclui_na_hora()
    {
        using var fx = new Fx();
        fx.Outbox.Enqueue(Alert);
        Assert.Equal(1, await fx.Outbox.ProcessDueAsync(Ct));
        Assert.Empty(fx.Outbox.Pending);
    }

    [Fact]
    public async Task Desiste_depois_do_maximo_de_tentativas()
    {
        await using var http = await FakeHttp.StartAsync();
        http.Status("POST /", 500);
        using var fx = new Fx(o => o.Push.NtfyUrl = http.Url + "/t");

        fx.Outbox.Enqueue(Alert);
        for (var i = 0; i < AlertOutbox.MaxAttempts; i++)
        {
            Assert.Single(fx.Outbox.Pending);
            await fx.Outbox.ProcessDueAsync(Ct);
            fx.Time.Advance(TimeSpan.FromHours(1));
        }
        Assert.Empty(fx.Outbox.Pending);
        Assert.Equal(AlertOutbox.MaxAttempts, http.Count("POST", "/"));
    }

    [Fact]
    public void Fila_sobrevive_a_reinicio()
    {
        using var fx = new Fx();
        var item = fx.Outbox.Enqueue(Alert);

        var reloaded = new AlertOutbox(fx.Notifier, new EventHub(), fx.Opt, Fx.Log<AlertOutbox>(), fx.Time);
        var p = Assert.Single(reloaded.Pending);
        Assert.Equal(item.Id, p.Id);
        Assert.Equal(Alert, p.Alert);
    }

    [Fact]
    public void Fila_limitada_descarta_os_mais_antigos()
    {
        using var fx = new Fx();
        for (var i = 0; i < AlertOutbox.Capacity + 3; i++) fx.Outbox.Enqueue(Alert with { Ts = i });
        Assert.Equal(AlertOutbox.Capacity, fx.Outbox.Pending.Length);
        Assert.Equal(3, fx.Outbox.Pending[0].Alert.Ts);
    }

    [Fact]
    public void Backoff_exponencial_com_teto()
    {
        var first = AlertOutbox.Backoff(1);
        Assert.InRange(first.TotalSeconds, 12, 18);
        Assert.InRange(AlertOutbox.Backoff(3).TotalSeconds, 48, 72);
        Assert.InRange(AlertOutbox.Backoff(50).TotalMinutes, 24, 36);
    }

    [Fact]
    public void Motor_de_alertas_grava_na_outbox()
    {
        using var fx = new Fx();
        fx.Alerts.Simulate("power");
        Assert.Equal("Queda de energia", Assert.Single(fx.Outbox.Pending).Alert.Title);

        fx.Options.Alerts.Enabled = false;
        fx.Alerts.Simulate("disk");
        Assert.Single(fx.Outbox.Pending);
    }

    [Fact]
    public async Task Trace_atravessa_a_fila()
    {
        var spans = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == Telemetry.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => { lock (spans) spans.Add(a); },
        };
        ActivitySource.AddActivityListener(listener);
        using var fx = new Fx();

        ActivityTraceId traceId;
        using (var root = Telemetry.Source.StartActivity("collect teste"))
        {
            traceId = root!.TraceId;
            fx.Outbox.Enqueue(Alert);
        }
        await fx.Outbox.ProcessDueAsync(Ct);

        Activity[] mine;
        lock (spans) mine = [.. spans.Where(s => s.TraceId == traceId)];
        Assert.Equal(["alert.deliver", "alert.enqueue", "collect teste"], mine.Select(s => s.OperationName).Order());
        var enqueue = mine.Single(s => s.OperationName == "alert.enqueue");
        Assert.Equal(enqueue.SpanId, mine.Single(s => s.OperationName == "alert.deliver").ParentSpanId);
    }

    [Fact]
    public async Task Worker_acorda_quando_chega_alerta()
    {
        await using var http = await FakeHttp.StartAsync();
        http.Json("POST /", "{}");
        using var fx = new Fx(o => o.Push.NtfyUrl = http.Url + "/t");

        await fx.Outbox.StartAsync(Ct);
        fx.Outbox.Enqueue(Alert);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (http.Count("POST", "/") == 0 && DateTime.UtcNow < deadline) await Task.Delay(20, Ct);
        await fx.Outbox.StopAsync(Ct);

        Assert.Equal(1, http.Count("POST", "/"));
        Assert.Empty(fx.Outbox.Pending);
    }
}
