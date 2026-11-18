using AudioFlow.Core;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace AudioFlow.App;

public sealed class ApplicationPickerViewModel : Observable
{
    private readonly List<AvailableItem> applications;
    public ObservableCollection<AvailableItem> Choices { get; } = [];
    private string search = "";
    private AvailableItem? selected;
    public string Search { get => search; set { if (Set(ref search, value)) Refresh(); } }
    public AvailableItem? Selected { get => selected; set { if (Set(ref selected, value)) Changed(nameof(CanConfirm)); } }
    public bool CanConfirm => Selected != null;
    public bool NoChoices => Choices.Count == 0;
    public string ResultCount => $"{Choices.Count} 个应用";
    public ApplicationPickerViewModel(IEnumerable<AvailableItem> items)
    {
        applications = items.GroupBy(a => AppIdentity.Normalize(a.Key)).Select(g => g.First()).ToList();
        Refresh();
    }
    private void Refresh()
    {
        Choices.Clear(); foreach (var app in applications.Where(a => a.Matches(Search))) Choices.Add(app);
        if (Selected != null && !Choices.Contains(Selected)) Selected = null;
        Changed(nameof(NoChoices)); Changed(nameof(ResultCount));
    }
    public void Add(AvailableItem item)
    {
        var existing = applications.FirstOrDefault(a => AppIdentity.Normalize(a.Key) == AppIdentity.Normalize(item.Key));
        if (existing == null) { applications.Insert(0, item); existing = item; }
        Search = ""; Refresh(); Selected = existing;
    }
}

public partial class ApplicationPickerWindow : Window
{
    public ApplicationPickerViewModel ViewModel { get; }
    public AvailableItem? SelectedApplication { get; private set; }
    public ApplicationPickerWindow(IReadOnlyList<AvailableItem> applications)
    {
        InitializeComponent(); ViewModel = new(applications); DataContext = ViewModel;
        Loaded += (_, _) => ApplicationSearch.Focus();
    }
    private void ConfirmClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Selected is not { } selected) return;
        SelectedApplication = selected;
        if (IsVisible) DialogResult = true;
    }
    private void CancelClick(object sender, RoutedEventArgs e) => Close();
    private void ApplicationDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(ApplicationGrid, source) is ListBoxItem)
            ConfirmClick(sender, e);
    }
    private void PickerKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        else if (e.Key == Key.Enter && ViewModel.CanConfirm) { ConfirmClick(sender, e); e.Handled = true; }
    }
    private void BrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择目标应用", Filter = "应用程序 (*.exe)|*.exe" };
        if (dialog.ShowDialog(this) != true) return;
        string name = Path.GetFileNameWithoutExtension(dialog.FileName);
        try { if (FileVersionInfo.GetVersionInfo(dialog.FileName).FileDescription is { Length: > 0 } description) name = description; } catch { }
        ViewModel.Add(new(name, dialog.FileName, NodeKind.Application, "手动选择 · 等待软件运行"));
    }
}
