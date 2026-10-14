using System.Reflection;
using System.Security.Claims;
using Bureau.Infra;
using Bureau.Modules;
using Bureau.Push;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace Bureau;

public static class Api
{
    public static readonly string Version =
        (typeof(Api).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0")
        .Split('+')[0];

    public static void MapApi(this WebApplication app)
    {
        // ── Autenticação ────────────────────────────────────
        var auth = app.MapGroup("/api/auth");

        auth.MapPost("/login", async (LoginRequest req, HttpContext ctx, IOptionsMonitor<BureauOptions> o, IHostEnvironment env) =>
        {
            var hash = o.CurrentValue.PasswordHash;
            var ok = string.IsNullOrEmpty(hash) && env.IsDevelopment()
                ? req.Password == "bureau" // senha padrão só em desenvolvimento
                : PasswordHasher.Verify(req.Password ?? "", hash);
            if (!ok)
            {
                await Task.Delay(350);
                return Results.Json(new OkResponse(false, "ACESSO NEGADO"), AppJson.Default.OkResponse, statusCode: 401);
            }
            var id = new ClaimsIdentity([new Claim(ClaimTypes.Name, "admin")], CookieAuthenticationDefaults.AuthenticationScheme);
            await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(id),
                new AuthenticationProperties { IsPersistent = true });
            return Results.Json(new OkResponse(true, null), AppJson.Default.OkResponse);
        }).RequireRateLimiting("login");

        auth.MapPost("/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return TypedResults.Ok(new OkResponse(true, null));
        });

        auth.MapGet("/me", (HttpContext ctx, IOptionsMonitor<BureauOptions> o) =>
        {
            var authed = ctx.User.Identity?.IsAuthenticated == true;
            return TypedResults.Ok(new MeResponse(authed, authed ? Environment.MachineName : null, Version, o.CurrentValue.AllowPower));
        });

        // App mobile: troca a senha por um token de aparelho (revogável).
        auth.MapPost("/token", async (TokenRequest req, IOptionsMonitor<BureauOptions> o, IHostEnvironment env, DeviceTokens tokens) =>
        {
            var hash = o.CurrentValue.PasswordHash;
            var ok = string.IsNullOrEmpty(hash) && env.IsDevelopment()
                ? req.Password == "bureau"
                : PasswordHasher.Verify(req.Password ?? "", hash);
            if (!ok)
            {
                await Task.Delay(350);
                return Results.Json(new OkResponse(false, "ACESSO NEGADO"), AppJson.Default.OkResponse, statusCode: 401);
            }
            var (token, entry) = tokens.Create(req.Name ?? "Aparelho");
            return Results.Json(new TokenResponse(token, entry.Id), AppJson.Default.TokenResponse);
        }).RequireRateLimiting("login");

        var api = app.MapGroup("/api").RequireAuthorization();

        api.MapGet("/auth/tokens", (HttpContext ctx, DeviceTokens tokens) =>
        {
            var me = ctx.User.FindFirst("device")?.Value;
            return TypedResults.Ok(tokens.All.Select(t => new DeviceSummary(t.Id, t.Name, "app", t.Created, t.Id == me)).ToArray());
        });

        api.MapDelete("/auth/tokens/{id}", (string id, HttpContext ctx, DeviceTokens tokens, PushStore push) =>
        {
            if (id == "self") id = ctx.User.FindFirst("device")?.Value ?? "";
            var ok = tokens.Revoke(id);
            push.RemoveWhere(d => d.Owner == id);
            return TypedResults.Ok(new OkResponse(ok, ok ? null : "Aparelho não encontrado"));
        });

        // ── Notificações push ───────────────────────────────
        api.MapGet("/push/config", (HttpContext ctx, WebPushSender web, FcmSender fcm, Notifier n) =>
            TypedResults.Ok(new PushConfig(web.PublicKey, fcm.Configured, n.NtfyConfigured, ctx.User.FindFirst("device")?.Value)));

        api.MapGet("/push/devices", (PushStore push) =>
            TypedResults.Ok(push.All.Select(d => new DeviceSummary(d.Id, d.Name, d.Kind, d.Created, false)).ToArray()));

        api.MapPost("/push/register", (PushRegisterRequest req, HttpContext ctx, PushStore push) =>
        {
            var valid = req.Kind switch
            {
                "fcm" => !string.IsNullOrWhiteSpace(req.Token) && req.Token.Length < 4096,
                "webpush" => Uri.TryCreate(req.Endpoint, UriKind.Absolute, out var u) && u.Scheme == "https"
                             && !string.IsNullOrEmpty(req.P256dh) && !string.IsNullOrEmpty(req.Auth),
                _ => false,
            };
            if (!valid) return TypedResults.Ok(new OkResponse(false, "Inscrição inválida"));

            var name = string.IsNullOrWhiteSpace(req.Name) ? (req.Kind == "fcm" ? "Android" : "Navegador") : req.Name.Trim()[..Math.Min(req.Name.Trim().Length, 60)];
            var d = push.Upsert(new PushDevice(Guid.NewGuid().ToString("N")[..12], req.Kind, name,
                req.Kind == "fcm" ? req.Token : null,
                req.Kind == "webpush" ? req.Endpoint : null,
                req.Kind == "webpush" ? req.P256dh : null,
                req.Kind == "webpush" ? req.Auth : null,
                ctx.User.FindFirst("device")?.Value,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
            return TypedResults.Ok(new OkResponse(true, d.Id));
        });

        if (app.Environment.IsDevelopment())
            api.MapPost("/dev/simulate/{kind}", (string kind, AlertEngine alerts) =>
            {
                alerts.Simulate(kind);
                return TypedResults.Ok(new OkResponse(true, null));
            });

        api.MapDelete("/push/{id}",(string id, PushStore push) =>
            TypedResults.Ok(new OkResponse(push.Remove(id), null)));

        api.MapPost("/push/test", async (AlertEngine alerts) =>
        {
            var sent = await alerts.TestAsync(null);
            return TypedResults.Ok(sent > 0
                ? new OkResponse(true, $"Teste enviado para {sent} destino(s)")
                : new OkResponse(false, "Nada foi entregue. Ative as notificações em um aparelho (e confira o Firebase/ntfy na configuração)."));
        });

        // ── Stream em tempo real (Server-Sent Events) ──────
        api.MapGet("/stream", async (HttpContext ctx, EventHub hub) =>
        {
            var ct = ctx.RequestAborted;
            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache, no-transform";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";
            ctx.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

            using var sub = hub.Subscribe();
            try
            {
                await ctx.Response.WriteAsync("retry: 1500\n\n", ct);
                await ctx.Response.Body.FlushAsync(ct);
                while (await sub.Reader.WaitToReadAsync(ct))
                {
                    while (sub.Reader.TryRead(out var msg)) await ctx.Response.WriteAsync(msg, ct);
                    await ctx.Response.Body.FlushAsync(ct);
                }
            }
            catch (OperationCanceledException) { /* cliente desconectou */ }
        });

        // ── Serviços systemd ────────────────────────────────
        api.MapGet("/services/available", async (ServicesModule s) => TypedResults.Ok(await s.ListAvailableAsync()));

        api.MapPut("/services/watch", async (WatchRequest req, WatchStore w, ServicesModule s) =>
        {
            w.Set(req.Units);
            await s.RefreshAsync();
            return TypedResults.Ok(new OkResponse(true, null));
        });

        api.MapPost("/services/{unit}/{action}", async (string unit, string action, ServicesModule s) =>
            TypedResults.Ok(await s.ActionAsync(unit, action)));

        api.MapGet("/services/{unit}/logs", async (string unit, ServicesModule s) =>
            TypedResults.Ok(new LogsResponse(await s.LogsAsync(unit))));

        // ── Docker ──────────────────────────────────────────
        api.MapPost("/containers/{id}/{action}", async (string id, string action, DockerModule d) =>
            TypedResults.Ok(await d.ActionAsync(id, action)));

        api.MapGet("/containers/{id}/logs", async (string id, DockerModule d) =>
            TypedResults.Ok(new LogsResponse(await d.LogsAsync(id))));

        // ── Pi-hole ─────────────────────────────────────────
        api.MapPost("/pihole/blocking", async (BlockingRequest req, PiHoleModule p) =>
            TypedResults.Ok(await p.SetBlockingAsync(req.Enabled, req.Timer)));

        // ── Rede ────────────────────────────────────────────
        api.MapPost("/network/wol", async (WolRequest req) =>
            TypedResults.Ok(await NetworkModule.WakeAsync(req.Mac)
                ? new OkResponse(true, "Pacote mágico enviado")
                : new OkResponse(false, "MAC inválido")));

        api.MapPut("/network/alias", (AliasRequest req, NetworkModule net) =>
            TypedResults.Ok(net.SetAlias(req.Mac, req.Name) ? new OkResponse(true, null) : new OkResponse(false, "MAC inválido")));

        // ── Energia ─────────────────────────────────────────
        api.MapPost("/power/{action}", (string action, IOptionsMonitor<BureauOptions> o, ILogger<BureauOptions> log) =>
        {
            if (!o.CurrentValue.AllowPower) return TypedResults.Ok(new OkResponse(false, "Desativado na configuração"));
            if (action is not ("reboot" or "poweroff")) return TypedResults.Ok(new OkResponse(false, "Ação inválida"));
            log.LogWarning("Energia: {Action} solicitado pela interface", action);
            _ = Task.Run(async () =>
            {
                await Task.Delay(1000); // deixa a resposta chegar ao cliente
                if (OperatingSystem.IsLinux()) await Proc.RunAsync("systemctl", ["--no-ask-password", action]);
            });
            return TypedResults.Ok(new OkResponse(true, null));
        });
    }
}
