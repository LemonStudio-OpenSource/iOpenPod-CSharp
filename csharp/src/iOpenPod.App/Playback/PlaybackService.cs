using IOpenPod.App.Devices;
using IOpenPod.App.Logging;
using IOpenPod.IPodDb;
using IOpenPod.Storage;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace IOpenPod.App.Playback;

/// <summary>播放状态快照。</summary>
public sealed record PlaybackState(
    TrackRecord? Current,
    bool IsPlaying,
    TimeSpan Position,
    TimeSpan Duration,
    double Volume,
    int QueueLength,
    int HistoryLength);

/// <summary>
/// 应用内播放服务。
///
/// 队列、历史与传输状态只存在于运行时；切换设备或存储介质会导致当前播放失败关闭。
/// </summary>
public sealed class PlaybackService : IDisposable
{
    private readonly MediaPlayer _player = new();
    private readonly List<TrackRecord> _queue = [];
    private readonly List<TrackRecord> _history = [];
    private readonly object _gate = new();
    private ActiveDevice? _device;
    private int _queueIndex = -1;
    private bool _disposed;

    public PlaybackService()
    {
        _player.AutoPlay = true;
        _player.MediaEnded += OnMediaEnded;
        _player.MediaFailed += OnMediaFailed;
        _player.PlaybackSession.PlaybackStateChanged += (_, _) => RaiseChanged();
        _player.PlaybackSession.PositionChanged += (_, _) => RaiseChanged();
        _player.Volume = 0.8;
    }

    /// <summary>播放状态发生变化。</summary>
    public event EventHandler<PlaybackState>? StateChanged;

    /// <summary>当前曲目。</summary>
    public TrackRecord? CurrentTrack { get; private set; }

    /// <summary>当前音量（0–1）。</summary>
    public double Volume
    {
        get => _player.Volume;
        set
        {
            _player.Volume = Math.Clamp(value, 0, 1);
            RaiseChanged();
        }
    }

    /// <summary>播放队列。</summary>
    public IReadOnlyList<TrackRecord> Queue
    {
        get
        {
            lock (_gate)
            {
                return _queue.ToArray();
            }
        }
    }

    public IReadOnlyList<TrackRecord> History
    {
        get
        {
            lock (_gate)
            {
                return _history.ToArray();
            }
        }
    }

    /// <summary>绑定当前设备。切换设备会停止播放。</summary>
    public void SetDevice(ActiveDevice? device)
    {
        if (ReferenceEquals(_device, device))
        {
            return;
        }

        Stop();
        _device = device;
        lock (_gate)
        {
            _queue.Clear();
            _history.Clear();
            _queueIndex = -1;
        }

        CurrentTrack = null;
        RaiseChanged();
    }

    /// <summary>立即播放一首曲目，并把给定集合作为后续队列。</summary>
    public void PlayNow(TrackRecord track, IEnumerable<TrackRecord>? queue = null)
    {
        lock (_gate)
        {
            if (CurrentTrack is not null)
            {
                _history.Add(CurrentTrack);
            }

            _queue.Clear();
            if (queue is not null)
            {
                _queue.AddRange(queue);
            }

            if (!_queue.Contains(track))
            {
                _queue.Insert(0, track);
            }

            _queueIndex = _queue.IndexOf(track);
        }

        StartCurrent();
    }

    /// <summary>把曲目追加到队列末尾。</summary>
    public void Enqueue(IEnumerable<TrackRecord> tracks)
    {
        lock (_gate)
        {
            _queue.AddRange(tracks);
        }

        RaiseChanged();
    }

    /// <summary>把曲目插入到当前曲目之后。</summary>
    public void PlayNext(IEnumerable<TrackRecord> tracks)
    {
        lock (_gate)
        {
            var index = _queueIndex < 0 ? 0 : _queueIndex + 1;
            _queue.InsertRange(Math.Min(index, _queue.Count), tracks);
        }

        RaiseChanged();
    }

    /// <summary>播放/暂停切换。</summary>
    public void TogglePlayPause()
    {
        if (CurrentTrack is null && _queue.Count > 0)
        {
            Next();
            return;
        }

        if (_player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
        {
            _player.Pause();
        }
        else
        {
            _player.Play();
        }

        RaiseChanged();
    }

    /// <summary>播放队列中的下一首。</summary>
    public void Next()
    {
        lock (_gate)
        {
            if (_queue.Count == 0)
            {
                return;
            }

            if (CurrentTrack is not null)
            {
                _history.Add(CurrentTrack);
            }

            _queueIndex = _queueIndex + 1 < _queue.Count ? _queueIndex + 1 : 0;
        }

        StartCurrent();
    }

    /// <summary>播放历史中的上一首。</summary>
    public void Previous()
    {
        lock (_gate)
        {
            if (_history.Count == 0)
            {
                return;
            }

            var track = _history[^1];
            _history.RemoveAt(_history.Count - 1);
            var index = _queue.IndexOf(track);
            _queueIndex = index >= 0 ? index : _queueIndex;
            CurrentTrack = track;
        }

        StartCurrent();
    }

    /// <summary>跳转到指定位置。</summary>
    public void Seek(TimeSpan position)
    {
        try
        {
            _player.PlaybackSession.Position = position;
            RaiseChanged();
        }
        catch (Exception error)
        {
            AppLog.Warning("播放", $"跳转失败：{error.Message}");
        }
    }

    /// <summary>停止播放并清空当前曲目。</summary>
    public void Stop()
    {
        try
        {
            _player.Pause();
            _player.Source = null;
        }
        catch (Exception)
        {
            // 停止失败无需报告。
        }

        CurrentTrack = null;
        RaiseChanged();
    }

    private void StartCurrent()
    {
        TrackRecord? track;
        lock (_gate)
        {
            track = _queueIndex >= 0 && _queueIndex < _queue.Count ? _queue[_queueIndex] : null;
            CurrentTrack = track;
        }

        if (track is null)
        {
            return;
        }

        var device = _device;
        if (device is null || !device.Session.IsUsable)
        {
            AppLog.Warning("播放", "设备会话已失效，无法播放。");
            return;
        }

        var relative = track.RelativePath;
        if (relative is null || !SessionPath.TryCreate(relative, out var path) || !device.Session.FileExists(path))
        {
            AppLog.Warning("播放", $"设备上找不到媒体文件：{relative ?? "（无路径）"}");
            return;
        }

        try
        {
            var host = device.Session.ToHostPath(path);
            _player.Source = MediaSource.CreateFromUri(new Uri(host));
            _player.Play();
            AppLog.Info("播放", $"正在播放：{track.DisplayFileName}（{host}）");
        }
        catch (Exception error)
        {
            AppLog.Error("播放", "无法开始播放", error);
        }

        RaiseChanged();
    }

    private void OnMediaEnded(MediaPlayer sender, object args)
    {
        // 播放结束后自动前进到下一首。
        Next();
    }

    private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        var message = args.ErrorMessage ?? args.Error.ToString();
        AppLog.Error("播放", $"播放失败：{message}");
        RaiseChanged();
    }

    /// <summary>取得当前状态快照。</summary>
    public PlaybackState Snapshot()
    {
        TimeSpan position;
        TimeSpan duration;
        try
        {
            position = _player.PlaybackSession.Position;
            duration = _player.PlaybackSession.NaturalDuration;
        }
        catch (Exception)
        {
            position = TimeSpan.Zero;
            duration = TimeSpan.Zero;
        }

        bool playing;
        try
        {
            playing = _player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;
        }
        catch (Exception)
        {
            playing = false;
        }

        lock (_gate)
        {
            return new PlaybackState(
                CurrentTrack, playing, position, duration, _player.Volume, _queue.Count, _history.Count);
        }
    }

    private void RaiseChanged() => StateChanged?.Invoke(this, Snapshot());

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _player.MediaEnded -= OnMediaEnded;
            _player.MediaFailed -= OnMediaFailed;
            _player.Dispose();
        }
        catch (Exception)
        {
            // 释放失败可忽略。
        }
    }
}
