namespace AudioFlow.Core;

public sealed record SavedApplication(string Name, string Path);

public static class ApplicationSelection
{
    public static IReadOnlyList<AudioApplication> Candidates(IEnumerable<AudioApplication> running,
        IEnumerable<SavedApplication> saved)
    {
        var applications = new Dictionary<string, AudioApplication>(StringComparer.Ordinal);
        foreach (var app in running)
            applications[AppIdentity.Normalize(app.Path)] = app;
        foreach (var app in saved.Where(a => !string.IsNullOrWhiteSpace(a.Path)))
        {
            var key = AppIdentity.Normalize(app.Path);
            applications.TryAdd(key, new(key, app.Name, app.Path, [], [], 0, false, false));
        }
        return applications.Values.OrderByDescending(a => a.Peak > .001f)
            .ThenByDescending(a => a.HasAudio).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static IReadOnlyList<AudioApplication> Sidebar(IEnumerable<AudioApplication> running,
        IEnumerable<SavedApplication> manual, IEnumerable<SavedApplication> configured,
        IEnumerable<SavedApplication> knownAudio, string search = "")
    {
        var saved = manual.ToList();
        var configuredApps = configured.ToList();
        var manualKeys = saved.Select(a => AppIdentity.Normalize(a.Path)).ToHashSet();
        var configuredKeys = configuredApps.Select(a => AppIdentity.Normalize(a.Path)).ToHashSet();
        var knownKeys = knownAudio.Select(a => AppIdentity.Normalize(a.Path)).ToHashSet();
        bool AudioCapable(AudioApplication app) => app.HasAudioSession ||
            app.ProcessIds.Count > 0 && knownKeys.Contains(app.Key);
        return Candidates(running, saved.Concat(configuredApps)).Where(a =>
            (AudioCapable(a) || manualKeys.Contains(a.Key) || configuredKeys.Contains(a.Key)) && Matches(a.Name, a.Path, search))
            .OrderBy(a => a.HasAudio ? 0 : configuredKeys.Contains(a.Key) ? 1 : AudioCapable(a) ? 2 : 3)
            .ThenByDescending(a => a.Peak > .001f).ThenByDescending(a => a.ProcessIds.Count > 0)
            .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static bool Matches(string name, string path, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        var executable = System.IO.Path.GetFileNameWithoutExtension(path);
        var aliases = executable.ToLowerInvariant() switch
        {
            "weixin" or "wechat" => "微信 WeChat Weixin",
            "wxwork" => "企业微信 WeCom",
            "cloudmusic" => "网易云音乐",
            "qqmusic" => "QQ音乐",
            "msedge" => "Edge 微软浏览器",
            "chrome" => "Chrome 谷歌浏览器",
            "firefox" => "Firefox 火狐浏览器",
            "bilibili" or "biliwin" => "哔哩哔哩 B站",
            _ => ""
        };
        if (name.Contains("哔哩", StringComparison.OrdinalIgnoreCase)) aliases += " B站";
        var searchable = name + " " + executable + " " + aliases;
        return query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .All(word => searchable.Contains(word, StringComparison.OrdinalIgnoreCase));
    }
}
