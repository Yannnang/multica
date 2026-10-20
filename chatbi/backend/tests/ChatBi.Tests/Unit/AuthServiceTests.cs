using ChatBi.Application.Auth;
using ChatBi.Application.Common;
using ChatBi.Application.Configuration;
using ChatBi.Domain.Entities;
using ChatBi.Domain.Enums;
using ChatBi.Domain.Security;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace ChatBi.Tests.Unit;

public class AuthServiceTests
{
    private const string AdminUnion = "ou_admin_seed";

    private readonly IFeishuAuthClient _feishu = Substitute.For<IFeishuAuthClient>();
    private readonly IAuthStateStore _stateStore = Substitute.For<IAuthStateStore>();
    private readonly ITokenService _tokenService = Substitute.For<ITokenService>();
    private readonly IUserAccountRepository _users = Substitute.For<IUserAccountRepository>();

    private AuthService CreateService() => new(
        _feishu,
        _stateStore,
        _tokenService,
        _users,
        Options.Create(new FeishuOptions { AdminUnionId = AdminUnion }),
        Options.Create(new AuthRedirectOptions()),
        TimeProvider.System,
        NullLogger<AuthService>.Instance);

    private void ArrangeState(string? redirect) =>
        _stateStore.ConsumeStateAsync("good-state", Arg.Any<CancellationToken>()).Returns(redirect ?? "/");

    private void ArrangeFeishuSuccess(string unionId = "ou_new")
    {
        _feishu.ExchangeCodeForUserAccessTokenAsync("good-code", Arg.Any<CancellationToken>())
            .Returns("uat");
        _feishu.GetUserInfoAsync("uat", Arg.Any<CancellationToken>())
            .Returns(new FeishuUserInfo(unionId, $"open-{unionId}", "张三", "z@corp.com", null, null));
    }

    private void ArrangeTokens()
    {
        _tokenService.CreateAccessToken(Arg.Any<User>(), Arg.Any<IReadOnlyList<string>>())
            .Returns(("access-token", 3600));
        _tokenService.CreateRefreshToken(Arg.Any<User>()).Returns(("refresh-token", 604800));
    }

    [Fact]
    public async Task Callback_MissingState_Throws_StateInvalid()
    {
        var act = async () => await CreateService().CompleteLoginAsync("good-code", null);
        await act.Should().ThrowAsync<AuthStateInvalidException>().Where(e => e.HttpStatus == 401);
    }

    [Fact]
    public async Task Callback_UnknownOrReusedState_Throws_StateInvalid()
    {
        _stateStore.ConsumeStateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var act = async () => await CreateService().CompleteLoginAsync("good-code", "bad-state");
        await act.Should().ThrowAsync<AuthStateInvalidException>();
    }

    [Fact]
    public async Task Callback_MissingCode_Throws_CodeInvalid()
    {
        ArrangeState("/");
        var act = async () => await CreateService().CompleteLoginAsync(null, "good-state");
        await act.Should().ThrowAsync<AuthCodeInvalidException>().Where(e => e.HttpStatus == 401);
    }

    [Fact]
    public async Task Callback_FeishuRejectsCode_Propagates_CodeInvalid()
    {
        ArrangeState("/");
        _feishu.ExchangeCodeForUserAccessTokenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new AuthCodeInvalidException());

        var act = async () => await CreateService().CompleteLoginAsync("bad-code", "good-state");
        await act.Should().ThrowAsync<AuthCodeInvalidException>();
    }

    [Fact]
    public async Task Callback_UpstreamUnavailable_Throws_502()
    {
        ArrangeState("/");
        _feishu.ExchangeCodeForUserAccessTokenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("timeout"));

        var act = async () => await CreateService().CompleteLoginAsync("any", "good-state");
        await act.Should().ThrowAsync<AuthUpstreamUnavailableException>().Where(e => e.HttpStatus == 502);
    }

    [Fact]
    public async Task Callback_UserInfoWithoutUnionId_Throws_UpstreamUnavailable()
    {
        ArrangeState("/");
        _feishu.ExchangeCodeForUserAccessTokenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("uat");
        _feishu.GetUserInfoAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new FeishuUserInfo(string.Empty, null, "无 union", null, null, null));

        var act = async () => await CreateService().CompleteLoginAsync("good-code", "good-state");
        await act.Should().ThrowAsync<AuthUpstreamUnavailableException>();
    }

    [Fact]
    public async Task Callback_FirstLogin_ProvisionsUserWithBusinessUser_AndIssuesTokens()
    {
        ArrangeState("/chat");
        ArrangeFeishuSuccess("ou_first");
        ArrangeTokens();
        _users.FindByUnionIdAsync("ou_first", Arg.Any<CancellationToken>()).Returns((User?)null);
        _users.GetRoleCodesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new[] { RoleCodes.BusinessUser });
        User? added = null;
        await _users.AddAsync(Arg.Do<User>(u => { u.Id = 100; added = u; }), Arg.Any<CancellationToken>());

        var result = await CreateService().CompleteLoginAsync("good-code", "good-state");

        added.Should().NotBeNull();
        added!.UnionId.Should().Be("ou_first");
        added.Status.Should().Be(UserStatus.Active);
        added.TokenVersion.Should().Be(0);
        await _users.Received(1).GrantRoleAsync(100, RoleCodes.BusinessUser, Arg.Any<CancellationToken>());
        await _users.Received(0).GrantRoleAsync(100, RoleCodes.SysAdmin, Arg.Any<CancellationToken>());
        result.Tokens.AccessToken.Should().Be("access-token");
        result.RedirectPath.Should().Be("/chat");
        result.Roles.Should().Equal(RoleCodes.BusinessUser);
    }

    [Fact]
    public async Task Callback_AdminUnion_FirstLogin_AlsoGrantsSysAdmin()
    {
        ArrangeState("/");
        ArrangeFeishuSuccess(AdminUnion);
        ArrangeTokens();
        _users.FindByUnionIdAsync(AdminUnion, Arg.Any<CancellationToken>()).Returns((User?)null);
        _users.GetRoleCodesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new[] { RoleCodes.BusinessUser, RoleCodes.SysAdmin });
        await _users.AddAsync(Arg.Do<User>(u => u.Id = 1), Arg.Any<CancellationToken>());

        await CreateService().CompleteLoginAsync("good-code", "good-state");

        await _users.Received(1).GrantRoleAsync(1, RoleCodes.BusinessUser, Arg.Any<CancellationToken>());
        await _users.Received(1).GrantRoleAsync(1, RoleCodes.SysAdmin, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Callback_DisabledUser_Throws_403()
    {
        ArrangeState("/");
        ArrangeFeishuSuccess("ou_disabled");
        _users.FindByUnionIdAsync("ou_disabled", Arg.Any<CancellationToken>())
            .Returns(new User { Id = 9, UnionId = "ou_disabled", Name = "离职", Status = UserStatus.Disabled });

        var act = async () => await CreateService().CompleteLoginAsync("good-code", "good-state");
        await act.Should().ThrowAsync<AuthUserDisabledException>().Where(e => e.HttpStatus == 403);
    }

    [Fact]
    public async Task Callback_SecondLogin_ReusesUser_RefreshesProfile_NoDuplicate()
    {
        ArrangeState("/");
        ArrangeFeishuSuccess("ou_again");
        ArrangeTokens();
        var existing = new User { Id = 5, UnionId = "ou_again", Name = "旧名", Status = UserStatus.Active };
        _users.FindByUnionIdAsync("ou_again", Arg.Any<CancellationToken>()).Returns(existing);
        _users.GetRoleCodesAsync(5, Arg.Any<CancellationToken>()).Returns(new[] { RoleCodes.BusinessUser });

        var result = await CreateService().CompleteLoginAsync("good-code", "good-state");

        await _users.DidNotReceive().AddAsync(Arg.Any<User>(), Arg.Any<CancellationToken>());
        existing.Name.Should().Be("张三", "登录时刷新显示名");
        existing.LastLoginAtUtc.Should().NotBeNull();
        result.UserId.Should().Be(5);
    }

    [Fact]
    public async Task Refresh_InvalidToken_Throws()
    {
        _tokenService.ValidateRefreshToken("garbage").Returns((RefreshTokenInfo?)null);
        var act = async () => await CreateService().RefreshAsync("garbage");
        await act.Should().ThrowAsync<AuthRefreshInvalidException>().Where(e => e.HttpStatus == 401);
    }

    [Fact]
    public async Task Refresh_DisabledUser_Throws_403()
    {
        _tokenService.ValidateRefreshToken("r").Returns(new RefreshTokenInfo(3, 0));
        _users.FindByIdAsync(3, Arg.Any<CancellationToken>())
            .Returns(new User { Id = 3, Status = UserStatus.Disabled });

        var act = async () => await CreateService().RefreshAsync("r");
        await act.Should().ThrowAsync<AuthUserDisabledException>();
    }

    [Fact]
    public async Task Refresh_StaleTokenVersion_Throws()
    {
        _tokenService.ValidateRefreshToken("r").Returns(new RefreshTokenInfo(3, 1));
        _users.FindByIdAsync(3, Arg.Any<CancellationToken>())
            .Returns(new User { Id = 3, Status = UserStatus.Active, TokenVersion = 2 });

        var act = async () => await CreateService().RefreshAsync("r");
        await act.Should().ThrowAsync<AuthRefreshInvalidException>();
    }

    [Fact]
    public async Task Refresh_Valid_SlidingReissue()
    {
        _tokenService.ValidateRefreshToken("r").Returns(new RefreshTokenInfo(3, 2));
        var user = new User { Id = 3, Status = UserStatus.Active, TokenVersion = 2 };
        _users.FindByIdAsync(3, Arg.Any<CancellationToken>()).Returns(user);
        _users.GetRoleCodesAsync(3, Arg.Any<CancellationToken>()).Returns(new[] { RoleCodes.BusinessUser });
        ArrangeTokens();

        var pair = await CreateService().RefreshAsync("r");

        pair.AccessToken.Should().Be("access-token");
        pair.RefreshToken.Should().Be("refresh-token");
    }

    [Fact]
    public async Task Logout_BumpsTokenVersion()
    {
        var user = new User { Id = 4, Status = UserStatus.Active, TokenVersion = 1 };
        _users.FindByIdAsync(4, Arg.Any<CancellationToken>()).Returns(user);

        await CreateService().LogoutAsync(4);

        user.TokenVersion.Should().Be(2);
        await _users.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BeginLogin_CreatesStateAndBuildsUrl()
    {
        _stateStore.CreateStateAsync("/chat", Arg.Any<CancellationToken>()).Returns("st-1");
        _feishu.BuildAuthorizeUrl("st-1", "/chat").Returns("https://feishu/authorize?state=st-1");

        var url = await CreateService().BeginLoginAsync("/chat");

        url.Should().Be("https://feishu/authorize?state=st-1");
    }

    [Theory]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("/chat", "/chat")]
    [InlineData("/chat/x?a=1", "/chat/x?a=1")]
    [InlineData("//evil.com", "/")]
    [InlineData("https://evil.com", "/")]
    [InlineData("/\\evil.com", "/")]
    [InlineData("javascript:alert(1)", "/")]
    public void SanitizeRedirect_BlocksOpenRedirect(string? input, string expected) =>
        CreateService().SanitizeRedirect(input).Should().Be(expected);
}
