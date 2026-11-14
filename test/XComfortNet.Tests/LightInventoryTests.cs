using System.Text.Json;
using XComfortDotNet;
using XComfortDotNet.Model;

namespace XComfortNet.Tests;

public class LightInventoryTests
{
    [Fact]
    public void SelectsFirstVerifiedLightInInventoryOrderAfterLastChunk()
    {
        var inventory = new LightInventory();
        inventory.Apply(Message(300, """
            {"devices":[
                {"deviceId":1,"devType":100,"usage":2},
                {"deviceId":2,"devType":101,"usage":1},
                {"deviceId":3,"devType":100},
                {"deviceId":4,"devType":200,"usage":0},
                {"deviceId":22,"compId":7,"devType":100,"usage":0,"name":"First light","switch":true},
                {"deviceId":11,"compId":8,"devType":101,"usage":0}
            ],"lastItem":false}
            """));

        Assert.False(inventory.IsInitialized);
        Assert.Null(inventory.FirstLight);
        Assert.Empty(inventory.Lights);
        Assert.Null(inventory.GetLight(22));
        inventory.Apply(Message(300, """{"lastItem":true}"""));

        Assert.Equal(22, inventory.FirstLight?.DeviceId);
        Assert.Equal(7, inventory.FirstLight?.ComponentId);
        Assert.False(inventory.FirstLight?.Dimmable);
        Assert.Equal([22, 11], inventory.Lights.Select(light => light.DeviceId));
        Assert.Equal(11, inventory.GetLight(11)?.DeviceId);
        foreach (var excludedId in new[] { 1, 2, 3, 4, 999 })
        {
            Assert.Null(inventory.GetLight(excludedId));
        }
    }

    [Fact]
    public void MergesPartialStateWithoutLosingUnchangedFields()
    {
        var inventory = Dimmer();
        inventory.Apply(Message(310, """{"item":[{"deviceId":5,"dimmvalue":37},{"roomId":9,"switch":false}]}"""));
        inventory.Apply(Message(291, """{"deviceId":5,"power":12.5}"""));

        Assert.Equal(new XComfortLight(5, 6, "Dimmer", true, true, 37, 12.5), inventory.FirstLight);

        inventory.Apply(Message(291, """{"deviceId":5,"switch":false}"""));
        Assert.False(inventory.FirstLight?.IsOn);
        Assert.Equal(37, inventory.FirstLight?.Brightness);
        Assert.Equal(0, inventory.FirstLight?.Power);
    }

    [Fact]
    public void DoesNotRetargetWhenSelectedLightIsDeletedOrReclassified()
    {
        var inventory = Dimmer();
        inventory.Apply(Message(292, """{"deviceId":7,"devType":100,"usage":0}"""));
        inventory.Apply(Message(292, """{"deviceId":5,"usage":2}"""));
        Assert.Null(inventory.FirstLight);
        Assert.Null(inventory.GetLight(5));
        Assert.Equal(7, Assert.Single(inventory.Lights).DeviceId);

        inventory.Apply(Message(296, """{"deviceId":5}"""));
        Assert.Null(inventory.FirstLight);
        Assert.Equal(7, Assert.Single(inventory.Lights).DeviceId);
    }

    [Fact]
    public void UpdatesEachLightIndependentlyAndDoesNotDuplicateReintroducedDevices()
    {
        var inventory = Dimmer();
        inventory.Apply(Message(292, """{"deviceId":7,"compId":8,"devType":100,"usage":0,"switch":false}"""));
        inventory.Apply(Message(291, """{"deviceId":7,"switch":true}"""));
        Assert.True(inventory.GetLight(7)?.IsOn);
        Assert.Equal(80, inventory.GetLight(5)?.Brightness);
        Assert.False(inventory.GetLight(7)?.Dimmable);

        inventory.Apply(Message(296, """{"deviceId":5}"""));
        Assert.Equal(7, Assert.Single(inventory.Lights).DeviceId);
        inventory.Apply(Message(292, """{"deviceId":5,"compId":6,"devType":101,"usage":0}"""));
        Assert.Equal([7, 5], inventory.Lights.Select(light => light.DeviceId));
    }

    [Fact]
    public void AllLightsRetainTheirOwnIdentityWhenReconnectInventoryOrderChanges()
    {
        var first = new LightInventory();
        var reconnect = new LightInventory();
        const string one = """{"deviceId":5,"compId":6,"devType":101,"usage":0}""";
        const string two = """{"deviceId":7,"compId":8,"devType":100,"usage":0}""";
        first.Apply(Message(300, $$"""{"devices":[{{one}},{{two}}],"lastItem":true}"""));
        reconnect.Apply(Message(300, $$"""{"devices":[{{two}},{{one}}],"lastItem":true}"""));
        Assert.Equal(first.Lights.OrderBy(light => light.DeviceId),
            reconnect.Lights.OrderBy(light => light.DeviceId));
    }

    [Fact]
    public void IgnoresDeletedDevicesWhenSelectingAtEndOfSnapshot()
    {
        var inventory = new LightInventory();
        inventory.Apply(Message(300, """{"devices":[{"deviceId":1,"devType":100,"usage":0}]}"""));
        inventory.Apply(Message(296, """{"deviceId":1}"""));
        inventory.Apply(Message(300, """
            {"devices":[{"deviceId":2,"devType":101,"usage":0}],"lastItem":true}
            """));
        Assert.Equal(2, inventory.FirstLight?.DeviceId);
    }

    [Fact]
    public void ComponentDeletionRemovesAllItsLightsButKeepsOtherComponents()
    {
        var inventory = new LightInventory();
        inventory.Apply(Message(300, """
            {"devices":[
                {"deviceId":1,"compId":4,"devType":100,"usage":0},
                {"deviceId":2,"compId":4,"devType":101,"usage":0},
                {"deviceId":3,"compId":5,"devType":100,"usage":0}
            ],"lastItem":true}
            """));

        inventory.Apply(Message(309, """{"compId":4}"""));

        Assert.Null(inventory.GetLight(1));
        Assert.Null(inventory.GetLight(2));
        Assert.Equal(3, Assert.Single(inventory.Lights).DeviceId);
        Assert.Null(inventory.FirstLight);
    }

    [Fact]
    public void ReconnectKeepsPreferredLightDespiteReorderedSnapshot()
    {
        var inventory = new LightInventory(preferredDeviceId: 5);
        inventory.Apply(Message(300, """
            {"devices":[
                {"deviceId":7,"devType":100,"usage":0},
                {"deviceId":5,"devType":101,"usage":0}
            ],"lastItem":true}
            """));
        Assert.Equal(5, inventory.FirstLight?.DeviceId);
    }

    [Fact]
    public void DoesNotSubstituteAnotherLightWhenPreferredDeviceIsNotALight()
    {
        var inventory = new LightInventory(preferredDeviceId: 5);
        inventory.Apply(Message(300, """
            {"devices":[
                {"deviceId":7,"devType":100,"usage":0},
                {"deviceId":5,"devType":100,"usage":2}
            ],"lastItem":true}
            """));
        Assert.Null(inventory.FirstLight);
    }

    [Fact]
    public void PreservesCommandPayloadWhenSerializedAsBaseType()
    {
        XComfortMessage message = new XComfortMessage<object>(Messages.ACTION_SWITCH_DEVICE,
            new { deviceId = 5, @switch = false });
        using var document = JsonDocument.Parse(message.ToUtf8Bytes());
        Assert.Equal(281, document.RootElement.GetProperty("type_int").GetInt32());
        Assert.Equal(5, document.RootElement.GetProperty("payload").GetProperty("deviceId").GetInt32());
        Assert.False(document.RootElement.GetProperty("payload").GetProperty("switch").GetBoolean());
    }

    private static LightInventory Dimmer()
    {
        var inventory = new LightInventory();
        inventory.Apply(Message(300, """
            {"devices":[{
                "deviceId":5,"compId":6,"devType":101,"usage":0,"name":"Dimmer",
                "switch":1,"dimmvalue":80,"power":20
            }],"lastItem":true}
            """));
        return inventory;
    }

    private static XComfortMessage Message(int type, string payload) =>
        JsonSerializer.Deserialize<XComfortMessage>($$"""{"type_int":{{type}},"payload":{{payload}}}""")!;
}
