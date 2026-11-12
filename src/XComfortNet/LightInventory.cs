using System.Text.Json;
using XComfortDotNet.Model;

namespace XComfortDotNet;

public sealed record XComfortLight(
    int DeviceId, int ComponentId, string Name, bool Dimmable,
    bool? IsOn, int? Brightness, double? Power);

public sealed class LightInventory(int? preferredDeviceId = null)
{
    private readonly object _gate = new();
    private readonly Dictionary<int, Device> _devices = new();
    private readonly List<int> _deviceOrder = [];
    private int? _selectedId = preferredDeviceId;
    private bool _initialized;

    public bool IsInitialized
    {
        get { lock (_gate) { return _initialized; } }
    }

    public IReadOnlyList<XComfortLight> Lights
    {
        get
        {
            lock (_gate)
            {
                return !_initialized ? [] : _deviceOrder
                    .Where(id => _devices[id].IsLight)
                    .Select(id => ToLight(id, _devices[id]))
                    .ToArray();
            }
        }
    }

    public XComfortLight? GetLight(int deviceId)
    {
        lock (_gate)
        {
            return _initialized && _devices.TryGetValue(deviceId, out var device) && device.IsLight
                ? ToLight(deviceId, device) : null;
        }
    }

    public XComfortLight? FirstLight
    {
        get
        {
            lock (_gate)
            {
                if (!_initialized || _selectedId is not int id ||
                    !_devices.TryGetValue(id, out var device) || !device.IsLight)
                {
                    return null;
                }
                return ToLight(id, device);
            }
        }
    }

    public void Apply(XComfortMessage message)
    {
        if (message.AdditionalData?.TryGetValue("payload", out var payload) != true ||
            payload.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        lock (_gate)
        {
            switch (message.TypeId)
            {
                case Messages.SET_ALL_DATA:
                    if (payload.TryGetProperty("devices", out var devices))
                    {
                        foreach (var device in devices.EnumerateArray())
                        {
                            Update(device);
                        }
                    }
                    if (payload.TryGetProperty("lastItem", out var lastItem) &&
                        lastItem.ValueKind == JsonValueKind.True)
                    {
                        _initialized = true;
                    }
                    break;
                case Messages.SET_DEVICE_STATE:
                case Messages.SET_DEVICE_INFO:
                    Update(payload);
                    break;
                case Messages.SET_STATE_INFO:
                    if (payload.TryGetProperty("item", out var items))
                    {
                        foreach (var item in items.EnumerateArray())
                        {
                            Update(item);
                        }
                    }
                    break;
                case Messages.DEVICE_DELETED:
                    if (payload.TryGetProperty("deviceId", out var removed))
                    {
                        _devices.Remove(removed.GetInt32());
                        _deviceOrder.Remove(removed.GetInt32());
                    }
                    break;
            }

            if (_initialized && _selectedId is null)
            {
                _selectedId = _deviceOrder.FirstOrDefault(
                    id => _devices.TryGetValue(id, out var device) && device.IsLight, -1);
                if (_selectedId == -1)
                {
                    _selectedId = null;
                }
            }
        }
    }

    private static XComfortLight ToLight(int id, Device device) =>
        new(id, device.ComponentId, device.Name ?? $"Light {id}",
            device.Dimmable ?? device.Type == 101, device.IsOn, device.Brightness, device.Power);

    private void Update(JsonElement payload)
    {
        if (!payload.TryGetProperty("deviceId", out var idProperty))
        {
            return;
        }
        var id = idProperty.GetInt32();
        if (!_devices.TryGetValue(id, out var device))
        {
            device = new Device();
            _devices.Add(id, device);
            _deviceOrder.Add(id);
        }

        if (payload.TryGetProperty("devType", out var type)) device.Type = type.GetInt32();
        if (payload.TryGetProperty("usage", out var usage)) device.Usage = usage.GetInt32();
        if (payload.TryGetProperty("compId", out var component)) device.ComponentId = component.GetInt32();
        if (payload.TryGetProperty("name", out var name)) device.Name = name.GetString();
        if (payload.TryGetProperty("dimmable", out var dimmable)) device.Dimmable = ReadBoolean(dimmable);
        if (payload.TryGetProperty("switch", out var state)) device.IsOn = ReadBoolean(state);
        if (payload.TryGetProperty("dimmvalue", out var brightness)) device.Brightness = brightness.GetInt32();
        if (payload.TryGetProperty("power", out var power)) device.Power = power.GetDouble();
        else if (device.IsOn == false) device.Power = 0;
    }

    private static bool ReadBoolean(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number ? value.GetInt32() != 0 : value.GetBoolean();

    private sealed class Device
    {
        public int? Type { get; set; }
        public int? Usage { get; set; }
        public int ComponentId { get; set; }
        public string? Name { get; set; }
        public bool? Dimmable { get; set; }
        public bool? IsOn { get; set; }
        public int? Brightness { get; set; }
        public double? Power { get; set; }
        public bool IsLight => Type is 100 or 101 && Usage == 0;
    }
}
