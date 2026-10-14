using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bureau.Infra;
using Microsoft.Extensions.Options;
using Microsoft.Win32.SafeHandles;

namespace Bureau.Modules;

/// <summary>
/// Terminal no navegador/app. Por segurança: desligado por padrão, nunca roda como root nem como o usuário do
/// BUREAU — abre "ssh usuario@127.0.0.1" num pseudoterminal real, então o Linux pede a SENHA DO USUÁRIO
/// (a senha do BUREAU não basta). A conexão WebSocket exige um tíquete de uso único (30 s).
/// </summary>
public sealed partial class TerminalModule(IOptionsMonitor<BureauOptions> opts, ILogger<TerminalModule> log)
{
    const int MaxSessions = 3;
    readonly ConcurrentDictionary<string, DateTime> _tickets = new();
    int _sessions;

    TerminalOptions O => opts.CurrentValue.Terminal;

    public bool Enabled => O.Enabled && (!OperatingSystem.IsLinux() || !string.IsNullOrWhiteSpace(O.User));

    public string? IssueTicket()
    {
        if (!Enabled) return null;
        foreach (var (k, exp) in _tickets) if (exp < DateTime.UtcNow) _tickets.TryRemove(k, out _);
        var t = B64Url.Encode(RandomNumberGenerator.GetBytes(24));
        _tickets[t] = DateTime.UtcNow.AddSeconds(30);
        return t;
    }

    bool Redeem(string? ticket) =>
        ticket is not null && _tickets.TryRemove(ticket, out var exp) && exp > DateTime.UtcNow;

    public async Task HandleAsync(HttpContext ctx)
    {
        if (!ctx.WebSockets.IsWebSocketRequest || !Enabled || !Redeem(ctx.Request.Query["ticket"]))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        if (Interlocked.Increment(ref _sessions) > MaxSessions)
        {
            Interlocked.Decrement(ref _sessions);
            ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            return;
        }
        try
        {
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            if (OperatingSystem.IsLinux()) await RunPtyAsync(ws, ctx.RequestAborted);
            else await RunFakeAsync(ws, ctx.RequestAborted);
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException) { /* fechou */ }
        finally { Interlocked.Decrement(ref _sessions); }
    }

    // ── Linux: ssh dentro de um pseudoterminal ──────────────
    async Task RunPtyAsync(WebSocket ws, CancellationToken ct)
    {
        var master = Native.posix_openpt(Native.O_RDWR | Native.O_NOCTTY);
        if (master < 0 || Native.grantpt(master) != 0 || Native.unlockpt(master) != 0)
            throw new IOException("Não foi possível criar o pseudoterminal");
        var name = new byte[128];
        if (Native.ptsname_r(master, name, (nuint)name.Length) != 0) throw new IOException("ptsname falhou");
        var slave = Encoding.ASCII.GetString(name, 0, Array.IndexOf(name, (byte)0));
        SetSize(master, 120, 32);

        // O shell abre o escravo como stdin/stdout/stderr e o setsid o torna o terminal de controle do ssh.
        var psi = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
        foreach (var a in new[] { "-c", "exec setsid -c \"$@\" <>\"$BUREAU_PTY\" >&0 2>&0", "sh", "ssh", "-tt",
                     "-o", "StrictHostKeyChecking=accept-new",
                     "-o", $"UserKnownHostsFile={Path.Combine(opts.CurrentValue.DataDir, "known_hosts")}",
                     "-o", "PreferredAuthentications=keyboard-interactive,password",
                     "-p", O.Port.ToString(), $"{O.User}@127.0.0.1" })
            psi.ArgumentList.Add(a);
        psi.Environment["BUREAU_PTY"] = slave;
        psi.Environment["TERM"] = "xterm-256color";
        psi.Environment["LANG"] = "C.UTF-8";
        using var proc = Process.Start(psi) ?? throw new IOException("Falha ao iniciar o ssh");
        log.LogWarning("Terminal aberto (ssh {User}@127.0.0.1)", O.User);

        using var handle = new SafeFileHandle(master, ownsHandle: true);
        using var pty = new FileStream(handle, FileAccess.ReadWrite, 1);

        var reader = Task.Run(async () =>
        {
            var buf = new byte[8192];
            try
            {
                int n;
                while ((n = pty.Read(buf, 0, buf.Length)) > 0)
                    await ws.SendAsync(buf.AsMemory(0, n), WebSocketMessageType.Binary, true, ct);
            }
            catch (IOException) { /* EIO: o processo terminou */ }
            if (ws.State == WebSocketState.Open)
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "fim", CancellationToken.None);
        }, ct);

        await PumpInputAsync(ws, data => { pty.Write(data); pty.Flush(); }, (c, r) => SetSize(master, c, r), ct);
        try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { /* já saiu */ }
        await Task.WhenAny(reader, Task.Delay(2000, CancellationToken.None));
    }

    static void SetSize(int fd, int cols, int rows)
    {
        var ws = new Native.WinSize { Row = (ushort)Math.Clamp(rows, 5, 500), Col = (ushort)Math.Clamp(cols, 10, 1000) };
        _ = Native.ioctl(fd, Native.TIOCSWINSZ, ref ws);
    }

    /// <summary>Mensagens binárias = teclas; texto = {"cols":N,"rows":N} (redimensionar).</summary>
    static async Task PumpInputAsync(WebSocket ws, Action<ReadOnlySpan<byte>> write, Action<int, int> resize, CancellationToken ct)
    {
        var buf = new byte[16 * 1024];
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var r = await ws.ReceiveAsync(buf, ct);
            if (r.MessageType == WebSocketMessageType.Close) break;
            if (r.MessageType == WebSocketMessageType.Binary) write(buf.AsSpan(0, r.Count));
            else
            {
                try
                {
                    using var doc = JsonDocument.Parse(buf.AsMemory(0, r.Count));
                    resize(doc.RootElement.GetProperty("cols").GetInt32(), doc.RootElement.GetProperty("rows").GetInt32());
                }
                catch { /* mensagem inválida */ }
            }
        }
    }

    // ── Desenvolvimento (Windows/macOS): terminal simulado ──
    static async Task RunFakeAsync(WebSocket ws, CancellationToken ct)
    {
        async Task Send(string s) => await ws.SendAsync(Encoding.UTF8.GetBytes(s), WebSocketMessageType.Binary, true, ct);
        await Send("\x1b[31m■\x1b[0m BUREAU · terminal simulado (no servidor Linux é um SSH real)\r\n\r\nadmin@servidor:~$ ");
        var line = new StringBuilder();
        await PumpInputAsync(ws, data =>
        {
            var text = Encoding.UTF8.GetString(data);
            var echo = new StringBuilder();
            foreach (var ch in text)
            {
                if (ch == '\r')
                {
                    var cmd = line.ToString().Trim();
                    echo.Append("\r\n").Append(cmd.Length == 0 ? "" : $"(simulado) {cmd}\r\n").Append("admin@servidor:~$ ");
                    line.Clear();
                }
                else if (ch == '\x7f') { if (line.Length > 0) { line.Length--; echo.Append("\b \b"); } }
                else if (!char.IsControl(ch)) { line.Append(ch); echo.Append(ch); }
            }
            _ = Send(echo.ToString());
        }, (_, _) => { }, ct);
    }

    static partial class Native
    {
        public const int O_RDWR = 2;
        public const int O_NOCTTY = 0x100;
        public const nuint TIOCSWINSZ = 0x5414;

        [StructLayout(LayoutKind.Sequential)]
        public struct WinSize { public ushort Row, Col, XPixel, YPixel; }

        [LibraryImport("libc", SetLastError = true)] public static partial int posix_openpt(int flags);
        [LibraryImport("libc", SetLastError = true)] public static partial int grantpt(int fd);
        [LibraryImport("libc", SetLastError = true)] public static partial int unlockpt(int fd);
        [LibraryImport("libc", SetLastError = true)] public static partial int ptsname_r(int fd, [Out] byte[] buf, nuint buflen);
        [LibraryImport("libc", SetLastError = true)] public static partial int ioctl(int fd, nuint request, ref WinSize ws);
    }
}
