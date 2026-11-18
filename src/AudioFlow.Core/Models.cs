using System.Text.Json.Serialization;

namespace AudioFlow.Core;

[JsonConverter(typeof(JsonStringEnumConverter<NodeKind>))]
public enum NodeKind { Application, Microphone, Playback, VirtualMicrophone }
[JsonConverter(typeof(JsonStringEnumConverter<RouteMode>))]
public enum RouteMode { Playback, MicrophoneFeed }

public sealed class RouteNode
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public NodeKind Kind { get; set; }
    public string Name { get; set; } = "";
    public string Key { get; set; } = "";
    public string? ChannelId { get; set; }
    public bool InputChannelPinned { get; set; }
    public string? OwnerAppKey { get; set; }
    public bool PrimaryOutput { get; set; }
    public string? LastOutputKey { get; set; }
    public string? InputDeviceKey { get; set; }
    public string? LastInputDeviceKey { get; set; }
    public bool RecordingConfigured { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public float Volume { get; set; } = 1;
    public bool Muted { get; set; }
    public bool VolumeManaged { get; set; }
}

public sealed class RouteConnection
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SourceId { get; set; } = "";
    public string TargetId { get; set; } = "";
    public RouteMode Mode { get; set; }
    public bool Enabled { get; set; } = true;
    public float Volume { get; set; } = 1;
    public bool Automatic { get; set; }
}

public sealed class VirtualChannel
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string RenderEndpointId { get; set; } = "";
    public string CaptureEndpointId { get; set; } = "";
    public string Provider { get; set; } = "Cable";
    public float Volume { get; set; } = 1;
    public override string ToString() => Name;
    public bool RequiresValidation { get; set; }
}

public sealed class RouteGraph
{
    public int Version { get; set; } = 1;
    public List<RouteNode> Nodes { get; set; } = [];
    public List<RouteConnection> Connections { get; set; } = [];
    public List<VirtualChannel> Channels { get; set; } = [];
    public bool ResumeOnLaunch { get; set; }
    public bool CloseToTray { get; set; }
    public string? SilentInputChannelId { get; set; }
}

public sealed record AudioEndpoint(string Id, string Name, bool Capture, bool Default,
    bool Virtual, int SampleRate, int Channels);
public sealed record AudioApplication(string Key, string Name, string Path,
    IReadOnlyList<int> ProcessIds, IReadOnlyList<int> RootProcessIds, float Peak, bool HasAudio, bool HasAudioSession, bool HasRecordingSession = false, string? RecordingEndpointId = null);
public sealed record AudioCatalogSnapshot(IReadOnlyList<AudioEndpoint> Endpoints,
    IReadOnlyList<AudioApplication> Applications, IReadOnlyList<VirtualChannel> SuggestedChannels);

public sealed record Destination(string EndpointId, bool Microphone, string? ChannelId, float Volume = 1, bool SystemDefault = false, bool Native = false);
public sealed record CompiledSource(RouteNode Node, Destination? Primary,
    IReadOnlyList<Destination> Mirrors, IReadOnlyList<Destination> Destinations);
public sealed record CompiledGraph(IReadOnlyList<CompiledSource> Sources,
    IReadOnlyList<string> Warnings);

public sealed record AudioRouteStatus(string NodeId, string State, string Message);
public sealed record SourceLevel(string NodeId, float Peak);
public sealed record EngineSnapshot(IReadOnlyList<AudioRouteStatus> Statuses,
    IReadOnlyList<SourceLevel> Levels, long BufferOverruns, long BufferUnderruns,
    double RunningSeconds);

public static class AppIdentity
{
    public static string Normalize(string path) =>
        string.IsNullOrWhiteSpace(path) ? "" : path.Trim().Replace('/', '\\').ToUpperInvariant();
}
