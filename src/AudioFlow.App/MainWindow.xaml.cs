using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Nodify.Events;
using AudioFlow.Core;
using System.Windows.Media;

namespace AudioFlow.App;

public partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }
    private TrayIcon? tray;
    private bool exit;
    private bool closing;
    private bool shutdownReady;
    private readonly bool verification;
    public MainWindow(string? dataDirectory = null, bool verification = false)
    {
        this.verification = verification;
        InitializeComponent();
        ViewModel = new(dataDirectory); DataContext = ViewModel;
        ViewModel.OpenSettings = () => new SettingsWindow(ViewModel) { Owner = this }.ShowDialog();
        ViewModel.PickSharedChannel = channels =>
        {
            var picker = new SharedChannelWindow(ViewModel, channels) { Owner = this };
            return picker.ShowDialog() == true ? picker.SelectedChannel : null;
        };
        ViewModel.PickApplication = applications =>
        {
            var picker = new ApplicationPickerWindow(applications) { Owner = this };
            return picker.ShowDialog() == true ? picker.SelectedApplication : null;
        };
        ViewModel.FitRequested = () => Dispatcher.BeginInvoke(() => Editor.FitToScreen());
        ViewModel.MeasureLayoutCard = MeasureLayoutCard;
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        if (File.Exists(iconPath)) Icon = BitmapFrame.Create(new Uri(iconPath));
        Loaded += async (_, _) =>
        {
            if (verification) return;
            tray = new(this);
            await ViewModel.InitializeAsync();
            if (!closing && IsVisible) ShowFirstRunGuide();
        };
    }
    internal bool ShowFirstRunGuide(Action<GettingStartedWindow>? prepare = null, Action<Uri>? openLink = null, Func<Window?, string>? installDriver = null)
    {
        if (!new GettingStartedState(ViewModel.Store.DataDirectory).NeedsGuide) return false;
        var guide = new GettingStartedWindow(ViewModel, openLink, installDriver) { Owner = this };
        prepare?.Invoke(guide);
        guide.ShowDialog();
        return true;
    }
    private LayoutCard MeasureLayoutCard(NodeViewModel node)
    {
        Editor.UpdateLayout();
        var container = Editor.ItemContainerGenerator.ContainerFromItem(node) as FrameworkElement;
        double width = container?.ActualWidth > 0 ? container.ActualWidth : 326;
        double height = container?.ActualHeight > 0 ? container.ActualHeight :
            (node.SoftwareCard ? 345 + (node.Receiving ? 90 : 0) + (node.ShowRecordingControls ? 175 : 0) : 330) + (node.InputLevels.Count + node.OutputLevels.Count) * 76;
        double PortY(PortViewModel? port)
        {
            var connector = container == null ? null : Descendants(container).OfType<Nodify.Connector>().FirstOrDefault(c => ReferenceEquals(c.DataContext, port));
            double offset = connector == null ? 140 : connector.TransformToAncestor(container!).Transform(new Point(connector.ActualWidth / 2, connector.ActualHeight / 2)).Y;
            return offset >= 0 && offset <= height ? offset : 140;
        }
        return new(node.Id, node.Name, node.Model.Kind, node.Model.PrimaryOutput, width, height,
            PortY(node.Inputs.FirstOrDefault()), PortY(node.Outputs.FirstOrDefault()));
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject value)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(value); i++)
        {
            var child = VisualTreeHelper.GetChild(value, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private void SettingsClick(object sender, RoutedEventArgs e) => ViewModel.OpenSettings?.Invoke();
    private void FitClick(object sender, RoutedEventArgs e) => Editor.FitToScreen();
    private void MinimizeClick(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void MaximizeClick(object sender, RoutedEventArgs e) { if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this); else SystemCommands.MaximizeWindow(this); }
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
    private void ExitClick(object sender, RoutedEventArgs e) => RequestExit();
    private void AddSoftwareContext(object sender, RoutedEventArgs e) => ViewModel.AddBlank(AudioFlow.Core.NodeKind.Application, (sender as MenuItem)?.DataContext as NodeViewModel);
    private void AddMicrophoneContext(object sender, RoutedEventArgs e) => ViewModel.AddBlank(AudioFlow.Core.NodeKind.Microphone, (sender as MenuItem)?.DataContext as NodeViewModel);
    private void AddPlaybackContext(object sender, RoutedEventArgs e) => ViewModel.AddBlank(AudioFlow.Core.NodeKind.Playback, (sender as MenuItem)?.DataContext as NodeViewModel);
    private void ArrangeContext(object sender, RoutedEventArgs e) => ViewModel.Arrange();
    private void CopyInputContext(object sender, RoutedEventArgs e) => ViewModel.CopyInputCommand.Execute((sender as MenuItem)?.DataContext);
    private void DeleteCardContext(object sender, RoutedEventArgs e) => ViewModel.DeleteCardCommand.Execute((sender as MenuItem)?.DataContext);
    private void ConfigureRecordingContext(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is NodeViewModel node) ViewModel.ConfigureRecording(node);
    }
    private void DeleteRouteClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is RouteLevelViewModel row) ViewModel.RemoveConnection(row.Connection);
        e.Handled = true;
    }
    private void RouteRowSelected(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is RouteLevelViewModel row)
        { ViewModel.SelectedConnections.Clear(); ViewModel.SelectedConnections.Add(row.Connection); }
        e.Handled = true;
    }
    private void PortConnectionCompleted(object sender, PendingConnectionEventArgs e)
    {
        ViewModel.Pending.WasCanceled = e.Canceled;
        if (e.Canceled) Dispatcher.BeginInvoke(() => { if (ViewModel.Reconnecting) ViewModel.CompleteReconnect(null, true); ViewModel.Pending.Source = null; ViewModel.Pending.WasCanceled = false; });
    }
    private void PortConnectionStarted(object sender, PendingConnectionEventArgs e)
    {
        if (e.SourceConnector is PortViewModel port) e.Anchor = ViewModel.PendingSource(port).Anchor;
    }
    private void ConnectionMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && (sender as FrameworkElement)?.DataContext is ConnectionViewModel connection)
        { ViewModel.RemoveConnection(connection); e.Handled = true; }
    }
    public void ShowFromTray() { Show(); WindowState = WindowState.Normal; Activate(); }
    public void RequestExit() { exit = true; Close(); }
    protected override async void OnClosing(CancelEventArgs e)
    {
        if (verification) { base.OnClosing(e); return; }
        if (shutdownReady) { base.OnClosing(e); return; }
        e.Cancel = true;
        if (closing) return;
        if (!exit && ViewModel.CloseToTray) { Hide(); return; }
        closing = true;
        IsEnabled = false;
        try { await ViewModel.DisposeAsync(recoverAfterExit: true); }
        catch (Exception error)
        {
            closing = false; IsEnabled = true;
            ViewModel.Notify("退出还原未完成，请点击“恢复原有状态”后重试：" + error.Message);
            return;
        }
        tray?.Dispose();
        shutdownReady = true;
        Application.Current.Shutdown();
    }
}
