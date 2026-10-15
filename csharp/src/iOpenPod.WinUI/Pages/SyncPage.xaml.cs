using IOpenPod.App.Logging;
using IOpenPod.App.Media;
using IOpenPod.App.Sync;
using IOpenPod.WinUI.Services;
using IOpenPod.WinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IOpenPod.WinUI.Pages;

/// <summary>同步页面：媒体来源、扫描、计划与执行。</summary>
public sealed partial class SyncPage : Page, IRefreshable
{
    private readonly List<string> _folders = [];
    private IReadOnlyList<HostMediaItem> _hostItems = [];
    private SyncPlan? _plan;
    private SyncChangeRow[] _addRows = [];
    private SyncChangeRow[] _updateRows = [];
    private SyncChangeRow[] _removeRows = [];

    public SyncPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    /// <summary>刷新来源列表与可用状态。</summary>
    public void Refresh()
    {
        var settings = AppState.Current.Settings;
        _folders.Clear();
        foreach (var folder in settings.MediaFolders.Where(Directory.Exists))
        {
            _folders.Add(folder);
        }

        FolderList.ItemsSource = _folders.ToArray();

        var device = AppState.Current.Device;
        var ready = device is { CanWrite: true };

        ScanButton.IsEnabled = ready;
        RunButton.IsEnabled = ready && _plan is not null;

        if (device is null)
        {
            NoticeBar.Severity = InfoBarSeverity.Warning;
            NoticeBar.Title = "尚未加载设备";
            NoticeBar.Message = "请先在“我的 iPod”页面加载一个 iPod。";
            NoticeBar.IsOpen = true;
        }
        else if (!device.CanWrite)
        {
            NoticeBar.Severity = InfoBarSeverity.Warning;
            NoticeBar.Title = "设备当前不可写入";
            NoticeBar.Message = device.RestrictionReason;
            NoticeBar.IsOpen = true;
        }
        else
        {
            NoticeBar.IsOpen = false;
        }
    }

    private async void OnAddFolderClick(object sender, RoutedEventArgs e)
    {
        var folder = await FilePickerService.PickFolderAsync();
        if (string.IsNullOrEmpty(folder) || _folders.Contains(folder, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        _folders.Add(folder);
        FolderList.ItemsSource = _folders.ToArray();
        AppState.Current.Settings.MediaFolders = [.. _folders];
        AppState.Current.Settings.Save();
        MainWindow.SetStatus($"已添加媒体文件夹：{folder}");
    }

    private void OnRemoveFolderClick(object sender, RoutedEventArgs e)
    {
        if (FolderList.SelectedIndex < 0)
        {
            return;
        }

        _folders.RemoveAt(FolderList.SelectedIndex);
        FolderList.ItemsSource = _folders.ToArray();
        AppState.Current.Settings.MediaFolders = [.. _folders];
        AppState.Current.Settings.Save();
    }

    private async void OnScanClick(object sender, RoutedEventArgs e)
    {
        var device = AppState.Current.Device;
        var library = AppState.Current.Library;
        if (device is null || library is null)
        {
            return;
        }

        if (_folders.Count == 0)
        {
            await MainWindow.ShowErrorAsync("没有媒体来源", "请先添加至少一个媒体文件夹。");
            return;
        }

        ScanButton.IsEnabled = false;
        RunButton.IsEnabled = false;
        ScanProgress.Visibility = Visibility.Visible;
        ScanProgress.Value = 0;

        try
        {
            var engine = new SyncEngine(device, AppState.Current.Settings);
            var progress = new Progress<SyncProgress>(report =>
            {
                ScanProgress.Value = report.Fraction;
                ScanStatus.Text = report.Display;
            });

            _hostItems = await engine.ScanHostAsync(_folders, progress);
            _plan = engine.BuildPlan(_hostItems);
            RenderPlan(_plan);

            ScanStatus.Text = $"扫描完成：{_hostItems.Count} 个媒体文件。{_plan.Summary}";
            MainWindow.SetStatus(_plan.Summary);
            RunButton.IsEnabled = !_plan.IsEmpty;
        }
        catch (Exception error)
        {
            AppLog.Error("同步", "扫描失败", error);
            ScanStatus.Text = $"扫描失败：{error.Message}";
            await MainWindow.ShowErrorAsync("扫描失败", error.Message);
        }
        finally
        {
            ScanProgress.Visibility = Visibility.Collapsed;
            ScanButton.IsEnabled = AppState.Current.Device is { CanWrite: true };
        }
    }

    private void RenderPlan(SyncPlan plan)
    {
        _addRows = plan.Adds.Select(change => new SyncChangeRow(change, true)).ToArray();
        _updateRows = plan.Updates.Select(change => new SyncChangeRow(change, true)).ToArray();
        // 移除候选默认不勾选，避免误删。
        _removeRows = plan.Removes.Select(change => new SyncChangeRow(change, false)).ToArray();

        AddList.ItemsSource = _addRows;
        UpdateList.ItemsSource = _updateRows;
        RemoveList.ItemsSource = _removeRows;

        AddCount.Text = $"（{_addRows.Length} 项，已全选）";
        UpdateCount.Text = $"（{_updateRows.Length} 项，已全选）";
        RemoveCount.Text = $"（{_removeRows.Length} 项）";
    }

    private void OnSelectAllAdds(object sender, RoutedEventArgs e)
    {
        var selectAll = _addRows.Any(row => !row.IsSelected);
        foreach (var row in _addRows)
        {
            row.IsSelected = selectAll;
        }
    }

    private async void OnRunSyncClick(object sender, RoutedEventArgs e)
    {
        var device = AppState.Current.Device;
        var library = AppState.Current.Library;
        if (device is null || library is null || _plan is null)
        {
            return;
        }

        var selected = _addRows.Concat(_updateRows).Concat(_removeRows)
            .Where(row => row.IsSelected)
            .Select(row => row.Change)
            .ToArray();

        if (selected.Length == 0)
        {
            await MainWindow.ShowErrorAsync("没有选中的变更", "请至少勾选一项要同步的内容。");
            return;
        }

        var confirmed = await MainWindow.ConfirmAsync(
            "确认同步",
            $"将向 {device.Name} 写入 {selected.Length} 项变更。\n" +
            $"其中移除 {selected.Count(change => change.Kind == SyncChangeKind.Remove)} 项。" +
            "开始后请不要拔出设备。是否继续？",
            "开始同步");

        if (!confirmed)
        {
            return;
        }

        RunButton.IsEnabled = false;
        ScanProgress.Visibility = Visibility.Visible;
        ScanProgress.Value = 0;

        try
        {
            var engine = new SyncEngine(device, AppState.Current.Settings);
            var progress = new Progress<SyncProgress>(report =>
            {
                ScanProgress.Value = report.Fraction;
                ScanStatus.Text = report.Display;
            });

            var plan = new SyncPlan(selected, _plan.UnchangedCount, _plan.HostFileCount, _plan.DeviceTrackCount);
            var outcome = await engine.ExecuteAsync(library, plan, progress);

            ScanStatus.Text = outcome.Message;
            MainWindow.SetStatus(outcome.Message);

            if (!outcome.Success)
            {
                await MainWindow.ShowErrorAsync("同步失败", outcome.Message);
            }
            else
            {
                AppState.Current.RaiseLibraryChanged();
                _plan = null;
                AddList.ItemsSource = null;
                UpdateList.ItemsSource = null;
                RemoveList.ItemsSource = null;
                AddCount.Text = string.Empty;
                UpdateCount.Text = string.Empty;
                RemoveCount.Text = string.Empty;
            }
        }
        catch (Exception error)
        {
            AppLog.Error("同步", "同步执行失败", error);
            await MainWindow.ShowErrorAsync("同步失败", error.Message);
        }
        finally
        {
            ScanProgress.Visibility = Visibility.Collapsed;
            RunButton.IsEnabled = true;
        }
    }
}
