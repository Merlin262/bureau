using System.Text.Json;
using Bureau.Infra;
using Microsoft.Extensions.Options;

namespace Bureau.Modules;

/// <summary>
/// Status da atualização automática. O trabalho é feito por /opt/bureau/update.sh (como root, via
/// bureau-update.service); aqui só lemos o estado que ele grava e o disparamos sob demanda.
/// </summary>
public sealed class UpdateModule(IOptions<BureauOptions> o)
{
    static readonly string Script = Path.Combine(AppContext.BaseDirectory, "update.sh");
    readonly string _state = Path.Combine(o.Value.DataDir, "update.json");

    public async Task<UpdateStatus> GetAsync()
    {
        var current = Installed();
        if (!OperatingSystem.IsLinux())
            return new UpdateStatus(current, current, "atualizado", "Desenvolvimento: atualização automática só no servidor Linux",
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(), false, false);

        string? latest = null, status = null, message = null;
        long checkedAt = 0;
        try
        {
            if (File.Exists(_state))
            {
                using var doc = JsonDocument.Parse(await File.ReadAllBytesAsync(_state));
                var r = doc.RootElement;
                latest = Str(r, "latest");
                status = Str(r, "status");
                message = Str(r, "message");
                checkedAt = r.TryGetProperty("checkedAt", out var c) && c.TryGetInt64(out var t) ? t : 0;
            }
        }
        catch { /* arquivo sendo reescrito */ }

        var timer = await Proc.RunAsync("systemctl", ["is-enabled", "bureau-update.timer"], 5000);
        return new UpdateStatus(current, latest, status ?? "nunca", message ?? "Ainda não verificado",
            checkedAt, timer.Out.Trim() == "enabled", File.Exists(Script));
    }

    public async Task<OkResponse> RunAsync()
    {
        if (!OperatingSystem.IsLinux()) return new(false, "Disponível só no servidor Linux");
        if (!File.Exists(Script)) return new(false, "Atualizador não instalado — rode o install.sh novamente");
        // Pedido manual: permite tentar de novo uma versão que falhou antes.
        try { await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(_state)!, "update.retry"), ""); } catch { /* segue sem */ }
        var r = await Proc.RunAsync("systemctl", ["--no-ask-password", "start", "--no-block", "bureau-update.service"], 10_000);
        return r.Ok ? new(true, "Verificando atualizações…") : new(false, r.Message);
    }

    static string Installed()
    {
        try
        {
            var v = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "VERSION")).Trim();
            if (v.Length > 0) return v;
        }
        catch { /* instalação antiga */ }
        return "v" + Api.Version;
    }

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;
}
