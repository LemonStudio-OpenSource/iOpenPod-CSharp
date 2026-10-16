using IOpenPod.App.Logging;
using IOpenPod.App.Rockbox;
using IOpenPod.App.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IOpenPod.WinUI.Pages;

/// <summary>Rockbox 安装页面。</summary>
public sealed partial class RockboxPage : Page, IRefreshable
{
    private static readonly (RockboxInstallMode Mode, string Label)[] Modes =
    [
        (RockboxInstallMode.SystemOnly, "只安装系统文件（可逆，不改动固件）"),
        (RockboxInstallMode.SystemAndBootloader, "安装系统文件并安装引导程序（完整安装）"),
        (RockboxInstallMode.BootloaderOnly, "只安装引导程序"),
        (RockboxInstallMode.Uninstall, "卸载：删除设备上的 .rockbox"),
    ];

    private readonly RockboxInstaller _installer;
    private CancellationTokenSource? _cancellation;
    private bool _loading;
    private RockboxTarget? _target;
    private RockboxStatus? _status;

    public RockboxPage()
    {
        InitializeComponent();

        _installer = new RockboxInstaller(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "iOpenPod",
            "rockbox"));

        _loading = true;
        foreach (var (_, label) in Modes)
        {
            ModeBox.Items.Add(label);
        }

        ChannelBox.Items.Add("稳定版（release）");
        ChannelBox.Items.Add("每日构建（daily）");
        _loading = false;

        Loaded += (_, _) => Refresh();
    }

    /// <summary>刷新设备与 Rockbox 状态。</summary>
    public void Refresh()
    {
        var device = AppState.Current.Device;
        var settings = AppState.Current.Settings;

        _loading = true;
        try
        {
            ModeBox.SelectedIndex = 0;
            ChannelBox.SelectedIndex = settings.RockboxChannel == "daily" ? 1 : 0;
            VersionBox.Value = double.TryParse(settings.RockboxReleaseVersion, out var version) ? version : 4.0;
            MirrorBox.Text = settings.RockboxMirrorUrl;
            BackupFirmwareSwitch.IsOn = settings.RockboxBackupBeforeInstall;
            BackupAppleSwitch.IsOn = settings.RockboxBackupBeforeInstall;
            DualBootSwitch.IsOn = settings.RockboxKeepAppleFirmware;
            BackupExistingSwitch.IsOn = true;
        }
        finally
        {
            _loading = false;
        }

        if (device is null)
        {
            _target = null;
            TargetText.Text = "尚未加载设备。请先在“我的 iPod”页面加载一个 iPod。";
            StatusText.Text = string.Empty;
            NoticeBar.Severity = InfoBarSeverity.Warning;
            NoticeBar.Title = "尚未加载设备";
            NoticeBar.Message = "Rockbox 安装需要先加载一个已连接的 iPod。";
            NoticeBar.IsOpen = true;
            RunButton.IsEnabled = false;
            InspectButton.IsEnabled = false;
            UpdateDownloadHint();
            return;
        }

        InspectButton.IsEnabled = true;
        _target = RockboxTargets.Resolve(device.Profile);

        TargetText.Text =
            $"{device.Name}（{device.ModelDisplayName}）　挂载点 {device.MountPoint}\n" +
            $"Rockbox 目标：{(_target?.Supported == true ? _target.Name : "不支持")}" +
            (_target?.IsExperimental == true ? "（实验性）" : string.Empty) +
            $"\n引导程序方式：{DescribeMethod(_target?.BootloaderMethod)}";

        if (_target is null || !_target.Supported)
        {
            NoticeBar.Severity = InfoBarSeverity.Warning;
            NoticeBar.Title = "该机型没有 Rockbox 支持";
            NoticeBar.Message = _target?.Note ?? "Rockbox 官方没有提供这个机型的构建。";
            NoticeBar.IsOpen = true;
            RunButton.IsEnabled = false;
        }
        else
        {
            NoticeBar.Severity = InfoBarSeverity.Informational;
            NoticeBar.Title = $"目标机型 {_target.Name}";
            NoticeBar.Message = string.IsNullOrWhiteSpace(_target.Note)
                ? "系统文件安装是安全且可逆的；引导程序安装会改写固件分区。"
                : _target.Note;
            NoticeBar.IsOpen = true;
            RunButton.IsEnabled = true;
        }

        StatusText.Text = _status is null
            ? "点击“刷新状态”检查设备上当前的 Rockbox 安装情况。"
            : $"系统文件：{(_status.SystemInstalled ? $"已安装（{_status.SystemVersion ?? "版本未知"}，{_status.InstalledBytes / 1024.0 / 1024.0:F1} MB）" : "未安装")}　" +
              $"引导程序：{(_status.BootloaderInstalled ? "已安装" : "未检测到")}　{_status.Note}";

        UpdateDownloadHint();
        UpdateRunButtonText();
    }

    private static string DescribeMethod(string? method) => method switch
    {
        "ipodpatcher" => "ipodpatcher（需要管理员权限）",
        "mks5lboot" => "mks5lboot（需要 DFU 模式）",
        _ => "不支持",
    };

    private RockboxOptions BuildOptions()
    {
        var settings = AppState.Current.Settings;
        return new RockboxOptions
        {
            MirrorBaseUrl = MirrorBox.Text.Trim(),
            Channel = ChannelBox.SelectedIndex == 1 ? "daily" : "release",
            ReleaseVersion = VersionBox.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture),
            Mode = Modes[Math.Max(0, ModeBox.SelectedIndex)].Mode,
            BackupFirmwarePartition = BackupFirmwareSwitch.IsOn,
            BackupAppleFirmware = BackupAppleSwitch.IsOn,
            KeepAppleFirmwareDualBoot = DualBootSwitch.IsOn,
            BackupExistingInstallation = BackupExistingSwitch.IsOn,
            RemoveBootloaderOnUninstall = RemoveBootloaderSwitch.IsOn,
            ExtraIpodpatcherArguments = ExtraArgsBox.Text.Trim(),
            CustomSystemZipPath = NullIfEmpty(CustomSystemZipBox.Text),
            CustomBootloaderPath = NullIfEmpty(CustomBootloaderBox.Text),
            CustomIpodpatcherPath = NullIfEmpty(CustomIpodpatcherBox.Text),
            Mks5lbootPath = NullIfEmpty(Mks5lbootBox.Text),
        };
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private void UpdateDownloadHint()
    {
        if (_target is null || !_target.Supported)
        {
            DownloadUrlText.Text = string.Empty;
            return;
        }

        var options = BuildOptions();
        var (system, bootloader) = RockboxTargets.DescribeDownloads(_target, options);
        DownloadUrlText.Text = $"系统文件：{system}\n引导程序：{bootloader}";
    }

    private void UpdateRunButtonText()
    {
        RunButtonText.Text = Modes[Math.Max(0, ModeBox.SelectedIndex)].Mode switch
        {
            RockboxInstallMode.Uninstall => "开始卸载",
            RockboxInstallMode.BootloaderOnly => "安装引导程序",
            RockboxInstallMode.SystemAndBootloader => "完整安装",
            _ => "安装系统文件",
        };
    }

    private void OnModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        UpdateRunButtonText();
    }

    private void OnChannelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        AppState.Current.Settings.RockboxChannel = ChannelBox.SelectedIndex == 1 ? "daily" : "release";
        AppState.Current.SaveSettings();
        UpdateDownloadHint();
    }

    private void OnVersionChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || double.IsNaN(args.NewValue))
        {
            return;
        }

        AppState.Current.Settings.RockboxReleaseVersion =
            VersionBox.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        AppState.Current.SaveSettings();
        UpdateDownloadHint();
    }

    private void OnOptionToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var options = BuildOptions();
        AppState.Current.Settings.RockboxMirrorUrl = options.MirrorBaseUrl;
        AppState.Current.Settings.RockboxBackupBeforeInstall = options.BackupFirmwarePartition;
        AppState.Current.Settings.RockboxKeepAppleFirmware = options.KeepAppleFirmwareDualBoot;
        AppState.Current.SaveSettings();
    }

    private async void OnInspectClick(object sender, RoutedEventArgs e)
    {
        var device = AppState.Current.Device;
        if (device is null)
        {
            return;
        }

        InspectButton.IsEnabled = false;
        ProgressText.Text = "正在检查设备状态…";

        try
        {
            _status = await _installer.InspectAsync(device.MountPoint);
            MainWindow.SetStatus($"Rockbox 状态：系统文件{(_status.SystemInstalled ? "已安装" : "未安装")}，" +
                                 $"引导程序{(_status.BootloaderInstalled ? "已安装" : "未检测到")}。");
        }
        catch (Exception error)
        {
            AppLog.Error("Rockbox", "状态检查失败", error);
            ProgressText.Text = $"检查失败：{error.Message}";
        }
        finally
        {
            InspectButton.IsEnabled = true;
            Refresh();
        }
    }

    private async void OnRunClick(object sender, RoutedEventArgs e)
    {
        var device = AppState.Current.Device;
        if (device is null || _target is null || !_target.Supported)
        {
            return;
        }

        var options = BuildOptions();

        if (options.Mode is RockboxInstallMode.SystemAndBootloader or RockboxInstallMode.BootloaderOnly &&
            _target.BootloaderMethod == "mks5lboot")
        {
            await MainWindow.ShowErrorAsync("该机型不能用 ipodpatcher",
                "iPod classic 需要 mks5lboot 在 DFU 模式下安装引导程序，" +
                "请使用 Rockbox Utility，或在本页面填写 mks5lboot 路径后手动执行。");
            return;
        }

        if (!await MainWindow.ConfirmAsync("确认执行",
                $"将在 {device.Name} 上执行：{Modes[Math.Max(0, ModeBox.SelectedIndex)].Label}。\n\n" +
                (options.Mode == RockboxInstallMode.Uninstall
                    ? "这会删除设备上的 .rockbox 目录（会先备份到电脑）。是否继续？"
                    : "系统文件安装可逆；引导程序安装会改写固件分区，失败后可能需要用 iTunes 恢复设备。是否继续？"),
                "继续"))
        {
            return;
        }

        _cancellation = new CancellationTokenSource();
        RunButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        Progress.Value = 0;
        StepList.ItemsSource = null;

        var steps = new List<string>();
        var progress = new Progress<RockboxProgress>(value =>
        {
            Progress.Value = value.Fraction;
            ProgressText.Text = value.Display;
        });

        try
        {
            var result = await _installer.RunAsync(
                device.MountPoint, _target, options, progress, _cancellation.Token);

            steps.AddRange(result.Steps);
            StepList.ItemsSource = steps;

            ProgressText.Text = result.Message;
            MainWindow.SetStatus(result.Message);

            NoticeBar.Severity = result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error;
            NoticeBar.Title = result.Success ? "执行完成" : "执行失败";
            NoticeBar.Message = result.Message +
                                (result.BackupDirectory is null ? string.Empty : $"\n备份目录：{result.BackupDirectory}") +
                                (result.LogFile is null ? string.Empty : $"\n日志文件：{result.LogFile}");
            NoticeBar.IsOpen = true;
        }
        catch (OperationCanceledException)
        {
            ProgressText.Text = "已取消。";
            MainWindow.SetStatus("Rockbox 操作已取消。");
        }
        catch (Exception error)
        {
            AppLog.Error("Rockbox", "安装失败", error);
            ProgressText.Text = $"失败：{error.Message}";
            await MainWindow.ShowErrorAsync("执行失败", error.Message);
        }
        finally
        {
            _cancellation?.Dispose();
            _cancellation = null;
            CancelButton.IsEnabled = false;
            RunButton.IsEnabled = true;
            await RefreshStatusQuietlyAsync();
        }
    }

    private async Task RefreshStatusQuietlyAsync()
    {
        var device = AppState.Current.Device;
        if (device is null)
        {
            return;
        }

        try
        {
            _status = await _installer.InspectAsync(device.MountPoint);
        }
        catch (Exception)
        {
            // 状态刷新失败不打扰用户。
        }

        Refresh();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        try
        {
            _cancellation?.Cancel();
            ProgressText.Text = "正在取消…";
        }
        catch (Exception)
        {
            // 取消失败无需处理。
        }
    }

    private void OnOpenBackupFolderClick(object sender, RoutedEventArgs e)
    {
        var directory = _installer.CacheDirectory;
        Directory.CreateDirectory(directory);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = directory,
            UseShellExecute = true,
        });
    }

    private void OnOpenRockboxSiteClick(object sender, RoutedEventArgs e) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "https://www.rockbox.org/",
            UseShellExecute = true,
        });
}
