using Microsoft.Extensions.Logging;

namespace XComfortDotNet;

public static class LoggerExtensions
{
    public static void LogDebug(this ILogger logger, string message, Func<object[]> args) =>
        logger.LogDebug(message, args());
}