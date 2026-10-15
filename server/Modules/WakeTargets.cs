using Bureau.Infra;
using Microsoft.Extensions.Options;

namespace Bureau.Modules;

/// <summary>
/// Endereços já usados no Wake-on-LAN (DataDir/wol.json) e o caminho de volta: desligar/reiniciar.
/// O pacote mágico só sabe ligar (quem escuta é a placa de rede); para desligar, o BUREAU entra por SSH
/// no Windows com uma chave própria (DataDir/ssh/id_ed25519) e roda "shutdown". Só vale para aparelhos
/// desta lista — ou seja, que já foram ligados pelo pacote mágico.
/// </summary>
public sealed class WakeTargets
{
    readonly EventHub _hub;
    readonly NetworkModule _net;
    readonly string _path, _keyPath, _knownHosts;
    readonly Lock _lock = new();
    List<WolTarget> _targets;
    string? _publicKey;

    public WakeTargets(EventHub hub, NetworkModule net, IOptions<BureauOptions> o)
    {
        _hub = hub;
        _net = net;
        _path = Path.Combine(o.Value.DataDir, "wol.json");
        _keyPath = Path.Combine(o.Value.DataDir, "ssh", "id_ed25519");
        _knownHosts = Path.Combine(o.Value.DataDir, "known_hosts");
        _targets = [.. JsonFile.Load(_path, AppJson.Default.WolTargetArray) ?? []];
        Publish();
    }

    static string? NormalizeMac(string? mac)
    {
        var hex = (mac ?? "").Replace(":", "").Replace("-", "").Replace(".", "").Trim();
        if (hex.Length != 12 || !hex.All(Uri.IsHexDigit)) return null;
        return string.Join(':', Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2))).ToLowerInvariant();
    }

    static string? Clean(string? s, int max)
    {
        s = new string((s ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return s.Length == 0 ? null : s[..Math.Min(s.Length, max)];
    }

    WolTarget? Get(string mac) { lock (_lock) return _targets.Find(t => t.Mac == mac); }

    /// <summary>Envia o pacote mágico e guarda o endereço na lista.</summary>
    public async Task<OkResponse> WakeAsync(string mac, string? name)
    {
        var m = NormalizeMac(mac);
        if (m is null || !await NetworkModule.WakeAsync(m)) return new(false, "MAC inválido");
        var dev = _net.Find(m);
        lock (_lock)
        {
            var cur = _targets.Find(t => t.Mac == m);
            _targets.RemoveAll(t => t.Mac == m);
            _targets.Insert(0, new WolTarget(m, Clean(name, 40) ?? cur?.Name ?? dev?.Alias ?? dev?.Name,
                cur?.User, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
            Save();
        }
        return new(true, "Pacote mágico enviado");
    }

    public OkResponse Update(string mac, WolTargetUpdate u)
    {
        var m = NormalizeMac(mac);
        var user = Clean(u.User, 64);
        // Conta do Windows: sem espaços nem caracteres que mudariam o comando do ssh.
        if (user is not null && !user.All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-' or '\\'))
            return new(false, "Usuário inválido (use só o nome da conta, ex.: joao)");
        lock (_lock)
        {
            var i = _targets.FindIndex(t => t.Mac == m);
            if (i < 0) return new(false, "Endereço não está na lista do Wake-on-LAN");
            _targets[i] = _targets[i] with { Name = Clean(u.Name, 40), User = user };
            Save();
        }
        return new(true, "Salvo");
    }

    public OkResponse Remove(string mac)
    {
        var m = NormalizeMac(mac);
        lock (_lock)
        {
            if (_targets.RemoveAll(t => t.Mac == m) == 0) return new(false, "Endereço não encontrado");
            Save();
        }
        return new(true, "Endereço removido da lista");
    }

    /// <summary>Desliga ou reinicia (Windows, via OpenSSH) um aparelho da lista que está ligado.</summary>
    public async Task<OkResponse> PowerAsync(string mac, string action)
    {
        var m = NormalizeMac(mac);
        var t = m is null ? null : Get(m);
        if (t is null) return new(false, "Só aparelhos ligados pelo pacote mágico podem ser desligados daqui");
        if (t.User is null) return new(false, "Configure o usuário do Windows deste aparelho primeiro");
        var cmd = action switch
        {
            "shutdown" => "shutdown /s /f /t 0",
            "restart" => "shutdown /r /f /t 0",
            _ => null,
        };
        if (cmd is null) return new(false, "Ação inválida");
        var dev = _net.Find(t.Mac);
        if (dev is not { Online: true }) return new(false, "Aparelho desligado ou fora da rede");
        if (!OperatingSystem.IsLinux()) return new(true, $"(simulado) {cmd} em {dev.Ip}");

        var key = await EnsureKeyAsync();
        if (key is null) return new(false, "Não foi possível criar a chave SSH do BUREAU");
        var user = t.User.Contains('\\') ? t.User[(t.User.IndexOf('\\') + 1)..] : t.User;
        var r = await Proc.RunAsync("ssh", [
            "-i", _keyPath, "-o", "IdentitiesOnly=yes", "-o", "BatchMode=yes", "-o", "ConnectTimeout=6",
            "-o", "StrictHostKeyChecking=accept-new", "-o", $"UserKnownHostsFile={_knownHosts}",
            "-l", user, dev.Ip, cmd], 20_000);
        if (r.Ok) return new(true, action == "shutdown" ? "Desligando…" : "Reiniciando…");
        var msg = r.Message;
        return new(false, msg.Contains("Permission denied") ? "Chave não autorizada no PC — refaça a configuração do SSH"
            : msg.Contains("Connection refused") ? "SSH desligado no PC (serviço sshd)"
            : msg.Contains("timed out") || msg.Contains("No route") ? "PC não respondeu na porta 22 (firewall?)"
            : msg.Length > 0 ? msg.Split('\n')[^1] : $"Falha (código {r.Code})");
    }

    /// <summary>Chave pública do BUREAU (gera na primeira vez).</summary>
    public async Task<string?> EnsureKeyAsync()
    {
        if (_publicKey is not null) return _publicKey;
        if (!OperatingSystem.IsLinux()) return _publicKey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIDEVDEVDEVDEVDEVDEVDEVDEVDEVDEVDEVDEVDEVDEV bureau@dev";
        try
        {
            if (!File.Exists(_keyPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_keyPath)!);
                var r = await Proc.RunAsync("ssh-keygen", ["-q", "-t", "ed25519", "-N", "", "-C", $"bureau@{Environment.MachineName}", "-f", _keyPath]);
                if (!r.Ok) return null;
            }
            return _publicKey = (await File.ReadAllTextAsync(_keyPath + ".pub")).Trim();
        }
        catch { return null; }
    }

    public async Task<WolState> StateAsync()
    {
        WolTarget[] list;
        lock (_lock) list = [.. _targets];
        return new(list, await EnsureKeyAsync());
    }

    /// <summary>Grava e publica — chamar com o _lock já obtido.</summary>
    void Save()
    {
        JsonFile.Save(_path, _targets.ToArray(), AppJson.Default.WolTargetArray);
        Publish();
    }

    void Publish() => _hub.Publish("wol", _targets.ToArray(), AppJson.Default.WolTargetArray);
}
