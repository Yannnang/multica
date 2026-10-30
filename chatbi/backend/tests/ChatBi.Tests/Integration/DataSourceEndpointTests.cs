using System.Net;
using ChatBi.Domain.Security;
using FluentAssertions;
using Xunit;

namespace ChatBi.Tests.Integration;

/// <summary>数据源端点契约：CRUD 成功路径、401/403/404/409/422、并发冲突、明文凭证拒收。</summary>
[Collection(IntegrationCollection.Name)]
public class DataSourceEndpointTests : SemanticEndpointTestBase
{
    public DataSourceEndpointTests(ChatBiWebApplicationFactory factory) : base(factory)
    {
    }

    private const string BasePath = "/api/semantic/datasources";

    private static object Payload(string? name = null, object? credentialRef = null, object? rowVersion = null) => new
    {
        Name = name ?? $"DS_{Guid.NewGuid():N}"[..20],
        DbType = 1,
        Host = "10.0.0.8",
        Port = 1433,
        DatabaseName = "biz",
        CredentialRef = credentialRef ?? "BIZ_DB_READONLY_CONNECTION",
        QueryTimeoutSeconds = 30,
        MaxRowsPerQuery = 5000,
        Status = 1,
        RowVersion = rowVersion,
    };

    [Fact]
    public async Task Post_Create_ThenGetById_RoundTrips()
    {
        var admin = await CreateUserTokenAsync(RoleCodes.SysAdmin);
        var client = ClientWith(admin);
        var name = $"DS_{Guid.NewGuid():N}"[..20];

        var create = await client.PostAsync(BasePath, JsonContent(ToJson(Payload(name))));
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await JsonAsync(create);
        ((int)created["id"]!).Should().BePositive();
        created["name"]!.GetValue<string>().Should().Be(name);
        created["credentialRef"]!.GetValue<string>().Should().Be("BIZ_DB_READONLY_CONNECTION");
        create.Headers.Location.Should().NotBeNull();

        var get = await client.GetAsync($"{BasePath}/{created["id"]}");
        get.StatusCode.Should().Be(HttpStatusCode.OK);
        (await JsonAsync(get))["name"]!.GetValue<string>().Should().Be(name);
    }

    [Fact]
    public async Task List_Paged_StableOrderById()
    {
        await SeedDataSourceAsync();
        await SeedDataSourceAsync();

        var reader = await CreateUserTokenAsync(RoleCodes.BusinessUser);
        var firstPage = await JsonAsync(await ClientWith(reader).GetAsync($"{BasePath}?page=1&pageSize=5"));

        ((int)firstPage["page"]!).Should().Be(1);
        ((int)firstPage["pageSize"]!).Should().Be(5);
        ((int)firstPage["total"]!).Should().BeGreaterThanOrEqualTo(2);

        var ids = firstPage["items"]!.AsArray().Select(x => (long)x!["id"]!).ToList();
        ids.Should().BeInAscendingOrder().And.HaveCount(5);

        var secondPage = await JsonAsync(await ClientWith(reader).GetAsync($"{BasePath}?page=2&pageSize=5"));
        secondPage["items"]!.AsArray().Select(x => (long)x!["id"]!).Should()
            .OnlyContain(id => id > ids.Max(), "第 2 页应严格延续第 1 页的 Id 排序");
    }

    [Fact]
    public async Task Put_Update_SucceedsWithFreshRowVersion()
    {
        var admin = await CreateUserTokenAsync(RoleCodes.SysAdmin);
        var client = ClientWith(admin);
        var created = await JsonAsync(await client.PostAsync(BasePath, JsonContent(ToJson(Payload()))));
        var id = (long)created["id"]!;
        var rowVersion = created["rowVersion"]!.GetValue<string>();

        var update = await client.PutAsync(
            $"{BasePath}/{id}",
            JsonContent(ToJson(Payload(name: $"DS2_{Guid.NewGuid():N}"[..20], rowVersion: rowVersion))));

        update.StatusCode.Should().Be(HttpStatusCode.OK);
        (await JsonAsync(update))["port"]!.GetValue<int>().Should().Be(1433);
    }

    [Fact]
    public async Task Put_StaleRowVersion_Returns409ConcurrencyConflict()
    {
        var admin = await CreateUserTokenAsync(RoleCodes.SysAdmin);
        var client = ClientWith(admin);
        var created = await JsonAsync(await client.PostAsync(BasePath, JsonContent(ToJson(Payload()))));
        var id = (long)created["id"]!;
        var stale = created["rowVersion"]!.GetValue<string>();

        // 第一次 PUT 用掉该令牌，第二次再带同一令牌即为过期。
        (await client.PutAsync($"{BasePath}/{id}", JsonContent(ToJson(Payload(rowVersion: stale)))))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var conflict = await client.PutAsync($"{BasePath}/{id}", JsonContent(ToJson(Payload(rowVersion: stale))));

        var (status, code) = await ProblemAsync(conflict);
        status.Should().Be(HttpStatusCode.Conflict);
        code.Should().Be("SEM_CONCURRENCY_CONFLICT");
    }

    [Fact]
    public async Task Delete_ThenGetById_Returns404()
    {
        var admin = await CreateUserTokenAsync(RoleCodes.SysAdmin);
        var client = ClientWith(admin);
        var created = await JsonAsync(await client.PostAsync(BasePath, JsonContent(ToJson(Payload()))));
        var id = (long)created["id"]!;

        (await client.DeleteAsync($"{BasePath}/{id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var (status, code) = await ProblemAsync(await client.GetAsync($"{BasePath}/{id}"));
        status.Should().Be(HttpStatusCode.NotFound);
        code.Should().Be("SEM_NOT_FOUND");
    }

    [Fact]
    public async Task Post_DuplicateName_Returns409()
    {
        var admin = await CreateUserTokenAsync(RoleCodes.SysAdmin);
        var client = ClientWith(admin);
        var name = $"DS_{Guid.NewGuid():N}"[..20];

        (await client.PostAsync(BasePath, JsonContent(ToJson(Payload(name))))).StatusCode
            .Should().Be(HttpStatusCode.Created);
        var (status, code) = await ProblemAsync(
            await client.PostAsync(BasePath, JsonContent(ToJson(Payload(name)))));

        status.Should().Be(HttpStatusCode.Conflict);
        code.Should().Be("SEM_DUPLICATE");
    }

    [Fact]
    public async Task Post_PlaintextCredential_Returns422_PlaintextCode()
    {
        var admin = await CreateUserTokenAsync(RoleCodes.SysAdmin);
        var client = ClientWith(admin);

        var (status, code) = await ProblemAsync(await client.PostAsync(BasePath, JsonContent(ToJson(Payload(
            credentialRef: "Server=10.0.0.8;Database=biz;User Id=sa;Password=P@ssw0rd!")))));

        status.Should().Be(HttpStatusCode.UnprocessableEntity);
        code.Should().Be("SEM_PLAINTEXT_CREDENTIAL");
    }

    [Fact]
    public async Task Post_MissingField_Returns422FieldInvalid()
    {
        var admin = await CreateUserTokenAsync(RoleCodes.SysAdmin);
        var client = ClientWith(admin);

        var body = """{"name":"x","dbType":1}"""; // 缺 host/port/databaseName/credentialRef
        var (status, code) = await ProblemAsync(await client.PostAsync(BasePath, JsonContent(body)));

        status.Should().Be(HttpStatusCode.UnprocessableEntity);
        code.Should().Be("SEM_FIELD_INVALID");
    }

    [Fact]
    public async Task Post_EnumOutOfRange_Returns422FieldInvalid()
    {
        var admin = await CreateUserTokenAsync(RoleCodes.SysAdmin);
        var client = ClientWith(admin);

        var body = ToJson(new { Name = "DS_X", DbType = 99, Host = "h", Port = 1, DatabaseName = "d", CredentialRef = "REF" });
        var (status, code) = await ProblemAsync(await client.PostAsync(BasePath, JsonContent(body)));

        status.Should().Be(HttpStatusCode.UnprocessableEntity);
        code.Should().Be("SEM_FIELD_INVALID");
    }

    [Fact]
    public async Task GetById_UnknownId_Returns404()
    {
        var reader = await CreateUserTokenAsync(RoleCodes.BusinessUser);
        var (status, code) = await ProblemAsync(await ClientWith(reader).GetAsync($"{BasePath}/999999999"));

        status.Should().Be(HttpStatusCode.NotFound);
        code.Should().Be("SEM_NOT_FOUND");
    }

    [Fact]
    public async Task NoToken_Returns401()
    {
        (await Factory.CreateClient().GetAsync(BasePath)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task BusinessUser_And_DataSteward_Write_Returns403()
    {
        var business = await CreateUserTokenAsync(RoleCodes.BusinessUser);
        var (bStatus, _) = await ProblemAsync(
            await ClientWith(business).PostAsync(BasePath, JsonContent(ToJson(Payload()))));
        bStatus.Should().Be(HttpStatusCode.Forbidden);

        var steward = await CreateUserTokenAsync(RoleCodes.DataSteward);
        var (sStatus, _) = await ProblemAsync(
            await ClientWith(steward).PostAsync(BasePath, JsonContent(ToJson(Payload()))));
        sStatus.Should().Be(HttpStatusCode.Forbidden, "datasource:manage 仅 SysAdmin");
    }

    [Fact]
    public async Task BusinessUser_Read_Returns200()
    {
        var reader = await CreateUserTokenAsync(RoleCodes.BusinessUser);
        var list = await ClientWith(reader).GetAsync($"{BasePath}?page=1&pageSize=1");
        list.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Get_PageSizeOver100_Returns422PagingInvalid()
    {
        var reader = await CreateUserTokenAsync(RoleCodes.BusinessUser);
        var (status, code) = await ProblemAsync(
            await ClientWith(reader).GetAsync($"{BasePath}?page=1&pageSize=101"));

        status.Should().Be(HttpStatusCode.UnprocessableEntity);
        code.Should().Be("SEM_PAGING_INVALID");
    }
}
