using Pulse.Diagnostics;
using Xunit;

namespace Pulse.Core.Tests;

public class RedactingLoggerTests
{
    private static readonly List<InMemoryLogSink> Sinks = new();

    private static RedactingLogger CreateLogger(SecretScrubber? scrubber = null, bool debug = false)
    {
        var sink = new InMemoryLogSink();
        Sinks.Add(sink);
        return new RedactingLogger(new[] { sink }, debug, scrubber);
    }

    private static InMemoryLogSink Sink => Sinks[^1];

    [Fact]
    public void Bearer_Token_Is_Redacted()
    {
        var logger = CreateLogger();
        logger.Info("request sent with Bearer sk-abc123secret");
        Assert.Contains("Bearer [REDACTED]", Sink.Lines[0]);
        Assert.DoesNotContain("sk-abc123secret", Sink.Lines[0]);
    }

    [Fact]
    public void Authorization_Header_Is_Redacted()
    {
        var logger = CreateLogger();
        logger.Info("Authorization: Bearer eyJhbGciOi.eyJzdWIi.sig");
        Assert.DoesNotContain("eyJ", Sink.Lines[0]);
        Assert.Contains("[REDACTED]", Sink.Lines[0]);
    }

    [Fact]
    public void Cookie_Header_Is_Redacted()
    {
        var logger = CreateLogger();
        logger.Info("cookie: session=super-secret-value; other=x");
        Assert.DoesNotContain("super-secret-value", Sink.Lines[0]);
    }

    [Fact]
    public void Registered_Canary_Is_Redacted_Literally()
    {
        var scrubber = new SecretScrubber();
        scrubber.RegisterSecret("PULSE_TEST_API_KEY_canary_123");
        var logger = CreateLogger(scrubber);
        logger.Info("loaded key PULSE_TEST_API_KEY_canary_123 ok");
        Assert.DoesNotContain("canary_123", Sink.Lines[0]);
        Assert.Contains("[REDACTED]", Sink.Lines[0]);
    }

    [Fact]
    public void ApiKey_Shaped_Assignment_Is_Redacted()
    {
        var logger = CreateLogger();
        logger.Info("config api_key = abc123 not logged");
        Assert.DoesNotContain("abc123", Sink.Lines[0]);
    }

    [Fact]
    public void Debug_Requires_Debug_Mode()
    {
        var enabledSink = new InMemoryLogSink();
        var disabledSink = new InMemoryLogSink();
        var enabled = new RedactingLogger(new[] { enabledSink }, debugEnabled: true);
        var disabled = new RedactingLogger(new[] { disabledSink }, debugEnabled: false);
        enabled.Debug("debug line");
        disabled.Debug("debug line");
        Assert.Equal(1, enabledSink.Lines.Count);
        Assert.Empty(disabledSink.Lines);
        Assert.Contains("debug line", enabledSink.Lines[0]);
    }
}
