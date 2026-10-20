using Serilog.Core;
using Serilog.Events;

namespace ChatBi.Api.Logging;

/// <summary>
/// Serilog 脱敏 enricher：敏感属性名（token/secret/password/authorization/连接串等）
/// 与 JWT 形态的字符串值一律替换为 [REDACTED]，不落 token/连接串（工程红线）。
/// </summary>
public class SensitiveDataRedactionEnricher : ILogEventEnricher
{
    public const string RedactedValue = "[REDACTED]";

    private static readonly System.Text.RegularExpressions.Regex SensitiveNamePattern =
        new("token|secret|password|passwd|pwd|authorization|auth_header|connectionstring|connection_string|api[_-]?key|access[_-]?key|private[_-]?key|credential",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    // JWT / 长 base64url 载荷形态：header.payload(…)[.signature]
    private static readonly System.Text.RegularExpressions.Regex JwtLikeValuePattern =
        new(@"^""?eyJ[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+", System.Text.RegularExpressions.RegexOptions.Compiled);

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        foreach (var (name, value) in logEvent.Properties.ToArray())
        {
            if (SensitiveNamePattern.IsMatch(name) || IsSensitiveValue(value))
            {
                logEvent.AddOrUpdateProperty(new LogEventProperty(name, new ScalarValue(RedactedValue)));
            }
        }
    }

    private static bool IsSensitiveValue(LogEventPropertyValue value) =>
        value is ScalarValue { Value: string text } && JwtLikeValuePattern.IsMatch(text);
}
