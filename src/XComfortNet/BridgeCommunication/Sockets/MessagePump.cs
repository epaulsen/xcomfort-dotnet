using System.Reactive.Subjects;
using XComfortDotNet;
using XComfortDotNet.Model;

namespace XComfortNet;

internal class MessagePump
{
    public Subject<XComfortMessage> MessageReceived { get; } = new();

    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly IXComfortWebSocket _socket;
    private CancellationTokenSource _cancellationTokenSource = new();
    private Task _pumpTask;
    private int _sequenceNumber = 100;

    public MessagePump(IXComfortWebSocket socket)
    {
        _socket = socket;
        _pumpTask = Task.Run(() => RunPumpAsync(_cancellationTokenSource.Token));
    }

    public async Task SendMessageAsync(XComfortMessage message, bool waitForAck = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _semaphore.WaitAsync(cancellationToken);
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
        try
        {
            await _semaphore.WaitAsync(cancellationToken);

            _sequenceNumber++;
            var msg = new XComfortMessage<T>(type, message)
            {
                MessageCounter = _sequenceNumber
            };

            await _socket.SendAsync(msg, cancellationToken);
        }
        finally
        {
            _semaphore.Release();
        }
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
                    await _socket.SendAckAsync(msg.MessageCounter, cancellationToken);
                }

                MessageReceived.OnNext(msg);
            }
            catch (TaskCanceledException e)
            {
            }
        }
    }
}