using System.Threading.RateLimiting;
using Bureau;
using Bureau.Infra;
using Bureau.Modules;
using Bureau.Push;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var configPath = ConfigPaths.Resolve();

if (args.Length > 0 && args[0] == "set-password")
    return Cli.SetPassword(configPath);

// Publicado: wwwroot fica ao lado do binário (funciona de qualquer diretório). Em dev: diretório atual.
var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = Directory.Exists(Path.Combine(AppContext.BaseDirectory, "wwwroot"))
        ? AppContext.BaseDirectory
        : Directory.GetCurrentDirectory(),
});
builder.Configuration.AddJsonFile(configPath, optional: true, reloadOnChange: true);
builder.Configuration.AddEnvironmentVariables("BUREAU_");

var opts = builder.Configuration.Get<BureauOptions>() ?? new BureauOptions();
builder.Services.Configure<BureauOptions>(builder.Configuration);
Directory.CreateDirectory(opts.DataDir);

builder.WebHost.ConfigureKestrel(k =>
{
    k.AddServerHeader = false;
    k.ListenAnyIP(opts.Port);
});

// Sessão via cookie (30 dias, renovação automática). Chaves persistidas p/ sobreviver a reinícios.
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(opts.DataDir, "keys")))
    .SetApplicationName("bureau");
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "bureau";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.ExpireTimeSpan = TimeSpan.FromDays(30);
        o.SlidingExpiration = true;
        o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; };
        o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; };
    });
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "?",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(1) }));
});
builder.Services.Configure<ForwardedHeadersOptions>(o =>
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJson.Default));

// Observabilidade: traces, métricas e logs via OTLP (Grafana Tempo/Mimir/Loki, Collector…). Só liga com endpoint.
var otlp = opts.Telemetry.OtlpEndpoint ?? Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
if (!string.IsNullOrWhiteSpace(otlp))
{
    // Conexões longas (SSE e terminal) virariam spans de horas: ficam fora dos traces.
    static bool Traced(HttpContext ctx) =>
        !ctx.Request.Path.StartsWithSegments("/api/stream") && !ctx.Request.Path.StartsWithSegments("/api/terminal/ws");

    builder.Logging.AddOpenTelemetry(l => { l.IncludeFormattedMessage = true; l.IncludeScopes = true; });
    var otel = builder.Services.AddOpenTelemetry()
        .ConfigureResource(r => r.AddService("bureau", serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString()))
        .WithTracing(t => t
            .AddSource(Telemetry.Name)
            .AddAspNetCoreInstrumentation(o => o.Filter = Traced)
            .AddHttpClientInstrumentation())
        .WithMetrics(m => m
            .AddMeter(Telemetry.Name)
            .AddMeter("System.Runtime") // GC, threads, memória (nativo no .NET 9+)
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation());
    if (!string.IsNullOrWhiteSpace(opts.Telemetry.OtlpEndpoint))
        otel.UseOtlpExporter(OtlpExportProtocol.Grpc, new Uri(opts.Telemetry.OtlpEndpoint));
    else
        otel.UseOtlpExporter(); // lê OTEL_EXPORTER_OTLP_* do ambiente
}

builder.Services.AddSingleton<EventHub>();
builder.Services.AddSingleton<WatchStore>();
builder.Services.AddSingleton<ServicesModule>();
builder.Services.AddSingleton<DockerModule>();
builder.Services.AddSingleton<PiHoleModule>();
builder.Services.AddSingleton<NetworkModule>();
builder.Services.AddSingleton<WakeTargets>();
builder.Services.AddSingleton<DeviceTokens>();
builder.Services.AddSingleton<AccessLog>();
builder.Services.AddSingleton<MonitorsModule>();
builder.Services.AddSingleton<DeviceBlocks>();
builder.Services.AddSingleton<TerminalModule>();
builder.Services.AddSingleton<ConfigBackup>();
builder.Services.AddSingleton<PushStore>();
builder.Services.AddSingleton<WebPushSender>();
builder.Services.AddSingleton<FcmSender>();
builder.Services.AddSingleton<ApnsSender>();
builder.Services.AddSingleton<Notifier>();
builder.Services.AddSingleton<AlertOutbox>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AlertOutbox>());
builder.Services.AddSingleton<AlertEngine>();
builder.Services.AddSingleton<UpdateModule>();
builder.Services.AddSingleton<AptModule>();
builder.Services.AddSingleton<BackupsModule>();
builder.Services.AddSingleton<HomeAssistantModule>();
builder.Services.AddSingleton<JellyfinModule>();
builder.Services.AddSingleton(new MetricsStore(opts.DataDir));
builder.Services.AddHostedService<SystemMonitor>();
builder.Services.AddHostedService<Collectors>();

var app = builder.Build();
app.Services.GetRequiredService<WakeTargets>(); // publica a lista do Wake-on-LAN no stream desde o início

if (string.IsNullOrEmpty(opts.PasswordHash) && !app.Environment.IsDevelopment())
    app.Logger.LogWarning("Nenhuma senha definida. Rode: sudo /opt/bureau/bureau set-password");

app.UseForwardedHeaders();
app.Use(Security.Headers);
app.UseAuthentication();
app.Use(DeviceTokens.Middleware);
app.UseAuthorization();
app.UseRateLimiter();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

app.MapApi();
app.Map("/api/{**rest}", () => Results.NotFound());
app.UseStaticWeb();

app.Logger.LogInformation("BUREAU online na porta {Port}", opts.Port);
app.Run();
return 0;

/// <summary>Visível para os testes de integração (WebApplicationFactory).</summary>
public partial class Program;
