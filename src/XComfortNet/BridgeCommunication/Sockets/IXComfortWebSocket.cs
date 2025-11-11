using XComfortDotNet;
using XComfortDotNet.Model;

namespace XComfortNet;

internal interface IXComfortWebSocket
{
    public Task SendAsync(XComfortMessage message, CancellationToken cancellationToken = default);
    
    public Task SendAsync<T>(Messages type, T message, CancellationToken cancellationToken = default);
    
    public Task SendAckAsync(int messageId, CancellationToken cancellationToken = default);
    
    public Task<XComfortMessage> ReceiveAsync(CancellationToken cancellationToken = default);
}