using System.Text.Json;
using System.Text.Json.Serialization;

namespace AudioFlow.Core;

[JsonConverter(typeof(JsonStringEnumConverter<CanvasView>))]
public enum CanvasView { PerApplication, Global }

public sealed class WorkspaceState
{
    public int Version { get; set; } = 1;
    public CanvasView View { get; set; } = CanvasView.PerApplication;
    public bool RoutingPaused { get; set; }
    public string? ActiveConfigurationPath { get; set; }
    public string? UnavailableConfigurationPath { get; set; }
    public bool? LastRoutingEnabled { get; set; }
    public bool RestoreRoutingStateOnLaunch { get; set; } = true;
    public string? FocusedApplication { get; set; }
    public List<SavedApplication> ManualApplications { get; set; } = [];
    public List<SavedApplication> KnownAudioApplications { get; set; } = [];
    public List<SavedApplication> KnownRecordingApplications { get; set; } = [];
    public RouteGraph Configuration { get; set; } = new();
}

public static class WorkspacePersistence
{
    public static WorkspaceState LoadWorkspace(this SceneStore store)
    {
        var path = Path.Combine(store.DataDirectory, "workspace.json");
        if (!File.Exists(path)) return new();
        if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new GraphValidationException("工作区文件超过 4 MB。");
        var state = LegacyWorkspace.Read(File.ReadAllText(path), out bool migrated);
        Validate(state);
        if (migrated)
        {
            var backup = path + ".pre-migration";
            if (!File.Exists(backup)) File.Copy(path, backup);
            store.SaveWorkspace(state);
        }
        return state;
    }
    public static void SaveWorkspace(this SceneStore store, WorkspaceState state)
    {
        Validate(state);
        var path = Path.Combine(store.DataDirectory, "workspace.json");
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, SceneStore.JsonOptions));
        File.Move(temporary, path, true);
    }
    private static void Validate(WorkspaceState state)
    {
        if (state.Version != 1) throw new GraphValidationException("工作区版本不受支持。");
        if (!Enum.IsDefined(state.View)) throw new GraphValidationException("画布视图不受支持。");
        GraphCompiler.Compile(state.Configuration, []);
    }
}
