using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace AudioFlow.App;

internal static class VerificationRuntime
{
    public static string[] CreateTones(string directory, params string[] names)
    {
        CopyRuntime(directory);
        return names.Select(name =>
        {
            var path = Path.Combine(directory, name + ".exe");
            File.Copy(Path.Combine(directory, "AudioFlow.Verification.exe"), path, true);
            return path;
        }).ToArray();
    }
    public static string CopyRuntime(string directory)
    {
        string project = AppContext.BaseDirectory;
        for (int level = 0; level < 9 && !Directory.Exists(Path.Combine(project, "tests", "AudioFlow.Verification")); level++)
            project = Directory.GetParent(project)?.FullName ?? throw new InvalidOperationException("Source workspace was not found.");
        string configuration = typeof(VerificationRuntime).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Release";
        string source = Path.Combine(project, "tests", "AudioFlow.Verification", "bin", configuration, "net10.0-windows10.0.22000.0");
        if (!File.Exists(Path.Combine(source, "AudioFlow.Verification.exe"))) throw new InvalidOperationException("Verification executable missing.");
        Directory.CreateDirectory(directory);
        foreach (var file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(directory, Path.GetFileName(file)), true);
        return directory;
    }
    public static Process StartTone(string path, int frequency)
        => Start(path, "--tone", frequency.ToString(System.Globalization.CultureInfo.InvariantCulture), "120");

    public static Process Start(string path, params string[] arguments)
    {
        var info = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return Process.Start(info) ?? throw new InvalidOperationException("Verification process did not start.");
    }
}
