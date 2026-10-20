using ChatBi.Api.Authorization;
using ChatBi.Domain.Security;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using System.Security.Claims;
using Xunit;

namespace ChatBi.Tests.Unit;

public class PermissionMatrixTests
{
    [Theory]
    [InlineData(RoleCodes.BusinessUser, Permissions.ChatAsk, true)]
    [InlineData(RoleCodes.BusinessUser, Permissions.SqlView, true)]
    [InlineData(RoleCodes.BusinessUser, Permissions.SemanticRead, true)]
    [InlineData(RoleCodes.BusinessUser, Permissions.SemanticManage, false)]
    [InlineData(RoleCodes.BusinessUser, Permissions.DataSourceManage, false)]
    [InlineData(RoleCodes.BusinessUser, Permissions.UsersManage, false)]
    [InlineData(RoleCodes.BusinessUser, Permissions.AuditRead, false)]
    [InlineData(RoleCodes.BusinessUser, Permissions.DataPermManage, false)]
    [InlineData(RoleCodes.DataSteward, Permissions.SemanticManage, true)]
    [InlineData(RoleCodes.DataSteward, Permissions.DataPermManage, true)]
    [InlineData(RoleCodes.DataSteward, Permissions.ChatAsk, true)]
    [InlineData(RoleCodes.DataSteward, Permissions.DataSourceManage, false)]
    [InlineData(RoleCodes.DataSteward, Permissions.UsersManage, false)]
    [InlineData(RoleCodes.DataSteward, Permissions.AuditRead, false)]
    [InlineData(RoleCodes.SysAdmin, Permissions.DataSourceManage, true)]
    [InlineData(RoleCodes.SysAdmin, Permissions.UsersManage, true)]
    [InlineData(RoleCodes.SysAdmin, Permissions.AuditRead, true)]
    [InlineData(RoleCodes.SysAdmin, Permissions.SemanticManage, true)]
    public void Matrix_MatchesDesignBaseline(string role, string permission, bool expected) =>
        PermissionMatrix.HasPermission(role, permission).Should().Be(expected);

    [Fact]
    public void UnknownRole_IsDenied_DefaultDeny()
    {
        PermissionMatrix.HasPermission("Ghost", Permissions.ChatAsk).Should().BeFalse();
        PermissionMatrix.ForRole("Ghost").Should().BeEmpty();
    }

    [Fact]
    public void AllPairs_MatchesSeedShape()
    {
        var pairs = PermissionMatrix.AllPairs().ToList();
        pairs.Should().HaveCount(3 + 5 + 8); // BusinessUser 3 + DataSteward 5 + SysAdmin 8
        pairs.Select(p => p.Permission).Should().OnlyContain(p => Permissions.All.Contains(p));
    }
}

public class PermissionAuthorizationHandlerTests
{
    private static AuthorizationHandlerContext CreateContext(IEnumerable<string> roles, OperationAuthorizationRequirement requirement)
    {
        var identity = new ClaimsIdentity(
            roles.Select(r => new Claim(ChatBiClaimTypes.Role, r)),
            authenticationType: "Test");
        identity.AddClaim(new Claim(ChatBiClaimTypes.Subject, "42"));
        var principal = new ClaimsPrincipal(identity);
        return new AuthorizationHandlerContext(
            new[] { (IAuthorizationRequirement)requirement },
            principal,
            resource: null);
    }

    [Fact]
    public async Task BusinessUser_ChatAsk_Succeeds()
    {
        var handler = new PermissionAuthorizationHandler();
        var context = CreateContext(new[] { RoleCodes.BusinessUser }, new OperationAuthorizationRequirement(Permissions.ChatAsk));

        await handler.HandleAsync(context);

        context.HasSucceeded.Should().BeTrue();
    }

    [Fact]
    public async Task BusinessUser_SemanticManage_Fails()
    {
        var handler = new PermissionAuthorizationHandler();
        var context = CreateContext(new[] { RoleCodes.BusinessUser }, new OperationAuthorizationRequirement(Permissions.SemanticManage));

        await handler.HandleAsync(context);

        context.HasSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task SysAdmin_AllPermissions_Succeed()
    {
        var handler = new PermissionAuthorizationHandler();
        foreach (var permission in Permissions.All)
        {
            var context = CreateContext(new[] { RoleCodes.SysAdmin }, new OperationAuthorizationRequirement(permission));
            await handler.HandleAsync(context);
            context.HasSucceeded.Should().BeTrue("SysAdmin must hold {Permission}", permission);
        }
    }

    [Fact]
    public async Task MultiRole_UnionOfPermissions()
    {
        var handler = new PermissionAuthorizationHandler();
        var context = CreateContext(
            new[] { RoleCodes.BusinessUser, RoleCodes.DataSteward },
            new OperationAuthorizationRequirement(Permissions.DataPermManage));

        await handler.HandleAsync(context);

        context.HasSucceeded.Should().BeTrue("DataSteward grants dataperm:manage; multi-role = union");
    }

    [Fact]
    public async Task NoRoles_Denied()
    {
        var handler = new PermissionAuthorizationHandler();
        var context = CreateContext(Array.Empty<string>(), new OperationAuthorizationRequirement(Permissions.ChatAsk));

        await handler.HandleAsync(context);

        context.HasSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task UnknownRole_Denied()
    {
        var handler = new PermissionAuthorizationHandler();
        var context = CreateContext(new[] { "SuperUser" }, new OperationAuthorizationRequirement(Permissions.UsersManage));

        await handler.HandleAsync(context);

        context.HasSucceeded.Should().BeFalse();
    }
}
