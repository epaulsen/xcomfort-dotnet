using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using XComfortDotNet.Model;
using XComfortNet;

namespace XComfortDotNet;

public class XComfortBridge
{
    private readonly MessagePump _messagePump;
    private readonly ILoggerFactory _loggerFactory;

    public IObservable<XComfortMessage> MessageReceived => _messagePump.MessageReceived;
    internal XComfortBridge(MessagePump messagePump, ILoggerFactory? loggerFactory = null)
    {
        _messagePump = messagePump;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        MessageReceived.Subscribe(msg =>
        {
            var logger = _loggerFactory.CreateLogger<MessagePump>();
            logger.LogInformation("Message received: {message}", JsonSerializer.Serialize(msg));
        });
    }

    public async Task ReadAllDataAsync(CancellationToken cancellationToken = default)
    {
        await _messagePump.SendMessageAsync(new XComfortMessage(Messages.INITIAL_DATA),cancellationToken: cancellationToken);
        
        await _messagePump.SendMessageAsync(new XComfortMessage(Messages.HOME_DATA),cancellationToken: cancellationToken);
    }
}