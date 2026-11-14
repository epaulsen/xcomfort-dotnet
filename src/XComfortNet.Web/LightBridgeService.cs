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
    XComfortLight? Light, IReadOnlyList<XComfortLight> Lights,
    IReadOnlyList<XComfortClimate> Climates, IReadOnlyList<XComfortSensor> Sensors);

internal sealed class LightBridgeService(
    IOptions<XComfortOptions> bridgeOptions,
    IOptions<MqttOptions> mqttOptions,
    ILoggerFactory loggerFactory,
    IHostEnvironment environment,
    ILogger<LightBridgeService> logger) : BackgroundService
{
    private readonly IMqttClient _mqtt = new MqttClientFactory().CreateMqttClient();
    private readonly ILogger _mqttMessageLogger = loggerFactory.CreateLogger("XComfortNet.Web.Mqtt");
    private readonly LightMqttProtocol _protocol = new(mqttOptions.Value.TopicPrefix);
    private readonly EnergyTracker _energy = new(Path.Combine(environment.ContentRootPath, "data", "energy.json"), TimeProvider.System);
    private readonly string _manifestPath = Path.Combine(environment.ContentRootPath, "data", "mqtt-publications.json");
    private readonly object _energyObservationGate = new();
    private readonly Channel<bool> _changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.DropOldest
    });
    private volatile BridgeState _state = new(null, [], XComfortTelemetry.Empty, false);
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
            var telemetry = state.BridgeId is string bridgeId
                ? _energy.AppendSensors(bridgeId, state.Telemetry) : state.Telemetry;
            return new LightBridgeStatus(_mqtt.IsConnected,
                bridge?.BridgeId is not null && !bridge.Completion.IsCompleted,
                state.Ready, state.BridgeId, state.Lights.FirstOrDefault(), state.Lights,
                telemetry.Climates, telemetry.Sensors);
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
                PublishChangesAsync(stoppingToken), TrackEnergyAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            using var energySaveTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try
            {
                await _energy.SaveAsync(energySaveTimeout.Token);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("Energy totals could not be saved: {ErrorType}", error.GetType().Name);
            }
            catch (OperationCanceledException) when (energySaveTimeout.IsCancellationRequested)
            {
                logger.LogWarning("Energy totals save timed out during shutdown.");
            }
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
            using var refreshCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            Task? refreshTask = null;
            await using (var bridge = new XComfortBridge(bridgeOptions.Value.Host, bridgeOptions.Value.AuthKey,
                loggerFactory))
            {
                _bridge = bridge;
                using var subscription = bridge.LightsChanged.Subscribe(lights =>
                {
                    _state = new BridgeState(bridge.BridgeId, lights, bridge.Telemetry, true);
                    ObserveEnergy();
                    SignalChange();
                });
                using var telemetrySubscription = bridge.TelemetryChanged.Subscribe(telemetry =>
                {
                    _state = new BridgeState(bridge.BridgeId, bridge.Lights, telemetry, true);
                    ObserveEnergy();
                    SignalChange();
                });
                try
                {
                    using var initialization = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    initialization.CancelAfter(TimeSpan.FromSeconds(20));
                    await bridge.ConnectAsync(initialization.Token);
                    await bridge.ReadAllDataAsync(initialization.Token);
                    await bridge.WaitForInitializationAsync(initialization.Token);
                    logger.LogInformation("Inventory completed with {LightCount} lights, {ClimateCount} climates and {SensorCount} sensors.",
                        bridge.Lights.Count, bridge.Telemetry.Climates.Count, bridge.Telemetry.Sensors.Count);
                    refreshTask = RefreshBridgeStateAsync(bridge, refreshCancellation.Token);
                    var completedTask = await Task.WhenAny(bridge.Completion, refreshTask).WaitAsync(stoppingToken);
                    if (completedTask == refreshTask)
                    {
                        await refreshTask;
                    }
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
                    await refreshCancellation.CancelAsync();
                    if (refreshTask is { IsCompleted: false })
                    {
                        try
                        {
                            await refreshTask;
                        }
                        catch (OperationCanceledException) when (refreshCancellation.IsCancellationRequested)
                        {
                        }
                    }
                    _bridge = null;
                    _state = _state with { Ready = false };
                    ObserveEnergy();
                    SignalChange();
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    private async Task RefreshBridgeStateAsync(XComfortBridge bridge, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            await bridge.ReadAllDataAsync(cancellationToken);
            logger.LogDebug("Requested Bridge state refresh.");
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
                    .WithTopicFilter(filter => filter.WithTopic(_protocol.ClimateCommandFilter)
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
        var manifest = MqttPublicationManifest.Load(_manifestPath);
        var discoveryCache = manifest.Discoveries;
        var stateCache = manifest.States;
        var publishedRevision = -1;
        var telemetryProtocol = new TelemetryMqttProtocol(_protocol);
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
                    var telemetry = _energy.AppendSensors(bridgeId, state.Telemetry);
                    var discoveries = telemetryProtocol.Discoveries(bridgeId, state.Lights, telemetry);
                    var obsoleteLightPower = force
                        ? telemetryProtocol.LightPowerRemovalDiscoveries(bridgeId, state.Lights, discoveries)
                        : new Dictionary<string, string>();
                    await PublishDiscoveriesAsync(discoveryCache, discoveries, obsoleteLightPower, force, stoppingToken);
                    var states = state.Lights
                        .Select(light => (Topic: _protocol.StateTopic(bridgeId, light.DeviceId),
                            Payload: LightMqttProtocol.State(light)))
                        .Where(item => item.Payload is not null)
                        .ToDictionary(item => item.Topic, item => item.Payload!);
                    foreach (var (topic, payload) in telemetryProtocol.States(bridgeId, telemetry))
                        states[topic] = payload;
                    await PublishRetainedSetAsync(stateCache, states, force, stoppingToken);
                    manifest = new MqttPublicationManifest { Discoveries = discoveryCache, States = stateCache };
                    await manifest.SaveAsync(_manifestPath, stoppingToken);
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

    private void ObserveEnergy()
    {
        lock (_energyObservationGate)
        {
            var state = _state;
            if (state.BridgeId is string bridgeId) _energy.Observe(bridgeId, state.Telemetry, state.Ready);
        }
    }

    private async Task TrackEnergyAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            ObserveEnergy();
            SignalChange();
            try
            {
                await _energy.SaveAsync(stoppingToken);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("Energy totals could not be saved: {ErrorType}", error.GetType().Name);
            }
        }
    }

    private async Task HandleMqttMessageAsync(MqttApplicationMessageReceivedEventArgs args)
    {
        var message = args.ApplicationMessage;
        var payload = message.ConvertPayloadToString();
        _mqttMessageLogger.LogDebug("Received MQTT message on {Topic} (retained: {Retain}): {Payload}",
            message.Topic, message.Retain, payload);
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
        if (state.Ready && bridge is not null && state.BridgeId is string climateBridgeId &&
            new TelemetryMqttProtocol(_protocol).TryParseCommand(message.Topic, climateBridgeId,
                state.Telemetry.Climates, payload, message.Retain, out var roomId, out var climateCommand))
        {
            try
            {
                await bridge.SetClimateAsync(roomId, climateCommand!, _stoppingToken);
                logger.LogInformation("Sent climate command to room {RoomId}.", roomId);
            }
            catch (OperationCanceledException) when (_stoppingToken.IsCancellationRequested)
            {
            }
            catch (Exception error)
            {
                logger.LogWarning("Climate command failed: {ErrorType}", error.GetType().Name);
            }
            return;
        }
        if (!state.Ready || bridge is null || state.BridgeId is not string bridgeId ||
            state.Lights.FirstOrDefault(light =>
                message.Topic == _protocol.CommandTopic(bridgeId, light.DeviceId)) is not { } light)
        {
            logger.LogWarning("Ignored MQTT command for an unavailable entity or an invalid/retained command.");
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

    private async Task PublishDiscoveriesAsync(Dictionary<string, string> cache,
        Dictionary<string, string> current, IReadOnlyDictionary<string, string> obsoleteComponents,
        bool force, CancellationToken cancellationToken)
    {
        foreach (var removed in cache.Keys.Except(current.Keys).ToArray())
        {
            await PublishAsync(removed, "", cancellationToken);
            cache.Remove(removed);
        }
        foreach (var (topic, payload) in current)
        {
            if (!force && cache.TryGetValue(topic, out var previous) && previous == payload) continue;

            if (obsoleteComponents.TryGetValue(topic, out var legacyRemoval))
                await PublishAsync(topic, legacyRemoval, cancellationToken);
            if (cache.TryGetValue(topic, out previous) &&
                TelemetryMqttProtocol.RemovedComponentsUpdate(previous, payload) is { } removal)
                await PublishAsync(topic, removal, cancellationToken);

            await PublishAsync(topic, payload, cancellationToken);
            cache[topic] = payload;
        }
    }

    private async Task PublishAsync(string topic, string payload, CancellationToken cancellationToken)
    {
        var message = new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(payload)
            .WithRetainFlag().WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build();
        await _mqtt.PublishAsync(message, cancellationToken);
        _mqttMessageLogger.LogDebug("Sent retained MQTT message on {Topic}: {Payload}", topic, payload);
    }

    private void SignalChange() => _changes.Writer.TryWrite(true);
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed record BridgeState(string? BridgeId, IReadOnlyList<XComfortLight> Lights,
        XComfortTelemetry Telemetry, bool Ready);
}
