using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;

namespace AudioFlow.App;

internal sealed class TrayIcon : IDisposable
{
    private const int Callback = 0x8001;
    private readonly MainWindow window;
    private readonly HwndSource source;
    private NotifyIcon data;
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIcon
    {
        public uint Size;
        public IntPtr Window;
        public uint Id, Flags, CallbackMessage;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Guid;
        public IntPtr BalloonIcon;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint message, ref NotifyIcon data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    public TrayIcon(MainWindow window)
    {
        this.window = window;
        var handle = new WindowInteropHelper(window).EnsureHandle();
        source = HwndSource.FromHwnd(handle); source.AddHook(Hook);
        data = new NotifyIcon
        {
            Size = (uint)Marshal.SizeOf<NotifyIcon>(), Window = handle, Id = 1,
            Flags = 1 | 2 | 4, CallbackMessage = Callback,
            Tip = "声流 AudioFlow · 为软件选择声音去向", Info = "", InfoTitle = "",
            Icon = LoadImage(IntPtr.Zero, System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"), 1, 32, 32, 16)
        };
        Shell_NotifyIcon(0, ref data);
    }
    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != Callback) return IntPtr.Zero;
        if ((int)lParam == 0x0203) window.ShowFromTray();
        if ((int)lParam == 0x0205)
        {
            var menu = new ContextMenu { Placement = PlacementMode.MousePoint };
            var show = new MenuItem { Header = "打开声流" }; show.Click += (_, _) => window.ShowFromTray();
            var pause = new MenuItem { Header = window.ViewModel.Running ? "停止声音连接" : "开启声音连接" }; pause.Click += async (_, _) => { if (window.ViewModel.Running) await window.ViewModel.PauseAsync(); else await window.ViewModel.StartAsync(); };
            var restore = new MenuItem { Header = "恢复原设置" }; restore.Click += async (_, _) => await window.ViewModel.RestoreOriginalStateAsync();
            var exit = new MenuItem { Header = "退出" }; exit.Click += (_, _) => window.RequestExit();
            menu.Items.Add(show); menu.Items.Add(pause); menu.Items.Add(restore); menu.Items.Add(new Separator()); menu.Items.Add(exit); menu.IsOpen = true;
        }
        return IntPtr.Zero;
    }
    public void Dispose() { Shell_NotifyIcon(2, ref data); source.RemoveHook(Hook); if (data.Icon != IntPtr.Zero) DestroyIcon(data.Icon); }
}
