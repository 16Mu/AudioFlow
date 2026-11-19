using System.IO;
using Microsoft.Win32;

namespace AudioFlow.App;

internal sealed class WindowsAutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly string registryPath, valueName, executable, dataDirectory;
    internal WindowsAutoStart(string dataDirectory, string? executable = null, string registryPath = RunKey, string valueName = "AudioFlow")
    {
        this.dataDirectory = Path.GetFullPath(dataDirectory);
        this.executable = Path.GetFullPath(executable ?? Environment.ProcessPath ?? throw new InvalidOperationException("找不到声流的程序位置。"));
        this.registryPath = registryPath; this.valueName = valueName;
    }
    internal string Command => Quote(executable) + " --autostart --data-dir " + Quote(dataDirectory);
    internal bool Enabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(registryPath);
            return IsOurCommand(key?.GetValue(valueName) as string);
        }
    }
    private static bool IsOurCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command) || command[0] != '"') return false;
        int end = command.IndexOf('"', 1);
        return end > 1 && string.Equals(Path.GetFileName(command.Substring(1, end - 1)), "AudioFlow.exe", StringComparison.OrdinalIgnoreCase);
    }
    internal void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(registryPath, true);
        if (enabled)
        {
            if (!string.Equals(Path.GetFileName(executable), "AudioFlow.exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(executable))
                throw new InvalidOperationException("请从 AudioFlow.exe 打开声流后，再设置开机启动。");
            key.SetValue(valueName, Command, RegistryValueKind.String);
        }
        else key.DeleteValue(valueName, false);
    }
    internal void RefreshPathIfEnabled()
    {
        if (Enabled) SetEnabled(true);
    }
    private static string Quote(string value)
    {
        if (value.Contains('"')) throw new InvalidOperationException("程序或设置文件夹的位置包含不能使用的字符。");
        int end = value.Length;
        while (end > 0 && value[end - 1] == '\\') end--;
        return "\"" + value[..end] + new string('\\', (value.Length - end) * 2) + "\"";
    }
}
