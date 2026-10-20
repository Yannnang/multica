using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using ChatBi.Infrastructure.Auth;
using Microsoft.IdentityModel.Tokens;

namespace ChatBi.Tests.Integration;

/// <summary>构造攻击/边界 token：过期、篡改签名、alg=none 降级（全部必须 401）。</summary>
public static class TestTokenFactory
{
    private static SymmetricSecurityKey Key(string signingKey) =>
        new(Encoding.UTF8.GetBytes(signingKey));

    public static string CreateValidAccess(string signingKey, long userId, int tokenVersion, params string[] roles)
    {
        var claims = new List<Claim>
        {
            new(ChatBiClaims.Subject, userId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new(ChatBiClaims.UnionId, "ou_token_test"),
            new(ChatBiClaims.TokenVersion, tokenVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new(ChatBiClaims.TokenUse, ChatBiClaims.TokenUseAccess),
        };
        claims.AddRange(roles.Select(r => new Claim(ChatBiClaims.Role, r)));

        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Issuer = "chatbi",
            Audience = "chatbi-web",
            Expires = DateTime.UtcNow.AddHours(1),
            SigningCredentials = new SigningCredentials(Key(signingKey), SecurityAlgorithms.HmacSha256),
        };
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityTokenHandler().CreateToken(descriptor));
    }

    public static string CreateExpiredAccess(string signingKey, long userId, int tokenVersion, params string[] roles)
    {
        var claims = new List<Claim>
        {
            new(ChatBiClaims.Subject, userId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new(ChatBiClaims.TokenVersion, tokenVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new(ChatBiClaims.TokenUse, ChatBiClaims.TokenUseAccess),
        };
        claims.AddRange(roles.Select(r => new Claim(ChatBiClaims.Role, r)));

        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Issuer = "chatbi",
            Audience = "chatbi-web",
            NotBefore = DateTime.UtcNow.AddHours(-2),
            Expires = DateTime.UtcNow.AddMinutes(-30), // 超出 ClockSkew，必过期
            SigningCredentials = new SigningCredentials(Key(signingKey), SecurityAlgorithms.HmacSha256),
        };
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityTokenHandler().CreateToken(descriptor));
    }

    public static string TamperSignature(string token)
    {
        var parts = token.Split('.');
        var signature = parts[2].ToCharArray();
        signature[0] = signature[0] == 'A' ? 'B' : 'A';
        return $"{parts[0]}.{parts[1]}.{new string(signature)}";
    }

    /// <summary>alg=none 降级攻击 token（无签名）。</summary>
    public static string CreateAlgNone(long userId, int tokenVersion, params string[] roles)
    {
        var header = Base64UrlEncoder.Encode("""{"alg":"none","typ":"JWT"}""");
        var roleArray = string.Join(',', roles.Select(r => $$""""{{r}}""""));
        var payloadJson = $$"""
            {
              "sub": "{{userId}}",
              "token_ver": "{{tokenVersion}}",
              "typ": "access",
              "role": [{{roleArray}}],
              "iss": "chatbi",
              "aud": "chatbi-web",
              "exp": {{DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()}}
            }
            """;
        return $"{header}.{Base64UrlEncoder.Encode(payloadJson)}.";
    }
}
