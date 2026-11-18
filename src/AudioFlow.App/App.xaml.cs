using System.Diagnostics;
using System.IO;
using System.Windows;
using AudioFlow.Audio;

namespace AudioFlow.App;

public partial class App : Application
{
    private Mutex? instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length == 2 && e.Args[0] == "--verify-onboarding")
        {
            _ = VerifyOnboardingAsync(e.Args[1]); return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--verify-settings")
        {
            _ = VerifySettingsAsync(e.Args[1]); return;
        }
        if (e.Args.Length == 3 && e.Args[0] == "--verify-startup")
        {
            _ = VerifyStartupAsync(e.Args[1], e.Args[2]); return;
        }
        if (e.Args.Length == 3 && e.Args[0] == "--recover-pending" && int.TryParse(e.Args[2], out int parentId))
        {
            _ = RecoverPendingAsync(e.Args[1], parentId);
            return;
        }
        int verification = Array.IndexOf(e.Args, "--verify-ui");
        if (verification >= 0)
        {
            string destination = verification + 1 < e.Args.Length ? e.Args[verification + 1] : Path.Combine(Path.GetTempPath(), "AudioFlow-UI");
            _ = VerifyAsync(destination);
            return;
        }
        if (e.Args.Length >= 4 && e.Args[0] == "--verify-audio-lifecycle")
        {
            _ = VerifyAudioLifecycleAsync(e.Args[1], e.Args[2], e.Args[3], e.Args.Length > 4 ? e.Args[4] : "exit");
            return;
        }
        instance = new Mutex(true, "Local\\AudioFlow.Desktop", out bool first);
        if (!first) { MessageBox.Show("声流已经在运行，可从任务栏托盘打开。", "声流"); Shutdown(); return; }
        string? directory = null;
        for (int i = 0; i < e.Args.Length - 1; i++) if (e.Args[i] == "--data-dir") directory = e.Args[i + 1];
        var window = new MainWindow(directory);
        MainWindow = window;
        window.Show();
        DispatcherUnhandledException += (_, args) =>
        {
            try { File.AppendAllText(Path.Combine(window.ViewModel.Store.DataDirectory, "errors.log"), DateTime.Now + " " + args.Exception + Environment.NewLine); } catch { }
            window.ViewModel.Notify("操作未完成：" + args.Exception.Message); args.Handled = true;
        };
    }
    protected override void OnExit(ExitEventArgs e) { instance?.Dispose(); base.OnExit(e); }
    private async Task RecoverPendingAsync(string directory, int parentId)
    {
        try { await AudioRecovery.RunAsync(directory, parentId); Shutdown(0); }
        catch (Exception error)
        {
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "recovery-errors.log"), error + Environment.NewLine);
            Shutdown(1);
        }
    }
    private async Task VerifyAsync(string destination)
    {
        try { await UiChecks.RunAsync(destination); Shutdown(0); }
        catch (Exception error)
        {
            Directory.CreateDirectory(destination);
            File.WriteAllText(Path.Combine(destination, "ui-error.log"), error.ToString());
            Shutdown(1);
        }
    }
    private async Task VerifySettingsAsync(string directory)
    {
        try { await SettingsChecks.RunAsync(directory); Shutdown(0); }
        catch (Exception error) { Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, "settings-error.log"), error.ToString()); Shutdown(1); }
    }
    private async Task VerifyOnboardingAsync(string directory)
    {
        try { await GettingStartedChecks.RunAsync(directory); Shutdown(0); }
        catch (Exception error) { Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, "onboarding-error.log"), error.ToString()); Shutdown(1); }
    }
    private async Task VerifyStartupAsync(string directory, string report)
    {
        try
        {
            await using (var model = new MainViewModel(directory))
            {
                await model.InitializeAsync();
                var source = model.Graph.Nodes.FirstOrDefault(n => n.Kind == AudioFlow.Core.NodeKind.Application);
                var catalog = new AudioCatalog();
                var pids = source == null ? [] : catalog.FindApplicationProcesses(source.Key);
                File.WriteAllText(report, System.Text.Json.JsonSerializer.Serialize(new
                {
                    model.Running, NodeCount = model.Graph.Nodes.Count, Volume = source?.Volume,
                    SessionVolume = catalog.GetApplicationVolume(pids)?.volume,
                    Path = model.Workspace.ActiveConfigurationPath, model.SaveState
                }, AudioFlow.Core.SceneStore.JsonOptions));
            }
            Shutdown(0);
        }
        catch (Exception error) { File.WriteAllText(report + ".error", error.ToString()); Shutdown(1); }
    }
    private async Task VerifyAudioLifecycleAsync(string directory, string scene, string device, string mode)
    {
        try { await AudioLifecycleVerification.RunAsync(directory, scene, device, mode); }
        catch (Exception error)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "lifecycle-error.log"), error.ToString());
            Shutdown(1);
        }
    }
}
