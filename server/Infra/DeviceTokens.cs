using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Bureau.Infra;

/// <summary>
/// Tokens de longa duração para o app mobile (Authorization: Bearer). Só o hash SHA-256 é guardado;
/// cada aparelho pode ser revogado individualmente pela interface.
/// </summary>
public sealed class DeviceTokens
{
    readonly string _path;
    readonly ConcurrentDictionary<string, DeviceToken> _byHash = new();

    public DeviceTokens(IOptions<BureauOptions> o)
    {
        _path = Path.Combine(o.Value.DataDir, "tokens.json");
        foreach (var t in JsonFile.Load(_path, AppJson.Default.DeviceTokenArray) ?? []) _byHash[t.Hash] = t;
    }

    public IEnumerable<DeviceToken> All => _byHash.Values.OrderBy(t => t.Created);

    public (string Token, DeviceToken Entry) Create(string name)
    {
        var token = B64Url.Encode(RandomNumberGenerator.GetBytes(32));
        var entry = new DeviceToken(Guid.NewGuid().ToString("N")[..12], Clean(name), Hash(token), DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        _byHash[entry.Hash] = entry;
        Save();
        return (token, entry);
    }

    public DeviceToken? Validate(string token) => _byHash.GetValueOrDefault(Hash(token));

    public bool Revoke(string id)
    {
        var hit = _byHash.Values.FirstOrDefault(t => t.Id == id);
        if (hit is null || !_byHash.TryRemove(hit.Hash, out _)) return false;
        Save();
        return true;
    }

    void Save() => JsonFile.Save(_path, _byHash.Values.ToArray(), AppJson.Default.DeviceTokenArray);

    static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    static string Clean(string name)
    {
        name = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return name.Length == 0 ? "Aparelho" : name[..Math.Min(name.Length, 60)];
    }

    /// <summary>Autentica requisições com "Authorization: Bearer &lt;token&gt;" (o app não usa cookies).</summary>
    public static async Task Middleware(HttpContext ctx, Func<Task> next)
    {
        if (ctx.User.Identity?.IsAuthenticated != true)
        {
            var header = ctx.Request.Headers.Authorization.ToString();
            if (header.StartsWith("Bearer ", StringComparison.Ordinal) &&
                ctx.RequestServices.GetRequiredService<DeviceTokens>().Validate(header[7..].Trim()) is { } t)
            {
                ctx.User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.Name, "admin"), new Claim("device", t.Id)], "Bearer"));
            }
        }
        await next();
    }
}
