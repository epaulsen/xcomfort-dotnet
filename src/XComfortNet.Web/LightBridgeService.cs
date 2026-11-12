using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;
using XComfortDotNet;

namespace XComfortNet.Web;

internal sealed class XComfortOptions
{
    public string Host { get; set; } = "";
    public string AuthKey { get; set; } = "";
}

internal sealed class MqttOptions
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 1883;
    public string TopicPrefix { get; set; } = "xcomfort2mqtt";

    public bool IsValid() => !string.IsNullOrWhiteSpace(Host) && Port is > 0 and <= 65535 &&
        Regex.IsMatch(TopicPrefix, "^[a-zA-Z0-9_-]+$");
}

internal sealed record LightBridgeStatus(
    bool MqttConnected, bool BridgeConnected, bool InventoryReady, string? BridgeId,
    XComfortLight? Light, IReadOnlyList<XComfortLight> Lights);

internal sealed class LightBridgeService(
    IOptions<XComfortOptions> bridgeOptions,
    IOptions<MqttOptions> mqttOptions,
    ILoggerFactory loggerFactory,
    ILogger<LightBridgeService> logger) : BackgroundService
{
    private readonly IMqttClient _mqtt = new MqttClientFactory().CreateMqttClient();
    private readonly LightMqttProtocol _protocol = new(mqttOptions.Value.TopicPrefix);
    private readonly Channel<bool> _changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.DropOldest
    });
    private volatile BridgeState _state = new(null, [], false);
    private volatile XComfortBridge? _bridge;
    private TaskCompletionSource _mqttDisconnected = NewSignal();
    private CancellationToken _stoppingToken;
    private int _discoveryRevision;

    public LightBridgeStatus Status
    {
        get
        {
            var state = _state;
            var bridge = _bridge;
            return new LightBridgeStatus(_mqtt.IsConnected,
                bridge?.BridgeId is not null && !bridge.Completion.IsCompleted,
                state.Ready, state.BridgeId, state.Lights.FirstOrDefault(), state.Lights);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;
        _mqtt.DisconnectedAsync += _ =>
        {
            _mqttDisconnected.TrySetResult();
            return Task.CompletedTask;
        };
        _mqtt.ApplicationMessageReceivedAsync += HandleMqttMessageAsync;
        try
        {
            await Task.WhenAll(RunBridgeAsync(stoppingToken), RunMqttAsync(stoppingToken),
                PublishChangesAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            if (_mqtt.IsConnected)
            {
                using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try
                {
                    await PublishAsync(_protocol.ServiceAvailabilityTopic, "offline", shutdown.Token);
                    await _mqtt.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), shutdown.Token);
                }
                catch (Exception)
                {
                    // Closing the connection also activates the offline Last Will.
                }
            }
            _mqtt.Dispose();
        }
    }

    private async Task RunBridgeAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await using (var bridge = new XComfortBridge(bridgeOptions.Value.Host, bridgeOptions.Value.AuthKey,
                loggerFactory))
            {
                _bridge = bridge;
                using var subscription = bridge.LightsChanged.Subscribe(lights =>
                {
                    _state = new BridgeState(bridge.BridgeId, lights, true);
                    SignalChange();
                });
                try
                {
                    using var initialization = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    initialization.CancelAfter(TimeSpan.FromSeconds(20));
                    await bridge.ConnectAsync(initialization.Token);
                    await bridge.ReadAllDataAsync(initialization.Token);
                    await bridge.WaitForInitializationAsync(initialization.Token);
                    logger.LogInformation("Inventory completed with {LightCount} verified lights.", bridge.Lights.Count);
                    await bridge.Completion.WaitAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception error)
                {
                    logger.LogWarning("Bridge connection failed: {ErrorType}: {Message}",
                        error.GetType().Name, error.Message);
                }
                finally
                {
                    _bridge = null;
                    _state = _state with { Ready = false };
                    SignalChange();
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    private async Task RunMqttAsync(CancellationToken stoppingToken)
    {
        var options = new MqttClientOptionsBuilder()
            .WithClientId($"{mqttOptions.Value.TopicPrefix}-web")
            .WithTcpServer(mqttOptions.Value.Host, mqttOptions.Value.Port)
            .WithProtocolVersion(MqttProtocolVersion.V500)
            .WithCleanSession()
            .WithWillTopic(_protocol.ServiceAvailabilityTopic)
            .WithWillPayload("offline")
            .WithWillRetain()
            .Build();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _mqttDisconnected = NewSignal();
                await _mqtt.ConnectAsync(options, stoppingToken);
                var subscriptions = new MqttClientSubscribeOptionsBuilder()
                    .WithTopicFilter(filter => filter.WithTopic(_protocol.CommandFilter)
                        .WithAtLeastOnceQoS().WithRetainAsPublished()
                        .WithRetainHandling(MqttRetainHandling.DoNotSendOnSubscribe))
                    .WithTopicFilter("homeassistant/status")
                    .Build();
                await _mqtt.SubscribeAsync(subscriptions, stoppingToken);
                Interlocked.Increment(ref _discoveryRevision);
                // Invalidate retained availability until this session has fresh Bridge state.
                await PublishAsync(_protocol.ServiceAvailabilityTopic, "offline", stoppingToken);
                SignalChange();
                logger.LogInformation("MQTT connected.");
                await _mqttDisconnected.Task.WaitAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                logger.LogWarning("MQTT connection failed: {ErrorType}: {Message}",
                    error.GetType().Name, error.Message);
            }
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    private async Task PublishChangesAsync(CancellationToken stoppingToken)
    {
        var discoveryCache = new Dictionary<string, string>();
        var stateCache = new Dictionary<string, string>();
        var publishedRevision = -1;
        await foreach (var _ in _changes.Reader.ReadAllAsync(stoppingToken))
        {
            if (!_mqtt.IsConnected)
            {
                continue;
            }
            try
            {
                var state = _state;
                if (state.BridgeId is not string bridgeId)
                {
                    continue;
                }
                if (state.Ready)
                {
                    var revision = Volatile.Read(ref _discoveryRevision);
                    var force = revision != publishedRevision;
                    if (force)
                    {
                        // Retire the single-light document; entity unique IDs remain unchanged.
                        await PublishAsync(_protocol.LegacyDiscoveryTopic(bridgeId), "", stoppingToken);
                    }
                    var discoveries = state.Lights.GroupBy(light => light.ComponentId).ToDictionary(
                        group => _protocol.DiscoveryTopic(bridgeId, group.Key),
                        group => _protocol.Discovery(bridgeId, group.OrderBy(light => light.DeviceId).ToArray()));
                    await PublishRetainedSetAsync(discoveryCache, discoveries, force, stoppingToken);
                    var states = state.Lights
                        .Select(light => (Topic: _protocol.StateTopic(bridgeId, light.DeviceId),
                            Payload: LightMqttProtocol.State(light)))
                        .Where(item => item.Payload is not null)
                        .ToDictionary(item => item.Topic, item => item.Payload!);
                    await PublishRetainedSetAsync(stateCache, states, force, stoppingToken);
                    publishedRevision = revision;
                }
                await PublishAsync(_protocol.BridgeAvailabilityTopic(bridgeId),
                    state.Ready ? "online" : "offline", stoppingToken);
                await PublishAsync(_protocol.ServiceAvailabilityTopic, "online", stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                logger.LogWarning("MQTT state publication failed: {ErrorType}", error.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                SignalChange();
            }
        }
    }

    private async Task HandleMqttMessageAsync(MqttApplicationMessageReceivedEventArgs args)
    {
        var message = args.ApplicationMessage;
        var payload = message.ConvertPayloadToString();
        if (message.Topic == "homeassistant/status")
        {
            if (payload == "online")
            {
                Interlocked.Increment(ref _discoveryRevision);
                SignalChange();
            }
            return;
        }

        var state = _state;
        var bridge = _bridge;
        if (!state.Ready || bridge is null || state.BridgeId is not string bridgeId ||
            state.Lights.FirstOrDefault(light =>
                message.Topic == _protocol.CommandTopic(bridgeId, light.DeviceId)) is not { } light)
        {
            logger.LogWarning("Ignored MQTT command for an unavailable or unverified light.");
            return;
        }
        if (!_protocol.TryParseCommand(message.Topic, bridgeId, light, payload, message.Retain, out var command))
        {
            logger.LogWarning("Ignored invalid or retained MQTT light command.");
            return;
        }

        try
        {
            await bridge.SetLightAsync(light.DeviceId, command!.IsOn, command.Brightness, _stoppingToken);
            logger.LogInformation("Sent command to light {DeviceId}.", light.DeviceId);
        }
        catch (OperationCanceledException) when (_stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            logger.LogWarning("Light command failed: {ErrorType}", error.GetType().Name);
        }
    }

    private async Task PublishRetainedSetAsync(Dictionary<string, string> cache,
        Dictionary<string, string> current, bool force, CancellationToken cancellationToken)
    {
        foreach (var removed in cache.Keys.Except(current.Keys).ToArray())
        {
            await PublishAsync(removed, "", cancellationToken);
            cache.Remove(removed);
        }
        foreach (var (topic, payload) in current)
        {
            if (force || !cache.TryGetValue(topic, out var previous) || previous != payload)
            {
                await PublishAsync(topic, payload, cancellationToken);
                cache[topic] = payload;
            }
        }
    }

    private Task PublishAsync(string topic, string payload, CancellationToken cancellationToken) =>
        _mqtt.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(payload)
            .WithRetainFlag().WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build(),
            cancellationToken);

    private void SignalChange() => _changes.Writer.TryWrite(true);
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed record BridgeState(string? BridgeId, IReadOnlyList<XComfortLight> Lights, bool Ready);
}
