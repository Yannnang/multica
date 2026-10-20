using ChatBi.Api.Logging;
using FluentAssertions;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace ChatBi.Tests.Unit;

public class SensitiveDataRedactionEnricherTests
{
    private sealed class CapturingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = new();

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    private static (Serilog.ILogger Logger, CapturingSink Sink) CreateLogger()
    {
        var sink = new CapturingSink();
        var logger = new Serilog.LoggerConfiguration()
            .Enrich.With<SensitiveDataRedactionEnricher>()
            .WriteTo.Sink(sink)
            .CreateLogger();
        return (logger, sink);
    }

    [Fact]
    public void SensitivePropertyNames_AreRedacted()
    {
        var (logger, sink) = CreateLogger();

        logger.Information("ctx {AccessToken} {RefreshToken} {Password} {Authorization} {ConnectionString} {ApiKey} {UserSecret}",
            "aaa", "bbb", "ccc", "Bearer xyz", "Server=db;Password=p", "k", "s");

        var props = sink.Events.Single().Properties;
        props["AccessToken"].ToString().Should().Contain(SensitiveDataRedactionEnricher.RedactedValue);
        props["RefreshToken"].ToString().Should().Contain(SensitiveDataRedactionEnricher.RedactedValue);
        props["Password"].ToString().Should().Contain(SensitiveDataRedactionEnricher.RedactedValue);
        props["Authorization"].ToString().Should().Contain(SensitiveDataRedactionEnricher.RedactedValue);
        props["ConnectionString"].ToString().Should().Contain(SensitiveDataRedactionEnricher.RedactedValue);
        props["ApiKey"].ToString().Should().Contain(SensitiveDataRedactionEnricher.RedactedValue);
        props["UserSecret"].ToString().Should().Contain(SensitiveDataRedactionEnricher.RedactedValue);
    }

    [Fact]
    public void JwtLikeValues_AreRedacted_EvenWithSafeNames()
    {
        var (logger, sink) = CreateLogger();
        var jwt = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxIn0.abcdefg";

        logger.Information("payload {Payload}", jwt);

        sink.Events.Single().Properties["Payload"].ToString()
            .Should().Contain(SensitiveDataRedactionEnricher.RedactedValue);
    }

    [Fact]
    public void NonSensitiveData_IsPreserved()
    {
        var (logger, sink) = CreateLogger();

        logger.Information("user {UserId} did {Action}", 42, "login");

        var props = sink.Events.Single().Properties;
        props["UserId"].ToString().Should().Be("42");
        props["Action"].ToString().Should().Be("\"login\"");
    }
}
