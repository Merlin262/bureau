using System.Diagnostics;

namespace Bureau.Infra;

public readonly record struct ProcResult(int Code, string Out, string Err)
{
    public bool Ok => Code == 0;
    public string Message => (string.IsNullOrWhiteSpace(Err) ? Out : Err).Trim();
}

public static class Proc
{
    public static async Task<ProcResult> RunAsync(string file, IEnumerable<string> args, int timeoutMs = 15_000, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["LC_ALL"] = "C";
        psi.Environment["SYSTEMD_COLORS"] = "0";

        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Falha ao executar {file}");
        var stdout = p.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = p.StandardError.ReadToEndAsync(CancellationToken.None);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* já terminou */ }
            return new ProcResult(-1, "", "Tempo esgotado");
        }
        return new ProcResult(p.ExitCode, await stdout, await stderr);
    }
}
