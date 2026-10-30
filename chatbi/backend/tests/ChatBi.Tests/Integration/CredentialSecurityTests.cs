using System.Net;
using ChatBi.Domain.Security;
using FluentAssertions;
using Xunit;

namespace ChatBi.Tests.Integration;

/// <summary>明文凭证安全契约：422 拒收 + 全量日志抽查（进程内 ILoggerProvider 收集）确认敏感值不落日志。</summary>
[Collection(IntegrationCollection.Name)]
public class CredentialSecurityTests : SemanticEndpointTestBase
{
    public CredentialSecurityTests(ChatBiWebApplicationFactory factory) : base(factory)
    {
    }

    private const string BasePath = "/api/semantic/datasources";
    private const string Plaintext = "Server=secret-host;Database=secret-db;User Id=secret-user;Password=Secret_P@ss_0x9f";

    [Fact]
    public async Task PlaintextCredential_Rejected_And_NeverLogged()
    {
        var admin = await CreateUserTokenAsync(RoleCodes.SysAdmin);
        var client = ClientWith(admin);

        var (status, code) = await ProblemAsync(await client.PostAsync(BasePath, JsonContent(ToJson(new
        {
            Name = $"DS_{Guid.NewGuid():N}"[..20],
            DbType = 1,
            Host = "10.0.0.8",
            Port = 1433,
            DatabaseName = "biz",
            CredentialRef = Plaintext,
            QueryTimeoutSeconds = 30,
            MaxRowsPerQuery = 5000,
            Status = 1,
        }))));

        status.Should().Be(HttpStatusCode.UnprocessableEntity);
        code.Should().Be("SEM_PLAINTEXT_CREDENTIAL");

        // 全量进程内日志抽查：明文连接串及其片段不得出现在任何日志输出。
        Factory.LogSink.ContainsSecret(Plaintext).Should().BeFalse("明文连接串本体不得落日志");
        Factory.LogSink.ContainsSecret("secret-host").Should().BeFalse("主机名片段不得落日志");
        Factory.LogSink.ContainsSecret("Secret_P@ss_0x9f").Should().BeFalse("密码片段不得落日志");
    }

    [Fact]
    public async Task ValidCredentialRef_Accepted_And_NeverLogged()
    {
        var admin = await CreateUserTokenAsync(RoleCodes.SysAdmin);
        var marker = $"KV_REF_{Guid.NewGuid():N}"[..24];

        var response = await ClientWith(admin).PostAsync(BasePath, JsonContent(ToJson(new
        {
            Name = $"DS_{Guid.NewGuid():N}"[..20],
            DbType = 1,
            Host = "10.0.0.8",
            Port = 1433,
            DatabaseName = "biz",
            CredentialRef = marker,
            QueryTimeoutSeconds = 30,
            MaxRowsPerQuery = 5000,
            Status = 1,
        })));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        // 合法引用名也只应随请求体出现，不应被服务层日志回显。
        Factory.LogSink.ContainsSecret(marker).Should().BeFalse("日志只记录资源 id 与操作者，不回显请求字段");
    }
}
