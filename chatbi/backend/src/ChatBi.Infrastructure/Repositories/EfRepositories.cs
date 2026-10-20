using ChatBi.Application.Auth;
using ChatBi.Application.DataPermissions;
using ChatBi.Domain.Entities;
using ChatBi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ChatBi.Infrastructure.Repositories;

/// <summary>用户账号仓储（EF Core 实现；软删过滤器已在全局配置）。</summary>
public class EfUserAccountRepository : IUserAccountRepository
{
    private readonly ChatBiDbContext _db;

    public EfUserAccountRepository(ChatBiDbContext db)
    {
        _db = db;
    }

    public Task<User?> FindByUnionIdAsync(string unionId, CancellationToken cancellationToken = default) =>
        _db.Users.FirstOrDefaultAsync(x => x.UnionId == unionId, cancellationToken);

    public Task<User?> FindByIdAsync(long userId, CancellationToken cancellationToken = default) =>
        _db.Users.FirstOrDefaultAsync(x => x.Id == userId, cancellationToken);

    public async Task AddAsync(User user, CancellationToken cancellationToken = default) =>
        await _db.Users.AddAsync(user, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<string>> GetRoleCodesAsync(long userId, CancellationToken cancellationToken = default) =>
        await (from ur in _db.UserRoles
               join r in _db.Roles on ur.RoleId equals r.Id
               where ur.UserId == userId
               orderby r.Code
               select r.Code).ToListAsync(cancellationToken).ConfigureAwait(false);

    public async Task GrantRoleAsync(long userId, string roleCode, CancellationToken cancellationToken = default)
    {
        var role = await _db.Roles.FirstOrDefaultAsync(r => r.Code == roleCode, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Role '{roleCode}' is not seeded.");

        var alreadyGranted = await _db.UserRoles.AnyAsync(x => x.UserId == userId && x.RoleId == role.Id, cancellationToken).ConfigureAwait(false);
        if (alreadyGranted)
        {
            return; // 幂等
        }

        _db.UserRoles.Add(new UserRole
        {
            UserId = userId,
            RoleId = role.Id,
            GrantedAtUtc = DateTime.UtcNow,
        });
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        _db.SaveChangesAsync(cancellationToken: cancellationToken);
}

/// <summary>DataPermissionRules 读取（EF 实现；显式物化 roleIds，避免客户端评估）。</summary>
public class EfDataPermissionRuleReader : IDataPermissionRuleReader
{
    private readonly ChatBiDbContext _db;

    public EfDataPermissionRuleReader(ChatBiDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<DataPermissionRule>> GetEnabledRulesAsync(
        long userId,
        IReadOnlyList<string> roleCodes,
        CancellationToken cancellationToken = default)
    {
        var roleIds = roleCodes.Count == 0
            ? new List<long>()
            : await _db.Roles.Where(r => roleCodes.Contains(r.Code)).Select(r => r.Id).ToListAsync(cancellationToken).ConfigureAwait(false);

        return await _db.DataPermissionRules
            .AsNoTracking()
            .Where(x => x.Enabled
                && ((x.SubjectType == Domain.Enums.PermissionSubjectType.User && x.SubjectId == userId)
                    || (x.SubjectType == Domain.Enums.PermissionSubjectType.Role && roleIds.Contains(x.SubjectId))))
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
