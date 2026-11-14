using System.Text.Json;

namespace XComfortNet.Web;

internal sealed class MqttPublicationManifest
{
    public Dictionary<string, string> Discoveries { get; init; } = [];
    public Dictionary<string, string> States { get; init; } = [];

    public static MqttPublicationManifest Load(string path)
    {
        if (!File.Exists(path)) return new MqttPublicationManifest();
        var manifest = JsonSerializer.Deserialize<MqttPublicationManifest>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Invalid MQTT publication manifest.");
        if (manifest.Discoveries is null || manifest.States is null)
            throw new InvalidDataException("MQTT publication manifest is missing topic records.");
        return manifest;
    }

    public async Task SaveAsync(string path, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(this), cancellationToken);
        File.Move(temporary, path, overwrite: true);
    }
}
