using System.Text.Json;
using System.Text.Json.Serialization;

namespace AudioFlow.Core;

public sealed class SceneStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true, PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public string DataDirectory { get; }
    public string ScenePath => Path.Combine(DataDirectory, "scene.json");
    public SceneStore(string? directory = null)
    {
        DataDirectory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AudioFlow");
        Directory.CreateDirectory(DataDirectory);
    }
    public RouteGraph Load(string? path = null)
    {
        var actual = path ?? ScenePath;
        if (!File.Exists(actual)) return new();
        if (new FileInfo(actual).Length > 2 * 1024 * 1024)
            throw new GraphValidationException("声音设置文件太大了（超过 2 MB），请换一个文件。");
        var graph = JsonSerializer.Deserialize<RouteGraph>(File.ReadAllText(actual), JsonOptions)
            ?? throw new GraphValidationException("这个声音设置文件是空的，请换一个文件。");
        GraphCompiler.Compile(graph, []);
        return graph;
    }
    public void Save(RouteGraph graph, string? path = null)
    {
        GraphCompiler.Compile(graph, []);
        var actual = path ?? ScenePath;
        var temporary = actual + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(graph, JsonOptions));
        File.Move(temporary, actual, true);
    }
    public static RouteGraph Clone(RouteGraph graph) =>
        JsonSerializer.Deserialize<RouteGraph>(JsonSerializer.Serialize(graph, JsonOptions), JsonOptions)!;
}
