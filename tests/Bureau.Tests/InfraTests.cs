using System.Text.Json;
using Bureau.Infra;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Bureau.Tests;

public class JsonFileTests
{
    [Fact]
    public void Salva_e_carrega_de_volta()
    {
        using var dir = new TempDir();
        var path = dir.File("x.json");
        JsonFile.Save(path, new Dictionary<string, string> { ["a"] = "1" }, AppJson.Default.DictionaryStringString);

        Assert.Equal("1", JsonFile.Load(path, AppJson.Default.DictionaryStringString)!["a"]);
        Assert.False(File.Exists(path + ".tmp")); // escrita atômica: o temporário vira o arquivo final
    }

    [Fact]
    public void Arquivo_inexistente_ou_corrompido_retorna_default()
    {
        using var dir = new TempDir();
        Assert.Null(JsonFile.Load(dir.File("nada.json"), AppJson.Default.StringArray));
        File.WriteAllText(dir.File("ruim.json"), "{ isto não é json");
        Assert.Null(JsonFile.Load(dir.File("ruim.json"), AppJson.Default.StringArray));
    }
}

public class B64UrlTests
{
    [Theory]
    [InlineData(new byte[] { }, "")]
    [InlineData(new byte[] { 0xfb }, "-w")]
    [InlineData(new byte[] { 0xff, 0xff }, "__8")]
    [InlineData(new byte[] { 1, 2, 3 }, "AQID")]
    public void Codifica_sem_padding_e_com_alfabeto_de_url(byte[] data, string expected)
    {
        Assert.Equal(expected, B64Url.Encode(data));
        Assert.Equal(data, B64Url.Decode(expected));
    }

    [Fact]
    public void Ida_e_volta_com_todos_os_tamanhos()
    {
        for (var n = 0; n < 40; n++)
        {
            var bytes = Enumerable.Range(0, n).Select(i => (byte)(i * 37)).ToArray();
            Assert.Equal(bytes, B64Url.Decode(B64Url.Encode(bytes)));
        }
    }
}

public class PasswordHasherTests
{
    [Fact]
    public void Hash_confere_so_com_a_senha_certa()
    {
        var h = PasswordHasher.Hash("segredo-123");
        Assert.StartsWith("pbkdf2-sha256$210000$", h);
        Assert.True(PasswordHasher.Verify("segredo-123", h));
        Assert.False(PasswordHasher.Verify("segredo-124", h));
        Assert.NotEqual(h, PasswordHasher.Hash("segredo-123")); // sal aleatório
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("md5$abc")]
    [InlineData("pbkdf2-sha256$x$a$b")]
    [InlineData("bcrypt$1$a$b")]
    [InlineData("pbkdf2-sha256$1000$***$***")]
    public void Hash_invalido_nunca_confere(string? stored) => Assert.False(PasswordHasher.Verify("qualquer", stored));
}

public class EventHubTests
{
    [Fact]
    public async Task Novo_cliente_recebe_o_ultimo_estado_e_depois_as_mudancas()
    {
        var hub = new EventHub();
        hub.Publish("a", "um", AppJson.Default.String);
        hub.Publish("a", "dois", AppJson.Default.String);
        hub.SetReplay("hist", () => "[1,2]");

        var join = hub.NextJoin;
        using var sub = hub.Subscribe();
        Assert.True(join.IsCompleted);
        Assert.Equal(1, hub.Count);

        Assert.True(sub.Reader.TryRead(out var replay));
        Assert.Equal("event: hist\ndata: [1,2]\n\n", replay);
        Assert.True(sub.Reader.TryRead(out var last));
        Assert.Equal("event: a\ndata: \"dois\"\n\n", last);
        Assert.False(sub.Reader.TryRead(out _));

        hub.Publish("a", "tres", AppJson.Default.String);
        Assert.Contains("tres", await sub.Reader.ReadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Valor_repetido_nao_e_reenviado_salvo_sem_dedupe()
    {
        var hub = new EventHub();
        using var sub = hub.Subscribe();
        hub.Publish("x", "v", AppJson.Default.String);
        hub.Publish("x", "v", AppJson.Default.String);
        hub.Publish("x", "v", AppJson.Default.String, dedupe: false);

        var n = 0;
        while (sub.Reader.TryRead(out _)) n++;
        Assert.Equal(2, n);
    }

    [Fact]
    public void Dispose_remove_o_assinante()
    {
        var hub = new EventHub();
        var sub = hub.Subscribe();
        sub.Dispose();
        Assert.Equal(0, hub.Count);
        Assert.True(sub.Reader.Completion.IsCompleted);
    }
}

public class AccessLogTests
{
    static DefaultHttpContext Ctx(string ip, string ua = "", string? country = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(ip);
        if (ua.Length > 0) ctx.Request.Headers.UserAgent = ua;
        if (country is not null) ctx.Request.Headers["CF-IPCountry"] = country;
        return ctx;
    }

    [Fact]
    public void Registra_ip_pais_aparelho_e_conta_falhas()
    {
        using var dir = new TempDir();
        var log = new AccessLog(Options.Create(new BureauOptions { DataDir = dir.Path }));

        var (known, fails) = log.Record(Ctx("::ffff:203.0.113.9", "Mozilla/5.0 (Windows NT 10.0) Chrome/130", "BR"), "web", false);
        Assert.False(known);
        Assert.Equal(1, fails);

        var e = log.Recent[0];
        Assert.Equal("203.0.113.9", e.Ip);
        Assert.Equal("BR", e.Country);
        Assert.Equal("Chrome · Windows", e.Device);

        log.Record(Ctx("203.0.113.9"), "web", true, "Pixel");
        Assert.Equal("Pixel", log.Recent[0].Device);
        Assert.True(log.Record(Ctx("203.0.113.9"), "app", true).KnownIp);

        // Persistido: uma nova instância lê o histórico.
        Assert.Equal(3, new AccessLog(Options.Create(new BureauOptions { DataDir = dir.Path })).Recent.Length);
    }

    [Fact]
    public void Guarda_so_os_200_mais_recentes()
    {
        using var dir = new TempDir();
        var log = new AccessLog(Options.Create(new BureauOptions { DataDir = dir.Path }));
        for (var i = 0; i < 205; i++) log.Record(Ctx("10.0.0.1"), "web", true);
        Assert.Equal(200, log.Recent.Length);
    }

    [Theory]
    [InlineData("", "Desconhecido")]
    [InlineData("Mozilla/5.0 (Linux; Android 14) Chrome/130 Mobile", "Chrome · Android")]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) Safari/604.1", "Safari · iOS")]
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 14_0) Firefox/130", "Firefox · macOS")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0) Chrome/130 Edg/130", "Edge · Windows")]
    [InlineData("Mozilla/5.0 (X11; Linux x86_64) Firefox/130", "Firefox · Linux")]
    [InlineData("curl/8.5", "curl")]
    [InlineData("AlgumBot/1.0", "Navegador")]
    public void Descreve_o_navegador(string ua, string expected)
    {
        using var dir = new TempDir();
        var log = new AccessLog(Options.Create(new BureauOptions { DataDir = dir.Path }));
        log.Record(Ctx("10.0.0.1", ua), "web", true);
        Assert.Equal(expected, log.Recent[0].Device);
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("192.168.100.5", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("100.64.0.1", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.1", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("fe80::1", true)]
    [InlineData("fd00::1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("2001:db8::1", false)]
    public void Identifica_ips_locais(string ip, bool local) => Assert.Equal(local, AccessLog.IsLocal(ip));
}

public class DeviceTokensTests
{
    [Fact]
    public void Cria_valida_lista_e_revoga()
    {
        using var dir = new TempDir();
        var opts = Options.Create(new BureauOptions { DataDir = dir.Path });
        var tokens = new DeviceTokens(opts);

        var (token, entry) = tokens.Create("  Pixel 8\n ");
        Assert.Equal("Pixel 8", entry.Name);
        Assert.Equal(entry, tokens.Validate(token));
        Assert.Null(tokens.Validate(token + "x"));
        Assert.DoesNotContain(token, File.ReadAllText(dir.File("tokens.json"))); // só o hash é salvo

        var reloaded = new DeviceTokens(opts);
        Assert.Single(reloaded.All);
        Assert.True(reloaded.Revoke(entry.Id));
        Assert.False(reloaded.Revoke(entry.Id));
        Assert.Null(reloaded.Validate(token));
    }

    [Fact]
    public void Nome_vazio_ou_longo_e_ajustado()
    {
        using var dir = new TempDir();
        var tokens = new DeviceTokens(Options.Create(new BureauOptions { DataDir = dir.Path }));
        Assert.Equal("Aparelho", tokens.Create("").Entry.Name);
        Assert.Equal(60, tokens.Create(new string('x', 100)).Entry.Name.Length);
    }
}

public class ProcTests
{
    static (string File, string[] Args) Echo(string text) =>
        OperatingSystem.IsWindows() ? ("cmd", ["/c", "echo " + text]) : ("sh", ["-c", "echo " + text]);

    [Fact]
    public async Task Captura_saida_e_codigo()
    {
        var (f, a) = Echo("ola");
        var r = await Proc.RunAsync(f, a, ct: TestContext.Current.CancellationToken);
        Assert.True(r.Ok);
        Assert.Equal("ola", r.Message);
    }

    [Fact]
    public async Task Falha_usa_stderr_como_mensagem()
    {
        var r = OperatingSystem.IsWindows()
            ? await Proc.RunAsync("cmd", ["/c", "echo erro 1>&2 & exit 3"], ct: TestContext.Current.CancellationToken)
            : await Proc.RunAsync("sh", ["-c", "echo erro >&2; exit 3"], ct: TestContext.Current.CancellationToken);
        Assert.False(r.Ok);
        Assert.Equal(3, r.Code);
        Assert.Equal("erro", r.Message);
    }

    [Fact]
    public async Task Tempo_esgotado_mata_o_processo()
    {
        var r = OperatingSystem.IsWindows()
            ? await Proc.RunAsync("powershell", ["-NoProfile", "-Command", "Start-Sleep 10"], 300, TestContext.Current.CancellationToken)
            : await Proc.RunAsync("sleep", ["10"], 300, TestContext.Current.CancellationToken);
        Assert.Equal(-1, r.Code);
        Assert.Equal("Tempo esgotado", r.Message);
    }

    [Fact]
    public void Message_prefere_stderr()
    {
        Assert.Equal("b", new ProcResult(1, "a", " b ").Message);
        Assert.Equal("a", new ProcResult(0, " a\n", "  ").Message);
    }
}

[Collection("env")]
public class CliTests
{
    [Fact]
    public void Define_senha_preservando_o_resto_da_configuracao()
    {
        using var dir = new TempDir();
        var cfg = dir.File("sub/bureau.json");
        Directory.CreateDirectory(Path.GetDirectoryName(cfg)!);
        File.WriteAllText(cfg, "{ // comentário\n \"Port\": 9000, }");

        var oldIn = Console.In;
        var oldOut = Console.Out;
        try
        {
            Console.SetIn(new StringReader("senha-forte-1\n"));
            Console.SetOut(TextWriter.Null);
            Assert.Equal(0, Cli.SetPassword(cfg));
        }
        finally
        {
            Console.SetIn(oldIn);
            Console.SetOut(oldOut);
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(cfg));
        Assert.Equal(9000, doc.RootElement.GetProperty("Port").GetInt32());
        Assert.True(PasswordHasher.Verify("senha-forte-1", doc.RootElement.GetProperty("PasswordHash").GetString()));
    }

    [Fact]
    public void Recusa_senha_curta()
    {
        using var dir = new TempDir();
        var oldIn = Console.In;
        var oldErr = Console.Error;
        try
        {
            Console.SetIn(new StringReader("curta\n"));
            Console.SetError(TextWriter.Null);
            Assert.Equal(1, Cli.SetPassword(dir.File("bureau.json")));
        }
        finally
        {
            Console.SetIn(oldIn);
            Console.SetError(oldErr);
        }
        Assert.False(File.Exists(dir.File("bureau.json")));
    }
}

[Collection("env")]
public class ConfigPathsTests
{
    [Fact]
    public void Variavel_de_ambiente_tem_prioridade()
    {
        var old = Environment.GetEnvironmentVariable("BUREAU_CONFIG");
        try
        {
            Environment.SetEnvironmentVariable("BUREAU_CONFIG", "/tmp/x.json");
            Assert.Equal("/tmp/x.json", ConfigPaths.Resolve());
            Environment.SetEnvironmentVariable("BUREAU_CONFIG", null);
            Assert.EndsWith(".json", ConfigPaths.Resolve());
        }
        finally { Environment.SetEnvironmentVariable("BUREAU_CONFIG", old); }
    }
}
