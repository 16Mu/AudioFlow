using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AudioFlow.Core;

namespace AudioFlow.App;

internal static class GettingStartedChecks
{
    public static async Task RunAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<string>();
        void Check(bool passed, string name)
        {
            if (!passed) throw new InvalidOperationException(name);
            checks.Add(name);
        }
        static IEnumerable<DependencyObject> Children(DependencyObject parent)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i); yield return child;
                foreach (var nested in Children(child)) yield return nested;
            }
        }
        static void Click(Window window, string name)
        {
            var button = Children(window).OfType<Button>().Single(b => b.Name == name);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
            window.UpdateLayout();
        }
        static void HideForVerification(Window window)
        {
            window.Opacity = 0; window.ShowActivated = false; window.ShowInTaskbar = false;
            window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = -32000; window.Top = -32000;
        }
        ThemeChecks.Run(directory, checks);
        string fixtures = Path.Combine(directory, "profiles-" + Guid.NewGuid().ToString("N"));
        string root = Path.Combine(fixtures, "first-run");
        var main = new MainWindow(root, true); HideForVerification(main); main.Show();
        await using var model = main.ViewModel;
        var state = new GettingStartedState(root);
        var openedLinks = new List<Uri>(); int installerSelections = 0;
        Exception? interactionError = null;
        void Prepare(GettingStartedWindow guide, Action<GettingStartedWindow> interaction)
        {
            HideForVerification(guide);
            guide.Loaded += (_, _) => guide.Dispatcher.BeginInvoke(() =>
            {
                try { interaction(guide); }
                catch (Exception error) { interactionError = error; }
                finally { guide.Close(); }
            }, DispatcherPriority.ApplicationIdle);
        }
        try
        {
            Check(state.NeedsGuide, "a new local profile requests the getting-started guide");
            Check(openedLinks.Count == 0 && installerSelections == 0, "entering the application does not open download links or an installer");
            string before = JsonSerializer.Serialize(model.Graph, SceneStore.JsonOptions);
            bool appeared = main.ShowFirstRunGuide(guide => Prepare(guide, current =>
            {
                Check(current.Step == 0, "the desktop first-run entry starts with the product introduction");
                Check(!main.IsVisible && current.IsVisible, "only the introduction is visible until the user finishes or skips it");
                UiChecks.SavePreview(current, Path.Combine(directory, "guide-01-welcome.png"), 1040, 740);
                Click(current, "GuideNext");
                Check(current.Step == 1 && Children(current).OfType<SimulatedPracticePanel>().Any() && !((Button)current.FindName("GuideNext")).IsEnabled,
                    "Next opens an in-wizard simulation that waits for the user's practice");
                UiChecks.SavePreview(current, Path.Combine(directory, "guide-02-connect.png"), 1040, 740);
                Click(current, "GuidePrevious");
                Check(current.Step == 0, "Back returns to the introduction");
                Click(current, "GuideNext"); Click(current, "GuideStep2");
                Check(current.Step == 2, "the third page provides optional driver preparation");
                Check(Children(current).OfType<TextBlock>().Any(t => t.Text.Contains("还没找到设备")),
                    "a profile without virtual audio devices sees an explicit optional-install state");
                Check(openedLinks.Count == 0 && installerSelections == 0, "browsing all three pages leaves downloads and installation under user control");
                UiChecks.SavePreview(current, Path.Combine(directory, "guide-03-drivers.png"), 1040, 740);
                foreach (var driver in DriverDownloads.All)
                {
                    Click(current, "Download" + driver.Id);
                    Click(current, "Website" + driver.Id);
                }
                Check(openedLinks.Select(u => u.AbsoluteUri).SequenceEqual(DriverDownloads.All.SelectMany(d => new[] { d.DownloadUri.AbsoluteUri, d.WebsiteUri.AbsoluteUri })),
                    "each actual download and website button dispatches the corresponding official destination");
                Click(current, "InstallDownloadedDriver");
                Check(installerSelections == 1, "installation is requested only by its explicit button");
                var content = (FrameworkElement)current.Content;
                current.Width = 860; current.Height = 640; current.UpdateLayout();
                current.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                content.UpdateLayout();
                foreach (var button in Children(current).OfType<Button>().Where(b => b.Name is "GuideNext" or "GuideSkip"))
                {
                    var bounds = button.TransformToAncestor(content).TransformBounds(new(0, 0, button.ActualWidth, button.ActualHeight));
                    Check(bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= content.ActualWidth && bounds.Bottom <= content.ActualHeight && button.ActualHeight > 20,
                        "the compact layout keeps the " + button.Name + " action visible: " + bounds);
                }
                UiChecks.SavePreview(current, Path.Combine(directory, "guide-compact.png"), (int)content.ActualWidth, (int)content.ActualHeight);
                Click(current, "GuideSkip");
            }), openedLinks.Add, _ => { installerSelections++; return "安装选择已取消，仍可先看看。"; });
            if (interactionError != null) throw interactionError;
            Check(appeared && !new GettingStartedState(root).NeedsGuide, "skipping the actual modal guide persists across a fresh preference read");
            Check(!main.ShowFirstRunGuide() && main.IsEnabled && main.IsVisible, "later launches keep the guide dismissed and return to the usable main window");
            Check(before == JsonSerializer.Serialize(model.Graph, SceneStore.JsonOptions) && !model.Running,
                "skipping does not create audio routes or change the routing state");

            await model.RefreshCatalogAsync();
            before = JsonSerializer.Serialize(model.Graph, SceneStore.JsonOptions);
            var settings = new SettingsWindow(model, guide => Prepare(guide, current =>
            {
                Check(current.Step == 0, "settings can reopen the guide after it has already been dismissed");
                Click(current, "GuideStep2");
                UiChecks.SavePreview(current, Path.Combine(directory, "guide-installed-devices.png"), 1040, 740);
                Click(current, "GuideNext");
            }), openedLinks.Add, _ => "已取消选择") { Owner = main };
            HideForVerification(settings); settings.Show(); settings.UpdateLayout();
            try
            {
                Click(settings, "ShowDriverDownloads");
                UiChecks.SavePreview(settings, Path.Combine(directory, "settings-driver-downloads.png"), 780, 740);
                Check(Children(settings).OfType<Button>().Any(b => b.Name == "InstallDownloadedDriver"),
                    "settings retains download, installation and refresh controls");
                int linkCount = openedLinks.Count;
                foreach (var driver in DriverDownloads.All) Click(settings, "Download" + driver.Id);
                Check(openedLinks.Count == linkCount + DriverDownloads.All.Count, "the settings download buttons also dispatch official downloads");
                Click(settings, "ReopenGettingStarted");
                if (interactionError != null) throw interactionError;
                Check(!state.NeedsGuide, "finishing a guide reopened from settings preserves its dismissed state");
                Check(before == JsonSerializer.Serialize(model.Graph, SceneStore.JsonOptions), "reopening the guide preserves the existing audio configuration");
            }
            finally { settings.Close(); }

            string finishRoot = Path.Combine(fixtures, "finish-without-drivers");
            var finishMain = new MainWindow(finishRoot, true); HideForVerification(finishMain); finishMain.Show();
            await using (var finishModel = finishMain.ViewModel)
            {
                try
                {
                    finishMain.ShowFirstRunGuide(guide => Prepare(guide, current =>
                    {
                        Click(current, "GuideStep2");
                        Check(!finishModel.Graph.Channels.Any(), "the optional-driver fixture has no virtual microphone");
                        Click(current, "GuideNext");
                    }));
                    if (interactionError != null) throw interactionError;
                    Check(!new GettingStartedState(finishRoot).NeedsGuide, "Start using completes the guide even with no virtual driver installed");
                }
                finally { finishMain.Close(); }
            }
            string corruptRoot = Path.Combine(fixtures, "corrupt-preference"); Directory.CreateDirectory(corruptRoot);
            foreach (string damaged in new[] { "null", "broken-json" })
            {
                File.WriteAllText(Path.Combine(corruptRoot, "getting-started.json"), damaged);
                Check(new GettingStartedState(corruptRoot).NeedsGuide, "a " + damaged + " guide preference is treated as unseen without touching a workspace");
            }
            string installerRoot = Path.Combine(fixtures, "installer-selection"); Directory.CreateDirectory(installerRoot);
            foreach (var driver in DriverDownloads.All)
            {
                string file = Path.Combine(installerRoot, driver.InstallerName); File.WriteAllText(file, "");
                ProcessStartInfo? dispatched = null;
                DriverDownloads.LaunchInstaller(file, start => dispatched = start);
                Check(dispatched?.FileName == file && dispatched.UseShellExecute && dispatched.Verb == "runas" && dispatched.WorkingDirectory == installerRoot,
                    "the selected " + driver.Name + " setup requests elevation from its extracted directory");
            }
            string wrongSetup = Path.Combine(installerRoot, "unrelated.exe"); File.WriteAllText(wrongSetup, "");
            bool rejected = false;
            try { DriverDownloads.LaunchInstaller(wrongSetup, _ => throw new Exception("Unexpected execution")); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "an unrelated executable is not launched as a driver installer");
            string cancelResult = DriverDownloads.LaunchInstaller(Path.Combine(installerRoot, DriverDownloads.All[0].InstallerName),
                _ => throw new System.ComponentModel.Win32Exception(1223));
            Check(cancelResult.Contains("已取消"), "cancelling the Windows administrator prompt remains a recoverable optional action");
            File.WriteAllText(Path.Combine(directory, "onboarding-report.json"), JsonSerializer.Serialize(new { Passed = true, Count = checks.Count, Checks = checks }, SceneStore.JsonOptions));
        }
        finally { main.Close(); }
    }
}
