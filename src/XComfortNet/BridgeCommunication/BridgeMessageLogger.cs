using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using XComfortDotNet;
using XComfortDotNet.Model;

namespace XComfortNet;

internal static class BridgeMessageLogger
{
    private static readonly HashSet<Messages> SensitivePayloadTypes =
    [
        Messages.CONNECTION_START,
        Messages.CONNECTION_CONFIRM,
        Messages.CONNECTION_ESTABLISHED,
        Messages.CONNECTION_DECLINED,
        Messages.SC_INIT,
        Messages.SC_PUBKEY,
        Messages.SC_SECRET,
        Messages.SC_ESTABLISHED,
        Messages.SC_INVALID,
        Messages.AUTH_LOGIN,
        Messages.AUTH_LOGIN_DENIED,
        Messages.AUTH_LOGIN_SUCCESS,
        Messages.AUTH_APPLY_TOKEN,
        Messages.AUTH_APPLY_TOKEN_RESPONSE,
        Messages.AUTH_VERIFY_TOKEN,
        Messages.AUTH_VERIFY_TOKEN_RESPONSE,
        Messages.AUTH_RENEW_TOKEN,
        Messages.AUTH_RENEW_TOKEN_RESPONSE,
        Messages.AUTH_KILL_TOKEN
    ];

    public static void Log(ILogger? logger, string direction, XComfortMessage message, string? bridgeId = null)
    {
        if (logger?.IsEnabled(LogLevel.Debug) != true)
        {
            return;
        }

        logger.LogDebug("{Direction} xComfort Bridge message for {BridgeId}: {Message}",
            direction, bridgeId, FormatForLogging(message));
    }

    internal static string FormatForLogging(XComfortMessage message)
    {
        var json = JsonSerializer.SerializeToNode(message, message.GetType());
        if (json is JsonObject messageObject)
        {
            RedactSensitiveFields(messageObject);
            if (SensitivePayloadTypes.Contains(message.TypeId) && messageObject.ContainsKey("payload"))
            {
                messageObject["payload"] = "[REDACTED]";
            }
        }

        return json?.ToJsonString() ?? "null";
    }

    private static void RedactSensitiveFields(JsonNode? node)
    {
        if (node is JsonObject jsonObject)
        {
            foreach (var key in jsonObject.Select(property => property.Key).ToArray())
            {
                if (IsSensitiveField(key))
                {
                    jsonObject[key] = "[REDACTED]";
                }
                else
                {
                    RedactSensitiveFields(jsonObject[key]);
                }
            }
        }
        else if (node is JsonArray jsonArray)
        {
            foreach (var item in jsonArray)
            {
                RedactSensitiveFields(item);
            }
        }
    }

    private static bool IsSensitiveField(string key)
    {
        var normalized = new string(key.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        return normalized.Contains("token", StringComparison.Ordinal) ||
            normalized.Contains("password", StringComparison.Ordinal) ||
            normalized.Contains("secret", StringComparison.Ordinal) ||
            normalized.Contains("authkey", StringComparison.Ordinal) ||
            normalized.Contains("credential", StringComparison.Ordinal) ||
            normalized.Contains("authorization", StringComparison.Ordinal) ||
            normalized is "salt" or "apikey" or "privatekey";
    }
}
