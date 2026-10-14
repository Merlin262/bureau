using System.Text.Json;
using Bureau.Infra;
using Microsoft.Extensions.Options;

namespace Bureau.Modules;

/// <summary>
/// Exporta/importa o estado do BUREAU (DataDir) num único JSON: apelidos, monitores, bloqueios, aparelhos
/// conhecidos, serviços monitorados, alertas, push, tokens dos apps e chaves VAPID — o suficiente para
/// reinstalar o servidor sem perder nada. O histórico de métricas (binário) fica de fora.
/// O bureau.json vai junto, mas na importação é gravado ao lado (o serviço não tem permissão em /etc).
/// </summary>
public sealed class ConfigBackup(IOptions<BureauOptions> o, IHostApplicationLifetime life, ILogger<ConfigBackup> log)
{
    static readonly string[] Files =
    [
        "watch.json", "aliases.json", "monitors.json", "blocks.json", "known-devices.json", "backups.json",
        "alerts.json", "push.json", "tokens.json", "vapid.json", "access.json",
    ];

    string DataDir => o.Value.DataDir;

    public BackupBundle Export()
    {
        var files = new Dictionary<string, string>();
        foreach (var f in Files)
        {
            var path = Path.Combine(DataDir, f);
            if (File.Exists(path)) files[f] = File.ReadAllText(path);
        }
        string? config = null;
        try { config = File.ReadAllText(ConfigPaths.Resolve()); } catch { /* sem config legível */ }
        return new BackupBundle("bureau-backup", 1, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Environment.MachineName, Api.Version, files, config);
    }

    public OkResponse Import(BackupBundle b)
    {
        if (b.Kind != "bureau-backup" || b.Version != 1) return new(false, "Arquivo não é um backup do BUREAU");
        var restored = 0;
        foreach (var (name, content) in b.Files ?? [])
        {
            if (!Files.Contains(name)) continue; // só nomes conhecidos (nada de caminhos arbitrários)
            try { using var _ = JsonDocument.Parse(content); }
            catch { return new(false, $"{name} está corrompido no backup"); }
            File.WriteAllText(Path.Combine(DataDir, name), content);
            restored++;
        }

        var msg = $"{restored} arquivo(s) restaurado(s). Reiniciando o BUREAU…";
        if (!string.IsNullOrWhiteSpace(b.Config))
        {
            var side = Path.Combine(DataDir, "bureau.json.restaurado");
            File.WriteAllText(side, b.Config);
            msg += $" A configuração foi salva em {side} — para aplicá-la: sudo cp {side} /etc/bureau/bureau.json";
        }

        log.LogWarning("Backup importado ({Count} arquivos, de {Host} em {Created})", restored, b.Host, DateTimeOffset.FromUnixTimeSeconds(b.Created));
        // Os módulos leem esses arquivos ao iniciar: reinicia (o systemd sobe o serviço de novo).
        _ = Task.Run(async () => { await Task.Delay(1500); life.StopApplication(); });
        return new(true, msg);
    }
}
