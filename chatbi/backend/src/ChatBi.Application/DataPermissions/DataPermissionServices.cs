using System.Text.Json;
using ChatBi.Domain.Entities;
using ChatBi.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace ChatBi.Application.DataPermissions;

/// <summary>
/// 结构化自定义谓词（CustomPredicateJson 反序列化结果）。
/// 白名单 op；由 S4 的 PermissionAwareSqlRewriter 转参数化 WHERE，严禁 SQL 片段拼接。
/// </summary>
public record CustomPredicate(string Column, string Op, string Value)
{
    /// <summary>允许的谓词操作符白名单（服务端校验，防注入红线）。</summary>
    public static readonly IReadOnlySet<string> AllowedOperators =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "eq", "neq", "in", "like_prefix" };

    public bool IsValid =>
        !string.IsNullOrWhiteSpace(Column)
        && AllowedOperators.Contains(Op)
        && Value is not null;
}

/// <summary>数据范围评估结果（S1 只出决策，SQL 改写器 S4 实现）。</summary>
public record DataScopeDecision(
    DataScopeType ScopeType,
    string? DepartmentPathPrefix,
    IReadOnlyList<CustomPredicate> CustomPredicates,
    long? MatchedRuleId)
{
    /// <summary>无任何规则时的保守默认：SelfOnly（见设计基线 4.4）。</summary>
    public static DataScopeDecision ConservativeDefault { get; } =
        new(DataScopeType.SelfOnly, null, Array.Empty<CustomPredicate>(), null);
}

/// <summary>DataPermissionRules 读取抽象（Infrastructure EF 实现）。</summary>
public interface IDataPermissionRuleReader
{
    /// <summary>取对某主体生效的全部启用规则（User 主体 + 其角色的 Role 主体）。</summary>
    Task<IReadOnlyList<DataPermissionRule>> GetEnabledRulesAsync(
        long userId,
        IReadOnlyList<string> roleCodes,
        CancellationToken cancellationToken = default);
}

/// <summary>数据权限评估服务接口（SQL 改写器留给 S4 实现）。</summary>
public interface IDataPermissionEvaluator
{
    /// <summary>
    /// 评估用户对某语义表的行级数据范围。
    /// 规则优先级：User 主体 &gt; Role 主体；表级规则 &gt; 全局规则（SemanticTableId=NULL）；
    /// 同层多规则取最宽范围（All &gt; DepartmentAndBelow &gt; Custom &gt; SelfOnly）；无规则 → SelfOnly 保守默认。
    /// </summary>
    Task<DataScopeDecision> EvaluateAsync(
        long userId,
        IReadOnlyList<string> roleCodes,
        long? semanticTableId,
        CancellationToken cancellationToken = default);
}

/// <summary>数据权限评估（纯逻辑，规则来自 <see cref="IDataPermissionRuleReader"/>）。</summary>
public class DataPermissionEvaluator : IDataPermissionEvaluator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IDataPermissionRuleReader _ruleReader;
    private readonly ILogger<DataPermissionEvaluator> _logger;

    public DataPermissionEvaluator(IDataPermissionRuleReader ruleReader, ILogger<DataPermissionEvaluator> logger)
    {
        _ruleReader = ruleReader;
        _logger = logger;
    }

    public async Task<DataScopeDecision> EvaluateAsync(
        long userId,
        IReadOnlyList<string> roleCodes,
        long? semanticTableId,
        CancellationToken cancellationToken = default)
    {
        var rules = await _ruleReader.GetEnabledRulesAsync(userId, roleCodes, cancellationToken).ConfigureAwait(false);

        DataPermissionRule? best = null;
        IReadOnlyList<CustomPredicate> bestPredicates = Array.Empty<CustomPredicate>();
        var bestPriority = CandidatePriority.Worst;

        foreach (var rule in rules)
        {
            // 指向其他表的规则不参与本次评估；全局规则（NULL）总是候选。
            if (rule.SemanticTableId is not null && rule.SemanticTableId != semanticTableId)
            {
                continue;
            }

            IReadOnlyList<CustomPredicate> predicates = Array.Empty<CustomPredicate>();
            if (rule.ScopeType == DataScopeType.Custom)
            {
                if (!TryParsePredicates(rule.CustomPredicateJson, out var parsed))
                {
                    _logger.LogWarning("DataPermissionRule {RuleId} has invalid CustomPredicateJson; rule ignored (conservative).", rule.Id);
                    continue; // 非法 JSON / 非白名单 op → 规则作废
                }

                predicates = parsed;
            }

            var priority = CandidatePriority.For(rule);
            if (best is null || priority.BetterThan(bestPriority) || (priority.Equals(bestPriority) && rule.Id < best.Id))
            {
                best = rule;
                bestPredicates = predicates;
                bestPriority = priority;
            }
        }

        if (best is null)
        {
            return DataScopeDecision.ConservativeDefault;
        }

        if (best.ScopeType == DataScopeType.DepartmentAndBelow && string.IsNullOrWhiteSpace(best.DepartmentPathPrefix))
        {
            // 规则数据不完整 → 保守退化 SelfOnly。
            _logger.LogWarning("DataPermissionRule {RuleId} is DepartmentAndBelow without prefix; falling back to SelfOnly.", best.Id);
            return DataScopeDecision.ConservativeDefault with { MatchedRuleId = best.Id };
        }

        return new DataScopeDecision(best.ScopeType, best.DepartmentPathPrefix, bestPredicates, best.Id);
    }

    /// <summary>候选规则优先级：主体（User&gt;Role）→ 表级&gt;全局 → 范围宽度降序。</summary>
    private readonly record struct CandidatePriority(int SubjectRank, int TableRank, int BreadthRank)
    {
        public static CandidatePriority Worst { get; } = new(-1, -1, -1);

        public static CandidatePriority For(DataPermissionRule rule) => new(
            rule.SubjectType == PermissionSubjectType.User ? 1 : 0,
            rule.SemanticTableId is null ? 0 : 1,
            ScopeBreadth(rule.ScopeType));

        public bool BetterThan(CandidatePriority other) =>
            SubjectRank != other.SubjectRank ? SubjectRank > other.SubjectRank
            : TableRank != other.TableRank ? TableRank > other.TableRank
            : BreadthRank > other.BreadthRank;

        private static int ScopeBreadth(DataScopeType scopeType) => scopeType switch
        {
            DataScopeType.All => 4,
            DataScopeType.DepartmentAndBelow => 3,
            DataScopeType.Custom => 2,
            DataScopeType.SelfOnly => 1,
            _ => 0,
        };
    }

    private static bool TryParsePredicates(string? json, out IReadOnlyList<CustomPredicate> predicates)
    {
        predicates = Array.Empty<CustomPredicate>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<List<CustomPredicate>>(json, JsonOptions);
            if (parsed is null || parsed.Count == 0 || parsed.Any(p => !p.IsValid))
            {
                return false;
            }

            predicates = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
