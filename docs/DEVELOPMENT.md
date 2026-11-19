# 开发与验证

## 构建

工具链由 vsm 管理。依赖使用 NuGet 与本地 .NET 工具清单。

    vsm current
    vsm list dotnet
    dotnet tool restore
    .\scripts\build.ps1 -Verify

公开版本 v0.0.1。构建输出为 dist\AudioFlow-0.0.1：MSI、便携 ZIP、源码 ZIP、使用说明、发布说明与校验文件。
发布为框架依赖的 .NET 10 软件，不在项目内安装运行时副本。
旧版含 Mode 与两份画布配置的工作区会先保存 workspace.json.pre-migration，再合并为共享配置。

当前开发机 vsm 的 10.0.102 纳管路径实际含
11.0.100-rc.2.26504.105 SDK；global.json 为本机锁定该可用 SDK。
软件目标运行时为稳定 .NET 10。更换开发机时应按实际已纳管 SDK 调整 global.json。

## 验证

    dotnet build tests/AudioFlow.Verification -c Release
    .\tests\AudioFlow.Verification\bin\Release\net10.0-windows10.0.22000.0\AudioFlow.Verification.exe --unit
    .\tests\AudioFlow.Verification\bin\Release\net10.0-windows10.0.22000.0\AudioFlow.Verification.exe --channel-tests
    .\tests\AudioFlow.Verification\bin\Release\net10.0-windows10.0.22000.0\AudioFlow.Verification.exe --live 1800

新手引导与设置检查：

    .\src\AudioFlow.App\bin\Release\net10.0-windows10.0.22000.0\win-x64\AudioFlow.exe --verify-onboarding .cache\onboarding-check
    .\src\AudioFlow.App\bin\Release\net10.0-windows10.0.22000.0\win-x64\AudioFlow.exe --verify-settings .cache\settings-check
    .\src\AudioFlow.App\bin\Release\net10.0-windows10.0.22000.0\win-x64\AudioFlow.exe --verify-startup-features .cache\startup-check
    .\src\AudioFlow.App\bin\Release\net10.0-windows10.0.22000.0\win-x64\AudioFlow.exe --verify-connections .cache\connection-check

新增检查覆盖真实设置开关、Windows 启动命令的路径解析、引导与主窗口的显示顺序，以及线条的点选、右键、暂停、恢复、删除、撤销与保存。自启动检查使用隔离的测试注册表位置，不修改用户的真实开机启动项；连线检查编译声音计划，不开启真实声音。

界面验证使用 --verify-ui <报告目录>，会启动自有测试音程序并走实际退出与还原流程，
同时验证已关闭应用重开、无音频会话时保留恢复记录及退出后的恢复助手。

真实音频验证使用两种不同频率的合成信号，检查四路输出矩阵、音量、
按连线停止耳机输出、两路麦克风隔离、静音及退出恢复。只记录统计数据，不保存录音。
本机 live 验证选择内置扬声器、Steam 虚拟输出和两路虚拟音频线；
其他硬件组合可调整验证器中的设备选择。

## 结构

- AudioFlow.Core：共享路由图、工作区与导入导出、校验、有界音频缓冲。
- AudioFlow.Audio：按进程树捕获、设备混音、Windows 输出偏好与恢复日志。
- AudioFlow.App：WPF / Nodify 节点界面与托盘。
- AudioFlow.Verification：纯逻辑检查与真实音频信号验证。

[App Audio Router 参考项目](https://github.com/Eververdants/AppAudioRouter)。
第三方许可见 THIRD_PARTY_NOTICES.md。图谱与工具链记录见 docs/IMPLEMENTATION.md。
主题颜色、字体与圆角统一由 src/AudioFlow.App/Themes/Tokens.xaml 管理，共享控件模板见 Themes/Controls.xaml。
主题检查会拒绝视图中的硬编码颜色，并验证正常、悬停、选中与禁用文字对比度至少为 4.5:1。

录音软件卡片可更改默认录音设备，“不进行输入”与开关同步。关闭录音使用一条空闲、无声音的独立虚拟通道，保持其他软件的录音。通道池根据已安装设备动态扩容，用尽时显示占用对象并明确选择是否共享混音。
