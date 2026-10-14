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
    public HomeAssistantOptions HomeAssistant { get; set; } = new();
    public JellyfinOptions Jellyfin { get; set; } = new();

    /// <summary>Jobs de backup (serviços systemd, normalmente acionados por um .timer) a acompanhar.</summary>
    public BackupJob[] Backups { get; set; } = [];

    public TerminalOptions Terminal { get; set; } = new();

    /// <summary>Mostrar e aplicar atualizações de pacotes do sistema (apt).</summary>
    public bool SystemUpdates { get; set; } = true;
}

/// <summary>Terminal no navegador: SSH para este usuário em 127.0.0.1 (pede a senha do Linux).</summary>
public sealed class TerminalOptions
{
    public bool Enabled { get; set; }
    public string User { get; set; } = "";
    public int Port { get; set; } = 22;
}

public sealed class HomeAssistantOptions
{
    public bool Enabled { get; set; }
    public string Url { get; set; } = "http://127.0.0.1:8123";

    /// <summary>Token de longa duração (Perfil → Segurança → Tokens de acesso de longa duração).</summary>
    public string? Token { get; set; }

    /// <summary>Entidades a exibir (ex.: "light.sala"). Vazio: luzes, interruptores e ventiladores.</summary>
    public string[] Entities { get; set; } = [];
}

public sealed class JellyfinOptions
{
    public bool Enabled { get; set; }
    public string Url { get; set; } = "http://127.0.0.1:8096";

    /// <summary>Chave de API (Painel → Avançado → Chaves de API).</summary>
    public string? ApiKey { get; set; }
}

public sealed class BackupJob
{
    public string Name { get; set; } = "";

    /// <summary>Serviço que executa o backup (ex.: "restic-backup.service").</summary>
    public string Unit { get; set; } = "";

    /// <summary>Alerta se o último sucesso for mais antigo que isso.</summary>
    public int MaxAgeHours { get; set; } = 26;
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

    public ApnsOptions Apns { get; set; } = new();
}

/// <summary>Push nativo no iPhone (requer conta paga de desenvolvedor Apple).</summary>
public sealed class ApnsOptions
{
    /// <summary>Chave .p8 de "Apple Push Notifications service" (Certificates, IDs &amp; Profiles → Keys).</summary>
    public string? KeyFile { get; set; }
    public string? KeyId { get; set; }
    public string? TeamId { get; set; }
    public string BundleId { get; set; } = "dev.bureau.app";

    /// <summary>true para apps instalados pelo Xcode; false para TestFlight/App Store (o outro é tentado se falhar).</summary>
    public bool Sandbox { get; set; } = true;
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
