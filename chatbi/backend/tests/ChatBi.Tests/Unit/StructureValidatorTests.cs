using ChatBi.Application.Common;
using ChatBi.Application.Semantic;
using FluentAssertions;
using Xunit;

namespace ChatBi.Tests.Unit;

/// <summary>结构化校验器纯函数单测：CredentialRef / MetricExpression / JOIN 条件 / 权限谓词。</summary>
public class StructureValidatorTests
{
    // ---- ValidateCredentialRef ----

    [Theory]
    [InlineData("BIZ_DB_READONLY_CONNECTION")]
    [InlineData("kv.secret-db.01")]
    [InlineData("KeyVault-Prod-Conn")]
    public void CredentialRef_ValidReferenceNames_Pass(string credentialRef) =>
        FluentActions.Invoking(() => StructureValidators.ValidateCredentialRef(credentialRef))
            .Should().NotThrow();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1starts_with_digit")]
    [InlineData("含空格 名")]
    [InlineData("ref!")]
    public void CredentialRef_InvalidForms_ThrowCredentialRefInvalid(string? credentialRef) =>
        FluentActions.Invoking(() => StructureValidators.ValidateCredentialRef(credentialRef))
            .Should().Throw<SemanticValidationException>()
            .Where(ex => ex.Code == SemanticErrorCodes.CredentialRefInvalid);

    [Theory]
    [InlineData("Server=10.0.0.1;Database=x;User Id=sa;Password=y")]
    [InlineData("password=whatever")]
    [InlineData("Initial Catalog=biz")]
    [InlineData("Trusted_Connection=True")]
    [InlineData("https://user:pass@host/db")]
    [InlineData("admin@10.0.0.1")]
    [InlineData("host.example.com:1433/db")]
    public void CredentialRef_PlaintextPatterns_ThrowPlaintextCredential(string credentialRef) =>
        FluentActions.Invoking(() => StructureValidators.ValidateCredentialRef(credentialRef))
            .Should().Throw<SemanticValidationException>()
            .Where(ex => ex.Code == SemanticErrorCodes.PlaintextCredential);

    // ---- ValidateMetricExpression ----

    [Theory]
    [InlineData("SUM({t}.amount)")]
    [InlineData("COUNT({t}.id)")]
    [InlineData("SUM({t}.a) / NULLIF(COUNT({t}.b), 0)")]
    [InlineData("AVG(CASE WHEN {t}.flag = 1 THEN {t}.x ELSE NULL END)")]
    public void MetricExpression_ValidTemplates_Pass(string expression) =>
        FluentActions.Invoking(() => StructureValidators.ValidateMetricExpression(expression))
            .Should().NotThrow();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("SUM({t}.amount); DROP TABLE x")] // 分号
    [InlineData("SUM({t}.amount) -- tail")] // 行注释
    [InlineData("/* head */ SUM({t}.amount)")] // 块注释
    [InlineData("SUM({t}.amount) FROM {t}")] // 数据访问关键字
    [InlineData("COUNT('abc')")] // 字符串字面量
    [InlineData("{t}.amount")] // 无聚合函数
    [InlineData("SUM({t}.amount")] // 括号不配对
    [InlineData("SUM({x}.amount)")] // 非 {t} 占位符
    [InlineData("SUM({t.amount)")] // 占位符未闭合
    public void MetricExpression_Violations_ThrowExpressionInvalid(string? expression) =>
        FluentActions.Invoking(() => StructureValidators.ValidateMetricExpression(expression))
            .Should().Throw<SemanticValidationException>()
            .Where(ex => ex.Code == SemanticErrorCodes.ExpressionInvalid);

    [Fact]
    public void MetricExpression_OverLength_Throws() =>
        FluentActions.Invoking(() => StructureValidators.ValidateMetricExpression(
            "SUM({t}.a) + " + new string('1', 1024)))
            .Should().Throw<SemanticValidationException>()
            .Where(ex => ex.Code == SemanticErrorCodes.ExpressionInvalid);

    // ---- ParseJoinConditionsJson ----

    [Fact]
    public void JoinConditions_Valid_ReturnsPairs()
    {
        var conditions = StructureValidators.ParseJoinConditionsJson(
            """[{"leftColumnId":1,"rightColumnId":2},{"leftColumnId":3,"rightColumnId":4}]""");

        conditions.Should().HaveCount(2);
        conditions[0].LeftColumnId.Should().Be(1);
        conditions[0].RightColumnId.Should().Be(2);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("""[{"leftColumnId":1}]""")]
    [InlineData("""[{"leftColumnId":1,"rightColumnId":2,"extra":3}]""")]
    [InlineData("""[{"leftColumnId":-1,"rightColumnId":2}]""")]
    [InlineData("""[{"leftColumnId":"1","rightColumnId":2}]""")]
    public void JoinConditions_Violations_ThrowConditionsInvalid(string? json) =>
        FluentActions.Invoking(() => StructureValidators.ParseJoinConditionsJson(json))
            .Should().Throw<SemanticValidationException>()
            .Where(ex => ex.Code == SemanticErrorCodes.ConditionsInvalid);

    // ---- ParsePredicateJson ----

    [Fact]
    public void Predicate_Valid_ReturnsConditions()
    {
        var conditions = StructureValidators.ParsePredicateJson(
            """[{"column":"dept_id","op":"eq","value":100},{"column":"region","op":"in","value":["north","south"]}]""");

        conditions.Should().HaveCount(2);
        conditions[0].Column.Should().Be("dept_id");
        conditions[0].Op.Should().Be("eq");
        conditions[1].Op.Should().Be("in");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not json")]
    [InlineData("""[{"column":"1bad","op":"eq","value":1}]""")]
    [InlineData("""[{"column":"dept","op":"drop","value":1}]""")]
    [InlineData("""[{"column":"dept","op":"eq"}]""")]
    [InlineData("""[{"column":"dept","op":"in","value":[]}]""")]
    [InlineData("""[{"column":"dept","op":"in","value":[{"nested":1}]}]""")]
    [InlineData("""[{"column":"dept","op":"eq","value":1,"extra":2}]""")]
    public void Predicate_Violations_ThrowFieldInvalid(string? json) =>
        FluentActions.Invoking(() => StructureValidators.ParsePredicateJson(json))
            .Should().Throw<SemanticValidationException>()
            .Where(ex => ex.Code == SemanticErrorCodes.FieldInvalid);
}
