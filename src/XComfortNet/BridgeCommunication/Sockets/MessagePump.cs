using System.Net.WebSockets;
using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using XComfortDotNet;
using XComfortDotNet.Model;

namespace XComfortNet;

internal sealed class MessagePump : IAsyncDisposable
{
    public Subject<XComfortMessage> MessageReceived { get; } = new();
    public Subject<Exception> SocketError { get; } = new();
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly IXComfortWebSocket _socket;
    private readonly ILogger<MessagePump> _logger;
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private Task _pumpTask = Task.CompletedTask;
    private int _sequenceNumber = 100;

    public string BridgeId { get; }
    public string? FirmwareVersion { get; }
    public Task Completion => _pumpTask;

    public MessagePump(IXComfortWebSocket socket, string bridgeId, ILogger<MessagePump>? logger = null,
        string? firmwareVersion = null)
    {
        _socket = socket;
        _logger = logger ?? NullLogger<MessagePump>.Instance;
        BridgeId = bridgeId;
        FirmwareVersion = firmwareVersion;
    }

    public void Start()
    {
        _pumpTask = RunPumpAsync(_cancellationTokenSource.Token);
    }

    public async Task SendMessageAsync(XComfortMessage message, bool waitForAck = false,
        CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            _sequenceNumber++;
            message.MessageCounter = _sequenceNumber;
            await _socket.SendAsync(message, cancellationToken);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task SendMessageAsync<T>(Messages type, T message, bool waitForAck = false, CancellationToken cancellationToken = default)
    {
        await SendMessageAsync(new XComfortMessage<T>(type, message), waitForAck, cancellationToken);
    }

    private async Task RunPumpAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var msg = await _socket.ReceiveAsync(cancellationToken);
                if (msg.MessageCounter != -1)
                {
                    await _semaphore.WaitAsync(cancellationToken);
                    try
                    {
                        await _socket.SendAckAsync(msg.MessageCounter, cancellationToken);
                    }
                    finally
                    {
                        _semaphore.Release();
                    }
                }

                MessageReceived.OnNext(msg);
            }

            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                _logger.LogWarning("Bridge message pump stopped: {ErrorType}", e.GetType().Name);
                SocketError.OnNext(e);
                throw;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cancellationTokenSource.CancelAsync();
        try
        {
            await _pumpTask;
        }
        catch (Exception)
        {
            // The connection owner observes failures through Completion.
        }
        await _socket.DisposeAsync();
        MessageReceived.Dispose();
        SocketError.Dispose();
        _cancellationTokenSource.Dispose();
        _semaphore.Dispose();
    }
}