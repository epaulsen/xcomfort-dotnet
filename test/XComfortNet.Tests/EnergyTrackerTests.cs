using XComfortDotNet;
using XComfortNet.Web;

namespace XComfortNet.Tests;

public class EnergyTrackerTests
{
    [Fact]
    public async Task IntegratesPreviousPowerPersistsTotalsAndSkipsOfflineIntervals()
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"energy-{Guid.NewGuid():N}.json");
        try
        {
            var clock = new ManualClock();
            var tracker = new EnergyTracker(path, clock);
            var telemetry = Power(1000d);
            tracker.Observe("bridge", telemetry, true);
            clock.Advance(TimeSpan.FromMinutes(30));
            telemetry = Power(2000d);
            tracker.Observe("bridge", telemetry, true);
            Assert.Equal(0.5d, Estimated(tracker, telemetry));
            clock.Advance(TimeSpan.FromMinutes(15));
            tracker.Observe("bridge", telemetry, false);
            Assert.Equal(1d, Estimated(tracker, telemetry));
            clock.Advance(TimeSpan.FromHours(5));
            tracker.Observe("bridge", telemetry, true);
            Assert.Equal(1d, Estimated(tracker, telemetry));
            await tracker.SaveAsync();
            var restored = new EnergyTracker(path, clock);
            Assert.Equal(1d, Estimated(restored, telemetry));
            clock.Advance(TimeSpan.FromHours(1));
            restored.Observe("bridge", telemetry, true);
            Assert.Equal(1d, Estimated(restored, telemetry));
            clock.Advance(TimeSpan.FromHours(1));
            restored.Observe("bridge", telemetry, true);
            Assert.Equal(3d, Estimated(restored, telemetry));
            Assert.Single(restored.AppendSensors("another-bridge", telemetry).Sensors);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".tmp");
        }
    }

    [Fact]
    public void InvalidPersistedTotalsAreRejectedInsteadOfSilentlyResettingEnergy()
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"invalid-energy-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """{"bridge_device_1_power":-1}""");
            Assert.Throws<InvalidDataException>(() => new EnergyTracker(path, new ManualClock()));
            File.WriteAllText(path, "null");
            Assert.Throws<InvalidDataException>(() => new EnergyTracker(path, new ManualClock()));
            File.WriteAllText(path, "{");
            Assert.Throws<System.Text.Json.JsonException>(() => new EnergyTracker(path, new ManualClock()));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void UnknownPowerDoesNotInventEnergyOrIntegrateAcrossUnknownIntervals()
    {
        var clock = new ManualClock();
        var tracker = new EnergyTracker(Path.Combine(AppContext.BaseDirectory, $"absent-{Guid.NewGuid():N}.json"), clock);
        tracker.Observe("bridge", Power(null), true);
        Assert.Single(tracker.AppendSensors("bridge", Power(null)).Sensors);
        tracker.Observe("bridge", Power(1000d), true);
        clock.Advance(TimeSpan.FromHours(1));
        tracker.Observe("bridge", Power(null), true);
        clock.Advance(TimeSpan.FromHours(5));
        tracker.Observe("bridge", Power(1000d), true);
        Assert.Equal(1d, Estimated(tracker, Power(1000d)));
    }

    private static object? Estimated(EnergyTracker tracker, XComfortTelemetry telemetry) =>
        tracker.AppendSensors("bridge", telemetry).Sensors.Single(sensor => sensor.DeviceClass == "energy").Value;

    private static XComfortTelemetry Power(object? value) => new([], [
        new XComfortSensor("device_1_power", "comp_4", "Test", "xComfort", "Power", "power", "W", "measurement", false, value)
    ]);

    private sealed class ManualClock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public void Advance(TimeSpan duration) => _timestamp += duration.Ticks;
    }
}
