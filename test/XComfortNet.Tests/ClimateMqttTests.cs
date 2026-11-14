using System.Text.Json;
using XComfortDotNet;
using XComfortDotNet.Model;
using XComfortNet.Web;

namespace XComfortNet.Tests;

public class ClimateMqttTests
{
    private static readonly XComfortClimate Room = new(7, "Test room", 20, 45, 21, 3, 2, 2, 0, 0);
    private readonly LightMqttProtocol _topics = new("test-xcomfort");

    [Fact]
    public void ClimateControlUsesReportedStateAndSafeHeatingTopics()
    {
        var protocol = new TelemetryMqttProtocol(_topics);
        var telemetry = new XComfortTelemetry([Room], []);
        var documents = protocol.Discoveries("bridge-1", [], telemetry);
        using var document = JsonDocument.Parse(Assert.Single(documents).Value);
        var climate = document.RootElement.GetProperty("components").GetProperty("climate");
        Assert.Equal("climate", climate.GetProperty("platform").GetString());
        Assert.False(climate.GetProperty("optimistic").GetBoolean());
        Assert.False(climate.GetProperty("retain").GetBoolean());
        Assert.Equal(_topics.ClimateCommandTopic("bridge-1", 7, "temperature"),
            climate.GetProperty("temperature_command_topic").GetString());
        Assert.Equal(_topics.ClimateStateTopic("bridge-1", 7), climate.GetProperty("mode_state_topic").GetString());
        Assert.Equal(["off", "heat"], climate.GetProperty("modes").EnumerateArray().Select(value => value.GetString()));
        using var state = JsonDocument.Parse(Assert.Single(protocol.States("bridge-1", telemetry)).Value);
        Assert.Equal("heat", state.RootElement.GetProperty("mode").GetString());
        Assert.Equal("comfort", state.RootElement.GetProperty("preset").GetString());
        Assert.Equal(21, state.RootElement.GetProperty("setpoint").GetDouble());
    }

    [Fact]
    public void DiagnosticAndMeasurementSensorsSharePhysicalDevicesWithExistingLights()
    {
        var protocol = new TelemetryMqttProtocol(_topics);
        var light = new XComfortLight(1, 4, "Light", true, false, 0, 0);
        var sensors = new[]
        {
            new XComfortSensor("comp_4_battery", "comp_4", "Device", "xComfort", "Battery", "battery", "%", "measurement", true, 75d),
            new XComfortSensor("device_1_temperature", "comp_4", "Device", "xComfort", "Temperature", "temperature", "\u00b0C", "measurement", false, null)
        };
        var telemetry = new XComfortTelemetry([], sensors);
        var documents = protocol.Discoveries("bridge-1", [light], telemetry);
        Assert.Equal(_topics.DiscoveryTopic("bridge-1", 4), Assert.Single(documents).Key);
        using var document = JsonDocument.Parse(Assert.Single(documents).Value);
        var components = document.RootElement.GetProperty("components");
        Assert.Equal(3, components.EnumerateObject().Count());
        Assert.Equal("test-xcomfort_bridge-1_1_light", components.GetProperty("light_1").GetProperty("unique_id").GetString());
        Assert.True(components.GetProperty("light_1").GetProperty("optimistic").GetBoolean());
        Assert.Equal("diagnostic", components.GetProperty("comp_4_battery").GetProperty("entity_category").GetString());
        Assert.False(components.GetProperty("device_1_temperature").TryGetProperty("entity_category", out _));
        using var state = JsonDocument.Parse(Assert.Single(protocol.States("bridge-1", telemetry)).Value);
        Assert.Equal(JsonValueKind.Null, state.RootElement.GetProperty("device_1_temperature").ValueKind);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(101)]
    public void LightDiscoveryRemovesOldPowerAndEnergyWithoutRemovingLightsOrDiagnostics(int deviceType)
    {
        var inventory = new TelemetryInventory();
        inventory.Apply(JsonSerializer.Deserialize<XComfortMessage>($$$"""
            {"type_int":300,"payload":{"devices":[{"deviceId":1,"compId":4,"devType":{{{deviceType}}},
             "usage":0,"power":0,"info":[{"text":"1109","value":"28.5"}]}],"lastItem":true}}
            """)!);
        var tracker = new EnergyTracker(Path.Combine(AppContext.BaseDirectory, $"absent-{Guid.NewGuid():N}.json"), TimeProvider.System);
        tracker.Observe("bridge-1", inventory.Snapshot, true);
        var telemetry = tracker.AppendSensors("bridge-1", inventory.Snapshot);
        Assert.DoesNotContain(telemetry.Sensors, sensor => sensor.DeviceClass is "power" or "energy");
        var light = new XComfortLight(1, 4, "Light", deviceType == 101, false, 0, 0);
        var protocol = new TelemetryMqttProtocol(_topics);
        var discoveries = protocol.Discoveries("bridge-1", [light], telemetry);
        using var normal = JsonDocument.Parse(Assert.Single(discoveries).Value);
        var components = normal.RootElement.GetProperty("components");
        Assert.True(components.TryGetProperty("light_1", out _));
        Assert.True(components.TryGetProperty("device_1_device_temperature", out _));
        Assert.False(components.TryGetProperty("device_1_power", out _));
        Assert.False(components.TryGetProperty("device_1_power_energy", out _));
        using var removal = JsonDocument.Parse(Assert.Single(
            protocol.LightPowerRemovalDiscoveries("bridge-1", [light], discoveries)).Value);
        var removals = removal.RootElement.GetProperty("components");
        foreach (var key in new[] { "device_1_power", "device_1_power_energy" })
        {
            Assert.Equal("sensor", removals.GetProperty(key).GetProperty("platform").GetString());
            Assert.Single(removals.GetProperty(key).EnumerateObject());
        }
        Assert.True(removals.TryGetProperty("light_1", out _));
        Assert.True(removals.TryGetProperty("device_1_device_temperature", out _));
    }

    [Fact]
    public void DiscoveryReconciliationExplicitlyRemovesEntitiesMissingFromReplacementDocument()
    {
        var protocol = new TelemetryMqttProtocol(_topics);
        var light = new XComfortLight(1, 4, "Light", true, false, 0, 0);
        var oldTelemetry = new XComfortTelemetry([], [
            new XComfortSensor("comp_4_battery", "comp_4", "Actuator", "xComfort", "Battery",
                "battery", "%", "measurement", true, 75d)
        ]);
        var previous = Assert.Single(protocol.Discoveries("bridge-1", [light], oldTelemetry)).Value;
        var current = Assert.Single(protocol.Discoveries("bridge-1", [light], XComfortTelemetry.Empty)).Value;

        var removal = TelemetryMqttProtocol.RemovedComponentsUpdate(previous, current);
        Assert.NotNull(removal);
        using var document = JsonDocument.Parse(removal);
        var components = document.RootElement.GetProperty("components");
        Assert.True(components.TryGetProperty("light_1", out _));
        Assert.Equal("sensor", components.GetProperty("comp_4_battery").GetProperty("platform").GetString());
        Assert.Single(components.GetProperty("comp_4_battery").EnumerateObject());
    }

    [Fact]
    public async Task PublicationManifestSurvivesRestartWithDiscoveryAndStateTopics()
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"mqtt-manifest-{Guid.NewGuid():N}.json");
        try
        {
            var manifest = new MqttPublicationManifest
            {
                Discoveries = new Dictionary<string, string> { ["homeassistant/device/test/config"] = "{}" },
                States = new Dictionary<string, string> { ["xcomfort2mqtt/test/state"] = "{}" }
            };
            await manifest.SaveAsync(path);

            var restored = MqttPublicationManifest.Load(path);

            Assert.Equal(manifest.Discoveries, restored.Discoveries);
            Assert.Equal(manifest.States, restored.States);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".tmp");
        }
    }

    [Fact]
    public void InvalidPublicationManifestFailsInsteadOfSilentlyForgettingRetainedTopics()
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"invalid-mqtt-manifest-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """{"Discoveries":null,"States":{}}""");
            Assert.Throws<InvalidDataException>(() => MqttPublicationManifest.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("mode", "off")]
    [InlineData("mode", "heat")]
    [InlineData("temperature", "22.5")]
    [InlineData("preset", "eco")]
    public void ParsesOnlyCommandsForVerifiedRooms(string kind, string value)
    {
        var protocol = new TelemetryMqttProtocol(_topics);
        var topic = _topics.ClimateCommandTopic("bridge-1", Room.RoomId, kind);
        Assert.True(protocol.TryParseCommand(topic, "bridge-1", [Room], value, false, out var roomId, out var command));
        Assert.Equal(Room.RoomId, roomId);
        Assert.NotEmpty(command!.Build(Room));
        Assert.False(protocol.TryParseCommand(topic, "bridge-1", [Room], value, true, out _, out _));
        Assert.False(protocol.TryParseCommand(topic, "another-bridge", [Room], value, false, out _, out _));
        Assert.False(protocol.TryParseCommand(topic, "bridge-1", [], value, false, out _, out _));
    }

    [Theory]
    [InlineData("mode", "cool")]
    [InlineData("temperature", "17")]
    [InlineData("temperature", "41")]
    [InlineData("temperature", "NaN")]
    [InlineData("temperature", "Infinity")]
    [InlineData("temperature", "{\"roomId\":8,\"setpoint\":22}")]
    [InlineData("preset", "boost")]
    public void RejectsUnsupportedOrUnsafeCommands(string kind, string value)
    {
        var protocol = new TelemetryMqttProtocol(_topics);
        Assert.False(protocol.TryParseCommand(_topics.ClimateCommandTopic("bridge-1", 7, kind),
            "bridge-1", [Room], value, false, out _, out _));
    }

    [Fact]
    public void ModeAndSetpointPayloadsMatchPythonWithoutInventingState()
    {
        var off = Assert.Single(new ClimateCommand(ClimateCommandKind.Mode, "off").Build(Room));
        Assert.Equal(new HeatingPayload(7, 0, true), off);
        var on = Assert.Single(new ClimateCommand(ClimateCommandKind.Mode, "heat").Build(Room with { HeatingState = 0 }));
        Assert.Equal(new HeatingPayload(7, 2, true), on);
        var temperature = Assert.Single(new ClimateCommand(ClimateCommandKind.Temperature, "22.5").Build(Room));
        Assert.Equal(new HeatingPayload(7, 2, false, 3, 22.5), temperature);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(off));
        Assert.False(json.RootElement.TryGetProperty("mode", out _));
        Assert.False(json.RootElement.TryGetProperty("setpoint", out _));
        Assert.Equal(7, json.RootElement.GetProperty("roomId").GetInt32());
    }

    [Fact]
    public void PresetChangeWakesThenSwitchesToManualAndChangesPresetWithoutDefaultSetpoints()
    {
        var commands = new ClimateCommand(ClimateCommandKind.Preset, "eco").Build(Room with { HeatingState = 0 });
        Assert.Equal(new[] { new HeatingPayload(7, 2, true), new HeatingPayload(7, 2, false, 3), new HeatingPayload(7, 2, false, 2) }, commands);
        Assert.All(commands, command => Assert.Null(command.Setpoint));
        Assert.Empty(new ClimateCommand(ClimateCommandKind.Preset, "comfort").Build(Room));
        Assert.Throws<InvalidOperationException>(() => new ClimateCommand(ClimateCommandKind.Temperature, "22").Build(Room with { Preset = null }));
        Assert.Throws<InvalidOperationException>(() => new ClimateCommand(ClimateCommandKind.Preset, "eco").Build(Room with { HeatingState = 5 }));
    }
}
