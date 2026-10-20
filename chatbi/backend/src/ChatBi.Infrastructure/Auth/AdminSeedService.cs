using ChatBi.Application.Configuration;
using ChatBi.Domain.Security;
using ChatBi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ChatBi.Infrastructure.Auth;

/// <summary>
/// 首个 SysAdmin 引导（避免管理员死锁）：配置 &lt;ADMIN_UNION_ID&gt; 后，
/// 该 union_id 用户存在即幂等授予 SysAdmin；启动时对既有用户兜底，登录建档路径在 AuthService 内处理。
/// </summary>
public class AdminSeedService
{
    private readonly ChatBiDbContext _db;
    private readonly FeishuOptions _options;
    private readonly ILogger<AdminSeedService> _logger;

    public AdminSeedService(ChatBiDbContext db, IOptions<FeishuOptions> options, ILogger<AdminSeedService> logger)
    {
        _db = db;
        _options = options.Value;
        _logger = logger;
    }

    public async Task EnsureAdminAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.AdminUnionId))
        {
            return;
        }

        var user = await _db.Users.FirstOrDefaultAsync(x => x.UnionId == _options.AdminUnionId, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            _logger.LogInformation("AdminUnionId configured but user not provisioned yet; SysAdmin will be granted on first login.");
            return;
        }

        var adminRoleId = await _db.Roles.Where(r => r.Code == RoleCodes.SysAdmin).Select(r => r.Id).SingleAsync(cancellationToken).ConfigureAwait(false);
        var alreadyGranted = await _db.UserRoles.AnyAsync(x => x.UserId == user.Id && x.RoleId == adminRoleId, cancellationToken).ConfigureAwait(false);
        if (alreadyGranted)
        {
            return;
        }

        _db.UserRoles.Add(new Domain.Entities.UserRole
        {
            UserId = user.Id,
            RoleId = adminRoleId,
            GrantedAtUtc = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Granted SysAdmin to configured admin user {UserId}.", user.Id);
    }
}
