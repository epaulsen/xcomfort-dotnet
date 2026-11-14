using System.Text.Json;
using System.Text.Json.Nodes;
using XComfortDotNet;

namespace XComfortNet.Web;

internal sealed class TelemetryMqttProtocol(LightMqttProtocol protocol)
{
    public Dictionary<string, string> Discoveries(string bridgeId, IReadOnlyList<XComfortLight> lights,
        XComfortTelemetry telemetry)
    {
        var groups = new Dictionary<string, DeviceGroup>();
        foreach (var group in lights.GroupBy(light => light.ComponentId))
        {
            var channels = group.OrderBy(light => light.DeviceId).ToArray();
            var first = channels[0];
            groups[$"comp_{group.Key}"] = new DeviceGroup(
                channels.Length == 1 ? first.Name : $"xComfort {group.Key}",
                channels.Length == 1 ? first.Dimmable ? "xComfort Dimmer" : "xComfort Switch" : "xComfort Actuator",
                protocol.LightComponents(bridgeId, channels));
        }
        foreach (var sensor in telemetry.Sensors)
        {
            if (!groups.TryGetValue(sensor.OwnerId, out var group))
                groups[sensor.OwnerId] = group = new DeviceGroup(sensor.OwnerName, sensor.Model, []);
            if (group.Model == "xComfort" && sensor.Model != "xComfort") group.Model = sensor.Model;
            var component = new Dictionary<string, object?>
            {
                ["platform"] = "sensor",
                ["name"] = sensor.Name,
                ["unique_id"] = protocol.UniqueId(bridgeId, sensor.Key),
                ["state_topic"] = protocol.TelemetryStateTopic(bridgeId, sensor.OwnerId),
                ["value_template"] = Template(sensor.Key)
            };
            if (sensor.DeviceClass is not null) component["device_class"] = sensor.DeviceClass;
            if (sensor.Unit is not null) component["unit_of_measurement"] = sensor.Unit;
            if (sensor.StateClass is not null) component["state_class"] = sensor.StateClass;
            if (sensor.Diagnostic) component["entity_category"] = "diagnostic";
            group.Components[sensor.Key] = component;
        }
        foreach (var climate in telemetry.Climates)
        {
            var owner = $"room_{climate.RoomId}";
            if (!groups.TryGetValue(owner, out var group))
                groups[owner] = group = new DeviceGroup(climate.Name, "xComfort Room", []);
            var stateTopic = protocol.ClimateStateTopic(bridgeId, climate.RoomId);
            group.Components["climate"] = new Dictionary<string, object?>
            {
                ["platform"] = "climate",
                ["name"] = null,
                ["unique_id"] = protocol.UniqueId(bridgeId, $"{owner}_climate"),
                ["modes"] = new[] { "off", "heat" },
                ["mode_command_topic"] = protocol.ClimateCommandTopic(bridgeId, climate.RoomId, "mode"),
                ["mode_state_topic"] = stateTopic,
                ["mode_state_template"] = Template("mode"),
                ["temperature_command_topic"] = protocol.ClimateCommandTopic(bridgeId, climate.RoomId, "temperature"),
                ["temperature_state_topic"] = stateTopic,
                ["temperature_state_template"] = Template("setpoint"),
                ["current_temperature_topic"] = stateTopic,
                ["current_temperature_template"] = Template("temperature"),
                ["current_humidity_topic"] = stateTopic,
                ["current_humidity_template"] = Template("humidity"),
                ["preset_modes"] = new[] { "Frost Protection", "eco", "comfort" },
                ["preset_mode_command_topic"] = protocol.ClimateCommandTopic(bridgeId, climate.RoomId, "preset"),
                ["preset_mode_state_topic"] = stateTopic,
                ["preset_mode_value_template"] = Template("preset"),
                ["action_topic"] = stateTopic,
                ["action_template"] = Template("action"),
                ["temperature_unit"] = "C",
                ["min_temp"] = climate.MinTemperature,
                ["max_temp"] = climate.MaxTemperature,
                ["temp_step"] = 0.5,
                ["optimistic"] = false,
                ["retain"] = false
            };
        }
        return groups.ToDictionary(pair => protocol.OwnerDiscoveryTopic(bridgeId, pair.Key),
            pair => protocol.DeviceDiscovery(bridgeId, pair.Key, pair.Value.Name, pair.Value.Model, pair.Value.Components));
    }

    public Dictionary<string, string> LightPowerRemovalDiscoveries(string bridgeId,
        IReadOnlyList<XComfortLight> lights, IReadOnlyDictionary<string, string> discoveries)
    {
        return lights.GroupBy(light => light.ComponentId).ToDictionary(
            group => protocol.DiscoveryTopic(bridgeId, group.Key),
            group =>
            {
                var document = JsonNode.Parse(discoveries[protocol.DiscoveryTopic(bridgeId, group.Key)])!;
                var components = document["components"]!;
                // Home Assistant requires explicit empty components before they are omitted.
                foreach (var light in group)
                    foreach (var suffix in new[] { "power", "power_energy" })
                        components[$"device_{light.DeviceId}_{suffix}"] = new JsonObject { ["platform"] = "sensor" };
                return document.ToJsonString();
            });
    }

    public Dictionary<string, string> States(string bridgeId, XComfortTelemetry telemetry)
    {
        var states = telemetry.Sensors.GroupBy(sensor => sensor.OwnerId).ToDictionary(
            group => protocol.TelemetryStateTopic(bridgeId, group.Key),
            group => JsonSerializer.Serialize(group.ToDictionary(sensor => sensor.Key, sensor => sensor.Value)));
        foreach (var room in telemetry.Climates)
            states[protocol.ClimateStateTopic(bridgeId, room.RoomId)] = JsonSerializer.Serialize(new
            {
                mode = room.HvacMode,
                preset = room.PresetName,
                setpoint = room.Setpoint,
                temperature = room.Temperature,
                humidity = room.Humidity,
                action = room.Action
            });
        return states;
    }

    public bool TryParseCommand(string topic, string bridgeId, IReadOnlyList<XComfortClimate> rooms,
        string payload, bool retained, out int roomId, out ClimateCommand? command)
    {
        roomId = 0;
        command = null;
        if (retained || payload.Length > 128) return false;
        foreach (var room in rooms)
            foreach (var (name, kind) in new[]
            {
                ("mode", ClimateCommandKind.Mode), ("temperature", ClimateCommandKind.Temperature),
                ("preset", ClimateCommandKind.Preset)
            })
            {
                if (topic != protocol.ClimateCommandTopic(bridgeId, room.RoomId, name)) continue;
                var candidate = new ClimateCommand(kind, payload);
                try
                {
                    candidate.Build(room);
                    roomId = room.RoomId;
                    command = candidate;
                    return true;
                }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException)
                {
                    return false;
                }
            }
        return false;
    }

    private static string Template(string field) =>
        $"{{{{ value_json['{field}'] if value_json['{field}'] is not none else 'None' }}}}";

    private sealed class DeviceGroup(string name, string model, Dictionary<string, object> components)
    {
        public string Name { get; } = name;
        public string Model { get; set; } = model;
        public Dictionary<string, object> Components { get; } = components;
    }
}
