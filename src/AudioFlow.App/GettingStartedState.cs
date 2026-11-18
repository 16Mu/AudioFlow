using System.IO;
using System.Text.Json;
using AudioFlow.Core;

namespace AudioFlow.App;

// This is a local UI preference, independent of imported audio configurations.
internal sealed class GettingStartedState(string directory)
{
    private const int CurrentVersion = 1;
    private string StatePath => Path.Combine(directory, "getting-started.json");
    public bool NeedsGuide
    {
        get
        {
            try
            {
                if (!File.Exists(StatePath) || new FileInfo(StatePath).Length > 4096) return true;
                return (JsonSerializer.Deserialize<Preference>(File.ReadAllText(StatePath), SceneStore.JsonOptions)?.SeenVersion ?? 0) < CurrentVersion;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return true; }
        }
    }
    public void Dismiss()
    {
        Directory.CreateDirectory(directory);
        string temporary = StatePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new Preference { SeenVersion = CurrentVersion }, SceneStore.JsonOptions));
        File.Move(temporary, StatePath, true);
    }
    private sealed class Preference { public int SeenVersion { get; set; } }
}
