using AudioFlow.Core;
using System.Windows;
using System.Windows.Controls;

namespace AudioFlow.App;

public sealed class SharedChannelWindow : Window
{
    public VirtualChannel? SelectedChannel { get; private set; }
    public SharedChannelWindow(MainViewModel model, IReadOnlyList<VirtualChannel> channels)
    {
        Title = "独立虚拟麦克风已用完"; Width = 640; Height = 390; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Theme.Brush("Surface.Window"); Foreground = Theme.Brush("Text.Primary");
        var panel = new StackPanel { Margin = new(24) };
        panel.Children.Add(new TextBlock { Text = model.ChannelCapacity, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "继续共享会让接收软件听到此通道的同一份混音。选择前请核对现有接收软件与声音来源。", TextWrapping = TextWrapping.Wrap, Margin = new(0, 12, 0, 12) });
        var picker = new ComboBox { ItemsSource = channels, DisplayMemberPath = "Name", SelectedIndex = channels.Count > 0 ? 0 : -1 };
        panel.Children.Add(picker);
        var owners = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new(0, 14, 0, 18) }; panel.Children.Add(owners);
        void Update() => owners.Text = picker.SelectedItem is VirtualChannel channel ? model.ChannelOwners(channel) : "没有已连接的通道，请安装或重新检测虚拟设备。";
        picker.SelectionChanged += (_, _) => Update(); Update();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消", IsCancel = true };
        var refresh = new Button { Content = "重新检测设备" }; refresh.Click += async (_, _) => { await model.RefreshCatalogAsync(); DialogResult = false; };
        var share = new Button { Content = "使用此通道共享混音", IsEnabled = channels.Count > 0 };
        share.Click += (_, _) => { SelectedChannel = picker.SelectedItem as VirtualChannel; DialogResult = SelectedChannel != null; };
        actions.Children.Add(cancel); actions.Children.Add(refresh); actions.Children.Add(share); panel.Children.Add(actions); Content = panel;
    }
}
