using System.Net;
using ChatBi.Domain.Security;
using FluentAssertions;
using Xunit;

namespace ChatBi.Tests.Integration;

/// <summary>语义表/语义列端点契约：CRUD 成功路径、引用校验 422、唯一冲突 409、并发 409、软删/物删差异。</summary>
[Collection(IntegrationCollection.Name)]
public class SemanticTableColumnEndpointTests : SemanticEndpointTestBase
{
    public SemanticTableColumnEndpointTests(ChatBiWebApplicationFactory factory) : base(factory)
    {
    }

    private const string TablesPath = "/api/semantic/tables";
    private const string ColumnsPath = "/api/semantic/columns";

    private static object TablePayload(long dataSourceId, string? tableName = null, string? rowVersion = null) => new
    {
        DataSourceId = dataSourceId,
        SchemaName = "dbo",
        TableName = tableName ?? $"T_{Guid.NewGuid():N}"[..12],
        DisplayName = "订单表",
        Description = (string?)"集成测试",
        IsEnabled = true,
        RowVersion = rowVersion,
    };

    private static object ColumnPayload(long tableId, string? columnName = null, string? rowVersion = null) => new
    {
        SemanticTableId = tableId,
        ColumnName = columnName ?? $"C_{Guid.NewGuid():N}"[..12],
        DisplayName = "订单号",
        DbTypeName = "nvarchar(32)",
        SemanticType = 1,
        IsSensitive = false,
        Description = (string?)null,
        RowVersion = rowVersion,
    };

    [Fact]
    public async Task Table_Crud_RoundTrips_WithConcurrencyAndSoftDelete()
    {
        var steward = await CreateUserTokenAsync(RoleCodes.DataSteward);
        var client = ClientWith(steward);
        var dataSourceId = await SeedDataSourceAsync();

        var create = await client.PostAsync(TablesPath, JsonContent(ToJson(TablePayload(dataSourceId))));
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await JsonAsync(create);
        var id = (long)created["id"]!;
        var rowVersion = created["rowVersion"]!.GetValue<string>();

        // 正常更新（携带新鲜令牌）。
        var rename = TablePayload(dataSourceId, tableName: (string)created["tableName"]!, rowVersion: rowVersion);
        (await client.PutAsync($"{TablesPath}/{id}", JsonContent(ToJson(rename)))).StatusCode
            .Should().Be(HttpStatusCode.OK);

        // 过期令牌 → 409。
        var (conflictStatus, conflictCode) = await ProblemAsync(
            await client.PutAsync($"{TablesPath}/{id}", JsonContent(ToJson(rename))));
        conflictStatus.Should().Be(HttpStatusCode.Conflict);
        conflictCode.Should().Be("SEM_CONCURRENCY_CONFLICT");

        // 软删后单查 404。
        (await client.DeleteAsync($"{TablesPath}/{id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var (status, code) = await ProblemAsync(await client.GetAsync($"{TablesPath}/{id}"));
        status.Should().Be(HttpStatusCode.NotFound);
        code.Should().Be("SEM_NOT_FOUND");
    }

    [Fact]
    public async Task Table_UnknownDataSource_Returns422()
    {
        var steward = await CreateUserTokenAsync(RoleCodes.DataSteward);
        var (status, code) = await ProblemAsync(
            await ClientWith(steward).PostAsync(TablesPath, JsonContent(ToJson(TablePayload(dataSourceId: 999999999)))));

        status.Should().Be(HttpStatusCode.UnprocessableEntity);
        code.Should().Be("SEM_FIELD_INVALID");
    }

    [Fact]
    public async Task Table_DuplicateComposite_Returns409()
    {
        var steward = await CreateUserTokenAsync(RoleCodes.DataSteward);
        var client = ClientWith(steward);
        var dataSourceId = await SeedDataSourceAsync();
        var tableName = $"T_{Guid.NewGuid():N}"[..12];

        (await client.PostAsync(TablesPath, JsonContent(ToJson(TablePayload(dataSourceId, tableName))))).StatusCode
            .Should().Be(HttpStatusCode.Created);
        var (status, code) = await ProblemAsync(
            await client.PostAsync(TablesPath, JsonContent(ToJson(TablePayload(dataSourceId, tableName)))));

        status.Should().Be(HttpStatusCode.Conflict);
        code.Should().Be("SEM_DUPLICATE");
    }

    [Fact]
    public async Task Column_Crud_HardDelete()
    {
        var steward = await CreateUserTokenAsync(RoleCodes.DataSteward);
        var client = ClientWith(steward);
        var tableId = await SeedTableAsync(await SeedDataSourceAsync());

        var create = await client.PostAsync(ColumnsPath, JsonContent(ToJson(ColumnPayload(tableId))));
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await JsonAsync(create);
        var id = (long)created["id"]!;

        var freshRowVersion = created["rowVersion"]!.GetValue<string>();
        (await client.PutAsync($"{ColumnsPath}/{id}", JsonContent(ToJson(ColumnPayload(tableId, (string)created["columnName"]!, freshRowVersion)))))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await client.DeleteAsync($"{ColumnsPath}/{id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.GetAsync($"{ColumnsPath}/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Column_DuplicateNameInSameTable_Returns409()
    {
        var steward = await CreateUserTokenAsync(RoleCodes.DataSteward);
        var client = ClientWith(steward);
        var tableId = await SeedTableAsync(await SeedDataSourceAsync());
        var columnName = $"C_{Guid.NewGuid():N}"[..12];

        (await client.PostAsync(ColumnsPath, JsonContent(ToJson(ColumnPayload(tableId, columnName))))).StatusCode
            .Should().Be(HttpStatusCode.Created);
        var (status, code) = await ProblemAsync(
            await client.PostAsync(ColumnsPath, JsonContent(ToJson(ColumnPayload(tableId, columnName)))));

        status.Should().Be(HttpStatusCode.Conflict);
        code.Should().Be("SEM_DUPLICATE");
    }

    [Fact]
    public async Task Write_BusinessUser_403_Read_200()
    {
        var business = await CreateUserTokenAsync(RoleCodes.BusinessUser);
        var client = ClientWith(business);

        var (postStatus, _) = await ProblemAsync(
            await client.PostAsync(TablesPath, JsonContent(ToJson(TablePayload(dataSourceId: 1)))));
        postStatus.Should().Be(HttpStatusCode.Forbidden);

        (await client.GetAsync($"{TablesPath}?page=1&pageSize=1")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync($"{ColumnsPath}?page=1&pageSize=1")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task NoToken_Returns401()
    {
        (await Factory.CreateClient().GetAsync(TablesPath)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Factory.CreateClient().GetAsync(ColumnsPath)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
