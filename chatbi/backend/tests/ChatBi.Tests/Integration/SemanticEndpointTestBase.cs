using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ChatBi.Domain.Entities;
using ChatBi.Domain.Enums;
using ChatBi.Domain.Security;
using ChatBi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ChatBi.Tests.Integration;

/// <summary>语义层端点契约测试基座：造用户与 token、HTTP 收发、ProblemDetails 断言。</summary>
public abstract class SemanticEndpointTestBase
{
    protected readonly ChatBiWebApplicationFactory Factory;

    protected SemanticEndpointTestBase(ChatBiWebApplicationFactory factory)
    {
        Factory = factory;
    }

    /// <summary>造指定角色的用户并返回其 access token。</summary>
    protected async Task<string> CreateUserTokenAsync(string roleCode)
    {
        var unionId = $"ou_sem_{roleCode.ToLowerInvariant()}_{Guid.NewGuid():N}"[..40];
        long userId;
        await using (var db = Factory.CreateDbContext())
        {
            var user = new User
            {
                UnionId = unionId,
                Name = $"SEM {roleCode}",
                Status = UserStatus.Active,
                TokenVersion = 0,
                CreatedAtUtc = DateTime.UtcNow,
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            userId = user.Id;

            var roleId = await db.Roles.Where(r => r.Code == roleCode).Select(r => r.Id).SingleAsync();
            db.UserRoles.Add(new UserRole { UserId = userId, RoleId = roleId, GrantedAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        return TestTokenFactory.CreateValidAccess(
            ChatBiWebApplicationFactory.TestSigningKey, userId, 0, roleCode);
    }

    protected HttpClient ClientWith(string token)
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>读取 ProblemDetails 的（status, code）；无响应体（如授权中间件的 403）返回 code=null。</summary>
    protected static async Task<(HttpStatusCode Status, string? Code)> ProblemAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(body))
        {
            return (response.StatusCode, null);
        }

        var json = System.Text.Json.Nodes.JsonNode.Parse(body) as System.Text.Json.Nodes.JsonObject;
        return (response.StatusCode, json?["code"]?.GetValue<string>());
    }

    protected static async Task<JsonObject> JsonAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.NotNull(json);
        return json!;
    }

    protected static StringContent JsonContent(string json) => new(json, Encoding.UTF8, "application/json");

    protected static string ToJson(object payload) => System.Text.Json.JsonSerializer.Serialize(payload);

    // ---- 直接落库的铺数据助手（不走 HTTP，减少用例间耦合） ----

    protected async Task<long> SeedDataSourceAsync(string? name = null)
    {
        await using var db = Factory.CreateDbContext();
        var dataSource = new DataSource
        {
            Name = name ?? $"DS_{Guid.NewGuid():N}"[..20],
            DbType = DataSourceType.SqlServer,
            Host = "10.0.0.8",
            Port = 1433,
            DatabaseName = "biz",
            CredentialRef = "BIZ_DB_READONLY_CONNECTION",
            CreatedAtUtc = DateTime.UtcNow,
        };
        db.DataSources.Add(dataSource);
        await db.SaveChangesAsync();
        return dataSource.Id;
    }

    protected async Task<long> SeedTableAsync(long dataSourceId, string? tableName = null)
    {
        await using var db = Factory.CreateDbContext();
        var table = new SemanticTable
        {
            DataSourceId = dataSourceId,
            SchemaName = "dbo",
            TableName = tableName ?? $"T_{Guid.NewGuid():N}"[..12],
            DisplayName = "测试表",
            CreatedAtUtc = DateTime.UtcNow,
        };
        db.SemanticTables.Add(table);
        await db.SaveChangesAsync();
        return table.Id;
    }

    protected async Task<long> SeedColumnAsync(long tableId, string? columnName = null, SemanticColumnType semanticType = SemanticColumnType.Dimension)
    {
        await using var db = Factory.CreateDbContext();
        var column = new SemanticColumn
        {
            SemanticTableId = tableId,
            ColumnName = columnName ?? $"C_{Guid.NewGuid():N}"[..12],
            DisplayName = "测试列",
            DbTypeName = "nvarchar",
            SemanticType = semanticType,
            CreatedAtUtc = DateTime.UtcNow,
        };
        db.SemanticColumns.Add(column);
        await db.SaveChangesAsync();
        return column.Id;
    }
}
