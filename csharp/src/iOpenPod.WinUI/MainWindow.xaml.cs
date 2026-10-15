using IOpenPod.App.Devices;
using IOpenPod.App.Logging;
using IOpenPod.App.Playback;
using IOpenPod.IPodDb;
using IOpenPod.WinUI.Converters;
using IOpenPod.WinUI.Pages;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Windowing;

namespace IOpenPod.WinUI;

/// <summary>主窗口：导航外壳、播放条与状态栏。</summary>
public sealed partial class MainWindow : Window
{
    private readonly DispatcherTimer _timer;
    private bool _updatingSeek;
    private bool _updatingVolume;
    private string _currentTag = "device";

    public MainWindow()
    {
        InitializeComponent();

        Title = "iOpenPod 管理器";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        TryApplyBackdrop();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += OnTimerTick;
        _timer.Start();

        AppState.Current.StateChanged += (_, _) => DispatcherQueue.TryEnqueue(RefreshHeader);
        AppState.Current.Playback.StateChanged += (_, state) => DispatcherQueue.TryEnqueue(() => UpdatePlayer(state));

        Nav.SelectedItem = Nav.MenuItems.OfType<NavigationViewItem>().First();
        Navigate("device");
        RefreshHeader();
        UpdatePlayer(AppState.Current.Playback.Snapshot());

        Closed += OnClosed;
        AppLog.Info("界面", "主窗口已就绪。");

        // 自检模式：把根元素渲染为 PNG 后退出，用于无人值守环境下的界面校验。
        var selfTestPath = Environment.GetEnvironmentVariable("IOPENPOD_SELFTEST");
        if (!string.IsNullOrEmpty(selfTestPath))
        {
            AppLog.Info("自检", $"已启用界面自检，输出到 {selfTestPath}。");
            _ = RunSelfTestAsync(selfTestPath);
        }
    }

    /// <summary>把当前界面渲染为 PNG 并退出。仅用于自动化界面校验。</summary>
    private async Task RunSelfTestAsync(string path)
    {
        try
        {
            await Task.Delay(4000);
            var bitmap = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
            await bitmap.RenderAsync(RootGrid);
            var pixels = await bitmap.GetPixelsAsync();
            var bytes = new byte[pixels.Length];
            using (var reader = Windows.Storage.Streams.DataReader.FromBuffer(pixels))
            {
                reader.ReadBytes(bytes);
            }

            var width = bitmap.PixelWidth;
            var height = bitmap.PixelHeight;
            var bgra = new byte[width * height * 4];
            Array.Copy(bytes, bgra, Math.Min(bytes.Length, bgra.Length));

            var png = IOpenPod.IPodDb.Artwork.ImageLoader.EncodePng(bgra, width, height);
            await File.WriteAllBytesAsync(path, png);
            AppLog.Info("自检", $"界面自检图像已写入 {path}（{width}×{height}）。");
        }
        catch (Exception error)
        {
            AppLog.Error("自检", "界面自检失败", error);
        }

        Environment.Exit(0);
    }

    /// <summary>状态栏文本。</summary>
    public static void SetStatus(string text)
    {
        if (App.MainWindow is { } window)
        {
            window.DispatcherQueue.TryEnqueue(() => window.StatusText.Text = text);
        }
    }

    /// <summary>状态栏右侧文本（通常是统计信息）。</summary>
    public static void SetStatusRight(string text)
    {
        if (App.MainWindow is { } window)
        {
            window.DispatcherQueue.TryEnqueue(() => window.StatusRightText.Text = text);
        }
    }

    /// <summary>请求导航到指定页面。</summary>
    public void NavigateTo(string tag)
    {
        foreach (var item in Nav.MenuItems.OfType<NavigationViewItem>())
        {
            if (string.Equals(item.Tag as string, tag, StringComparison.Ordinal))
            {
                Nav.SelectedItem = item;
                Navigate(tag);
                return;
            }
        }
    }

    private void TryApplyBackdrop()
    {
        try
        {
            if (MicaController.IsSupported())
            {
                SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
            }
        }
        catch (Exception error)
        {
            AppLog.Warning("界面", $"云母材质不可用，使用纯色背景：{error.Message}");
        }
    }

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
        {
            Navigate(tag);
        }
    }

    private void Navigate(string tag)
    {
        _currentTag = tag;
        var pageType = tag switch
        {
            "device" => typeof(DevicePage),
            "tracks" => typeof(TracksPage),
            "albums" => typeof(AlbumsPage),
            "artists" => typeof(ArtistsPage),
            "genres" => typeof(GenresPage),
            "playlists" => typeof(PlaylistsPage),
            "sync" => typeof(SyncPage),
            "backup" => typeof(BackupPage),
            "logs" => typeof(LogPage),
            "settings" => typeof(SettingsPage),
            _ => typeof(DevicePage),
        };

        if (ContentFrame.CurrentSourcePageType != pageType)
        {
            ContentFrame.Navigate(pageType);
        }

        RefreshHeader();
    }

    private void RefreshHeader()
    {
        var device = AppState.Current.Device;
        if (device is null)
        {
            TitleDeviceText.Text = "未连接设备";
            SetStatusRight(string.Empty);
            return;
        }

        TitleDeviceText.Text = $"{device.Name} · {device.ModelDisplayName}";
        var view = AppState.Current.View;
        if (view is not null)
        {
            SetStatusRight(
                $"{view.Tracks.Count} 首 · {view.Albums.Count} 张专辑 · " +
                $"{DurationConverter.FormatDuration(view.TotalDuration)} · " +
                $"{BytesToSizeConverter.FormatSize(view.TotalSizeBytes)}");
        }

        // 重新进入当前页面以刷新内容。
        if (ContentFrame.Content is IRefreshable refreshable)
        {
            refreshable.Refresh();
        }
    }

    private void OnTimerTick(object? sender, object e)
    {
        var state = AppState.Current.Playback.Snapshot();
        UpdatePlayer(state);
    }

    private void UpdatePlayer(PlaybackState state)
    {
        var track = state.Current;
        if (track is null)
        {
            PlayerTitle.Text = "未在播放";
            PlayerArtist.Text = "选择一首歌曲开始播放";
            PlayerCover.Source = null;
        }
        else
        {
            PlayerTitle.Text = track.Title.Length > 0 ? track.Title : track.DeviceFileName;
            PlayerArtist.Text = $"{track.Artist} · {track.Album}";
            _ = LoadCoverAsync(track);
        }

        PlayIcon.Glyph = state.IsPlaying ? "\uE769" : "\uE768";

        _updatingSeek = true;
        try
        {
            var total = state.Duration.TotalSeconds > 0 ? state.Duration.TotalSeconds : 1;
            SeekSlider.Maximum = total;
            SeekSlider.Value = Math.Clamp(state.Position.TotalSeconds, 0, total);
            PositionText.Text = DurationConverter.FormatDuration(state.Position);
            DurationText.Text = DurationConverter.FormatDuration(state.Duration);
        }
        finally
        {
            _updatingSeek = false;
        }

        _updatingVolume = true;
        try
        {
            VolumeSlider.Value = state.Volume;
        }
        finally
        {
            _updatingVolume = false;
        }
    }

    private async Task LoadCoverAsync(TrackRecord track)
    {
        var artwork = AppState.Current.Artwork;
        if (artwork is null)
        {
            PlayerCover.Source = null;
            return;
        }

        try
        {
            var png = await artwork.GetCoverPngAsync(track, 96);
            PlayerCover.Source = await Helpers.ImageHelper.ToBitmapAsync(png);
        }
        catch (Exception error)
        {
            AppLog.Debug("界面", $"播放条封面加载失败：{error.Message}");
        }
    }

    private void OnPlayPauseClick(object sender, RoutedEventArgs e) => AppState.Current.Playback.TogglePlayPause();

    private void OnNextClick(object sender, RoutedEventArgs e) => AppState.Current.Playback.Next();

    private void OnPreviousClick(object sender, RoutedEventArgs e) => AppState.Current.Playback.Previous();

    private void OnSeekValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_updatingSeek)
        {
            return;
        }

        AppState.Current.Playback.Seek(TimeSpan.FromSeconds(e.NewValue));
    }

    private void OnVolumeValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_updatingVolume)
        {
            return;
        }

        AppState.Current.Playback.Volume = e.NewValue;
        AppState.Current.Settings.Save();
    }

    private async void OnQueueClick(object sender, RoutedEventArgs e)
    {
        var dialog = new QueueDialog
        {
            XamlRoot = RootGrid.XamlRoot,
        };
        await dialog.ShowAsync();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _timer.Stop();
        try
        {
            AppState.Current.Settings.Save();
            AppState.Current.Dispose();
        }
        catch (Exception error)
        {
            AppLog.Error("界面", "关闭窗口时清理失败", error);
        }
    }

    /// <summary>供页面调用的常用操作。</summary>
    public static async Task ShowErrorAsync(string title, string message)
    {
        if (App.MainWindow is not { } window)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "知道了",
            XamlRoot = window.RootGrid.XamlRoot,
        };

        await dialog.ShowAsync();
    }

    public static async Task<bool> ConfirmAsync(string title, string message, string primaryText = "确定")
    {
        if (App.MainWindow is not { } window)
        {
            return false;
        }

        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = primaryText,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = window.RootGrid.XamlRoot,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    public static async Task<string?> PromptAsync(string title, string placeholder, string initial = "")
    {
        if (App.MainWindow is not { } window)
        {
            return null;
        }

        var box = new TextBox { Text = initial, PlaceholderText = placeholder, Width = 320 };
        var dialog = new ContentDialog
        {
            Title = title,
            Content = box,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = window.RootGrid.XamlRoot,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary ? box.Text : null;
    }
}

/// <summary>页面可实现本接口以在设备或音乐库变化时刷新内容。</summary>
public interface IRefreshable
{
    void Refresh();
}
