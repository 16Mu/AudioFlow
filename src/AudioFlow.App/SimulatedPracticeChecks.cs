using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AudioFlow.Core;

namespace AudioFlow.App;

internal static class SimulatedPracticeChecks
{
    public static async Task RunAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<string>();
        void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); checks.Add(name); }
        static IEnumerable<DependencyObject> Children(DependencyObject parent)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i); yield return child;
                foreach (var nested in Children(child)) yield return nested;
            }
        }
        var main = new MainWindow(Path.Combine(directory, "profile-" + Guid.NewGuid().ToString("N")), true)
        {
            Opacity = 0, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000, Top = -32000
        };
        main.Show(); await using var model = main.ViewModel;
        string graphBefore = JsonSerializer.Serialize(model.Graph, SceneStore.JsonOptions);
        Exception? interactionError = null;
        try
        {
            main.ShowFirstRunGuide(guide =>
            {
                guide.Opacity = 0; guide.ShowActivated = false; guide.ShowInTaskbar = false;
                guide.WindowStartupLocation = WindowStartupLocation.Manual; guide.Left = -32000; guide.Top = -32000;
                guide.Loaded += (_, _) => guide.Dispatcher.BeginInvoke(async () =>
                {
                    try
                    {
                        void Click(string name)
                        {
                            var button = Children(guide).OfType<Button>().Single(b => b.Name == name);
                            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button)); guide.UpdateLayout();
                        }
                        Click("GuideNext");
                        var exercise = Children(guide).OfType<SimulatedPracticePanel>().Single();
                        var next = (Button)guide.FindName("GuideNext");
                        Check(exercise.Stage == 1 && !next.IsEnabled && ReferenceEquals(Window.GetWindow(exercise), guide),
                            "practice starts inside the same guide window and waits for the user");
                        Check(!exercise.BeginDrag(), "dragging is not accepted before selecting software and a device");
                        UiChecks.SavePreview(guide, Path.Combine(directory, "practice-01-start.png"), 1040, 740);
                        Click("DemoChooseMusic");
                        Check(exercise.Stage == 2, "the simulated software button advances to adding a device");
                        UiChecks.SavePreview(guide, Path.Combine(directory, "practice-02-software.png"), 1040, 740);
                        Click("DemoAddDevice");
                        Check(exercise.Stage == 3 && !next.IsEnabled, "adding the simulated card still requires selecting a device");
                        var picker = Children(guide).OfType<ComboBox>().Single(b => b.Name == "DemoDeviceChoice");
                        picker.SelectedIndex = 2; guide.UpdateLayout();
                        Check(exercise.Stage == 4, "choosing the simulated speaker advances to dragging");
                        UiChecks.SavePreview(guide, Path.Combine(directory, "practice-03-drag.png"), 1040, 740);
                        Check(exercise.BeginDrag(), "the simulated source circle can begin a drag after setup");
                        exercise.MoveDrag(exercise.TargetAnchor + new Vector(90, 60));
                        exercise.FinishDrag(exercise.TargetAnchor + new Vector(90, 60));
                        Check(exercise.Stage == 4 && !next.IsEnabled, "releasing away from the target does not automatically connect or complete practice");
                        exercise.BeginDrag(); exercise.MoveDrag(exercise.TargetAnchor); exercise.FinishDrag(exercise.TargetAnchor);
                        Check(exercise.Stage == 5 && !next.IsEnabled, "releasing on the simulated target waits for the start action");
                        UiChecks.SavePreview(guide, Path.Combine(directory, "practice-04-connected.png"), 1040, 740);
                        Click("DemoStart"); await Task.Delay(160);
                        Check(exercise.IsCompleted && next.IsEnabled, "starting the simulated preview unlocks the wizard's next step");
                        UiChecks.SavePreview(guide, Path.Combine(directory, "practice-05-complete.png"), 1040, 740);
                        Check(!model.Running && graphBefore == JsonSerializer.Serialize(model.Graph, SceneStore.JsonOptions),
                            "all simulated selections, dragging and preview leave actual audio and the user's graph untouched");
                        Click("DemoStart");
                        Check(exercise.IsCompleted && next.IsEnabled, "stopping the simulated preview retains completion and the user's practice line");
                        Click("DemoReset");
                        Check(exercise.Stage == 1 && !next.IsEnabled, "resetting clears only the simulated exercise and requires completing it again");
                        Click("DemoChooseVideo"); Click("DemoAddDevice");
                        picker.SelectedIndex = 1; exercise.BeginDrag(); exercise.FinishDrag(exercise.TargetAnchor); Click("DemoStart");
                        Click("GuideNext");
                        Check(guide.Step == 2 && guide.IsVisible, "completed simulation continues to the driver page inside the same guide");
                        Check(Children(guide).OfType<TextBlock>().Any(t => t.Text.Contains("两款都装，才有 2 路")),
                            "the driver page states that each driver alone has one independent sound and both are needed for two");
                        Click("GuideSkip");
                    }
                    catch (Exception error) { interactionError = error; }
                    finally { guide.Close(); }
                }, DispatcherPriority.ApplicationIdle);
            });
            if (interactionError != null) throw interactionError;
            Check(main.IsEnabled && !model.Running && graphBefore == JsonSerializer.Serialize(model.Graph, SceneStore.JsonOptions),
                "closing the guide returns to unchanged, usable software");
            Check(!new GettingStartedState(model.Store.DataDirectory).NeedsGuide, "finishing or skipping the guide is remembered after simulated practice");
            File.WriteAllText(Path.Combine(directory, "practice-report.json"), JsonSerializer.Serialize(new { Passed = true, Count = checks.Count, Checks = checks }, SceneStore.JsonOptions));
        }
        finally { main.Close(); }
    }
}
