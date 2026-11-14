using System.Text.Json;
using XComfortDotNet;

namespace XComfortNet.Web;

internal sealed class EnergyTracker
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly TimeProvider _clock;
    private readonly Dictionary<string, double> _totals;
    private readonly Dictionary<string, Sample> _samples = [];

    public EnergyTracker(string path, TimeProvider clock)
    {
        _path = path;
        _clock = clock;
        _totals = File.Exists(path)
            ? JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(path))
                ?? throw new InvalidDataException("Invalid energy totals file.")
            : [];
        if (_totals.Values.Any(value => !double.IsFinite(value) || value < 0))
            throw new InvalidDataException("Energy totals must be finite and non-negative.");
    }

    public void Observe(string bridgeId, XComfortTelemetry telemetry, bool ready)
    {
        lock (_gate)
        {
            var now = _clock.GetTimestamp();
            foreach (var (key, previous) in _samples)
                _totals[key] += previous.Watts * _clock.GetElapsedTime(previous.Timestamp, now).TotalHours / 1000;
            if (!ready)
            {
                _samples.Clear();
                return;
            }
            var active = new HashSet<string>();
            foreach (var power in telemetry.Sensors.Where(sensor => sensor.DeviceClass == "power"))
            {
                if (power.Value is not double watts || !double.IsFinite(watts) || watts < 0) continue;
                var key = $"{bridgeId}_{power.Key}";
                active.Add(key);
                _totals.TryAdd(key, 0);
                _samples[key] = new Sample(watts, now);
            }
            foreach (var key in _samples.Keys.Except(active).ToArray()) _samples.Remove(key);
        }
    }

    public XComfortTelemetry AppendSensors(string bridgeId, XComfortTelemetry telemetry)
    {
        lock (_gate)
        {
            var energy = telemetry.Sensors.Where(sensor => sensor.DeviceClass == "power" &&
                    _totals.ContainsKey($"{bridgeId}_{sensor.Key}"))
                .Select(sensor => sensor with
                {
                    Key = $"{sensor.Key}_energy",
                    Name = "Estimated energy",
                    DeviceClass = "energy",
                    Unit = "kWh",
                    StateClass = "total_increasing",
                    Diagnostic = false,
                    Value = Math.Round(_totals[$"{bridgeId}_{sensor.Key}"], 9)
                });
            return telemetry with { Sensors = telemetry.Sensors.Concat(energy).OrderBy(sensor => sensor.Key).ToArray() };
        }
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        string json;
        lock (_gate) { json = JsonSerializer.Serialize(_totals); }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var temporary = _path + ".tmp";
        await File.WriteAllTextAsync(temporary, json, cancellationToken);
        File.Move(temporary, _path, overwrite: true);
    }

    private sealed record Sample(double Watts, long Timestamp);
}
