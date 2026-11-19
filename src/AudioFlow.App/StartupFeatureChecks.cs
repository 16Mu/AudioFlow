using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AudioFlow.Core;
using Microsoft.Win32;

namespace AudioFlow.App;

internal static class StartupFeatureChecks
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string command, out int count);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
    private static string[] ParseCommand(string command)
    {
        var pointer = CommandLineToArgvW(command, out int count);
        if (pointer == IntPtr.Zero) throw new InvalidOperationException("Command parsing failed.");
        try { return Enumerable.Range(0, count).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, i * IntPtr.Size))!).ToArray(); }
        finally { LocalFree(pointer); }
    }
    private static IEnumerable<DependencyObject> Children(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); yield return child;
            foreach (var nested in Children(child)) yield return nested;
        }
    }
    public static async Task RunAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<string>();
        void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); checks.Add(name); }
        string fixtures = Path.Combine(directory, "profile-" + Guid.NewGuid().ToString("N"));
        string keyPath = @"Software\AudioFlow\Verification\" + Guid.NewGuid().ToString("N");
        string programFolder = Path.Combine(fixtures, "模拟安装目录 with spaces"); Directory.CreateDirectory(programFolder);
        string program = Path.Combine(programFolder, "AudioFlow.exe"); File.WriteAllText(program, "");
        var registration = new WindowsAutoStart(fixtures, program, keyPath, "StartupProbe");
        try
        {
            Check(!registration.Enabled, "a new profile does not enable Windows startup automatically");
            await using (var model = new MainViewModel(fixtures))
            {
                model.UseAutoStartRegistration(registration);
                var settings = new SettingsWindow(model) { Opacity = 0, ShowActivated = false, ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000 };
                settings.Show(); settings.UpdateLayout();
                try
                {
                    var checkbox = Children(settings).OfType<CheckBox>().Single(c => c.Name == "StartWithWindows");
                    Check(checkbox.IsChecked == false, "the settings page exposes an unchecked startup control initially");
                    checkbox.IsChecked = true;
                    Check(registration.Enabled && model.StartWithWindows, "the actual checkbox registers the startup command");
                    using (var key = Registry.CurrentUser.OpenSubKey(keyPath, true)!)
                    {
                        key.SetValue("UnrelatedValue", "keep", RegistryValueKind.String);
                        var args = ParseCommand((string)key.GetValue("StartupProbe")!);
                        int data = Array.IndexOf(args, "--data-dir");
                        Check(args[0] == program && args.Contains("--autostart") && data >= 0 && args[data + 1] == Path.GetFullPath(fixtures),
                            "Windows parses executable and data paths with spaces as separate, intact arguments");
                    }
                    var moved = new WindowsAutoStart(fixtures, Environment.ProcessPath, keyPath, "StartupProbe");
                    moved.RefreshPathIfEnabled();
                    using (var key = Registry.CurrentUser.OpenSubKey(keyPath)!)
                        Check(ParseCommand((string)key.GetValue("StartupProbe")!)[0] == Environment.ProcessPath,
                            "an enabled entry follows the executable's new installation location");
                    checkbox.IsChecked = false;
                    using (var key = Registry.CurrentUser.OpenSubKey(keyPath)!)
                        Check(key.GetValue("StartupProbe") == null && (string?)key.GetValue("UnrelatedValue") == "keep",
                            "disabling startup removes only the application's entry");
                }
                finally { settings.Close(); }
            }
            var main = new MainWindow(Path.Combine(fixtures, "first-run"), true) { Opacity = 0, ShowActivated = false, ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000 };
            Application.Current.MainWindow = main;
            await using (var model = main.ViewModel)
            {
                Exception? interactionError = null;
                int guideCount = 0;
                try
                {
                    Check(!main.IsVisible, "the main window is not displayed before first-run preparation");
                    await App.ShowInitialWindowAsync(main, guide =>
                    {
                        guideCount++;
                        guide.Opacity = 0; guide.ShowActivated = false; guide.ShowInTaskbar = false;
                        guide.WindowStartupLocation = WindowStartupLocation.Manual; guide.Left = -32000; guide.Top = -32000;
                        guide.Loaded += (_, _) => guide.Dispatcher.BeginInvoke(() =>
                        {
                            try
                            {
                                Check(!main.IsVisible && guide.IsVisible && !model.Running,
                                    "the real startup function displays only the guide and does not start routing behind it");
                                Check(Application.Current.Windows.OfType<Window>().Where(w => w.IsVisible).SequenceEqual([guide]),
                                    "the introduction is the only visible application window");
                                UiChecks.SavePreview(guide, Path.Combine(directory, "first-guide-only.png"), 1040, 740);
                                var skip = (Button)guide.FindName("GuideSkip");
                                skip.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, skip));
                            }
                            catch (Exception error) { interactionError = error; }
                            finally { guide.Close(); }
                        }, DispatcherPriority.ApplicationIdle);
                    });
                    if (interactionError != null) throw interactionError;
                    Check(guideCount == 1 && main.IsVisible && !new GettingStartedState(model.Store.DataDirectory).NeedsGuide,
                        "finishing or skipping the introduction reveals the main window and remembers completion");
                    await App.ShowInitialWindowAsync(main, _ => guideCount++);
                    Check(guideCount == 1 && main.IsVisible, "subsequent launches display the main window without another introduction");
                }
                finally { main.Close(); }
            }
            File.WriteAllText(Path.Combine(directory, "startup-feature-report.json"),
                JsonSerializer.Serialize(new { Passed = true, Count = checks.Count, Checks = checks }, SceneStore.JsonOptions));
        }
        finally
        {
            if (!keyPath.StartsWith(@"Software\AudioFlow\Verification\", StringComparison.Ordinal) || keyPath.EndsWith('\\'))
                throw new InvalidOperationException("Unsafe fixture registry key.");
            Registry.CurrentUser.DeleteSubKeyTree(keyPath, false);
        }
    }
}
