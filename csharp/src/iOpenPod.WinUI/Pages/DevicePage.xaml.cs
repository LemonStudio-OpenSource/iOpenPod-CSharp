using IOpenPod.App.Devices;
using IOpenPod.App.Logging;
using IOpenPod.DeviceRegistry;
using IOpenPod.Storage;
using IOpenPod.WinUI.Converters;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Media;

namespace IOpenPod.WinUI.Pages;

/// <summary>设备列表中的一项。</summary>
public sealed class DeviceItem
{
    public DeviceItem(DeviceCandidateInfo candidate)
    {
        Candidate = candidate;
        DisplayName = candidate.DisplayName;
        Detail = $"{candidate.Detail}｜卷 {candidate.Candidate.Volume.Identity.FileSystem}，" +
                 $"{BytesToSizeConverter.FormatSize(candidate.Candidate.Volume.Identity.TotalBytes)} 总容量，" +
                 $"{BytesToSizeConverter.FormatSize(candidate.Candidate.Volume.Identity.FreeBytes)} 可用";
        StatusText = candidate.IsIdentified ? "已识别" : "需要确认型号";
    }

    public DeviceCandidateInfo Candidate { get; }

    public string DisplayName { get; }

    public string Detail { get; }

    public string StatusText { get; }
}

/// <summary>设备发现与加载页面。</summary>
public sealed partial class DevicePage : Page, IRefreshable
{
    private readonly IReadOnlyList<DeviceCandidateInfo> _lastCandidates = [];

    public DevicePage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (DeviceList.Items.Count == 0)
            {
                Scan();
            }

            BuildDetail();
        };
    }

    /// <summary>重新扫描设备。</summary>
    public void Refresh() => Scan();

    private void OnRefreshClick(object sender, RoutedEventArgs e) => Scan();

    private void Scan()
    {
        try
        {
            MainWindow.SetStatus("正在扫描可移动设备…");
            var candidates = AppState.Current.Devices.Discover();
            DeviceList.ItemsSource = candidates.Select(candidate => new DeviceItem(candidate)).ToArray();

            if (candidates.Count == 0)
            {
                NoticeBar.Severity = InfoBarSeverity.Warning;
                NoticeBar.Title = "没有检测到 iPod";
                NoticeBar.Message =
                    "请确认设备已通过 USB 连接、已在系统中挂载，并且设备上存在 iPod_Control 目录。" +
                    "iPod shuffle、iPod touch 与 iPhone 目前不在支持范围内。";
                NoticeBar.IsOpen = true;
            }
            else
            {
                NoticeBar.IsOpen = false;
            }

            MainWindow.SetStatus($"扫描完成，共发现 {candidates.Count} 个 iPod 候选设备。");
        }
        catch (Exception error)
        {
            AppLog.Error("设备", "扫描设备失败", error);
            NoticeBar.Severity = InfoBarSeverity.Error;
            NoticeBar.Title = "扫描失败";
            NoticeBar.Message = error.Message;
            NoticeBar.IsOpen = true;
        }
    }

    private void OnDeviceSelectionChanged(object sender, SelectionChangedEventArgs e) => BuildDetail();

    private async void OnOpenClick(object sender, RoutedEventArgs e)
    {
        if (DeviceList.SelectedItem is not DeviceItem item)
        {
            await MainWindow.ShowErrorAsync("请先选择设备", "请在上方列表中选择一个 iPod 后再加载音乐库。");
            return;
        }

        OpenButton.IsEnabled = false;
        MainWindow.SetStatus($"正在加载 {item.DisplayName}…");

        try
        {
            var device = await AppState.Current.OpenDeviceAsync(item.Candidate);
            AppState.Current.Playback.SetDevice(device);

            NoticeBar.Severity = device.CanWrite ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
            NoticeBar.Title = device.CanWrite ? "设备已就绪" : "设备已就绪（只读）";
            NoticeBar.Message = device.CanWrite
                ? $"{device.Name} 已加载，可以浏览、播放、编辑与同步。"
                : device.RestrictionReason;
            NoticeBar.IsOpen = true;

            MainWindow.SetStatus($"已加载 {device.Name}，共 {device.TrackCount} 首曲目。");
            BuildDetail();
        }
        catch (Exception error)
        {
            AppLog.Error("设备", "加载设备失败", error);
            await MainWindow.ShowErrorAsync("加载失败", error.Message);
            MainWindow.SetStatus("设备加载失败。");
        }
        finally
        {
            OpenButton.IsEnabled = true;
        }
    }

    private void BuildDetail()
    {
        DetailPanel.Children.Clear();
        var device = AppState.Current.Device;

        if (device is null)
        {
            if (DeviceList.SelectedItem is DeviceItem selected)
            {
                AddCard("待加载的设备", BuildCandidateDetail(selected.Candidate));
                return;
            }

            DetailPanel.Children.Add(new TextBlock
            {
                Text = "从左侧选择一个设备，然后点击“加载音乐库”。",
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                Margin = new Thickness(4, 20, 4, 0),
            });
            return;
        }

        DetailPanel.Children.Add(BuildDeviceHeader(device));
        AddCard("设备信息", BuildDeviceInfo(device));
        AddCard("音乐库统计", BuildLibraryStats(device));
        AddCard("操作", BuildActions(device));
    }

    private UIElement BuildDeviceHeader(ActiveDevice device)
    {
        var grid = new Grid { ColumnSpacing = 14 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = new Border
        {
            Width = 72,
            Height = 72,
            CornerRadius = new CornerRadius(14),
            Background = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
            Child = new FontIcon
            {
                Glyph = "\uE8EA",
                FontSize = 34,
                Foreground = (Brush)Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"],
            },
        };
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 4 };
        text.Children.Add(new TextBlock { Text = device.Name, FontSize = 22, FontWeight = FontWeights.SemiBold });
        text.Children.Add(new TextBlock
        {
            Text = device.ModelDetail,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        text.Children.Add(new TextBlock
        {
            Text = $"挂载点 {device.MountPoint}｜序列号 {(device.SerialNumber.Length > 0 ? device.SerialNumber : "未知")}",
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            FontSize = 12,
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        return grid;
    }

    private void AddCard(string title, UIElement content)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 15 });
        panel.Children.Add(content);

        DetailPanel.Children.Add(new Border
        {
            Style = (Style)Application.Current.Resources["CardBorderStyle"],
            Child = panel,
        });
    }

    private UIElement BuildCandidateDetail(DeviceCandidateInfo candidate)
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(Row("名称", candidate.DisplayName));
        panel.Children.Add(Row("识别状态", candidate.Identification.ChineseStatus));
        panel.Children.Add(Row("挂载点", candidate.MountPoint));
        panel.Children.Add(Row("卷标", candidate.Candidate.Volume.Identity.Label));
        panel.Children.Add(Row("文件系统", candidate.Candidate.Volume.Identity.FileSystem));
        panel.Children.Add(Row("总容量", BytesToSizeConverter.FormatSize(candidate.Candidate.Volume.Identity.TotalBytes)));
        panel.Children.Add(Row("可写", candidate.Candidate.Volume.IsWriteSafe ? "是" : "否"));

        foreach (var issue in candidate.Identification.IssueList)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "⚠ " + issue.ChineseMessage,
                Foreground = (Brush)Application.Current.Resources["SystemFillColorCautionBrush"],
                TextWrapping = TextWrapping.Wrap,
            });
        }

        return panel;
    }

    private UIElement BuildDeviceInfo(ActiveDevice device)
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(Row("型号", device.ModelDisplayName));
        panel.Children.Add(Row("识别状态", device.Identification.ChineseStatus));
        panel.Children.Add(Row("连接模式", ChineseNames.Connection(device.Identification.ConnectionMode)));
        panel.Children.Add(Row("存储介质", device.Profile is null ? "未知" : ChineseNames.Storage(device.Profile.StorageTechnology)));
        panel.Children.Add(Row("数据库校验", device.Profile is null ? "未知" : ChineseNames.Checksum(device.Profile.Capabilities.Database.Checksum)));
        panel.Children.Add(Row("文件系统", device.Session.Identity.FileSystem));
        panel.Children.Add(Row("总容量", BytesToSizeConverter.FormatSize(device.Session.Identity.TotalBytes)));
        panel.Children.Add(Row("可用空间", BytesToSizeConverter.FormatSize(device.Session.Identity.FreeBytes)));
        panel.Children.Add(Row("FireWire GUID", device.Guid.Length == 8 ? Convert.ToHexString(device.Guid) : "设备未提供"));
        panel.Children.Add(Row("数据库形式", device.IsCompressed ? "压缩的 iTunesCDB" : "标准 iTunesDB"));
        panel.Children.Add(Row("写入权限", device.CanWrite ? "可写入" : $"受限：{device.RestrictionReason}"));
        return panel;
    }

    private UIElement BuildLibraryStats(ActiveDevice device)
    {
        var view = AppState.Current.View;
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(Row("曲目数量", $"{device.TrackCount}"));
        panel.Children.Add(Row("播放列表", $"{device.PlaylistCount}"));
        if (view is not null)
        {
            panel.Children.Add(Row("专辑数量", $"{view.Albums.Count}"));
            panel.Children.Add(Row("艺术家", $"{view.Artists.Count}"));
            panel.Children.Add(Row("流派", $"{view.Genres.Count}"));
            panel.Children.Add(Row("总时长", DurationConverter.FormatDuration(view.TotalDuration)));
            panel.Children.Add(Row("媒体总大小", BytesToSizeConverter.FormatSize(view.TotalSizeBytes)));
        }

        if (device.Artwork is not null)
        {
            panel.Children.Add(Row("插图库", $"{device.Artwork.Items.Count} 张插图"));
        }

        return panel;
    }

    private UIElement BuildActions(ActiveDevice device)
    {
        var panel = new StackPanel { Spacing = 10, Orientation = Orientation.Vertical };

        var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var nameBox = new TextBox { Text = device.Name, Width = 260, PlaceholderText = "设备名称" };
        var renameButton = new Button { Content = "重命名设备" };
        renameButton.Click += async (_, _) =>
        {
            var library = AppState.Current.Library;
            if (library is null)
            {
                return;
            }

            library.RenameDevice(nameBox.Text);
            var outcome = await library.SaveAsync();
            MainWindow.SetStatus(outcome.Message);
            if (!outcome.Success)
            {
                await MainWindow.ShowErrorAsync("重命名失败", outcome.Message);
            }
            else
            {
                AppState.Current.RaiseLibraryChanged();
            }
        };
        nameRow.Children.Add(nameBox);
        nameRow.Children.Add(renameButton);
        panel.Children.Add(nameRow);

        var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

        var saveButton = new Button { Content = "保存更改到设备" };
        saveButton.Click += async (_, _) =>
        {
            var library = AppState.Current.Library;
            if (library is null)
            {
                return;
            }

            var outcome = await library.SaveAsync();
            MainWindow.SetStatus(outcome.Message);
            if (!outcome.Success)
            {
                await MainWindow.ShowErrorAsync("保存失败", outcome.Message);
            }
            else
            {
                AppState.Current.RaiseLibraryChanged();
            }
        };

        var reloadButton = new Button { Content = "放弃更改并重新读取" };
        reloadButton.Click += async (_, _) =>
        {
            if (!await MainWindow.ConfirmAsync("放弃更改", "将丢弃尚未保存的修改并重新从设备读取音乐库，是否继续？", "放弃并重读"))
            {
                return;
            }

            try
            {
                await AppState.Current.ReloadAsync();
                MainWindow.SetStatus("已重新读取设备音乐库。");
                BuildDetail();
            }
            catch (Exception error)
            {
                await MainWindow.ShowErrorAsync("重新读取失败", error.Message);
            }
        };

        var ejectButton = new Button { Content = "安全弹出" };
        ejectButton.Click += async (_, _) =>
        {
            var target = AppState.Current.Device;
            if (target is null)
            {
                return;
            }

            var library = AppState.Current.Library;
            if (library is { IsDirty: true })
            {
                if (!await MainWindow.ConfirmAsync("尚有未保存的修改",
                        "设备上还有未保存的修改，弹出后这些修改会丢失。是否仍要弹出？", "仍然弹出"))
                {
                    return;
                }
            }

            AppState.Current.Playback.Stop();
            if (AppState.Current.Devices.Eject(target))
            {
                AppState.Current.CloseDevice();
                Scan();
            }
            else
            {
                await MainWindow.ShowErrorAsync("弹出失败",
                    "系统拒绝了弹出请求。请关闭正在占用该设备的程序后重试。");
            }
        };

        var closeButton = new Button { Content = "断开连接" };
        closeButton.Click += (_, _) =>
        {
            AppState.Current.Playback.Stop();
            AppState.Current.CloseDevice();
            BuildDetail();
        };

        buttonRow.Children.Add(saveButton);
        buttonRow.Children.Add(reloadButton);
        buttonRow.Children.Add(ejectButton);
        buttonRow.Children.Add(closeButton);
        panel.Children.Add(buttonRow);

        if (!device.CanWrite)
        {
            panel.Children.Add(new TextBlock
            {
                Text = device.RestrictionReason,
                Foreground = (Brush)Application.Current.Resources["SystemFillColorCautionBrush"],
                TextWrapping = TextWrapping.Wrap,
            });
        }

        return panel;
    }

    private static UIElement Row(string label, string value)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var labelBlock = new TextBlock
        {
            Text = label,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };
        Grid.SetColumn(labelBlock, 0);

        var valueBlock = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(valueBlock, 1);

        grid.Children.Add(labelBlock);
        grid.Children.Add(valueBlock);
        return grid;
    }
}
