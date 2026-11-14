using System.Globalization;
using System.Text.Json;
using XComfortDotNet.Model;

namespace XComfortDotNet;

public sealed record XComfortSensor(
    string Key, string OwnerId, string OwnerName, string Model, string Name,
    string? DeviceClass, string? Unit, string? StateClass, bool Diagnostic, object? Value);

public sealed record XComfortClimate(
    int RoomId, string Name, double? Temperature, double? Humidity, double? Setpoint,
    int? Preset, int? HeatingState, int LastHeatingState, double? Power, double? Valve)
{
    public double MinTemperature => Preset switch { 1 => 5, 2 => 10, 3 => 18, _ => 5 };
    public double MaxTemperature => Preset switch { 1 => 20, 2 => 30, _ => 40 };
    public string? HvacMode => HeatingState switch { 0 => "off", 1 or 2 => "heat", 3 or 4 => "cool", _ => null };
    public string? PresetName => Preset switch { 1 => "Frost Protection", 2 => "eco", 3 => "comfort", _ => null };
    public string? Action => HeatingState switch
    {
        0 => "off",
        1 or 2 => Power > 0 || Valve > 0 ? "heating" : Power is not null || Valve is not null ? "idle" : null,
        3 or 4 => Power > 0 || Valve > 0 ? "cooling" : Power is not null || Valve is not null ? "idle" : null,
        _ => null
    };
}

public sealed record XComfortTelemetry(IReadOnlyList<XComfortClimate> Climates, IReadOnlyList<XComfortSensor> Sensors)
{
    public static XComfortTelemetry Empty { get; } = new([], []);
}

public sealed class TelemetryInventory
{
    private readonly object _gate = new();
    private readonly Dictionary<int, Entity> _devices = [];
    private readonly Dictionary<int, Entity> _components = [];
    private readonly Dictionary<int, Entity> _rooms = [];
    private readonly Entity _bridge = new();
    private bool _initialized;

    public void SetBridgeInfo(string bridgeId, string? firmware, string host)
    {
        lock (_gate)
        {
            _bridge.Fields["bridge_id"] = JsonSerializer.SerializeToElement(bridgeId);
            _bridge.Fields["firmware"] = JsonSerializer.SerializeToElement(firmware);
            _bridge.Fields["host"] = JsonSerializer.SerializeToElement(host);
        }
    }

    public XComfortTelemetry Snapshot
    {
        get
        {
            lock (_gate)
            {
                if (!_initialized) return XComfortTelemetry.Empty;
                var climates = _rooms.OrderBy(pair => pair.Key)
                    .Where(pair => pair.Value.Boolean("temperatureOnly") == false)
                    .Select(pair => Climate(pair.Key, pair.Value)).ToArray();
                return new XComfortTelemetry(climates, Sensors().OrderBy(sensor => sensor.Key).ToArray());
            }
        }
    }

    public XComfortClimate? GetClimate(int roomId)
    {
        lock (_gate)
        {
            return _initialized && _rooms.TryGetValue(roomId, out var room) &&
                room.Boolean("temperatureOnly") == false ? Climate(roomId, room) : null;
        }
    }

    public void Apply(XComfortMessage message)
    {
        if (message.AdditionalData?.TryGetValue("payload", out var payload) != true ||
            payload.ValueKind != JsonValueKind.Object) return;
        lock (_gate)
        {
            switch (message.TypeId)
            {
                case Messages.SET_ALL_DATA:
                    ApplyArray(payload, "comps", _components, "compId");
                    ApplyArray(payload, "devices", _devices, "deviceId");
                    ApplyArray(payload, "rooms", _rooms, "roomId");
                    ApplyArray(payload, "roomHeating", _rooms, "roomId");
                    if (payload.TryGetProperty("lastItem", out var last) && last.ValueKind == JsonValueKind.True)
                        _initialized = true;
                    break;
                case Messages.SET_DEVICE_STATE:
                case Messages.SET_DEVICE_INFO:
                    Update(_devices, "deviceId", payload);
                    break;
                case Messages.SET_ROOM_STATE:
                case Messages.SET_ROOM_INFO:
                case Messages.SET_ROOM_HEATING_STATE:
                case Messages.SET_HEATING_STATE:
                    Update(_rooms, "roomId", payload);
                    break;
                case Messages.ADD_COMP:
                case Messages.SET_COMP_INFO:
                    Update(_components, "compId", payload);
                    break;
                case Messages.SET_STATE_INFO:
                    if (payload.TryGetProperty("item", out var items) && items.ValueKind == JsonValueKind.Array)
                        foreach (var item in items.EnumerateArray())
                        {
                            if (item.TryGetProperty("deviceId", out _)) Update(_devices, "deviceId", item);
                            else if (item.TryGetProperty("roomId", out _)) Update(_rooms, "roomId", item);
                            else Update(_components, "compId", item);
                        }
                    break;
                case Messages.DEVICE_DELETED:
                    Remove(_devices, "deviceId", payload);
                    break;
                case Messages.ROOM_DELETED:
                    Remove(_rooms, "roomId", payload);
                    break;
                case Messages.COMP_DELETED:
                    Remove(_components, "compId", payload);
                    if (payload.TryGetProperty("compId", out var compId) && compId.TryGetInt32(out var id))
                        foreach (var deviceId in _devices.Where(pair => pair.Value.Integer("compId") == id)
                            .Select(pair => pair.Key).ToArray()) _devices.Remove(deviceId);
                    break;
                case Messages.SET_HOME_DATA:
                    foreach (var field in new[] { "name", "bridgeType", "remoteOnline", "remoteAllowed" })
                        if (payload.TryGetProperty(field, out var value)) _bridge.Fields[field] = value.Clone();
                    break;
            }
        }
    }

    private XComfortClimate Climate(int id, Entity room)
    {
        var (temperature, humidity) = RoomReadings(room);
        return new XComfortClimate(id, room.Text("name") ?? $"Room {id}", temperature, humidity,
            room.Number("setpoint"), room.Integer("mode"), room.Integer("state"),
            room.LastHeatingState, room.Number("power"), room.Number("valve"));
    }

    private (double? Temperature, double? Humidity) RoomReadings(Entity room)
    {
        _devices.TryGetValue(room.Integer("roomSensorId") ?? -1, out var sensor);
        return (Temperature(sensor?.InfoNumber("1222") ?? sensor?.InfoNumber("1224") ?? room.Number("temp")),
            sensor?.InfoNumber("1223") ?? room.Number("humidity"));
    }

    private bool HasRoomTemperature(Entity room)
    {
        _devices.TryGetValue(room.Integer("roomSensorId") ?? -1, out var sensor);
        return room.Has("temp") || sensor?.HasInfo("1222") == true || sensor?.HasInfo("1224") == true;
    }

    private bool HasRoomHumidity(Entity room)
    {
        _devices.TryGetValue(room.Integer("roomSensorId") ?? -1, out var sensor);
        return room.Has("humidity") || sensor?.HasInfo("1223") == true;
    }

    private IEnumerable<XComfortSensor> Sensors()
    {
        var sensors = new List<XComfortSensor>();
        foreach (var (id, device) in _devices.OrderBy(pair => pair.Key))
        {
            var compId = device.Integer("compId");
            _components.TryGetValue(compId ?? -1, out var component);
            var owner = compId is > 0 ? $"comp_{compId}" : $"device_{id}";
            var ownerName = component?.Text("name") ?? device.Text("name") ?? $"Device {id}";
            var type = device.Integer("devType");
            var model = type switch
            {
                440 or 442 => "xComfort Heating Actuator",
                441 => "xComfort Heating Valve",
                450 => "xComfort RC Touch",
                410 or 451 => "xComfort Temperature Sensor",
                _ => "xComfort"
            };
            var prefix = $"device_{id}";
            void Add(string key, string name, double? value, string? deviceClass, string? unit, bool diagnostic = false) =>
                sensors.Add(new XComfortSensor($"{prefix}_{key}", owner, ownerName, model,
                    name, deviceClass, unit, "measurement", diagnostic, value));
            if (device.HasInfo("1222") || device.HasInfo("1224"))
                Add("temperature", "Temperature", Temperature(device.InfoNumber("1222") ?? device.InfoNumber("1224")), "temperature", "\u00b0C");
            if (device.HasInfo("1223")) Add("humidity", "Humidity", device.InfoNumber("1223"), "humidity", "%");
            if (device.HasInfo("1109"))
                Add("device_temperature", "Device temperature", Temperature(device.InfoNumber("1109")), "temperature", "\u00b0C", true);
            if (type is 440 or 441 or 442 && (device.Has("dimmvalue") || device.HasInfo("1225")))
                Add(type == 441 ? "valve_position" : "heating_demand",
                    type == 441 ? "Valve position" : "Heating demand",
                    device.Number("dimmvalue") ?? device.InfoNumber("1225"), null, "%");
            if (type is not (100 or 101) &&
                (device.Has("power") || device.HasInfo("1110") || device.HasInfo("1226")))
            {
                var power = device.Number("power") ?? device.InfoNumber("1110") ?? device.InfoNumber("1226");
                Add("power", "Power", power, "power", "W");
            }
        }
        foreach (var (id, room) in _rooms.OrderBy(pair => pair.Key))
        {
            var owner = $"room_{id}";
            var name = room.Text("name") ?? $"Room {id}";
            var readings = RoomReadings(room);
            void Add(string key, string label, double? value, string? deviceClass, string? unit) =>
                sensors.Add(new XComfortSensor($"{owner}_{key}", owner, name, "xComfort Room",
                    label, deviceClass, unit, "measurement", false, value));
            if (HasRoomTemperature(room)) Add("temperature", "Temperature", readings.Temperature, "temperature", "\u00b0C");
            if (HasRoomHumidity(room)) Add("humidity", "Humidity", readings.Humidity, "humidity", "%");
            if (room.Has("power")) Add("power", "Power", room.Number("power"), "power", "W");
            if (room.Has("valve")) Add("heating_demand", "Heating demand", room.Number("valve"), null, "%");
        }
        foreach (var (id, component) in _components.OrderBy(pair => pair.Key))
        {
            var owner = $"comp_{id}";
            var name = component.Text("name") ?? _devices.Values.FirstOrDefault(device => device.Integer("compId") == id)?.Text("name") ?? $"Component {id}";
            void Add(string key, string label, object? value, string? deviceClass = null, string? unit = null) =>
                sensors.Add(new XComfortSensor($"{owner}_{key}", owner, name, "xComfort",
                    label, deviceClass, unit, unit is null ? null : "measurement", true, value));
            if (component.HasInfo("1111"))
                Add("signal_quality", "Signal quality", component.InfoNumber("1111") switch
                {
                    0 => "Unknown",
                    1 => "Excellent",
                    2 => "Good",
                    3 => "Fair",
                    4 => "Poor",
                    _ => null
                });
            if (component.HasInfo("1108"))
                Add("signal_strength", "Signal strength", component.InfoNumber("1108") is double dbm ? -Math.Abs(dbm) : null, "signal_strength", "dBm");
            if (!component.HasInfo("1119") && Enumerable.Range(1113, 6).Any(code => component.HasInfo(code.ToString(CultureInfo.InvariantCulture))))
                Add("battery", "Battery", Enumerable.Range(1113, 5).Where(code => component.HasInfo(code.ToString(CultureInfo.InvariantCulture)))
                    .Select(code => (double?)((code - 1113) * 25)).FirstOrDefault(), "battery", "%");
        }
        var bridgeName = _bridge.Text("name") ?? "xComfort Bridge";
        foreach (var (field, label) in new[] { ("bridge_id", "Bridge ID"), ("firmware", "Firmware version"), ("host", "IP address"), ("name", "Bridge name"), ("bridgeType", "Bridge model") })
            if (_bridge.Has(field))
                sensors.Add(new XComfortSensor($"bridge_{field}", "bridge", bridgeName, "xComfort Bridge",
                    label, null, null, null, true, _bridge.Text(field)));
        foreach (var (field, label) in new[] { ("remoteOnline", "Remote connection"), ("remoteAllowed", "Remote access allowed") })
            if (_bridge.Has(field))
                sensors.Add(new XComfortSensor($"bridge_{field}", "bridge", bridgeName, "xComfort Bridge",
                    label, null, null, null, true, _bridge.Boolean(field) is bool value ? value ? "on" : "off" : null));
        return sensors;
    }

    private static double? Temperature(double? value) => value == -100 ? null : value;

    private static void ApplyArray(JsonElement payload, string key, Dictionary<int, Entity> entities, string idKey)
    {
        if (payload.TryGetProperty(key, out var array) && array.ValueKind == JsonValueKind.Array)
            foreach (var item in array.EnumerateArray()) Update(entities, idKey, item);
    }

    private static void Remove(Dictionary<int, Entity> entities, string idKey, JsonElement payload)
    {
        if (payload.TryGetProperty(idKey, out var id) && id.TryGetInt32(out var number)) entities.Remove(number);
    }

    private static void Update(Dictionary<int, Entity> entities, string idKey, JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty(idKey, out var id) || !id.TryGetInt32(out var number)) return;
        if (!entities.TryGetValue(number, out var entity)) entities[number] = entity = new Entity();
        foreach (var field in payload.EnumerateObject())
        {
            if (field.Name == "info" && field.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in field.Value.EnumerateArray())
                {
                    if (!item.TryGetProperty("text", out var text)) continue;
                    var code = text.ToString();
                    if (int.TryParse(code, out var battery) && battery is >= 1113 and <= 1119)
                        foreach (var old in Enumerable.Range(1113, 7)) entity.Info.Remove(old.ToString(CultureInfo.InvariantCulture));
                    entity.Info[code] = item.TryGetProperty("value", out var value) ? value.Clone() : default;
                    if ((code is "1110" or "1226") && !payload.TryGetProperty("power", out _))
                        entity.Fields["power"] = entity.Info[code];
                    if (code == "1225" && !payload.TryGetProperty("dimmvalue", out _))
                        entity.Fields["dimmvalue"] = entity.Info[code];
                }
            }
            else entity.Fields[field.Name] = field.Value.Clone();
        }
        if (!payload.TryGetProperty("mode", out _) && payload.TryGetProperty("currentMode", out var mode))
            entity.Fields["mode"] = mode.Clone();
        if (entity.Integer("state") is 1 or 2) entity.LastHeatingState = entity.Integer("state")!.Value;
    }

    private sealed class Entity
    {
        public Dictionary<string, JsonElement> Fields { get; } = [];
        public Dictionary<string, JsonElement> Info { get; } = [];
        public int LastHeatingState { get; set; } = 1;
        public bool Has(string key) => Fields.ContainsKey(key);
        public bool HasInfo(string key) => Info.ContainsKey(key);
        public string? Text(string key) => Fields.TryGetValue(key, out var value) && value.ValueKind != JsonValueKind.Null ? value.ToString() : null;
        public double? Number(string key) => Fields.TryGetValue(key, out var value) ? ReadNumber(value) : null;
        public double? InfoNumber(string key) => Info.TryGetValue(key, out var value) ? ReadNumber(value) : null;
        public int? Integer(string key) => Number(key) is double value && value == Math.Truncate(value) && value is >= int.MinValue and <= int.MaxValue ? (int)value : null;
        public bool? Boolean(string key) => Fields.TryGetValue(key, out var value) ? value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when value.TryGetInt32(out var number) && number is 0 or 1 => number == 1,
            _ => null
        } : null;
        private static double? ReadNumber(JsonElement value) =>
            double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : null;
    }
}
