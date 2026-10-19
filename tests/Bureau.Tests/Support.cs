using System.Collections.Concurrent;
using System.Text.Json;
using Bureau.Infra;
using Bureau.Modules;
using Bureau.Push;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bureau.Tests;

/// <summary>Diretório temporário (DataDir) apagado no fim do teste.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("bureau-test-").FullName;

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { /* arquivo ainda aberto */ }
    }
}

/// <summary>IOptionsMonitor fixo (sem recarga) para construir os módulos diretamente.</summary>
public sealed class StaticMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue { get; set; } = value;
    public T Get(string? name) => CurrentValue;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

/// <summary>Monta o grafo de dependências dos módulos com um DataDir temporário.</summary>
public sealed class Fx : IDisposable
{
    public TempDir Dir { get; } = new();
    public BureauOptions Options { get; }
    public StaticMonitor<BureauOptions> Monitor { get; }
    public EventHub Hub { get; } = new();

    public Fx(Action<BureauOptions>? configure = null)
    {
        Options = new BureauOptions { DataDir = Dir.Path };
        configure?.Invoke(Options);
        Monitor = new StaticMonitor<BureauOptions>(Options);
    }

    public IOptions<BureauOptions> Opt => Microsoft.Extensions.Options.Options.Create(Options);

    public static ILogger<T> Log<T>() => NullLogger<T>.Instance;

    PushStore? _push;
    public PushStore Push => _push ??= new PushStore(Opt);

    WebPushSender? _web;
    public WebPushSender Web => _web ??= new WebPushSender(Monitor, Log<WebPushSender>());

    public FcmSender Fcm => field ??= new FcmSender(Monitor, Log<FcmSender>());
    public ApnsSender Apns => field ??= new ApnsSender(Monitor, Log<ApnsSender>());
    public Notifier Notifier => field ??= new Notifier(Push, Web, Fcm, Apns, Monitor, Log<Notifier>());
    public Microsoft.Extensions.Time.Testing.FakeTimeProvider Time { get; } = new(DateTimeOffset.UtcNow);
    public AlertOutbox Outbox => field ??= new AlertOutbox(Notifier, Opt, Log<AlertOutbox>(), Time);
    public AlertEngine Alerts => field ??= new AlertEngine(Hub, Notifier, Outbox, Monitor, Log<AlertEngine>(), Time);
    public PiHoleModule PiHole => field ??= new PiHoleModule(Hub, Alerts, Monitor, Log<PiHoleModule>());
    public NetworkModule Network => field ??= new NetworkModule(Hub, PiHole, Alerts, Opt);

    public void Dispose()
    {
        _push = null;
        Dir.Dispose();
    }
}

public static class HubExtensions
{
    /// <summary>Último valor publicado num tópico (como um cliente SSE recém-conectado receberia).</summary>
    public static JsonElement? Last(this EventHub hub, string topic)
    {
        using var sub = hub.Subscribe();
        JsonElement? found = null;
        while (sub.Reader.TryRead(out var msg))
        {
            var lines = msg.Split('\n');
            if (lines[0] != $"event: {topic}") continue;
            found = JsonDocument.Parse(lines[1]["data: ".Length..]).RootElement.Clone();
        }
        return found;
    }

    public static JsonElement Require(this EventHub hub, string topic) =>
        hub.Last(topic) ?? throw new Xunit.Sdk.XunitException($"Tópico '{topic}' não publicado");
}

/// <summary>
/// Servidor HTTP de verdade (Kestrel em 127.0.0.1, porta livre) para simular Pi-hole, Home Assistant,
/// Jellyfin, ntfy, FCM… Cada rota é "MÉTODO /caminho" (sem a query) → função que escreve a resposta.
/// </summary>
public sealed class FakeHttp : IAsyncDisposable
{
    readonly WebApplication _app;
    public ConcurrentDictionary<string, Func<HttpContext, Task>> Routes { get; } = new();
    public ConcurrentQueue<(string Method, string PathAndQuery, string Body, Dictionary<string, string> Headers)> Requests { get; } = new();
    public string Url { get; }

    FakeHttp(WebApplication app, string url)
    {
        _app = app;
        Url = url;
    }

    /// <param name="http2">Só HTTP/2 sem TLS (h2c), como o APNs exige.</param>
    public static async Task<FakeHttp> StartAsync(bool http2 = false)
    {
        var b = WebApplication.CreateSlimBuilder();
        b.Logging.ClearProviders();
        b.WebHost.UseUrls("http://127.0.0.1:0");
        if (http2) b.WebHost.ConfigureKestrel(k => k.ConfigureEndpointDefaults(l => l.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2));
        var app = b.Build();
        FakeHttp? self = null;
        app.Run(async ctx =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync();
            self!.Requests.Enqueue((ctx.Request.Method, ctx.Request.Path + ctx.Request.QueryString, body,
                ctx.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase)));
            if (self.Routes.TryGetValue($"{ctx.Request.Method} {ctx.Request.Path}", out var h)) await h(ctx);
            else ctx.Response.StatusCode = 404;
        });
        await app.StartAsync();
        var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        self = new FakeHttp(app, url);
        return self;
    }

    public FakeHttp Json(string route, string json, int status = 200)
    {
        Routes[route] = async ctx =>
        {
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(json);
        };
        return this;
    }

    public FakeHttp Status(string route, int status)
    {
        Routes[route] = ctx => { ctx.Response.StatusCode = status; return Task.CompletedTask; };
        return this;
    }

    public int Count(string method, string pathPrefix) =>
        Requests.Count(r => r.Method == method && r.PathAndQuery.StartsWith(pathPrefix, StringComparison.Ordinal));

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

public static class Snap
{
    public static SystemSnapshot System(double cpu = 10, long memTotal = 1000, long memUsed = 500, double? temp = 50,
        BatteryInfo? battery = null, double rx = 100, double tx = 50) =>
        new(0, cpu, [cpu], [0.1, 0.2, 0.3], memTotal, memUsed, 0, 0, temp, battery, rx, tx, 1000);
}

/// <summary>Testes que mexem em estado global do processo (variáveis de ambiente, Console, app inteiro): um de cada vez.</summary>
[CollectionDefinition("env", DisableParallelization = true)]
public sealed class EnvCollection;
