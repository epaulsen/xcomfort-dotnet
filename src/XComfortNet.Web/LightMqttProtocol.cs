using System.Text.Json;
using System.Text.RegularExpressions;
using XComfortDotNet;

namespace XComfortNet.Web;

internal sealed record LightCommand(bool? IsOn, int? Brightness);

internal sealed class LightMqttProtocol(string prefix)
{
    public string ServiceAvailabilityTopic => $"{prefix}/availability";
    public string CommandFilter => $"{prefix}/+/device/+/set";
    public string BridgeAvailabilityTopic(string bridgeId) => $"{prefix}/{Id(bridgeId)}/availability";
    public string CommandTopic(string bridgeId, int deviceId) => $"{prefix}/{Id(bridgeId)}/device/{deviceId}/set";
    public string StateTopic(string bridgeId, int deviceId) => $"{prefix}/{Id(bridgeId)}/device/{deviceId}/state";
    public string LegacyDiscoveryTopic(string bridgeId) => $"homeassistant/device/{prefix}_{Id(bridgeId)}_first_light/config";
    public string DiscoveryTopic(string bridgeId, int componentId) =>
        $"homeassistant/device/{prefix}_{Id(bridgeId)}_comp_{componentId}/config";

    public string Discovery(string bridgeId, IReadOnlyList<XComfortLight> lights)
    {
        var first = lights.First();
        var components = new Dictionary<string, object>();
        foreach (var light in lights)
        {
            if (light.ComponentId != first.ComponentId)
            {
                throw new ArgumentException("Discovery must contain channels from one physical component.", nameof(lights));
            }
            var entity = new Dictionary<string, object?>
            {
                ["platform"] = "light",
                ["schema"] = "json",
                ["optimistic"] = true,
                ["name"] = lights.Count == 1 ? null : light.Name,
                ["unique_id"] = $"{prefix}_{Id(bridgeId)}_{light.DeviceId}_light",
                ["state_topic"] = StateTopic(bridgeId, light.DeviceId),
                ["command_topic"] = CommandTopic(bridgeId, light.DeviceId),
                ["supported_color_modes"] = new[] { light.Dimmable ? "brightness" : "onoff" },
                ["brightness"] = light.Dimmable
            };
            if (light.Dimmable)
            {
                entity["brightness_scale"] = 99;
            }
            components[$"light_{light.DeviceId}"] = entity;
        }

        return JsonSerializer.Serialize(new
        {
            device = new
            {
                identifiers = new[] { $"{prefix}_{Id(bridgeId)}_comp_{first.ComponentId}" },
                name = lights.Count == 1 ? first.Name : $"xComfort {first.ComponentId}",
                manufacturer = "Eaton",
                model = lights.Count == 1 ? first.Dimmable ? "xComfort Dimmer" : "xComfort Switch" : "xComfort Actuator"
            },
            origin = new { name = "Xcomfort2Mqtt" },
            availability = new[]
            {
                new { topic = ServiceAvailabilityTopic },
                new { topic = BridgeAvailabilityTopic(bridgeId) }
            },
            availability_mode = "all",
            components
        });
    }

    public static string? State(XComfortLight light)
    {
        if (light.IsOn is null)
        {
            return null;
        }
        var state = new Dictionary<string, object> { ["state"] = light.IsOn.Value ? "ON" : "OFF" };
        if (light.Dimmable && light.Brightness is int brightness)
        {
            state["brightness"] = brightness;
        }
        return JsonSerializer.Serialize(state);
    }

    public bool TryParseCommand(string topic, string bridgeId, XComfortLight light, string payload,
        bool retained, out LightCommand? command)
    {
        command = null;
        if (topic != CommandTopic(bridgeId, light.DeviceId) || retained || payload.Length > 1024)
        {
            return false;
        }
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }
            bool? isOn = null;
            int? brightness = null;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "state":
                        if (property.Value.ValueKind != JsonValueKind.String ||
                            property.Value.GetString() is not ("ON" or "OFF"))
                        {
                            return false;
                        }
                        isOn = property.Value.GetString() == "ON";
                        break;
                    case "brightness":
                        if (!light.Dimmable || property.Value.ValueKind != JsonValueKind.Number ||
                            !property.Value.TryGetInt32(out var value) || value is < 0 or > 99)
                        {
                            return false;
                        }
                        brightness = value;
                        break;
                    default:
                        return false;
                }
            }
            if (isOn is null && brightness is null)
            {
                return false;
            }
            command = new LightCommand(isOn, brightness);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string Id(string value) => Regex.Replace(value.ToLowerInvariant(), "[^a-z0-9_-]", "_");
}
