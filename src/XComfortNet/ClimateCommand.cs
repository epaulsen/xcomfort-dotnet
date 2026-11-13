using System.Text.Json.Serialization;

namespace XComfortDotNet;

public enum ClimateCommandKind { Mode, Temperature, Preset }

public sealed record ClimateCommand(ClimateCommandKind Kind, string Value)
{
    public IReadOnlyList<HeatingPayload> Build(XComfortClimate room)
    {
        if (Kind == ClimateCommandKind.Mode)
        {
            return Value switch
            {
                "off" => [new(room.RoomId, 0, true)],
                "heat" => [new(room.RoomId, room.LastHeatingState, true)],
                _ => throw new ArgumentException("Only off and heat are supported.")
            };
        }
        if (room.Preset is not (1 or 2 or 3) || room.HeatingState is not (0 or 1 or 2))
            throw new InvalidOperationException("Heating preset and state must be known before changing settings.");
        if (Kind == ClimateCommandKind.Temperature)
        {
            if (!double.TryParse(Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var temperature) ||
                !double.IsFinite(temperature) || temperature < room.MinTemperature || temperature > room.MaxTemperature)
                throw new ArgumentOutOfRangeException(nameof(Value), "Temperature is outside the current preset's allowed range.");
            return [new(room.RoomId, room.HeatingState.Value, false, room.Preset, temperature)];
        }
        if (Kind != ClimateCommandKind.Preset) throw new ArgumentException("Unsupported climate command.");
        var preset = Value switch { "Frost Protection" => 1, "eco" => 2, "comfort" => 3, _ => throw new ArgumentException("Unsupported preset.") };
        if (preset == room.Preset) return [];
        var commands = new List<HeatingPayload>();
        if (room.HeatingState == 0) commands.Add(new(room.RoomId, room.LastHeatingState, true));
        commands.Add(new(room.RoomId, 2, false, room.Preset));
        commands.Add(new(room.RoomId, 2, false, preset));
        return commands;
    }
}

public sealed record HeatingPayload(
    [property: JsonPropertyName("roomId")] int RoomId,
    [property: JsonPropertyName("state")] int State,
    [property: JsonPropertyName("confirmed")] bool Confirmed,
    [property: JsonPropertyName("mode"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Mode = null,
    [property: JsonPropertyName("setpoint"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Setpoint = null);
