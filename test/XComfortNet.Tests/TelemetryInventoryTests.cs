using System.Text.Json;
using XComfortDotNet;
using XComfortDotNet.Model;

namespace XComfortNet.Tests;

public class TelemetryInventoryTests
{
    [Fact]
    public void ClimateRequiresExplicitHeatingCapabilityIncludingLateHeatingSlices()
    {
        var inventory = new TelemetryInventory();
        inventory.Apply(Message(300, """{"rooms":[{"roomId":1,"name":"Room"},{"roomId":2,"temperatureOnly":true}]}"""));
        Assert.Empty(inventory.Snapshot.Climates);
        inventory.Apply(Message(300, """{"lastItem":true}"""));
        Assert.Empty(inventory.Snapshot.Climates);
        Assert.Null(inventory.GetClimate(1));
        inventory.Apply(Message(300, """
            {"roomHeating":[{"roomId":1,"temperatureOnly":false,"currentMode":3,"state":2,"setpoint":21}]}
            """));
        var climate = Assert.Single(inventory.Snapshot.Climates);
        Assert.Equal("Room", climate.Name);
        Assert.Equal("heat", climate.HvacMode);
        Assert.Equal(21, climate.Setpoint);
        Assert.Equal(climate, inventory.GetClimate(1));
        Assert.Null(inventory.GetClimate(2));
        inventory.Apply(Message(294, """{"roomId":1,"temperatureOnly":true}"""));
        Assert.Empty(inventory.Snapshot.Climates);
    }

    [Fact]
    public void MergesPartialMeasurementsAndLinkedSensorUpdatesWithoutDefaults()
    {
        var inventory = new TelemetryInventory();
        inventory.Apply(Message(300, """
            {"devices":[{"deviceId":10,"compId":4,"devType":450,"info":[{"text":"1222","value":"20.5"},{"text":"1223","value":"45"}]}],
             "rooms":[{"roomId":1,"temperatureOnly":false,"roomSensorId":10,"temp":19,"currentMode":3,"state":2,"setpoint":21}],
             "lastItem":true}
            """));
        inventory.Apply(Message(310, """{"item":[{"deviceId":10,"info":[{"text":"1222","value":"22.25"}]},{"roomId":1,"power":0}]}"""));
        var room = Assert.Single(inventory.Snapshot.Climates);
        Assert.Equal(22.25, room.Temperature);
        Assert.Equal(45, room.Humidity);
        Assert.Equal(21, room.Setpoint);
        Assert.Equal("idle", room.Action);
        Assert.Equal(45d, inventory.Snapshot.Sensors.Single(s => s.Key == "device_10_humidity").Value);

        inventory.Apply(Message(291, """{"deviceId":10,"info":[{"text":"1222","value":"-100"}]}"""));
        Assert.Null(inventory.Snapshot.Sensors.Single(s => s.Key == "device_10_temperature").Value);
        Assert.Null(Assert.Single(inventory.Snapshot.Climates).Temperature);
        inventory.Apply(Message(293, """{"roomId":1,"mode":1,"state":0}"""));
        room = Assert.Single(inventory.Snapshot.Climates);
        Assert.Equal(1, room.Preset);
        Assert.Equal(2, room.LastHeatingState);
        Assert.Equal(5, room.MinTemperature);
        Assert.Equal(20, room.MaxTemperature);
        Assert.Equal("off", room.Action);
    }

    [Fact]
    public void LinkedRoomTemperatureSensorRemainsPublishedWhenReadingBecomesUnknown()
    {
        var inventory = new TelemetryInventory();
        inventory.Apply(Message(300, """
            {"devices":[{"deviceId":10,"devType":450,"info":[{"text":"1222","value":"20.5"}]}],
             "rooms":[{"roomId":1,"temperatureOnly":false,"roomSensorId":10,"state":2,"mode":3}],"lastItem":true}
            """));
        Assert.Equal(20.5d, inventory.Snapshot.Sensors.Single(sensor => sensor.Key == "room_1_temperature").Value);

        inventory.Apply(Message(291, """{"deviceId":10,"info":[{"text":"1222","value":"-100"}]}"""));

        Assert.Null(Assert.Single(inventory.Snapshot.Sensors, sensor => sensor.Key == "room_1_temperature").Value);
    }

    [Fact]
    public void HandlesHeatingDemandPowerDeviceTemperatureAndValveReadings()
    {
        var inventory = new TelemetryInventory();
        inventory.Apply(Message(300, """
            {"devices":[
                {"deviceId":1,"compId":4,"devType":440,"dimmvalue":0,"power":0,"info":[{"text":1109,"value":"28.5"}]},
                {"deviceId":2,"compId":5,"devType":441,"info":[{"text":"1222","value":"19"},{"text":"1225","value":"33"}]},
                {"deviceId":3,"compId":6,"devType":100,"usage":0,"switch":false}
            ],"lastItem":true}
            """));
        var sensors = inventory.Snapshot.Sensors;
        Assert.Equal(0d, sensors.Single(s => s.Key == "device_1_heating_demand").Value);
        Assert.Equal(0d, sensors.Single(s => s.Key == "device_1_power").Value);
        Assert.True(sensors.Single(s => s.Key == "device_1_device_temperature").Diagnostic);
        Assert.Equal(33d, sensors.Single(s => s.Key == "device_2_valve_position").Value);
        Assert.DoesNotContain(sensors, s => s.Key == "device_3_power");
    }

    [Fact]
    public void HeatingDemandInfoUpdatesReplaceOlderDimmvalueUnlessExplicitlyOverridden()
    {
        var inventory = new TelemetryInventory();
        inventory.Apply(Message(300, """
            {"devices":[{"deviceId":1,"devType":440,"dimmvalue":0}],"lastItem":true}
            """));
        inventory.Apply(Message(292, """{"deviceId":1,"info":[{"text":"1225","value":"33"}]}"""));
        Assert.Equal(33d, inventory.Snapshot.Sensors.Single(sensor => sensor.Key == "device_1_heating_demand").Value);

        inventory.Apply(Message(292, """{"deviceId":1,"dimmvalue":12,"info":[{"text":"1225","value":"50"}]}"""));
        Assert.Equal(12d, inventory.Snapshot.Sensors.Single(sensor => sensor.Key == "device_1_heating_demand").Value);
    }

    [Fact]
    public void PowerInformationUpdatesReplaceOldMeasurements()
    {
        var inventory = new TelemetryInventory();
        inventory.Apply(Message(300, """
            {"devices":[{"deviceId":1,"devType":440,"power":250}],"lastItem":true}
            """));
        Assert.Equal(250d, inventory.Snapshot.Sensors.Single().Value);
        inventory.Apply(Message(291, """{"deviceId":1,"info":[{"text":"1110","value":"500"}]}"""));
        Assert.Equal(500d, inventory.Snapshot.Sensors.Single().Value);
        inventory.Apply(Message(292, """{"deviceId":1,"info":[{"text":"1226","value":"750"}]}"""));
        Assert.Equal(750d, inventory.Snapshot.Sensors.Single().Value);
        inventory.Apply(Message(291, """{"deviceId":1,"power":100,"info":[{"text":"1110","value":"90"}]}"""));
        Assert.Equal(100d, inventory.Snapshot.Sensors.Single().Value);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(101)]
    public void LightActuatorsNeverExposePowerDespiteGenericPowerFields(int deviceType)
    {
        var inventory = new TelemetryInventory();
        inventory.Apply(Message(300, $$"""
            {"devices":[{"deviceId":1,"devType":{{deviceType}},"usage":0,"switch":false,"power":0,
             "info":[{"text":"1109","value":"28.5"},{"text":"1110","value":"250"},{"text":"1226","value":"300"}]}],
             "lastItem":true}
            """));
        Assert.DoesNotContain(inventory.Snapshot.Sensors, sensor => sensor.DeviceClass == "power");
        Assert.Equal(28.5d, Assert.Single(inventory.Snapshot.Sensors).Value);
        inventory.Apply(Message(291, """{"deviceId":1,"switch":true,"power":500}"""));
        Assert.DoesNotContain(inventory.Snapshot.Sensors, sensor => sensor.DeviceClass == "power");
    }

    [Fact]
    public void DiagnosticUpdatesReplaceBatteryBucketsAndPreserveUnrelatedInformation()
    {
        var inventory = new TelemetryInventory();
        inventory.SetBridgeInfo("test-bridge", "1.2.3", "bridge.invalid");
        inventory.Apply(Message(300, """
            {"comps":[{"compId":4,"name":"Thermostat","info":[{"text":"1117"},{"text":"1111","value":"2"},{"text":"1108","value":"62"}]}],"lastItem":true}
            """));
        Assert.Equal(100d, inventory.Snapshot.Sensors.Single(s => s.Key == "comp_4_battery").Value);
        inventory.Apply(Message(310, """{"item":[{"compId":4,"info":[{"text":"1114"}]}]}"""));
        Assert.Equal(25d, inventory.Snapshot.Sensors.Single(s => s.Key == "comp_4_battery").Value);
        Assert.Equal("Good", inventory.Snapshot.Sensors.Single(s => s.Key == "comp_4_signal_quality").Value);
        Assert.Equal(-62d, inventory.Snapshot.Sensors.Single(s => s.Key == "comp_4_signal_strength").Value);
        inventory.Apply(Message(308, """{"compId":4,"info":[{"text":"1118"}]}"""));
        Assert.Null(inventory.Snapshot.Sensors.Single(s => s.Key == "comp_4_battery").Value);
        inventory.Apply(Message(308, """{"compId":4,"info":[{"text":"1119"}]}"""));
        Assert.DoesNotContain(inventory.Snapshot.Sensors, s => s.Key == "comp_4_battery");
        inventory.Apply(Message(303, """{"name":"Lab","remoteOnline":false}"""));
        Assert.Equal("1.2.3", inventory.Snapshot.Sensors.Single(s => s.Key == "bridge_firmware").Value);
        Assert.Equal("off", inventory.Snapshot.Sensors.Single(s => s.Key == "bridge_remoteOnline").Value);
    }

    [Fact]
    public void DeletingRoomsAndComponentsRemovesAssociatedEntities()
    {
        var inventory = new TelemetryInventory();
        inventory.Apply(Message(300, """
            {"devices":[{"deviceId":1,"compId":4,"info":[{"text":"1222","value":"21"}]}],
             "comps":[{"compId":4,"info":[{"text":"1117"}]}],
             "rooms":[{"roomId":1,"temperatureOnly":false,"temp":21}],"lastItem":true}
            """));
        inventory.Apply(Message(297, """{"roomId":1}"""));
        Assert.Empty(inventory.Snapshot.Climates);
        Assert.DoesNotContain(inventory.Snapshot.Sensors, s => s.OwnerId == "room_1");
        inventory.Apply(Message(309, """{"compId":4}"""));
        Assert.Empty(inventory.Snapshot.Sensors);
    }

    private static XComfortMessage Message(int type, string payload) =>
        JsonSerializer.Deserialize<XComfortMessage>($$"""{"type_int":{{type}},"payload":{{payload}}}""")!;
}
