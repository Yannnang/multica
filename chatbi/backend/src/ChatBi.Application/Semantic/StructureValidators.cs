using System.Text.RegularExpressions;
using ChatBi.Application.Common;

namespace ChatBi.Application.Semantic;

/// <summary>
/// 部分匹配结果：拼接时只允许白名单化的比较子句进入改写器（S4 实现 SQL 化）。
/// </summary>
public sealed record PredicateCondition(string Column, string Op, string? Value);

/// <summary>
/// 结构化白名单校验器集合（设计基线 3.2 安全设计）：
/// 凭证只收引用名、Expression 禁裸 SQL 片段、OnConditionsJson / CustomPredicateJson 只收结构化 JSON。
/// 全部纯函数，无 IO；校验失败抛 <see cref="SemanticValidationException"/>（422，SEM_*）。
/// </summary>
public static partial class StructureValidators
{
    // ---- CredentialRef：只接受引用名（环境变量/Key Vault 命名），疑似明文连接串单独定码 ----

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_.\-]{0,255}$")]
    private static partial Regex CredentialRefPattern();

    // 疑似明文连接串特征：SQL Server / 通用连接串键、带凭证的 URL、账号@主机。
    private static readonly (string Pattern, string Label)[] PlaintextPatterns =
    {
        (@"(?:server|data\s*source|address|network\s*address)\s*=", "连接串（Server/Data Source）"),
        (@"(?:password|pwd)\s*=", "连接串（Password）"),
        (@"(?:user\s*id|uid|user)\s*=", "连接串（User ID）"),
        (@"initial\s*catalog\s*=", "连接串（Initial Catalog）"),
        (@"integrated\s*security|trusted_connection", "连接串（集成认证）"),
        (@"encrypt\s*=", "连接串（Encrypt）"),
        (@":[0-9]{2,5}(?:/|;|\s|$)", "主机:端口形式"),
        (@"\w+://", "URL 形式"),
        (@"\w+@\w+", "账号@主机形式"),
    };

    /// <summary>
    /// 校验 DataSource.CredentialRef：必须是引用名；若检出疑似明文连接串/密码则抛
    /// SEM_PLAINTEXT_CREDENTIAL（消息不含原值），其余非法形态抛 SEM_CREDENTIAL_REF_INVALID。
    /// </summary>
    public static void ValidateCredentialRef(string? credentialRef)
    {
        if (string.IsNullOrWhiteSpace(credentialRef))
        {
            throw new SemanticValidationException(
                SemanticErrorCodes.CredentialRefInvalid, "CredentialRef 必填：请提供凭证引用名（环境变量/Key Vault），不接收明文凭证。");
        }

        if (CredentialRefPattern().IsMatch(credentialRef))
        {
            return;
        }

        foreach (var (pattern, label) in PlaintextPatterns)
        {
            if (Regex.IsMatch(credentialRef, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                throw new SemanticValidationException(
                    SemanticErrorCodes.PlaintextCredential,
                    $"CredentialRef 检出疑似明文凭证（{label}）。只接受环境变量/Key Vault 引用名，凭证本体请注入服务端环境。");
            }
        }

        throw new SemanticValidationException(
            SemanticErrorCodes.CredentialRefInvalid,
            "CredentialRef 只接受引用名（字母开头，含字母/数字/下划线/点/中划线，≤256 字符），不接收明文凭证。");
    }

    // ---- Metrics.Expression：聚合表达式模板白名单（{t} 为基表别名占位符） ----

    private static readonly HashSet<string> AllowedAggregates = new(StringComparer.OrdinalIgnoreCase)
    { "SUM", "COUNT", "AVG", "MIN", "MAX" };

    /// <summary>表达式外允许出现的标识符关键字（数据访问类关键字一律拒绝，见 DenyKeywords）。</summary>
    private static readonly HashSet<string> DenyKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "FROM", "WHERE", "INSERT", "UPDATE", "DELETE", "DROP", "ALTER", "CREATE",
        "EXEC", "EXECUTE", "UNION", "GRANT", "REVOKE", "TRUNCATE", "MERGE", "INTO", "JOIN",
        "INNER", "LEFT", "RIGHT", "OUTER", "CROSS", "ON", "GROUP", "ORDER", "BY", "HAVING",
        "DECLARE", "SET", "EXISTS", "TABLE", "WAITFOR", "OPENROWSET", "OPENQUERY",
    };

    /// <summary>
    /// 校验指标聚合表达式模板。白名单：{t} 占位符、标识符、数字、四则运算符、括号、逗号、
    /// CASE WHEN / AND / OR / NOT / NULL；必须含至少一个 SUM/COUNT/AVG/MIN/MAX。
    /// 拒绝：字符串字面量、注释（-- 与 — 类）、分号、一切非白名单字符与数据访问关键字。
    /// </summary>
    public static void ValidateMetricExpression(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            throw new SemanticValidationException(SemanticErrorCodes.ExpressionInvalid, "Expression 必填。");
        }

        if (expression.Length > 1024)
        {
            throw new SemanticValidationException(SemanticErrorCodes.ExpressionInvalid, "Expression 超长（上限 1024 字符）。");
        }

        if (expression.Contains("/*") || expression.Contains("*/"))
        {
            throw new SemanticValidationException(SemanticErrorCodes.ExpressionInvalid, "Expression 不允许注释。");
        }

        foreach (var ch in expression)
        {
            if (char.IsWhiteSpace(ch) || char.IsAsciiLetterOrDigit(ch) || ch is '_' or '.' or '(' or ')' or '{' or '}' or '+' or '-' or '*' or '/' or '%' or ',' or '<' or '>' or '=')
            {
                continue;
            }

            throw new SemanticValidationException(
                SemanticErrorCodes.ExpressionInvalid,
                $"Expression 含非法字符（仅允许 {{t}} 占位符、标识符、数字、四则运算、括号、逗号与比较符）。");
        }

        // 占位符：只允许 {t}（基表别名），且必须成对闭合。
        if (expression.Replace("{t}", string.Empty).Any(ch => ch is '{' or '}'))
        {
            throw new SemanticValidationException(
                SemanticErrorCodes.ExpressionInvalid, "Expression 只允许 {t} 一个占位符（代表基表别名），且必须成对闭合。");
        }

        // 注释以两个连续减号书写，会被白名单放行——显式拒绝。
        if (expression.Contains("--"))
        {
            throw new SemanticValidationException(SemanticErrorCodes.ExpressionInvalid, "Expression 不允许注释。");
        }

        // 词法检查：标识符不得是数据访问关键字；括号须配对；必须含聚合函数。
        var identifiers = Regex.Matches(expression, @"[A-Za-z_][A-Za-z0-9_.]*")
            .Select(m => m.Value)
            .ToList();

        foreach (var identifier in identifiers)
        {
            var bare = identifier.TrimEnd('.');
            if (DenyKeywords.Contains(bare))
            {
                throw new SemanticValidationException(
                    SemanticErrorCodes.ExpressionInvalid, $"Expression 不允许出现关键字 {bare.ToUpperInvariant()}（禁裸 SQL 片段）。");
            }
        }

        if (!identifiers.Any(identifier => AllowedAggregates.Contains(TokenBare(identifier))))
        {
            throw new SemanticValidationException(
                SemanticErrorCodes.ExpressionInvalid, "Expression 必须包含聚合函数（SUM/COUNT/AVG/MIN/MAX）。");
        }

        var depth = 0;
        foreach (var ch in expression)
        {
            switch (ch)
            {
                case '(': depth++; break;
                case ')': depth--; break;
            }

            if (depth < 0)
            {
                throw new SemanticValidationException(SemanticErrorCodes.ExpressionInvalid, "Expression 括号不配对。");
            }
        }

        if (depth != 0)
        {
            throw new SemanticValidationException(SemanticErrorCodes.ExpressionInvalid, "Expression 括号不配对。");
        }
    }

    private static string TokenBare(string identifier) => identifier.TrimEnd('.');

    // ---- SemanticRelations.OnConditionsJson：结构化 [ {"leftColumnId":1,"rightColumnId":2} ] ----

    /// <summary>JOIN 条件（列以白名单登记的 SemanticColumns.Id 表达，绝不收列名文本）。</summary>
    public sealed record JoinCondition(long LeftColumnId, long RightColumnId);

    /// <summary>
    /// 解析并校验 OnConditionsJson：只接受对象数组，每项恰有 leftColumnId/rightColumnId 两个正整数字段；
    /// 返回规范化形态（按输入顺序），供仓储持久化与 S4 执行期映射物理列名。
    /// </summary>
    public static IReadOnlyList<JoinCondition> ParseJoinConditionsJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new SemanticValidationException(SemanticErrorCodes.ConditionsInvalid, "OnConditionsJson 必填。");
        }

        System.Text.Json.JsonDocument document;
        try
        {
            document = System.Text.Json.JsonDocument.Parse(json);
        }
        catch (System.Text.Json.JsonException)
        {
            throw new SemanticValidationException(SemanticErrorCodes.ConditionsInvalid, "OnConditionsJson 不是合法 JSON。");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                throw new SemanticValidationException(SemanticErrorCodes.ConditionsInvalid, "OnConditionsJson 必须是 JSON 数组。");
            }

            var conditions = new List<JoinCondition>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                conditions.Add(ParseJoinCondition(element));
            }

            if (conditions.Count == 0)
            {
                throw new SemanticValidationException(SemanticErrorCodes.ConditionsInvalid, "OnConditionsJson 至少包含一个 JOIN 条件。");
            }

            return conditions;
        }
    }

    private static JoinCondition ParseJoinCondition(System.Text.Json.JsonElement element)
    {
        if (element.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            throw new SemanticValidationException(SemanticErrorCodes.ConditionsInvalid, "JOIN 条件必须是对象。");
        }

        long? left = null;
        long? right = null;
        foreach (var property in element.EnumerateObject())
        {
            switch (property.Name)
            {
                case "leftColumnId" when property.Value.ValueKind == System.Text.Json.JsonValueKind.Number
                                         && property.Value.TryGetInt64(out var value) && value > 0:
                    left = value;
                    break;
                case "rightColumnId" when property.Value.ValueKind == System.Text.Json.JsonValueKind.Number
                                          && property.Value.TryGetInt64(out var value) && value > 0:
                    right = value;
                    break;
                default:
                    throw new SemanticValidationException(
                        SemanticErrorCodes.ConditionsInvalid,
                        "JOIN 条件只允许 leftColumnId/rightColumnId 两个正整数字段。");
            }
        }

        if (left is null || right is null)
        {
            throw new SemanticValidationException(
                SemanticErrorCodes.ConditionsInvalid, "JOIN 条件缺少 leftColumnId 或 rightColumnId。");
        }

        return new JoinCondition(left.Value, right.Value);
    }

    // ---- DataPermissionRules.CustomPredicateJson：结构化 [ {"column":"…","op":"…","value":…} ] ----

    private static readonly HashSet<string> AllowedPredicateOps = new(StringComparer.Ordinal)
    { "eq", "ne", "gt", "gte", "lt", "lte", "like_prefix", "in" };

    /// <summary>
    /// 解析并校验 CustomPredicateJson（行级权限规则；S2 内无端点使用，随
    /// dataperm 管理面与 S4 改写器落地）。只接受 [ { column, op, value } ]：
    /// column 为物理列标识符，op ∈ 白名单，value 为标量或 in 数组。
    /// 服务端将其转参数化 WHERE，绝不存 SQL 片段。
    /// </summary>
    public static IReadOnlyList<PredicateCondition> ParsePredicateJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new SemanticValidationException(SemanticErrorCodes.FieldInvalid, "CustomPredicateJson 必填。");
        }

        System.Text.Json.JsonDocument document;
        try
        {
            document = System.Text.Json.JsonDocument.Parse(json);
        }
        catch (System.Text.Json.JsonException)
        {
            throw new SemanticValidationException(SemanticErrorCodes.FieldInvalid, "CustomPredicateJson 不是合法 JSON。");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                throw new SemanticValidationException(SemanticErrorCodes.FieldInvalid, "CustomPredicateJson 必须是 JSON 数组。");
            }

            var conditions = new List<PredicateCondition>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                conditions.Add(ParsePredicateCondition(element));
            }

            return conditions;
        }
    }

    private static PredicateCondition ParsePredicateCondition(System.Text.Json.JsonElement element)
    {
        if (element.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            throw new SemanticValidationException(SemanticErrorCodes.FieldInvalid, "权限条件必须是对象。");
        }

        string? column = null;
        string? op = null;
        string? value = null;
        foreach (var property in element.EnumerateObject())
        {
            switch (property.Name)
            {
                case "column" when property.Value.ValueKind == System.Text.Json.JsonValueKind.String:
                    column = property.Value.GetString();
                    break;
                case "op" when property.Value.ValueKind == System.Text.Json.JsonValueKind.String:
                    op = property.Value.GetString();
                    break;
                case "value":
                    value = SerializePredicateValue(property.Value);
                    break;
                default:
                    throw new SemanticValidationException(
                        SemanticErrorCodes.FieldInvalid, "权限条件只允许 column/op/value 三个字段。");
            }
        }

        if (string.IsNullOrWhiteSpace(column) || !Regex.IsMatch(column, @"^[A-Za-z_][A-Za-z0-9_]*$"))
        {
            throw new SemanticValidationException(
                SemanticErrorCodes.FieldInvalid, "权限条件 column 必须是物理列标识符（字母/下划线开头）。");
        }

        if (op is null || !AllowedPredicateOps.Contains(op))
        {
            throw new SemanticValidationException(
                SemanticErrorCodes.FieldInvalid, $"权限条件 op 只允许：{string.Join('/', AllowedPredicateOps.OrderBy(x => x, StringComparer.Ordinal))}。");
        }

        if (value is null)
        {
            throw new SemanticValidationException(SemanticErrorCodes.FieldInvalid, "权限条件缺少 value。");
        }

        return new PredicateCondition(column, op, value);
    }

    private static string SerializePredicateValue(System.Text.Json.JsonElement value)
    {
        if (value.ValueKind is System.Text.Json.JsonValueKind.String
            or System.Text.Json.JsonValueKind.Number
            or System.Text.Json.JsonValueKind.True
            or System.Text.Json.JsonValueKind.False)
        {
            return value.GetRawText();
        }

        if (value.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            var items = value.EnumerateArray().ToList();
            if (items.Count == 0 || items.Any(item => item.ValueKind is not (System.Text.Json.JsonValueKind.String or System.Text.Json.JsonValueKind.Number)))
            {
                throw new SemanticValidationException(
                    SemanticErrorCodes.FieldInvalid, "value 数组（in 操作）只能含字符串/数字，且不为空。");
            }

            return value.GetRawText();
        }

        throw new SemanticValidationException(
            SemanticErrorCodes.FieldInvalid, "value 只允许字符串/数字/布尔或其数组（in 操作）。");
    }
}
