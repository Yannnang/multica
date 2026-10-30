using System.Net;
using ChatBi.Domain.Security;
using FluentAssertions;
using Xunit;

namespace ChatBi.Tests.Integration;

/// <summary>指标端点契约：CRUD + TableIds 关联整体替换、Expression 白名单 422、引用校验 422、并发 409。</summary>
[Collection(IntegrationCollection.Name)]
public class MetricEndpointTests : SemanticEndpointTestBase
{
    public MetricEndpointTests(ChatBiWebApplicationFactory factory) : base(factory)
    {
    }

    private const string MetricsPath = "/api/semantic/metrics";

    private static object Payload(
        string? code = null,
        string? expression = null,
        long[]? tableIds = null,
        long? defaultTimeColumnId = null,
        string? rowVersion = null) => new
    {
        Code = code ?? $"M_{Guid.NewGuid():N}"[..12],
        DisplayName = "销售额",
        Description = (string?)"指标集成测试",
        Expression = expression ?? "SUM({t}.amount)",
        AggregationType = 1,
        Unit = "元",
        DefaultTimeColumnId = defaultTimeColumnId,
        IsEnabled = true,
        OwnerUserId = (long?)null,
        TableIds = tableIds ?? Array.Empty<long>(),
        RowVersion = rowVersion,
    };

    [Fact]
    public async Task Crud_RoundTrip_WithTableIds()
    {
        var steward = await CreateUserTokenAsync(RoleCodes.DataSteward);
        var client = ClientWith(steward);
        var dataSourceId = await SeedDataSourceAsync();
        var tableA = await SeedTableAsync(dataSourceId);
        var tableB = await SeedTableAsync(dataSourceId);

        var create = await client.PostAsync(MetricsPath, JsonContent(ToJson(Payload(tableIds: new[] { tableA, tableB }))));
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await JsonAsync(create);
        var id = (long)created["id"]!;
        created["tableIds"]!.AsArray().Select(x => (long)x!).Should().BeEquivalentTo(new[] { tableA, tableB });

        // PUT 整体替换 TableIds：去掉 tableB。
        var updated = await JsonAsync(await client.PutAsync($"{MetricsPath}/{id}", JsonContent(ToJson(Payload(
            code: (string)created["code"]!, tableIds: new[] { tableA }, rowVersion: created["rowVersion"]!.GetValue<string>())))));
        updated["tableIds"]!.AsArray().Select(x => (long)x!).Should().BeEquivalentTo(new[] { tableA });

        (await client.DeleteAsync($"{MetricsPath}/{id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.GetAsync($"{MetricsPath}/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Post_ExpressionWithSqlKeyword_Returns422ExpressionInvalid()
    {
        var steward = await CreateUserTokenAsync(RoleCodes.DataSteward);
        var client = ClientWith(steward);

        var (status, code) = await ProblemAsync(await client.PostAsync(MetricsPath, JsonContent(ToJson(Payload(
            expression: "SUM({t}.amount) FROM dbo.t")))));

        status.Should().Be(HttpStatusCode.UnprocessableEntity);
        code.Should().Be("SEM_EXPRESSION_INVALID");
    }

    [Theory]
    [InlineData("SUM({t}.amount); DROP TABLE x")]
    [InlineData("SUM({t}.amount) -- comment")]
    [InlineData("/* hidden */ SUM({t}.amount)")]
    [InlineData("SUM('abc')")]
    [InlineData("{t}.amount")] // 无聚合函数
    public async Task Post_ExpressionWhitelistViolations_Returns422(string expression)
    {
        var steward = await CreateUserTokenAsync(RoleCodes.DataSteward);
        var client = ClientWith(steward);

        var (status, code) = await ProblemAsync(
            await client.PostAsync(MetricsPath, JsonContent(ToJson(Payload(expression: expression)))));

        status.Should().Be(HttpStatusCode.UnprocessableEntity);
        code.Should().Be("SEM_EXPRESSION_INVALID");
    }

    [Fact]
    public async Task Post_UnknownTableId_Returns422()
    {
        var steward = await CreateUserTokenAsync(RoleCodes.DataSteward);
        var client = ClientWith(steward);
        var (status, code) = await ProblemAsync(await client.PostAsync(
            MetricsPath, JsonContent(ToJson(Payload(tableIds: new[] { 999999999L })))));

        status.Should().Be(HttpStatusCode.UnprocessableEntity);
        code.Should().Be("SEM_FIELD_INVALID");
    }

    [Fact]
    public async Task Post_UnknownDefaultTimeColumn_Returns422()
    {
        var steward = await CreateUserTokenAsync(RoleCodes.DataSteward);
        var client = ClientWith(steward);
        var (status, code) = await ProblemAsync(await client.PostAsync(
            MetricsPath, JsonContent(ToJson(Payload(defaultTimeColumnId: 999999999L)))));

        status.Should().Be(HttpStatusCode.UnprocessableEntity);
        code.Should().Be("SEM_FIELD_INVALID");
    }

    [Fact]
    public async Task Put_StaleRowVersion_Returns409()
    {
        var steward = await CreateUserTokenAsync(RoleCodes.DataSteward);
        var client = ClientWith(steward);
        var created = await JsonAsync(await client.PostAsync(MetricsPath, JsonContent(ToJson(Payload()))));
        var id = (long)created["id"]!;
        var stale = created["rowVersion"]!.GetValue<string>();

        (await client.PutAsync($"{MetricsPath}/{id}", JsonContent(ToJson(Payload(rowVersion: stale)))))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var (status, code) = await ProblemAsync(
            await client.PutAsync($"{MetricsPath}/{id}", JsonContent(ToJson(Payload(rowVersion: stale)))));

        status.Should().Be(HttpStatusCode.Conflict);
        code.Should().Be("SEM_CONCURRENCY_CONFLICT");
    }

    [Fact]
    public async Task Write_BusinessUser_403_Read_200()
    {
        var business = await CreateUserTokenAsync(RoleCodes.BusinessUser);
        var client = ClientWith(business);

        var (postStatus, _) = await ProblemAsync(
            await client.PostAsync(MetricsPath, JsonContent(ToJson(Payload()))));
        postStatus.Should().Be(HttpStatusCode.Forbidden);

        (await client.GetAsync($"{MetricsPath}?page=1&pageSize=1")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task NoToken_Returns401()
    {
        (await Factory.CreateClient().GetAsync(MetricsPath)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
