using IOpenPod.App.Logging;
using IOpenPod.App.Settings;
using IOpenPod.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IOpenPod.WinUI.Pages;

/// <summary>设置页面。</summary>
public sealed partial class SettingsPage : Page, IRefreshable
{
    private bool _loading;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    /// <summary>把设置读入界面。</summary>
    public void Refresh()
    {
        _loading = true;
        try
        {
            var settings = AppState.Current.Settings;

            ThemeBox.Items.Clear();
            ThemeBox.Items.Add("跟随系统");
            ThemeBox.Items.Add("浅色");
            ThemeBox.Items.Add("深色");
            ThemeBox.SelectedIndex = settings.Theme switch
            {
                AppTheme.Light => 1,
                AppTheme.Dark => 2,
                _ => 0,
            };

            ViewModeBox.Items.Clear();
            ViewModeBox.Items.Add("分栏表格（左侧列表 + 右侧详情）");
            ViewModeBox.Items.Add("整页表格");
            ViewModeBox.SelectedIndex = settings.ViewMode == LibraryViewMode.WholePageTable ? 1 : 0;

            DoubleClickBox.Items.Clear();
            DoubleClickBox.Items.Add("添加到播放队列");
            DoubleClickBox.Items.Add("下一首播放");
            DoubleClickBox.Items.Add("立即播放");
            DoubleClickBox.Items.Add("编辑信息");
            DoubleClickBox.SelectedIndex = settings.DoubleClickAction switch
            {
                DoubleClickAction.PlayNext => 1,
                DoubleClickAction.PlayNow => 2,
                DoubleClickAction.Edit => 3,
                _ => 0,
            };

            TranscodeBox.Items.Clear();
            TranscodeBox.Items.Add("保持原格式（推荐）");
            TranscodeBox.Items.Add("转为 AAC（.m4a）");
            TranscodeBox.Items.Add("转为 MP3");
            TranscodeBox.Items.Add("转为 Apple Lossless（.m4a）");
            TranscodeBox.SelectedIndex = settings.TranscodeFormat switch
            {
                "aac" => 1,
                "mp3" => 2,
                "alac" => 3,
                _ => 0,
            };

            BitrateBox.Value = settings.TranscodeBitrateKbps;
            SyncArtworkSwitch.IsOn = settings.SyncArtwork;
            BackupBeforeSyncSwitch.IsOn = settings.BackupBeforeSync;
            SystemMediaSwitch.IsOn = settings.EnableSystemMediaControls;
            RememberWindowSwitch.IsOn = settings.RememberWindowPosition;
            VerboseSwitch.IsOn = settings.VerboseLogging;
            BackupPathBox.Text = settings.EffectiveBackupDirectory;
        }
        finally
        {
            _loading = false;
        }
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var settings = AppState.Current.Settings;
        settings.Theme = ThemeBox.SelectedIndex switch
        {
            1 => AppTheme.Light,
            2 => AppTheme.Dark,
            _ => AppTheme.System,
        };

        if (App.MainWindow?.Content is FrameworkElement root)
        {
            root.RequestedTheme = settings.Theme switch
            {
                AppTheme.Light => ElementTheme.Light,
                AppTheme.Dark => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
        }

        AppState.Current.SaveSettings();
    }

    private void OnViewModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        AppState.Current.Settings.ViewMode = ViewModeBox.SelectedIndex == 1
            ? LibraryViewMode.WholePageTable
            : LibraryViewMode.SplitTable;
        AppState.Current.SaveSettings();
    }

    private void OnDoubleClickChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        AppState.Current.Settings.DoubleClickAction = DoubleClickBox.SelectedIndex switch
        {
            1 => DoubleClickAction.PlayNext,
            2 => DoubleClickAction.PlayNow,
            3 => DoubleClickAction.Edit,
            _ => DoubleClickAction.AddToQueue,
        };
        AppState.Current.SaveSettings();
    }

    private void OnTranscodeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        AppState.Current.Settings.TranscodeFormat = TranscodeBox.SelectedIndex switch
        {
            1 => "aac",
            2 => "mp3",
            3 => "alac",
            _ => "preserve",
        };
        AppState.Current.SaveSettings();
    }

    private void OnBitrateChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || double.IsNaN(args.NewValue))
        {
            return;
        }

        AppState.Current.Settings.TranscodeBitrateKbps = (int)args.NewValue;
        AppState.Current.SaveSettings();
    }

    private void OnSyncArtworkToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        AppState.Current.Settings.SyncArtwork = SyncArtworkSwitch.IsOn;
        AppState.Current.SaveSettings();
    }

    private void OnBackupBeforeSyncToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        AppState.Current.Settings.BackupBeforeSync = BackupBeforeSyncSwitch.IsOn;
        AppState.Current.SaveSettings();
    }

    private void OnSystemMediaToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        AppState.Current.Settings.EnableSystemMediaControls = SystemMediaSwitch.IsOn;
        AppState.Current.SaveSettings();
    }

    private void OnRememberWindowToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        AppState.Current.Settings.RememberWindowPosition = RememberWindowSwitch.IsOn;
        AppState.Current.SaveSettings();
    }

    private void OnVerboseToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        AppState.Current.Settings.VerboseLogging = VerboseSwitch.IsOn;
        AppState.Current.SaveSettings();
        AppLog.Info("设置", $"详细日志已{(VerboseSwitch.IsOn ? "启用" : "停用")}。");
    }

    private async void OnChangeBackupPath(object sender, RoutedEventArgs e)
    {
        var folder = await FilePickerService.PickFolderAsync();
        if (string.IsNullOrEmpty(folder))
        {
            return;
        }

        AppState.Current.Settings.BackupDirectory = folder;
        AppState.Current.SaveSettings();
        BackupPathBox.Text = AppState.Current.Settings.EffectiveBackupDirectory;
    }

    private void OnOpenLogFolder(object sender, RoutedEventArgs e)
    {
        var directory = AppLog.LogDirectory;
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        Directory.CreateDirectory(directory);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = directory,
            UseShellExecute = true,
        });
    }

    private async void OnResetSettings(object sender, RoutedEventArgs e)
    {
        if (!await MainWindow.ConfirmAsync("恢复默认设置",
                "将把所有设置恢复为默认值（不会影响设备上的内容）。是否继续？", "恢复默认"))
        {
            return;
        }

        AppState.Current.ReplaceSettings(new AppSettings());
        Refresh();
        MainWindow.SetStatus("设置已恢复为默认值。");
    }
}
