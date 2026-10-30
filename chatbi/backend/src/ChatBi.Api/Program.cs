using Microsoft.AspNetCore.Mvc;
using ChatBi.Application.Common;
using ChatBi.Api.Auth;
using ChatBi.Api.Authorization;
using ChatBi.Api.ErrorHandling;
using ChatBi.Api.Logging;
using ChatBi.Application.Configuration;
using ChatBi.Infrastructure;
using ChatBi.Infrastructure.Auth;
using ChatBi.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// ---- Serilog：结构化日志 + 脱敏 enricher（不落 token/连接串） ----
builder.Host.UseSerilog((context, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext()
    .Enrich.With<SensitiveDataRedactionEnricher>()
    .WriteTo.Console());

// ---- Infrastructure：DbContext、SSO（mock 可切换）、JWT、数据权限服务 ----
builder.Services.AddInfrastructure(builder.Configuration);

// ---- 认证：本地自签 JWT（HS256；密钥仅来自环境变量 JWT_SIGNING_KEY） ----
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false; // 保持 sub/role/token_ver 字面 claim 名
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = TokenVersionValidator.ValidateAsync,
        };
    });

// 校验参数依赖 JwtTokenService（签名密钥/Issuer/Audience），走命名选项延迟配置。
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<JwtTokenService>((options, tokenService) =>
    {
        options.TokenValidationParameters = tokenService.BuildTokenValidationParameters(validateLifetime: true);
    });

// ---- 授权：Policy-based，默认拒绝；Policy 名 = 权限码 ----
builder.Services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();
builder.Services.AddChatBiPolicies();

builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

// ---- 模型绑定失败（缺字段/类型不符/枚举越界）与字段校验统一为 422 SEM_FIELD_INVALID ----
builder.Services.Configure<ApiBehaviorOptions>(options => options.InvalidModelStateResponseFactory = context =>
{
    var detail = context.ModelState.Values
        .SelectMany(value => value.Errors)
        .Select(error => string.IsNullOrEmpty(error.ErrorMessage) ? "请求体格式非法。" : error.ErrorMessage)
        .FirstOrDefault()
        ?? "请求体格式非法。";

    var problem = new Microsoft.AspNetCore.Mvc.ProblemDetails
    {
        Status = StatusCodes.Status422UnprocessableEntity,
        Title = SemanticErrorCodes.FieldInvalid,
        Detail = detail,
        Type = $"https://chatbi.local/errors/{SemanticErrorCodes.FieldInvalid.ToLowerInvariant()}",
    };
    problem.Extensions["code"] = SemanticErrorCodes.FieldInvalid;

    return new Microsoft.AspNetCore.Mvc.ObjectResult(problem)
    {
        StatusCode = StatusCodes.Status422UnprocessableEntity,
        ContentTypes = { "application/problem+json" },
    };
});

// ---- HealthChecks：/health 含平台库 DB 检查 ----
var platformDbConnection = builder.Configuration.GetConnectionString(DependencyInjection.PlatformDbConnectionStringName);
if (string.IsNullOrWhiteSpace(platformDbConnection))
{
    throw new InvalidOperationException("Platform DB connection string is missing.");
}

builder.Services.AddHealthChecks()
    .AddSqlServer(platformDbConnection, name: "platform-db");

// ---- OpenTelemetry 骨架：OTLP 端点配置后才导出 ----
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("chatbi-api"))
    .WithTracing(tracing =>
    {
        tracing
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddSource("ChatBi");

        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT")))
        {
            tracing.AddOtlpExporter();
        }
    });

// ---- CORS：仅放行配置的前端源 ----
var corsOrigins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? Array.Empty<string>();
if (corsOrigins.Length > 0)
{
    builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
        .WithOrigins(corsOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod()));
}

var app = builder.Build();

// ---- 启动期：强校验 JWT 配置（fail fast）、应用迁移（幂等入口之一）、SysAdmin 引导 ----
var signingKey = app.Services
    .GetRequiredService<Microsoft.Extensions.Options.IOptions<JwtOptions>>()
    .Value.SigningKey;
if (string.IsNullOrWhiteSpace(signingKey) || signingKey.Length < 32)
{
    throw new InvalidOperationException(
        $"JWT signing key missing or too short; set env {JwtOptions.SigningKeyEnvironmentVariable} (>=32 chars).");
}

if (app.Configuration.GetValue("Database:ApplyMigrationsOnStart", defaultValue: true))
{
    using var scope = app.Services.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<ChatBiDbContext>();
    await dbContext.Database.MigrateAsync();

    var adminSeeder = scope.ServiceProvider.GetRequiredService<AdminSeedService>();
    await adminSeeder.EnsureAdminAsync();
}

app.UseExceptionHandler(exceptionBuilder => exceptionBuilder.Run(async context =>
{
    // IExceptionHandler 已写入 ProblemDetails；此处兜底避免空响应。
    if (!context.Response.HasStarted)
    {
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
    }

    await Task.CompletedTask;
}));

app.UseSerilogRequestLogging();

if (corsOrigins.Length > 0)
{
    app.UseCors();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/health").AllowAnonymous();

app.Run();

/// <summary>供 WebApplicationFactory 引用的入口标记。</summary>
public partial class Program
{
}
