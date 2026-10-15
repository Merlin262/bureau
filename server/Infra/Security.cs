using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bureau.Infra;

public static class PasswordHasher
{
    const int Iterations = 210_000;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return false;
        var p = stored.Split('$');
        if (p.Length != 4 || p[0] != "pbkdf2-sha256" || !int.TryParse(p[1], out var iter)) return false;
        try
        {
            var salt = Convert.FromBase64String(p[2]);
            var expected = Convert.FromBase64String(p[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iter, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException) { return false; }
    }
}

public static class Security
{
    const string Csp = "default-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; " +
                       "connect-src 'self'; font-src 'self'; manifest-src 'self'; worker-src 'self'; frame-ancestors 'none'";

    // Origens do app mobile (Capacitor): Android usa https://localhost, iOS usa capacitor://localhost.
    // O app autentica com token Bearer (sem cookies), então liberar essas origens não expõe a sessão web.
    static readonly HashSet<string> AppOrigins = ["https://localhost", "http://localhost", "capacitor://localhost"];

    public static async Task Headers(HttpContext ctx, Func<Task> next)
    {
        var h = ctx.Response.Headers;

        var origin = ctx.Request.Headers.Origin.ToString();
        if (origin.Length > 0 && AppOrigins.Contains(origin) && ctx.Request.Path.StartsWithSegments("/api"))
        {
            h.AccessControlAllowOrigin = origin;
            h.Vary = "Origin";
            if (HttpMethods.IsOptions(ctx.Request.Method))
            {
                h.AccessControlAllowMethods = "GET, POST, PUT, DELETE";
                h.AccessControlAllowHeaders = "Authorization, Content-Type, X-Bureau";
                h.AccessControlMaxAge = "86400";
                ctx.Response.StatusCode = StatusCodes.Status204NoContent;
                return;
            }
        }

        h.XContentTypeOptions = "nosniff";
        h.XFrameOptions = "DENY";
        h["Referrer-Policy"] = "no-referrer";
        h.ContentSecurityPolicy = Csp;

        var path = ctx.Request.Path;

        // Proteção CSRF: toda chamada que altera estado precisa do cabeçalho customizado
        // (navegadores não permitem enviá-lo de outra origem sem CORS).
        if (path.StartsWithSegments("/api") && !HttpMethods.IsGet(ctx.Request.Method) &&
            !HttpMethods.IsHead(ctx.Request.Method) && !ctx.Request.Headers.ContainsKey("X-Bureau"))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        await next();
    }
}

public static class Cli
{
    public static int SetPassword(string configPath)
    {
        string pw;
        if (Console.IsInputRedirected)
        {
            pw = Console.In.ReadLine() ?? "";
        }
        else
        {
            pw = ReadSecret("Nova senha do BUREAU: ");
            if (ReadSecret("Confirme a senha: ") != pw)
            {
                Console.Error.WriteLine("As senhas não conferem.");
                return 1;
            }
        }
        if (pw.Length < 8)
        {
            Console.Error.WriteLine("A senha precisa ter pelo menos 8 caracteres.");
            return 1;
        }

        var docOpts = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        var root = File.Exists(configPath)
            ? JsonNode.Parse(File.ReadAllText(configPath), null, docOpts) as JsonObject ?? new JsonObject()
            : new JsonObject();
        root["PasswordHash"] = PasswordHasher.Hash(pw);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(configPath))!);
        using (var fs = File.Create(configPath))
        using (var w = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            root.WriteTo(w);

        Console.WriteLine($"Senha definida em {configPath}.");
        return 0;
    }

    [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage] // lê o teclado
    static string ReadSecret(string prompt)
    {
        Console.Write(prompt);
        var chars = new List<char>();
        while (true)
        {
            var k = Console.ReadKey(intercept: true);
            if (k.Key == ConsoleKey.Enter) break;
            if (k.Key == ConsoleKey.Backspace) { if (chars.Count > 0) chars.RemoveAt(chars.Count - 1); continue; }
            if (!char.IsControl(k.KeyChar)) chars.Add(k.KeyChar);
        }
        Console.WriteLine();
        return new string(chars.ToArray());
    }
}
