using Microsoft.AspNetCore.StaticFiles;

namespace Bureau.Infra;

/// <summary>
/// Serve a interface: arquivos pré-comprimidos (.br/.gz gerados no publish) quando o navegador aceita,
/// cache eterno para /assets (nomes com hash do Vite) e index.html como fallback da SPA.
/// </summary>
public static class StaticWeb
{
    static readonly FileExtensionContentTypeProvider Types = new();

    public static void UseStaticWeb(this WebApplication app)
    {
        var root = Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "wwwroot"));
        var index = Path.Combine(root, "index.html");

        app.Use(async (ctx, next) =>
        {
            var path = ctx.Request.Path.Value;
            if (!HttpMethods.IsGet(ctx.Request.Method) || path is null || path.StartsWith("/api/") || !Types.TryGetContentType(path, out var type))
            {
                await next();
                return;
            }

            var file = Path.GetFullPath(Path.Combine(root, path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));
            if (!file.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(file))
            {
                await next();
                return;
            }

            var res = ctx.Response;
            res.ContentType = type;
            res.Headers.CacheControl = path.StartsWith("/assets/") ? "public, max-age=31536000, immutable" : "no-cache";
            res.Headers.Vary = "Accept-Encoding";

            var accept = ctx.Request.Headers.AcceptEncoding.ToString();
            if (accept.Contains("br") && File.Exists(file + ".br")) { res.Headers.ContentEncoding = "br"; file += ".br"; }
            else if (accept.Contains("gzip") && File.Exists(file + ".gz")) { res.Headers.ContentEncoding = "gzip"; file += ".gz"; }

            res.ContentLength = new FileInfo(file).Length;
            await res.SendFileAsync(file);
        });

        // Rotas da SPA (ex.: /#/servicos ou recarregar em qualquer caminho) → index.html
        app.MapFallback(async ctx =>
        {
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.Headers.CacheControl = "no-cache";
            await ctx.Response.SendFileAsync(index);
        });
    }
}
