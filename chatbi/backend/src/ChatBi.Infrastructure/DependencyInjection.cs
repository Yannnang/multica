using ChatBi.Application.Auth;
using ChatBi.Application.Configuration;
using ChatBi.Application.DataPermissions;
using ChatBi.Infrastructure.Auth;
using ChatBi.Infrastructure.Persistence;
using ChatBi.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Polly;
using Polly.Extensions.Http;

namespace ChatBi.Infrastructure;

/// <summary>Infrastructure 组合根扩展：DbContext、SSO 客户端（真实/mock 可切换）、JWT、数据权限服务。</summary>
public static class DependencyInjection
{
    public const string PlatformDbConnectionStringName = "PlatformDb";

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(PlatformDbConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"Connection string '{PlatformDbConnectionStringName}' is missing (env: ConnectionStrings__{PlatformDbConnectionStringName} / <PLATFORM_DB_CONNECTION>).");
        }

        services.AddDbContext<ChatBiDbContext>(options => options.UseSqlServer(connectionString));

        services.AddSingleton(TimeProvider.System);
        services.AddMemoryCache();

        // JWT：密钥仅来自环境变量（PostConfigure 覆盖任何配置文件值并强校验）。
        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.SectionName));
        services.PostConfigure<JwtOptions>(options =>
        {
            options.SigningKey = Environment.GetEnvironmentVariable(JwtOptions.SigningKeyEnvironmentVariable) ?? string.Empty;
        });
        services.AddSingleton<JwtTokenService>();
        services.AddSingleton<ITokenService>(sp => sp.GetRequiredService<JwtTokenService>());

        services.Configure<FeishuOptions>(configuration.GetSection(FeishuOptions.SectionName));
        services.Configure<AuthRedirectOptions>(configuration.GetSection(AuthRedirectOptions.SectionName));

        // 飞书客户端：配置开关切换 mock（契约测试与 S0 未完成时开发）/ 真实（IHttpClientFactory + Polly：5s 超时 + 1 次重试 + 熔断）。
        var feishuSection = configuration.GetSection(FeishuOptions.SectionName);
        if (feishuSection.GetValue<bool>("UseMock"))
        {
            services.AddSingleton<MockFeishuAuthClient>();
            services.AddSingleton<IFeishuAuthClient>(sp => sp.GetRequiredService<MockFeishuAuthClient>());
        }
        else
        {
            services.AddHttpClient<IFeishuAuthClient, FeishuAuthClient>(client =>
                {
                    var baseUrl = feishuSection.GetValue<string>("OpenApiBaseUrl") ?? "https://open.feishu.cn";
                    client.BaseAddress = new Uri(baseUrl);
                    client.Timeout = TimeSpan.FromSeconds(5);
                })
                .AddPolicyHandler(HttpPolicyExtensions.HandleTransientHttpError()
                    .Or<TaskCanceledException>()
                    .WaitAndRetryAsync(1, attempt => TimeSpan.FromMilliseconds(200 * attempt)))
                .AddPolicyHandler(HttpPolicyExtensions.HandleTransientHttpError()
                    .CircuitBreakerAsync(5, TimeSpan.FromSeconds(30)));
        }

        services.AddSingleton<IAuthStateStore, MemoryAuthStateStore>();

        services.AddScoped<IUserAccountRepository, EfUserAccountRepository>();
        services.AddScoped<IDataPermissionRuleReader, EfDataPermissionRuleReader>();
        services.AddScoped<IDataPermissionEvaluator, DataPermissionEvaluator>();
        services.AddScoped<AuthService>();
        services.AddScoped<AdminSeedService>();

        return services;
    }
}
