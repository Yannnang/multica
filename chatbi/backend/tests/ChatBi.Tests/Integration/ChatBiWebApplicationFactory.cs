using System.Collections.Concurrent;
using ChatBi.Domain.Security;
using ChatBi.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ChatBi.Tests.Integration;

/// <summary>
/// 授权矩阵探针端点（仅测试程序集，经 AddApplicationPart 挂进测试宿主）：
/// 每个权限码一个 GET 端点，用于 401/403 与角色矩阵的集成验证。
/// </summary>
[ApiController]
[Route("api/probe")]
public class ProbeController : ControllerBase
{
    [HttpGet("chat-ask")]
    [Authorize(Policy = Permissions.ChatAsk)]
    public IActionResult ChatAsk() => Ok(new { ok = nameof(Permissions.ChatAsk) });

    [HttpGet("sql-view")]
    [Authorize(Policy = Permissions.SqlView)]
    public IActionResult SqlView() => Ok(new { ok = nameof(Permissions.SqlView) });

    [HttpGet("semantic-read")]
    [Authorize(Policy = Permissions.SemanticRead)]
    public IActionResult SemanticRead() => Ok(new { ok = nameof(Permissions.SemanticRead) });

    [HttpGet("semantic-manage")]
    [Authorize(Policy = Permissions.SemanticManage)]
    public IActionResult SemanticManage() => Ok(new { ok = nameof(Permissions.SemanticManage) });

    [HttpGet("dataperm-manage")]
    [Authorize(Policy = Permissions.DataPermManage)]
    public IActionResult DataPermManage() => Ok(new { ok = nameof(Permissions.DataPermManage) });

    [HttpGet("datasource-manage")]
    [Authorize(Policy = Permissions.DataSourceManage)]
    public IActionResult DataSourceManage() => Ok(new { ok = nameof(Permissions.DataSourceManage) });

    [HttpGet("users-manage")]
    [Authorize(Policy = Permissions.UsersManage)]
    public IActionResult UsersManage() => Ok(new { ok = nameof(Permissions.UsersManage) });

    [HttpGet("audit-read")]
    [Authorize(Policy = Permissions.AuditRead)]
    public IActionResult AuditRead() => Ok(new { ok = nameof(Permissions.AuditRead) });
}

/// <summary>
/// 集成测试宿主（WebApplicationFactory + 真实 SQL Server LocalDB + 飞书 mock）。
/// 说明：运行时环境无 Docker，Testcontainers 方案不可用；LocalDB 为真 SQL Server，
/// rowversion / 过滤唯一索引 / CHECK 语义与生产一致（偏离已在 PR 说明）。
/// </summary>
public class ChatBiWebApplicationFactory : WebApplicationFactory<Program>
{
    /// <summary>仅测试进程内使用的签名密钥（非真实密钥；生产密钥只来自环境变量）。</summary>
    public const string TestSigningKey = "integration-test-only-signing-key-0123456789";

    /// <summary>与 Feishu:AdminUnionId 配置一致：该 union 首登自动获得 SysAdmin。</summary>
    public const string AdminUnionId = "ou_it_admin";

    private readonly string _databaseName = $"ChatBi_IT_{Guid.NewGuid():N}";

    static ChatBiWebApplicationFactory()
    {
        // EF 的 SQL 计数走全局 DiagnosticListener（AddDbContext 二次注册会被 TryAdd 吞掉，不可用）。
        System.Diagnostics.DiagnosticListener.AllListeners.Subscribe(SqlCommandCounter.ListenerObserver);
    }

    private static readonly string[] ManagedEnvironmentVariables =
    {
        "JWT_SIGNING_KEY",
        "ConnectionStrings__PlatformDb",
        "Feishu__UseMock",
        "Feishu__AppId",
        "Feishu__AdminUnionId",
        "Database__ApplyMigrationsOnStart",
        "Serilog__MinimumLevel__Default",
    };

    public string ConnectionString { get; }

    public ChatBiWebApplicationFactory()
    {
        ConnectionString =
            $@"Server=(localdb)\MSSQLLocalDB;Database={_databaseName};Trusted_Connection=True;MultipleActiveResultSets=true;Encrypt=False";

        // WebApplicationBuilder 的默认配置包含环境变量，且在 Program 服务注册（急切读取连接串）之前生效；
        // factory 的 ConfigureAppConfiguration 则晚于 Program 顶层代码，不能覆盖急切读取的值——故走环境变量。
        Environment.SetEnvironmentVariable("JWT_SIGNING_KEY", TestSigningKey);
        Environment.SetEnvironmentVariable("ConnectionStrings__PlatformDb", ConnectionString);
        Environment.SetEnvironmentVariable("Feishu__UseMock", "true");
        Environment.SetEnvironmentVariable("Feishu__AppId", "cli_test_app");
        Environment.SetEnvironmentVariable("Feishu__AdminUnionId", AdminUnionId);
        Environment.SetEnvironmentVariable("Database__ApplyMigrationsOnStart", "false");
        Environment.SetEnvironmentVariable("Serilog__MinimumLevel__Default", "Warning");
    }

    /// <summary>不跟随 302 的客户端（login/callback 重定向断言用）。</summary>
    public HttpClient CreateNoRedirectClient() =>
        CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    /// <summary>
    /// 测试内 SQL 计数（N+1 断言）：经 EF DiagnosticListener 统计 CommandExecuted 事件。
    /// 先 <see cref="ResetSqlCommandCount"/>，执行请求，再读取 <see cref="SqlCommandCount"/>。
    /// </summary>
    public long SqlCommandCount => SqlCommandCounter.ListenerObserver.Count;

    public void ResetSqlCommandCount() => SqlCommandCounter.ListenerObserver.Reset();

    /// <summary>进程内日志收集器（脱敏抽查）：注册为 ILoggerProvider，捕获全部 ILogger 输出。</summary>
    public CollectingLogSink LogSink { get; } = new();

    /// <summary>直接操作平台库做断言/铺数据（独立连接，不共享请求作用域）。</summary>
    public ChatBiDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ChatBiDbContext>()
            .UseSqlServer(ConnectionString)
            .Options;
        return new ChatBiDbContext(options);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services =>
        {
            // 把测试程序集里的 ProbeController 挂进 MVC。
            services.AddControllers().AddApplicationPart(typeof(ProbeController).Assembly);

            // 进程内日志收集（脱敏抽查）。
            services.AddSingleton<ILoggerProvider>(LogSink);
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatBiDbContext>();
        db.Database.Migrate();

        return host;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            foreach (var name in ManagedEnvironmentVariables)
            {
                Environment.SetEnvironmentVariable(name, null);
            }

            TryDropDatabase();
        }
    }

    private void TryDropDatabase()
    {
        try
        {
            using var masterConnection = new SqlConnection(@"Server=(localdb)\MSSQLLocalDB;Database=master;Trusted_Connection=True;Encrypt=False");
            masterConnection.Open();
            using var command = masterConnection.CreateCommand();
            command.CommandText =
                $"IF DB_ID('{_databaseName}') IS NOT NULL BEGIN " +
                $"ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; " +
                $"DROP DATABASE [{_databaseName}]; END";
            command.ExecuteNonQuery();
        }
        catch (SqlException)
        {
            // 测试清理失败不致命；LocalDB 实例回收时兜底。
        }
    }
}

/// <summary>
/// EF SQL 命令计数器：订阅 DiagnosticListener（EF 单一监听器名 "Microsoft.EntityFrameworkCore"），
/// 统计 CommandExecuted 事件。静态单例挂接 AllListeners，工厂实例只读自己的计数。
/// </summary>
public sealed class SqlCommandCounter : IObserver<System.Diagnostics.DiagnosticListener>, IObserver<KeyValuePair<string, object?>>
{
    public static readonly SqlCommandCounter ListenerObserver = new();

    private long _count;

    public long Count => Interlocked.Read(ref _count);

    public void Reset() => Interlocked.Exchange(ref _count, 0);

    public void OnNext(System.Diagnostics.DiagnosticListener listener)
    {
        if (listener.Name == "Microsoft.EntityFrameworkCore")
        {
            listener.Subscribe(this);
        }
    }

    public void OnNext(KeyValuePair<string, object?> value)
    {
        if (value.Key == "Microsoft.EntityFrameworkCore.Database.Command.CommandExecuted")
        {
            Interlocked.Increment(ref _count);
        }
    }

    public void OnCompleted()
    {
    }

    public void OnError(Exception error)
    {
    }
}

[CollectionDefinition(Name)]
public class IntegrationCollection : ICollectionFixture<ChatBiWebApplicationFactory>
{
    public const string Name = "Integration";
}

/// <summary>进程内 ILogger 输出收集（脱敏抽查：断言敏感值不落日志）。</summary>
public sealed class CollectingLogSink : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _entries = new();

    public IReadOnlyList<string> Snapshot() => _entries.ToArray();

    public bool ContainsSecret(string secret) =>
        _entries.Any(entry => entry.Contains(secret, StringComparison.Ordinal));

    public ILogger CreateLogger(string categoryName) => new Collector(_entries);

    public void Dispose()
    {
    }

    private sealed class Collector : ILogger
    {
        private readonly ConcurrentQueue<string> _entries;

        public Collector(ConcurrentQueue<string> entries)
        {
            _entries = entries;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _entries.Enqueue(formatter(state, exception));
    }
}
