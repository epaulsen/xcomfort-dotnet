using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using XComfortDotNet.Model;
using XComfortNet;

namespace XComfortDotNet;

public sealed class XComfortBridge : IAsyncDisposable
{
    private readonly string _ipAddress;
    private readonly string _authKey;
    private readonly ILoggerFactory _loggerFactory;
    private readonly LightInventory _inventory;
    private readonly TelemetryInventory _telemetry = new();
    private readonly SemaphoreSlim _climateCommands = new(1, 1);
    private readonly Subject<XComfortMessage> _messages = new();
    private readonly Subject<XComfortLight?> _lightChanges = new();
    private readonly Subject<IReadOnlyList<XComfortLight>> _allLightChanges = new();
    private readonly Subject<XComfortTelemetry> _telemetryChanges = new();
    private readonly TaskCompletionSource _initialData = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private MessagePump? _messagePump;
    private IDisposable? _messageSubscription;
    private XComfortLight? _lastLight;
    private IReadOnlyList<XComfortLight> _lastLights = [];
    private XComfortTelemetry _lastTelemetry = XComfortTelemetry.Empty;
    private bool _publishedSnapshot;

    public IObservable<XComfortMessage> MessageReceived => _messages;
    public IObservable<XComfortLight?> LightChanged => _lightChanges;
    public IObservable<IReadOnlyList<XComfortLight>> LightsChanged => _allLightChanges;
    public IObservable<XComfortTelemetry> TelemetryChanged => _telemetryChanges;
    public string? BridgeId => _messagePump?.BridgeId;
    public XComfortLight? FirstLight => _inventory.FirstLight;
    public IReadOnlyList<XComfortLight> Lights => _inventory.Lights;
    public XComfortTelemetry Telemetry => _telemetry.Snapshot;
    public Task Completion => _messagePump?.Completion ?? Task.CompletedTask;

    public XComfortBridge(string ipAddress, string authKey, ILoggerFactory? loggerFactory = null,
        int? preferredLightDeviceId = null)
    {
        _ipAddress = ipAddress;
        _authKey = authKey;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _inventory = new LightInventory(preferredLightDeviceId);
    }

    public async Task ConnectAsync(CancellationToken? cancellationToken = default)
    {
        if (_messagePump is not null)
        {
            throw new InvalidOperationException("Create a new bridge session before reconnecting.");
        }

        _messagePump = await XComfortBridgeFactory.ConnectAsync(
            _ipAddress, _authKey, _loggerFactory, cancellationToken ?? CancellationToken.None);
        _telemetry.SetBridgeInfo(_messagePump.BridgeId, _messagePump.FirmwareVersion, _ipAddress);
        _messageSubscription = _messagePump.MessageReceived.Subscribe(message =>
        {
            _inventory.Apply(message);
            _telemetry.Apply(message);
            _messages.OnNext(message);
            if (!_inventory.IsInitialized)
            {
                return;
            }

            var lights = _inventory.Lights;
            var telemetry = _telemetry.Snapshot;
            if (!_publishedSnapshot || !telemetry.Climates.SequenceEqual(_lastTelemetry.Climates) ||
                !telemetry.Sensors.SequenceEqual(_lastTelemetry.Sensors))
            {
                _lastTelemetry = telemetry;
                _telemetryChanges.OnNext(telemetry);
            }
            if (!_publishedSnapshot || !lights.SequenceEqual(_lastLights))
            {
                _lastLights = lights;
                _allLightChanges.OnNext(lights);
            }
            var light = _inventory.FirstLight;
            if (!_publishedSnapshot || light != _lastLight)
            {
                _publishedSnapshot = true;
                _lastLight = light;
                _lightChanges.OnNext(light);
            }
            _initialData.TrySetResult();
        });
        _messagePump.Start();
    }

    public async Task ReadAllDataAsync(CancellationToken cancellationToken = default)
    {
        var pump = GetPump();
        await pump.SendMessageAsync(Messages.INITIAL_DATA, new { }, cancellationToken: cancellationToken);
        await pump.SendMessageAsync(Messages.HOME_DATA, new { }, cancellationToken: cancellationToken);
        await pump.SendMessageAsync(Messages.HEARTBEAT, new { }, cancellationToken: cancellationToken);
    }

    public async Task WaitForInitializationAsync(CancellationToken cancellationToken = default)
    {
        var completed = await Task.WhenAny(_initialData.Task, GetPump().Completion).WaitAsync(cancellationToken);
        await completed;
        if (!_initialData.Task.IsCompletedSuccessfully)
        {
            throw new InvalidOperationException("Bridge disconnected before inventory completed.");
        }
    }

    public async Task SetLightAsync(int deviceId, bool? isOn, int? brightness = null,
        CancellationToken cancellationToken = default)
    {
        var light = _inventory.GetLight(deviceId);
        if (light is null)
        {
            throw new InvalidOperationException("Only verified lights can be controlled.");
        }
        if (isOn is null && brightness is null)
        {
            throw new ArgumentException("A light state or brightness is required.");
        }
        if (brightness is < 0 or > 99 || (brightness.HasValue && !light.Dimmable))
        {
            throw new ArgumentOutOfRangeException(nameof(brightness));
        }

        var pump = GetPump();
        if (isOn == false || !brightness.HasValue)
        {
            await pump.SendMessageAsync(Messages.ACTION_SWITCH_DEVICE,
                new { deviceId, @switch = isOn!.Value }, cancellationToken: cancellationToken);
        }
        else
        {
            await pump.SendMessageAsync(Messages.ACTION_SLIDE_DEVICE,
                new { deviceId, dimmvalue = brightness.Value }, cancellationToken: cancellationToken);
        }
    }

    public async Task SetClimateAsync(int roomId, ClimateCommand command, CancellationToken cancellationToken = default)
    {
        await _climateCommands.WaitAsync(cancellationToken);
        try
        {
            var room = _telemetry.GetClimate(roomId)
                ?? throw new InvalidOperationException("Only a verified climate-capable room can be controlled.");
            foreach (var payload in command.Build(room))
                await GetPump().SendMessageAsync(Messages.SET_HEATING_STATE, payload, cancellationToken: cancellationToken);
        }
        finally
        {
            _climateCommands.Release();
        }
    }

    private MessagePump GetPump() =>
        _messagePump ?? throw new InvalidOperationException("Bridge is not connected.");

    public async ValueTask DisposeAsync()
    {
        if (_messagePump is not null)
        {
            await _messagePump.DisposeAsync();
        }
        _messageSubscription?.Dispose();
        _messages.Dispose();
        _lightChanges.Dispose();
        _allLightChanges.Dispose();
        _telemetryChanges.Dispose();
        _climateCommands.Dispose();
    }
}
