using System.Text.Json;
using XComfortDotNet;
using XComfortNet.Web;

namespace XComfortNet.Tests;

public class LightMqttProtocolTests
{
    private readonly LightMqttProtocol _protocol = new("test-xcomfort");
    private static readonly XComfortLight Light = new(12, 4, "Hall", true, true, 45, 10);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DiscoveryExposesExactlyOneLightWithStableIdentityAndAvailability(bool dimmable)
    {
        using var discovery = JsonDocument.Parse(_protocol.Discovery("bridge-1", [Light with { Dimmable = dimmable }]));
        var root = discovery.RootElement;
        var components = root.GetProperty("components").EnumerateObject().ToArray();
        Assert.Single(components);
        var light = components[0].Value;
        Assert.Equal("light", light.GetProperty("platform").GetString());
        Assert.True(light.GetProperty("optimistic").GetBoolean());
        Assert.Equal(_protocol.StateTopic("bridge-1", Light.DeviceId),
            light.GetProperty("state_topic").GetString());
        Assert.Equal("test-xcomfort_bridge-1_12_light", light.GetProperty("unique_id").GetString());
        Assert.Equal(dimmable, light.GetProperty("brightness").GetBoolean());
        Assert.Equal(dimmable, light.TryGetProperty("brightness_scale", out _));
        Assert.Equal("all", root.GetProperty("availability_mode").GetString());
        Assert.Equal(2, root.GetProperty("availability").GetArrayLength());

        using var renamed = JsonDocument.Parse(_protocol.Discovery("bridge-1", [Light with { Name = "Renamed" }]));
        Assert.Equal(light.GetProperty("unique_id").GetString(),
            renamed.RootElement.GetProperty("components").GetProperty("light_12").GetProperty("unique_id").GetString());
    }

    [Fact]
    public void GroupsChannelsOnOnePhysicalDeviceWithIndependentStateAndCommands()
    {
        var second = Light with { DeviceId = 13, Name = "Kitchen", Dimmable = false };
        using var discovery = JsonDocument.Parse(_protocol.Discovery("bridge-1", [Light, second]));
        var components = discovery.RootElement.GetProperty("components");
        Assert.Equal(2, components.EnumerateObject().Count());
        var firstChannel = components.GetProperty("light_12");
        var secondChannel = components.GetProperty("light_13");
        Assert.Equal("Hall", firstChannel.GetProperty("name").GetString());
        Assert.Equal("Kitchen", secondChannel.GetProperty("name").GetString());
        Assert.Equal(_protocol.CommandTopic("bridge-1", 12), firstChannel.GetProperty("command_topic").GetString());
        Assert.Equal(_protocol.CommandTopic("bridge-1", 13), secondChannel.GetProperty("command_topic").GetString());
        Assert.NotEqual(firstChannel.GetProperty("state_topic").GetString(),
            secondChannel.GetProperty("state_topic").GetString());
        Assert.False(secondChannel.GetProperty("brightness").GetBoolean());
        Assert.True(_protocol.TryParseCommand(_protocol.CommandTopic("bridge-1", 13), "bridge-1",
            second, """{"state":"OFF"}""", false, out var command));
        Assert.Equal(new LightCommand(false, null), command);
        Assert.False(_protocol.TryParseCommand(_protocol.CommandTopic("bridge-1", 12), "bridge-1",
            second, """{"state":"ON"}""", false, out _));
    }

    [Fact]
    public void DoesNotCombineDifferentPhysicalComponentsInOneDiscoveryDocument()
    {
        Assert.Throws<ArgumentException>(() => _protocol.Discovery("bridge-1",
            [Light, Light with { DeviceId = 13, ComponentId = 99 }]));
        Assert.NotEqual(_protocol.DiscoveryTopic("bridge-1", 4), _protocol.DiscoveryTopic("bridge-1", 99));
    }

    [Theory]
    [InlineData("""{"state":"ON","brightness":42}""", true, 42)]
    [InlineData("""{"state":"OFF"}""", false, null)]
    [InlineData("""{"brightness":0}""", null, 0)]
    public void ParsesSupportedCommands(string payload, bool? isOn, int? brightness)
    {
        Assert.True(Parse(payload, out var command));
        Assert.Equal(new LightCommand(isOn, brightness), command);
    }

    [Theory]
    [InlineData("""{"brightness":100}""")]
    [InlineData("""{"brightness":-1}""")]
    [InlineData("""{"brightness":12.5}""")]
    [InlineData("""{"brightness":"20"}""")]
    [InlineData("""{"state":"unknown"}""")]
    [InlineData("""{"state":true}""")]
    [InlineData("""{"state":"ON","deviceId":900}""")]
    [InlineData("""{"state":"ON","temperature":25}""")]
    [InlineData("""{}""")]
    [InlineData("""null""")]
    [InlineData("""{"state":""")]
    public void RejectsUnsupportedOrMalformedCommands(string payload)
    {
        Assert.False(Parse(payload, out _));
    }

    [Fact]
    public void RejectsRetainedCommandsAndCommandsForAnotherDeviceOrBridge()
    {
        var topic = _protocol.CommandTopic("bridge-1", Light.DeviceId);
        Assert.False(_protocol.TryParseCommand(topic, "bridge-1", Light, """{"state":"ON"}""", true, out _));
        Assert.False(_protocol.TryParseCommand(_protocol.CommandTopic("bridge-1", 900), "bridge-1",
            Light, """{"state":"ON"}""", false, out _));
        Assert.False(_protocol.TryParseCommand(topic, "other-bridge", Light,
            """{"state":"ON"}""", false, out _));
    }

    [Fact]
    public void RejectsBrightnessForSwitchOnlyLights()
    {
        Assert.False(_protocol.TryParseCommand(_protocol.CommandTopic("bridge-1", Light.DeviceId),
            "bridge-1", Light with { Dimmable = false }, """{"state":"ON","brightness":50}""", false, out _));
    }

    [Fact]
    public void PublishesOnlyKnownBridgeState()
    {
        Assert.Null(LightMqttProtocol.State(Light with { IsOn = null }));
        using var state = JsonDocument.Parse(LightMqttProtocol.State(Light)!);
        Assert.Equal("ON", state.RootElement.GetProperty("state").GetString());
        Assert.Equal(45, state.RootElement.GetProperty("brightness").GetInt32());
        using var switchState = JsonDocument.Parse(LightMqttProtocol.State(Light with { Dimmable = false })!);
        Assert.False(switchState.RootElement.TryGetProperty("brightness", out _));
    }

    private bool Parse(string payload, out LightCommand? command) =>
        _protocol.TryParseCommand(_protocol.CommandTopic("bridge-1", Light.DeviceId),
            "bridge-1", Light, payload, false, out command);
}
