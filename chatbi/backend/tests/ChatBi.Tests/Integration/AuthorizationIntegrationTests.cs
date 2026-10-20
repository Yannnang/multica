using System.Net;
using System.Net.Http.Headers;
using ChatBi.Domain.Entities;
using ChatBi.Domain.Enums;
using ChatBi.Domain.Security;
using ChatBi.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ChatBi.Tests.Integration;

/// <summary>
/// RBAC Policy 集成验证：401（无 token）与 403（token 有效但无权限）严格区分；
/// 三角色对 8 个权限码的矩阵逐项对照设计基线 3.2。
/// </summary>
[Collection(IntegrationCollection.Name)]
public class AuthorizationIntegrationTests
{
    private readonly ChatBiWebApplicationFactory _factory;

    public AuthorizationIntegrationTests(ChatBiWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private async Task<string> SeedUserWithTokenAsync(string roleCode)
    {
        var unionId = $"ou_rbac_{roleCode.ToLowerInvariant()}_{Guid.NewGuid():N}"[..40];
        long userId;
        await using (var db = _factory.CreateDbContext())
        {
            var user = new User
            {
                UnionId = unionId,
                Name = $"RBAC {roleCode}",
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

    private static readonly (string Permission, string ProbePath)[] Matrix =
    {
        (Permissions.ChatAsk, "/api/probe/chat-ask"),
        (Permissions.SqlView, "/api/probe/sql-view"),
        (Permissions.SemanticRead, "/api/probe/semantic-read"),
        (Permissions.SemanticManage, "/api/probe/semantic-manage"),
        (Permissions.DataPermManage, "/api/probe/dataperm-manage"),
        (Permissions.DataSourceManage, "/api/probe/datasource-manage"),
        (Permissions.UsersManage, "/api/probe/users-manage"),
        (Permissions.AuditRead, "/api/probe/audit-read"),
    };

    public static TheoryData<string, string, bool> MatrixData()
    {
        var data = new TheoryData<string, string, bool>();
        foreach (var role in RoleCodes.All)
        {
            foreach (var (permission, path) in Matrix)
            {
                data.Add(role, path, PermissionMatrix.HasPermission(role, permission));
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(MatrixData))]
    public async Task RoleMatrix_Enforced(string roleCode, string probePath, bool expectedAllowed)
    {
        var token = await SeedUserWithTokenAsync(roleCode);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync(probePath);

        response.StatusCode.Should().Be(
            expectedAllowed ? HttpStatusCode.OK : HttpStatusCode.Forbidden,
            $"{roleCode} on {probePath} should be {(expectedAllowed ? "allowed" : "403 (token valid, no permission)")}");
    }

    [Fact]
    public async Task NoToken_Is401_Not403_OnProtectedProbe()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/probe/chat-ask");

        // 401/403 区分：未认证 → 401；已认证无权限 → 403。
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task InvalidToken_Is401_Not403()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "broken.token.value");

        var response = await client.GetAsync("/api/probe/chat-ask");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task BusinessUser_ManageEndpoints_403()
    {
        var token = await SeedUserWithTokenAsync(RoleCodes.BusinessUser);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        (await client.GetAsync("/api/probe/semantic-manage")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.GetAsync("/api/probe/datasource-manage")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.GetAsync("/api/probe/users-manage")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.GetAsync("/api/probe/audit-read")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.GetAsync("/api/probe/chat-ask")).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}

/// <summary>/health 与数据库级约束（CHECK、过滤唯一索引、数据权限规则读取）集成验证。</summary>
[Collection(IntegrationCollection.Name)]
public class HealthAndDbIntegrationTests
{
    private readonly ChatBiWebApplicationFactory _factory;

    public HealthAndDbIntegrationTests(ChatBiWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Health_Returns200_Anonymous_WithDbCheck()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("Healthy");
    }

    [Fact]
    public async Task CheckConstraint_UserMessageWithoutQuestionText_Rejected()
    {
        await using var db = _factory.CreateDbContext();
        var session = new ChatSession
        {
            UserId = (await db.Users.Select(u => (long?)u.Id).FirstOrDefaultAsync()) ?? await SeedUserAsync(db),
            Title = "t",
            LastMessageAtUtc = DateTime.UtcNow,
            CreatedAtUtc = DateTime.UtcNow,
        };
        db.ChatSessions.Add(session);
        await db.SaveChangesAsync();

        db.ChatMessages.Add(new ChatMessage
        {
            SessionId = session.Id,
            Role = ChatRole.User,
            QuestionText = null, // CHECK 应拒绝
            CreatedAtUtc = DateTime.UtcNow,
        });

        var act = async () => await db.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task FilteredUniqueIndex_SoftDeletedDuplicates_Allowed_ActiveDuplicates_Rejected()
    {
        await using var db = _factory.CreateDbContext();
        var dataSource = new DataSource
        {
            Name = $"DS_{Guid.NewGuid():N}"[..20],
            DbType = DataSourceType.SqlServer,
            Host = "localhost",
            Port = 1433,
            DatabaseName = "biz",
            CredentialRef = "BIZ_DB_READONLY_CONNECTION",
            CreatedAtUtc = DateTime.UtcNow,
        };
        db.DataSources.Add(dataSource);
        await db.SaveChangesAsync();

        // 第一条启用 + 第二条软删同名：允许（过滤唯一索引 WHERE IsDeleted=0）。
        db.SemanticTables.Add(new SemanticTable
        {
            DataSourceId = dataSource.Id, SchemaName = "dbo", TableName = "Orders",
            DisplayName = "订单", CreatedAtUtc = DateTime.UtcNow,
        });
        db.SemanticTables.Add(new SemanticTable
        {
            DataSourceId = dataSource.Id, SchemaName = "dbo", TableName = "Orders",
            DisplayName = "订单(已删)", IsDeleted = true, CreatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        // 第二条启用同名：唯一索引拒绝。
        db.SemanticTables.Add(new SemanticTable
        {
            DataSourceId = dataSource.Id, SchemaName = "dbo", TableName = "Orders",
            DisplayName = "订单(重复)", CreatedAtUtc = DateTime.UtcNow,
        });
        var act = async () => await db.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task DataPermissionRules_RoundTrip_AndEvaluatorResolution()
    {
        long userId;
        await using (var db = _factory.CreateDbContext())
        {
            userId = await SeedUserAsync(db);
            var roleId = await db.Roles.Where(r => r.Code == RoleCodes.BusinessUser).Select(r => r.Id).SingleAsync();
            db.UserRoles.Add(new UserRole { UserId = userId, RoleId = roleId, GrantedAtUtc = DateTime.UtcNow });
            db.DataPermissionRules.Add(new DataPermissionRule
            {
                SubjectType = PermissionSubjectType.Role,
                SubjectId = roleId,
                SemanticTableId = null,
                ScopeType = DataScopeType.DepartmentAndBelow,
                DepartmentPathPrefix = "/总部/技术部",
                Enabled = true,
                CreatedAtUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        using var scope = _factory.Services.CreateScope();
        var evaluator = scope.ServiceProvider.GetRequiredService<Application.DataPermissions.IDataPermissionEvaluator>();
        var decision = await evaluator.EvaluateAsync(userId, new[] { RoleCodes.BusinessUser }, semanticTableId: null);

        decision.ScopeType.Should().Be(DataScopeType.DepartmentAndBelow);
        decision.DepartmentPathPrefix.Should().Be("/总部/技术部");
    }

    private static async Task<long> SeedUserAsync(ChatBiDbContext db)
    {
        var user = new User
        {
            UnionId = $"ou_db_{Guid.NewGuid():N}"[..24],
            Name = "DB Test",
            Status = UserStatus.Active,
            CreatedAtUtc = DateTime.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }
}
