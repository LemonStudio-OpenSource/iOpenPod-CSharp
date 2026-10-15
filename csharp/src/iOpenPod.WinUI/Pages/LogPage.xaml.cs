using IOpenPod.App.Logging;
using IOpenPod.WinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace IOpenPod.WinUI.Pages;

/// <summary>运行日志页面。</summary>
public sealed partial class LogPage : Page, IRefreshable
{
    private static readonly (LogLevel? Level, string Label)[] Levels =
    [
        (null, "全部级别"),
        (LogLevel.Debug, "调试及以上"),
        (LogLevel.Info, "信息及以上"),
        (LogLevel.Warning, "警告及以上"),
        (LogLevel.Error, "仅错误"),
    ];

    private bool _initialized;

    public LogPage()
    {
        InitializeComponent();

        _initialized = true;
        foreach (var (_, label) in Levels)
        {
            LevelBox.Items.Add(label);
        }

        LevelBox.SelectedIndex = 0;
        _initialized = false;

        LogPathText.Text = $"日志文件：{AppLog.CurrentFile}";
        AppLog.EntryWritten += OnEntryWritten;
        Unloaded += (_, _) => AppLog.EntryWritten -= OnEntryWritten;

        Refresh();
    }

    /// <summary>重新读取日志。</summary>
    public void Refresh()
    {
        var level = LevelBox.SelectedIndex > 0 ? Levels[LevelBox.SelectedIndex].Level : null;
        var keyword = FilterBox.Text.Trim();

        var entries = AppLog.Snapshot().Where(entry =>
            (level is null || entry.Level >= level) &&
            (keyword.Length == 0 ||
             entry.Message.Contains(keyword, StringComparison.CurrentCultureIgnoreCase) ||
             entry.Category.Contains(keyword, StringComparison.CurrentCultureIgnoreCase)));

        var rows = entries.Select(entry => new LogRow(entry)).ToArray();
        LogList.ItemsSource = rows;
        LogPathText.Text = $"日志文件：{AppLog.CurrentFile}（当前显示 {rows.Length} 条）";

        if (AutoScrollCheck.IsChecked == true && rows.Length > 0)
        {
            LogList.ScrollIntoView(rows[^1]);
        }
    }

    private void OnEntryWritten(object? sender, LogEntry entry)
    {
        if (!DispatcherQueue.TryEnqueue(Refresh))
        {
            // 界面不可用时忽略。
        }
    }

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized)
        {
            Refresh();
        }
    }

    private void OnFilterTextChanged(object sender, TextChangedEventArgs e) => Refresh();

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
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

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var text = string.Join(Environment.NewLine, AppLog.Snapshot().Select(entry => entry.Display));
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
            MainWindow.SetStatus($"已复制 {AppLog.Snapshot().Count} 条日志到剪贴板。");
        }
        catch (Exception error)
        {
            AppLog.Error("日志", "复制日志失败", error);
        }
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        AppLog.ClearMemory();
        Refresh();
    }
}
