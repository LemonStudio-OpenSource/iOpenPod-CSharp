using IOpenPod.App.Artwork;
using IOpenPod.App.Backup;
using IOpenPod.App.Devices;
using IOpenPod.App.Library;
using IOpenPod.App.Logging;
using IOpenPod.App.Playback;
using IOpenPod.App.Settings;

namespace IOpenPod.WinUI;

/// <summary>
/// 应用的全局状态。
///
/// 界面只通过本类型访问应用层服务，因此页面之间不会互相持有引用。
/// </summary>
public sealed class AppState : IDisposable
{
    private static readonly Lazy<AppState> Instance =
        new(() => new AppState(), isThreadSafe: true);

    private ArtworkService? _artwork;
    private LibrarySession? _library;
    private PlaybackService? _playback;
    private bool _disposed;

    private AppState()
    {
        Settings = AppSettings.Load();
        Devices = new DeviceManager();
        Backups = new BackupManager();
        Devices.CurrentChanged += OnCurrentChanged;
    }

    /// <summary>全局唯一实例。</summary>
    public static AppState Current => Instance.Value;

    public AppSettings Settings { get; private set; }

    public DeviceManager Devices { get; }

    public BackupManager Backups { get; }

    /// <summary>当前打开的设备。</summary>
    public ActiveDevice? Device => Devices.Current;

    /// <summary>当前音乐库会话。</summary>
    public LibrarySession? Library => _library;

    /// <summary>当前音乐库浏览视图。</summary>
    public LibraryView? View { get; private set; }

    /// <summary>当前封面服务。</summary>
    public ArtworkService? Artwork => _artwork;

    /// <summary>播放服务。</summary>
    public PlaybackService Playback => _playback ??= new PlaybackService();

    /// <summary>设备、音乐库或设置发生变化。</summary>
    public event EventHandler? StateChanged;

    /// <summary>仅仅音乐库内容发生变化（例如保存后）。</summary>
    public event EventHandler? LibraryChanged;

    /// <summary>打开一个设备并建立音乐库会话。</summary>
    public async Task<ActiveDevice> OpenDeviceAsync(DeviceCandidateInfo candidate)
    {
        var device = await Devices.OpenAsync(candidate).ConfigureAwait(true);
        RebuildSession(device);
        Settings.LastVolumeKey = candidate.StableKey;
        Settings.Save();
        return device;
    }

    /// <summary>重新从设备读取音乐库（放弃未保存的修改）。</summary>
    public async Task ReloadAsync()
    {
        var device = Device;
        if (device is null)
        {
            return;
        }

        var candidates = Devices.Discover();
        var match = candidates.FirstOrDefault(candidate =>
            string.Equals(candidate.MountPoint, device.MountPoint, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            throw new InvalidOperationException("设备已经被移除，无法重新读取。");
        }

        await OpenDeviceAsync(match).ConfigureAwait(true);
    }

    /// <summary>关闭当前设备。</summary>
    public void CloseDevice()
    {
        Playback.Stop();
        _artwork?.Dispose();
        _artwork = null;
        _library = null;
        View = null;
        Devices.Close();
        RaiseStateChanged();
    }

    /// <summary>通知音乐库内容已变化（重建浏览视图与缓存）。</summary>
    public void RaiseLibraryChanged()
    {
        RebuildView();
        LibraryChanged?.Invoke(this, EventArgs.Empty);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>保存设置。</summary>
    public void SaveSettings()
    {
        Settings.Save();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>替换设置对象（例如恢复默认值后）。</summary>
    public void ReplaceSettings(AppSettings settings)
    {
        Settings = settings;
        Settings.Save();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RebuildSession(ActiveDevice device)
    {
        _artwork?.Dispose();
        _library = new LibrarySession(device, Settings);
        _artwork = new ArtworkService(device);
        RebuildView();
        RaiseStateChanged();
    }

    private void RebuildView()
    {
        View = Device is null ? null : new LibraryView(Device.Database);
    }

    private void OnCurrentChanged(object? sender, ActiveDevice? device)
    {
        if (device is null)
        {
            _artwork?.Dispose();
            _artwork = null;
            _library = null;
            View = null;
        }

        RaiseStateChanged();
    }

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _playback?.Dispose();
        _artwork?.Dispose();
        Devices.Dispose();
        AppLog.Info("应用", "已完成资源清理。");
    }
}
