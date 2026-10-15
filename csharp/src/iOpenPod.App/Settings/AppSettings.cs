using System.Text.Json;
using System.Text.Json.Serialization;
using IOpenPod.App.Logging;

namespace IOpenPod.App.Settings;

/// <summary>界面主题。</summary>
public enum AppTheme
{
    System,
    Light,
    Dark,
}

/// <summary>音乐库中曲目表格的呈现方式。</summary>
public enum LibraryViewMode
{
    /// <summary>左侧列表 + 右侧详情。</summary>
    SplitTable,

    /// <summary>整页表格。</summary>
    WholePageTable,
}

/// <summary>双击曲目时的默认动作。</summary>
public enum DoubleClickAction
{
    AddToQueue,
    PlayNext,
    PlayNow,
    Edit,
}

/// <summary>媒体库的排序方式。</summary>
public enum TrackSortField
{
    Title,
    Artist,
    Album,
    Genre,
    TrackNumber,
    Year,
    Duration,
    Rating,
    DateAdded,
    PlayCount,
}

/// <summary>应用设置。保存在宿主上，不写入设备。</summary>
public sealed class AppSettings
{
    /// <summary>上次选择过的卷身份键。</summary>
    public string? LastVolumeKey { get; set; }

    public AppTheme Theme { get; set; } = AppTheme.System;

    public LibraryViewMode ViewMode { get; set; } = LibraryViewMode.SplitTable;

    public DoubleClickAction DoubleClickAction { get; set; } = DoubleClickAction.AddToQueue;

    public TrackSortField SortField { get; set; } = TrackSortField.Title;

    public bool SortDescending { get; set; }

    /// <summary>是否在应用内播放时显示桌面媒体控件。</summary>
    public bool EnableSystemMediaControls { get; set; } = true;

    /// <summary>同步时是否复制封面到设备。</summary>
    public bool SyncArtwork { get; set; } = true;

    /// <summary>同步时音频转码的目标格式。</summary>
    public string TranscodeFormat { get; set; } = "preserve";

    /// <summary>同步时音频转码的目标码率（kbps）。</summary>
    public int TranscodeBitrateKbps { get; set; } = 256;

    /// <summary>备份快照的保留数量。</summary>
    public int BackupRetention { get; set; } = 10;

    /// <summary>备份目录。</summary>
    public string BackupDirectory { get; set; } = string.Empty;

    /// <summary>同步时是否在写入前自动创建备份。</summary>
    public bool BackupBeforeSync { get; set; }

    /// <summary>上次使用的宿主媒体文件夹。</summary>
    public List<string> MediaFolders { get; set; } = [];

    /// <summary>窗口宽度。</summary>
    public int WindowWidth { get; set; } = 1360;

    /// <summary>窗口高度。</summary>
    public int WindowHeight { get; set; } = 880;

    /// <summary>是否记住窗口位置。</summary>
    public bool RememberWindowPosition { get; set; } = true;

    /// <summary>是否启用详细日志。</summary>
    public bool VerboseLogging { get; set; }

    /// <summary>日志文件路径（只读展示用）。</summary>
    [JsonIgnore]
    public string DefaultBackupDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
        "iOpenPod 备份");

    /// <summary>取得实际使用的备份目录。</summary>
    public string EffectiveBackupDirectory =>
        string.IsNullOrWhiteSpace(BackupDirectory) ? DefaultBackupDirectory : BackupDirectory;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() },
    };

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "iOpenPod",
        "settings.json");

    /// <summary>加载设置；文件缺失或损坏时返回默认值。</summary>
    public static AppSettings Load()
    {
        try
        {
            var path = SettingsPath;
            if (!File.Exists(path))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
            AppLog.Info("设置", $"已加载设置：{path}");
            return settings;
        }
        catch (Exception error)
        {
            AppLog.Error("设置", "设置文件无法读取，已使用默认设置", error);
            return new AppSettings();
        }
    }

    /// <summary>保存设置。</summary>
    public void Save()
    {
        try
        {
            var path = SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception error)
        {
            AppLog.Error("设置", "设置保存失败", error);
        }
    }
}
