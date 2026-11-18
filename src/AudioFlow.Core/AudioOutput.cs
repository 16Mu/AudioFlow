namespace AudioFlow.Core;

public static class AudioOutput
{
    public const string SystemDefault = "windows-default";
    public const string None = "no-device";

    public static AudioEndpoint? Resolve(string key, IReadOnlyList<AudioEndpoint> endpoints) =>
        key == SystemDefault ? endpoints.FirstOrDefault(e => !e.Capture && e.Default) :
        endpoints.FirstOrDefault(e => !e.Capture && e.Id == key);

    public static string DefaultName(IReadOnlyList<AudioEndpoint> endpoints) =>
        Resolve(SystemDefault, endpoints)?.Name ?? "等待 Windows 播放设备连接";
}
