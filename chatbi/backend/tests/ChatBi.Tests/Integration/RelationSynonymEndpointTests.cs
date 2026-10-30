using System.Net;
using ChatBi.Domain.Security;
using FluentAssertions;
using Xunit;

namespace ChatBi.Tests.Integration;

/// <summary>表关联/同义词端点契约：结构化 JOIN 条件校验、列归属校验、目标存在性校验、物理删除。</summary>
[Collection(IntegrationCollection.Name)]
public class RelationSynonymEndpointTests : SemanticEndpointTestBase
{
    public RelationSynonymEndpointTests(ChatBiWebApplicationFactory factory) : base(factory)
    {
    }

    private const string RelationsPath = "/api/semantic/relations";
    private const string SynonymsPath = "/api/semantic/synonyms";

    private static object RelationPayload(
        long leftTableId, long rightTableId, string conditionsJson, byte[]? rowVersion = null) => new
    {
        LeftSemanticTableId = leftTableId,
        RightSemanticTableId = rightTableId,
        JoinType = 1,
        OnConditionsJson = conditionsJson,
        RowVersion = rowVersion,
    };

    private static object SynonymPayload(string? term = null, int targetType = 1, long targetId = 1) => new
    {
        Term = term ?? $"词{Guid.NewGuid():N}"[..10],
        TargetType = targetType,
        TargetId = targetId,
    };

    /// <summary>铺两侧表各一列，返回 (leftTableId, rightTableId, leftColumnId, rightColumnId)。</summary>
    private async Task<(long Left, long Right, long LeftCol, long RightCol)> SeedJoinPairAsync()
    {
        var dataSourceId = await SeedDataSourceAsync();
        var left = await SeedTableAsync(dataSourceId);
        var right = await SeedTableAsync(dataSourceId);
        var leftCol = await SeedColumnAsync(left);
        var rightCol = await SeedColumnAsync(right);
        return (left, right, leftCol, rightCol);
    }

    [Fact]
    public async Task Relation_Crud_RoundTrips_WithCanonicalJson()
    {
        var steward = await CreateUserTokenAsync(RoleCodes.DataSteward);
        var client = ClientWith(steward);
        var (left, right, leftCol, rightCol) = await SeedJoinPairAsync();

        var create = await client.PostAsync(RelationsPath, JsonContent(ToJson(RelationPayload(
            left, right, $$"""[{"leftColumnId":{{leftCol}},"rightColumnId":{{rightCol}}}]"""))));
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await JsonAsync(create);
        var id = (long)created["id"]!;

        // 规范化 JSON：列以 id 表达，与输入等价。
        created["onConditionsJson"]!.GetValue<string>().Should().Be(
            $$"""[{"leftColumnId":{{leftCol}},"rightColumnId":{{rightCol}}}]""");

        (await client.DeleteAsync($"{RelationsPath}/{id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.GetAsync($"{RelationsPath}/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Relation_SameTableBothSides_Returns422()
    {
        var steward = await CreateUserTokenAsync(RoleCodes.DataSteward);
        var client = ClientWith(steward);
        var dataSourceId = await SeedDataSourceAsync();
        var tableId = await SeedTableAsync(dataSourceId);

        var (status, code) = await ProblemAsync(await client.PostAsync(RelationsPath, JsonContent(ToJson(
            RelationPayload(tableId, tableId, """[{"leftColumnId":1,"rightColumnId":1}]""")))));

        status.Should().Be(HttpStatusCode.UnprocessableEntity);
        code.Should().Be("SEM_FIELD_INVALID");
    }

    [Theory]
    [InlineData("""{"leftColumnId":1}""", "SEM_CONDITIONS_INVALID")] // 不是数组
    [InlineData("""[]""", "SEM_CONDITIONS_INVALID")] // 空数组
    [InlineData("""not json""", "SEM_CONDITIONS_INVALID")] // 非法 JSON
    [InlineData("""[{"leftColumnId":"1","rightColumnId":2}]""", "SEM_CONDITIONS_INVALID")] // 类型不符
    public async Task Relation_StructureViolations_Returns422(string conditionsJson, string expectedCode)
    {
        var steward = await CreateUserTokenAsync(RoleCodes.DataSteward);
        var client = ClientWith(steward);
        var dataSourceId = await SeedDataSourceAsync();
        var left = await SeedTableAsync(dataSourceId);
        var right = await SeedTableAsync(dataSourceId);

        var (status, code) = await ProblemAsync(await client.PostAsync(
            RelationsPath, JsonContent(ToJson(RelationPayload(left, right, conditionsJson)))));

        status.Should().Be(HttpStatusCode.UnprocessableEntity);
        code.Should().Be(expectedCode);
    }

    [Fact]
    public async Task Relation_ColumnOwnershipViolation_Returns422()
    {
        var steward = await CreateUserTokenAsync(RoleCodes.DataSteward);
        var client = ClientWith(steward);
        var (left, right, leftCol, _) = await SeedJoinPairAsync();
        // 用左表的列冒充右表的列 → 归属校验失败。
        var (status, code) = await ProblemAsync(await client.PostAsync(RelationsPath, JsonContent(ToJson(
            RelationPayload(left, right, $$"""[{"leftColumnId":{{leftCol}},"rightColumnId":{{leftCol}}}]""")))));

        status.Should().Be(HttpStatusCode.UnprocessableEntity);
        code.Should().Be("SEM_CONDITIONS_INVALID");
    }

    [Fact]
    public async Task Relation_DuplicatePair_Returns409()
    {
        var steward = await CreateUserTokenAsync(RoleCodes.DataSteward);
        var client = ClientWith(steward);
        var (left, right, leftCol, rightCol) = await SeedJoinPairAsync();
        var conditions = $$"""[{"leftColumnId":{{leftCol}},"rightColumnId":{{rightCol}}}]""";

        (await client.PostAsync(RelationsPath, JsonContent(ToJson(RelationPayload(left, right, conditions)))))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        var (status, code) = await ProblemAsync(
            await client.PostAsync(RelationsPath, JsonContent(ToJson(RelationPayload(left, right, conditions)))));

        status.Should().Be(HttpStatusCode.Conflict);
        code.Should().Be("SEM_DUPLICATE");
    }

    [Fact]
    public async Task Synonym_Crud_RoundTrips()
    {
        var steward = await CreateUserTokenAsync(RoleCodes.DataSteward);
        var client = ClientWith(steward);
        var dataSourceId = await SeedDataSourceAsync();
        var tableId = await SeedTableAsync(dataSourceId);

        var create = await client.PostAsync(SynonymsPath, JsonContent(ToJson(SynonymPayload(targetType: 2, targetId: tableId))));
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await JsonAsync(create);
        var id = (long)created["id"]!;
        ((int)created["targetType"]!).Should().Be(2);

        // PUT 幂等覆盖（无并发令牌）。
        var update = await client.PutAsync($"{SynonymsPath}/{id}", JsonContent(ToJson(SynonymPayload("订单表别名", 2, tableId))));
        update.StatusCode.Should().Be(HttpStatusCode.OK);
        (await JsonAsync(update))["term"]!.GetValue<string>().Should().Be("订单表别名");

        (await client.DeleteAsync($"{SynonymsPath}/{id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.GetAsync($"{SynonymsPath}/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Synonym_TargetNotExists_Returns422TargetNotFound()
    {
        var steward = await CreateUserTokenAsync(RoleCodes.DataSteward);
        var client = ClientWith(steward);
        var (status, code) = await ProblemAsync(await client.PostAsync(
            SynonymsPath, JsonContent(ToJson(SynonymPayload(targetType: 1, targetId: 999999999)))));

        status.Should().Be(HttpStatusCode.UnprocessableEntity);
        code.Should().Be("SEM_TARGET_NOT_FOUND");
    }

    [Fact]
    public async Task Synonym_DuplicateBinding_Returns409()
    {
        var steward = await CreateUserTokenAsync(RoleCodes.DataSteward);
        var client = ClientWith(steward);
        var tableId = await SeedTableAsync(await SeedDataSourceAsync());
        var term = $"词{Guid.NewGuid():N}"[..10];

        (await client.PostAsync(SynonymsPath, JsonContent(ToJson(SynonymPayload(term, 2, tableId))))).StatusCode
            .Should().Be(HttpStatusCode.Created);
        var (status, code) = await ProblemAsync(
            await client.PostAsync(SynonymsPath, JsonContent(ToJson(SynonymPayload(term, 2, tableId)))));

        status.Should().Be(HttpStatusCode.Conflict);
        code.Should().Be("SEM_DUPLICATE");
    }

    [Fact]
    public async Task Write_BusinessUser_403_Read_200()
    {
        var business = await CreateUserTokenAsync(RoleCodes.BusinessUser);
        var client = ClientWith(business);

        var (postStatus, _) = await ProblemAsync(
            await client.PostAsync(RelationsPath, JsonContent(ToJson(RelationPayload(1, 2, "[]")))));
        postStatus.Should().Be(HttpStatusCode.Forbidden);

        (await client.GetAsync($"{RelationsPath}?page=1&pageSize=1")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync($"{SynonymsPath}?page=1&pageSize=1")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task NoToken_Returns401()
    {
        (await Factory.CreateClient().GetAsync(RelationsPath)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Factory.CreateClient().GetAsync(SynonymsPath)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
