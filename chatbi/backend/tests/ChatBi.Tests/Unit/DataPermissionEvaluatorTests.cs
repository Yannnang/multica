using ChatBi.Application.DataPermissions;
using ChatBi.Domain.Entities;
using ChatBi.Domain.Enums;
using ChatBi.Domain.Security;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ChatBi.Tests.Unit;

public class DataPermissionEvaluatorTests
{
    private sealed class FakeRuleReader(IReadOnlyList<DataPermissionRule> rules) : IDataPermissionRuleReader
    {
        public Task<IReadOnlyList<DataPermissionRule>> GetEnabledRulesAsync(
            long userId, IReadOnlyList<string> roleCodes, CancellationToken cancellationToken = default) =>
            Task.FromResult(rules);
    }

    private static DataPermissionEvaluator CreateEvaluator(params DataPermissionRule[] rules) =>
        new(new FakeRuleReader(rules), NullLogger<DataPermissionEvaluator>.Instance);

    private static readonly string[] BusinessRoles = { RoleCodes.BusinessUser };

    [Fact]
    public async Task NoRules_ConservativeSelfOnly()
    {
        var decision = await CreateEvaluator().EvaluateAsync(1, BusinessRoles, 10);

        decision.ScopeType.Should().Be(DataScopeType.SelfOnly);
        decision.MatchedRuleId.Should().BeNull();
    }

    [Fact]
    public async Task RoleGlobalRule_AppliesToAnyTable()
    {
        var evaluator = CreateEvaluator(new DataPermissionRule
        {
            Id = 1,
            SubjectType = PermissionSubjectType.Role,
            SubjectId = 1,
            SemanticTableId = null,
            ScopeType = DataScopeType.DepartmentAndBelow,
            DepartmentPathPrefix = "/总部/",
            Enabled = true,
        });

        var decision = await evaluator.EvaluateAsync(1, BusinessRoles, 77);

        decision.ScopeType.Should().Be(DataScopeType.DepartmentAndBelow);
        decision.DepartmentPathPrefix.Should().Be("/总部/");
        decision.MatchedRuleId.Should().Be(1);
    }

    [Fact]
    public async Task UserTableSpecificRule_Wins_OverRoleGlobal()
    {
        var evaluator = CreateEvaluator(
            new DataPermissionRule
            {
                Id = 1, SubjectType = PermissionSubjectType.Role, SubjectId = 1,
                SemanticTableId = null, ScopeType = DataScopeType.All, Enabled = true,
            },
            new DataPermissionRule
            {
                Id = 2, SubjectType = PermissionSubjectType.User, SubjectId = 42,
                SemanticTableId = 10, ScopeType = DataScopeType.SelfOnly, Enabled = true,
            });

        var decision = await evaluator.EvaluateAsync(42, BusinessRoles, 10);

        decision.MatchedRuleId.Should().Be(2);
        decision.ScopeType.Should().Be(DataScopeType.SelfOnly);
    }

    [Fact]
    public async Task SamePriority_BroadestScopeWins()
    {
        var evaluator = CreateEvaluator(
            new DataPermissionRule
            {
                Id = 1, SubjectType = PermissionSubjectType.Role, SubjectId = 1,
                SemanticTableId = null, ScopeType = DataScopeType.SelfOnly, Enabled = true,
            },
            new DataPermissionRule
            {
                Id = 2, SubjectType = PermissionSubjectType.Role, SubjectId = 2,
                SemanticTableId = null, ScopeType = DataScopeType.All, Enabled = true,
            });

        var decision = await evaluator.EvaluateAsync(1, new[] { RoleCodes.BusinessUser, RoleCodes.DataSteward }, null);

        decision.ScopeType.Should().Be(DataScopeType.All);
        decision.MatchedRuleId.Should().Be(2);
    }

    [Fact]
    public async Task RuleForOtherTable_Excluded()
    {
        var evaluator = CreateEvaluator(new DataPermissionRule
        {
            Id = 1, SubjectType = PermissionSubjectType.Role, SubjectId = 1,
            SemanticTableId = 99, ScopeType = DataScopeType.All, Enabled = true,
        });

        var decision = await evaluator.EvaluateAsync(1, BusinessRoles, 10);

        decision.ScopeType.Should().Be(DataScopeType.SelfOnly);
        decision.MatchedRuleId.Should().BeNull();
    }

    [Fact]
    public async Task CustomRule_ValidJson_ReturnsWhitelistedPredicates()
    {
        var evaluator = CreateEvaluator(new DataPermissionRule
        {
            Id = 1, SubjectType = PermissionSubjectType.User, SubjectId = 7,
            SemanticTableId = 10, ScopeType = DataScopeType.Custom,
            CustomPredicateJson = """[{"column":"region","op":"eq","value":"华东"},{"column":"dept_path","op":"like_prefix","value":"/总部/"}]""",
            Enabled = true,
        });

        var decision = await evaluator.EvaluateAsync(7, BusinessRoles, 10);

        decision.ScopeType.Should().Be(DataScopeType.Custom);
        decision.CustomPredicates.Should().HaveCount(2);
        decision.CustomPredicates[0].Should().Be(new CustomPredicate("region", "eq", "华东"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-json")]
    [InlineData("""[{"column":"x","op":"raw_sql","value":"1=1"}]""")] // 非白名单 op
    [InlineData("""[{"column":"","op":"eq","value":"v"}]""")] // 空列名
    [InlineData("[]")] // 空数组
    public async Task CustomRule_InvalidJson_RuleIgnored_Conservative(string? json)
    {
        var evaluator = CreateEvaluator(new DataPermissionRule
        {
            Id = 1, SubjectType = PermissionSubjectType.User, SubjectId = 7,
            SemanticTableId = null, ScopeType = DataScopeType.Custom,
            CustomPredicateJson = json, Enabled = true,
        });

        var decision = await evaluator.EvaluateAsync(7, BusinessRoles, 10);

        decision.ScopeType.Should().Be(DataScopeType.SelfOnly);
        decision.MatchedRuleId.Should().BeNull();
    }

    [Fact]
    public async Task DepartmentRuleWithoutPrefix_FallsBackSelfOnly_ButRecordsRule()
    {
        var evaluator = CreateEvaluator(new DataPermissionRule
        {
            Id = 3, SubjectType = PermissionSubjectType.Role, SubjectId = 1,
            SemanticTableId = null, ScopeType = DataScopeType.DepartmentAndBelow,
            DepartmentPathPrefix = null, Enabled = true,
        });

        var decision = await evaluator.EvaluateAsync(1, BusinessRoles, 10);

        decision.ScopeType.Should().Be(DataScopeType.SelfOnly);
        decision.MatchedRuleId.Should().Be(3);
    }

    [Fact]
    public void PredicateOperatorWhitelist_IsEnforced()
    {
        new CustomPredicate("c", "eq", "v").IsValid.Should().BeTrue();
        new CustomPredicate("c", "in", "v").IsValid.Should().BeTrue();
        new CustomPredicate("c", "like_prefix", "v").IsValid.Should().BeTrue();
        new CustomPredicate("c", "drop_table", "v").IsValid.Should().BeFalse();
    }
}
