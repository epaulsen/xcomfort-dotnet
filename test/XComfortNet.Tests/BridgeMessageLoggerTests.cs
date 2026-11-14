using XComfortDotNet;
using XComfortDotNet.Model;
using XComfortNet;

namespace XComfortNet.Tests;

public sealed class BridgeMessageLoggerTests
{
    [Fact]
    public void FormatsRegularMessagesWithTheirPayload()
    {
        var message = new XComfortMessage<Dictionary<string, string>>(
            Messages.SET_DEVICE_STATE, new() { ["name"] = "Hall light", ["switch"] = "true" })
        {
            MessageCounter = 42
        };

        var formatted = BridgeMessageLogger.FormatForLogging(message);

        Assert.Contains("\"mc\":42", formatted);
        Assert.Contains("Hall light", formatted);
        Assert.Contains("\"type_int\":291", formatted);
    }

    [Fact]
    public void RedactsCredentialsFromRegularMessagePayloads()
    {
        var message = new XComfortMessage<Dictionary<string, string>>(
            Messages.SET_DEVICE_STATE, new() { ["name"] = "Hall light", ["access_token"] = "sensitive-token" });

        var formatted = BridgeMessageLogger.FormatForLogging(message);

        Assert.DoesNotContain("sensitive-token", formatted);
        Assert.Contains("[REDACTED]", formatted);
        Assert.Contains("Hall light", formatted);
    }

    [Fact]
    public void RedactsAuthenticationHandshakePayloads()
    {
        var message = new XComfortMessage<Dictionary<string, string>>(
            Messages.AUTH_LOGIN, new() { ["password"] = "hashed-password", ["salt"] = "login-salt" });

        var formatted = BridgeMessageLogger.FormatForLogging(message);

        Assert.DoesNotContain("hashed-password", formatted);
        Assert.DoesNotContain("login-salt", formatted);
        Assert.Contains("[REDACTED]", formatted);
        Assert.Contains("\"type_int\":30", formatted);
    }
}
