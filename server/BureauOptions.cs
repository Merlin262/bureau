namespace Bureau;

public sealed class BureauOptions
{
    public int Port { get; set; } = 8420;
    public string? PasswordHash { get; set; }
    public string DataDir { get; set; } = "/var/lib/bureau";

    /// <summary>Serviços systemd monitorados inicialmente (depois editáveis pela interface).</summary>
    public string[] Services { get; set; } = [];

    /// <summary>Permite reiniciar/desligar o servidor pela interface.</summary>
    public bool AllowPower { get; set; } = true;

    public DockerOptions Docker { get; set; } = new();
    public PiHoleOptions PiHole { get; set; } = new();
    public AlertOptions Alerts { get; set; } = new();
    public PushOptions Push { get; set; } = new();
}

public sealed class AlertOptions
{
    public bool Enabled { get; set; } = true;
    public int DiskPercent { get; set; } = 90;
    public int TempCelsius { get; set; } = 85;
    public int BatteryCritical { get; set; } = 20;
}

public sealed class PushOptions
{
    /// <summary>Arquivo JSON da conta de serviço do Firebase (push nativo do app Android).</summary>
    public string? FcmServiceAccount { get; set; }

    /// <summary>Contato exigido pelo Web Push (VAPID). Use seu e-mail real: a Apple rejeita domínios inválidos.</summary>
    public string WebPushSubject { get; set; } = "mailto:bureau@example.com";

    /// <summary>Tópico do ntfy (ex.: https://ntfy.sh/meu-topico-secreto) — alternativa sem Firebase.</summary>
    public string? NtfyUrl { get; set; }
    public string? NtfyToken { get; set; }
}

public sealed class DockerOptions
{
    public bool Enabled { get; set; } = true;
    public string Socket { get; set; } = "/var/run/docker.sock";
}

public sealed class PiHoleOptions
{
    public bool Enabled { get; set; }
    public string Url { get; set; } = "http://127.0.0.1";

    /// <summary>Senha ou "app password" do Pi-hole v6 (Settings → Web interface / API).</summary>
    public string? Password { get; set; }
}

public static class ConfigPaths
{
    public static string Resolve()
    {
        var env = Environment.GetEnvironmentVariable("BUREAU_CONFIG");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        return OperatingSystem.IsLinux() && File.Exists("/etc/bureau/bureau.json")
            ? "/etc/bureau/bureau.json"
            : Path.GetFullPath("bureau.dev.json");
    }
}
