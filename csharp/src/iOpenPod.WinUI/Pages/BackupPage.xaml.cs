using IOpenPod.App.Backup;
using IOpenPod.App.Logging;
using IOpenPod.WinUI.Services;
using IOpenPod.WinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IOpenPod.WinUI.Pages;

/// <summary>备份快照页面。</summary>
public sealed partial class BackupPage : Page, IRefreshable
{
    private BackupRow? _selected;

    public BackupPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    /// <summary>刷新备份列表与路径。</summary>
    public void Refresh()
    {
        var settings = AppState.Current.Settings;
        var root = settings.EffectiveBackupDirectory;
        BackupPathText.Text = root;

        try
        {
            var snapshots = AppState.Current.Backups.List(root);
            BackupList.ItemsSource = snapshots.Select(snapshot => new BackupRow(snapshot)).ToArray();
            MainWindow.SetStatus($"共找到 {snapshots.Count} 个备份快照。");
        }
        catch (Exception error)
        {
            AppLog.Error("备份", "读取备份列表失败", error);
            BackupList.ItemsSource = null;
        }

        var device = AppState.Current.Device;
        CreateButton.IsEnabled = device is not null;
        RestoreButton.IsEnabled = false;
        DeleteButton.IsEnabled = false;
        _selected = null;

        if (device is null)
        {
            NoticeBar.Severity = InfoBarSeverity.Warning;
            NoticeBar.Title = "尚未加载设备";
            NoticeBar.Message = "请先在“我的 iPod”页面加载一个 iPod 后再创建备份。";
            NoticeBar.IsOpen = true;
        }
        else if (!device.CanWrite)
        {
            NoticeBar.Severity = InfoBarSeverity.Informational;
            NoticeBar.Title = "设备只读";
            NoticeBar.Message = $"{device.RestrictionReason}（仍然可以创建备份。）";
            NoticeBar.IsOpen = true;
        }
        else
        {
            NoticeBar.IsOpen = false;
        }
    }

    private void OnBackupSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selected = BackupList.SelectedItem as BackupRow;
        RestoreButton.IsEnabled = _selected is not null && AppState.Current.Device is { CanWrite: true };
        DeleteButton.IsEnabled = _selected is not null;
    }

    private async void OnChangeFolderClick(object sender, RoutedEventArgs e)
    {
        var folder = await FilePickerService.PickFolderAsync();
        if (string.IsNullOrEmpty(folder))
        {
            return;
        }

        AppState.Current.Settings.BackupDirectory = folder;
        AppState.Current.SaveSettings();
        Refresh();
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        var root = AppState.Current.Settings.EffectiveBackupDirectory;
        Directory.CreateDirectory(root);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = root,
            UseShellExecute = true,
        });
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => Refresh();

    private async void OnCreateClick(object sender, RoutedEventArgs e)
    {
        var device = AppState.Current.Device;
        if (device is null)
        {
            return;
        }

        CreateButton.IsEnabled = false;
        Progress.Visibility = Visibility.Visible;
        Progress.Value = 0;

        try
        {
            var progress = new Progress<BackupProgress>(report =>
            {
                Progress.Value = report.Fraction;
                ProgressText.Text = report.Display;
            });

            var snapshot = await AppState.Current.Backups.CreateAsync(
                device, AppState.Current.Settings.EffectiveBackupDirectory, progress, CancellationToken.None);

            ProgressText.Text = $"备份完成：{snapshot.FileCount} 个文件。";
            MainWindow.SetStatus($"备份完成：{snapshot.Display}");
            Refresh();
        }
        catch (Exception error)
        {
            AppLog.Error("备份", "创建备份失败", error);
            await MainWindow.ShowErrorAsync("备份失败", error.Message);
        }
        finally
        {
            Progress.Visibility = Visibility.Collapsed;
            CreateButton.IsEnabled = true;
        }
    }

    private async void OnRestoreClick(object sender, RoutedEventArgs e)
    {
        var device = AppState.Current.Device;
        if (_selected is null || device is null)
        {
            return;
        }

        var confirmed = await MainWindow.ConfirmAsync(
            "确认还原",
            $"将把快照「{_selected.Snapshot.Name}」完整写回 {device.Name}，覆盖设备上同路径的文件。\n\n" +
            "还原前会自动为当前设备再创建一次安全快照。是否继续？",
            "开始还原");

        if (!confirmed)
        {
            return;
        }

        RestoreButton.IsEnabled = false;
        Progress.Visibility = Visibility.Visible;
        Progress.Value = 0;

        try
        {
            var progress = new Progress<BackupProgress>(report =>
            {
                Progress.Value = report.Fraction;
                ProgressText.Text = report.Display;
            });

            var message = await AppState.Current.Backups.RestoreAsync(
                device, _selected.Snapshot, AppState.Current.Settings.EffectiveBackupDirectory,
                progress, CancellationToken.None);

            ProgressText.Text = message;
            MainWindow.SetStatus(message);
            await MainWindow.ShowErrorAsync("还原完成", message + "\n\n建议断开并重新连接 iPod，让固件重新读取音乐库。");
        }
        catch (Exception error)
        {
            AppLog.Error("备份", "还原失败", error);
            await MainWindow.ShowErrorAsync("还原失败", error.Message);
        }
        finally
        {
            Progress.Visibility = Visibility.Collapsed;
            RestoreButton.IsEnabled = true;
        }
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (_selected is null)
        {
            return;
        }

        if (!await MainWindow.ConfirmAsync(
                "删除快照", $"确定删除快照「{_selected.Snapshot.Name}」吗？此操作不可撤销。", "删除"))
        {
            return;
        }

        AppState.Current.Backups.Delete(_selected.Snapshot);
        Refresh();
    }
}
