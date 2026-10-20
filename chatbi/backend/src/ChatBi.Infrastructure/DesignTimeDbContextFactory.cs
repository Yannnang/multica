using ChatBi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ChatBi.Infrastructure;

/// <summary>
/// EF Core 设计时工厂：让 dotnet ef（migrations add / script / database update）无需启动完整 API 宿主
/// （不触发 JWT 密钥等运行时强校验）。
/// 连接串优先级：--connection 参数 &gt; 环境变量 PLATFORM_DB_CONNECTION &gt; LocalDB 设计时默认。
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ChatBiDbContext>
{
    public ChatBiDbContext CreateDbContext(string[] args)
    {
        var connectionString = GetConnectionString(args);
        var options = new DbContextOptionsBuilder<ChatBiDbContext>()
            .UseSqlServer(connectionString)
            .Options;

        return new ChatBiDbContext(options);
    }

    private static string GetConnectionString(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--connection" || args[i] == "-c")
            {
                return args[i + 1];
            }
        }

        var fromEnvironment = Environment.GetEnvironmentVariable("PLATFORM_DB_CONNECTION");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment;
        }

        return @"Server=(localdb)\MSSQLLocalDB;Database=ChatBi_DesignTime;Trusted_Connection=True;MultipleActiveResultSets=true";
    }
}
