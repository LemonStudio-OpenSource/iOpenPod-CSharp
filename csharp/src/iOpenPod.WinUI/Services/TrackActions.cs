using IOpenPod.App.Library;
using IOpenPod.App.Logging;
using IOpenPod.IPodDb;
using IOpenPod.Storage;
using IOpenPod.WinUI.Pages;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IOpenPod.WinUI.Services;

/// <summary>曲目通用操作：供各页面的按钮与右键菜单复用。</summary>
public static class TrackActions
{
    /// <summary>立即播放选中的曲目，并把整个集合作为后续队列。</summary>
    public static void PlayNow(IReadOnlyList<TrackRecord> tracks)
    {
        if (tracks.Count == 0)
        {
            return;
        }

        var playback = AppState.Current.Playback;
        playback.SetDevice(AppState.Current.Device);
        playback.PlayNow(tracks[0], tracks);
        MainWindow.SetStatus($"正在播放：{tracks[0].DisplayFileName}");
    }

    /// <summary>把曲目追加到播放队列。</summary>
    public static void Enqueue(IReadOnlyList<TrackRecord> tracks)
    {
        if (tracks.Count == 0)
        {
            return;
        }

        var playback = AppState.Current.Playback;
        playback.SetDevice(AppState.Current.Device);
        playback.Enqueue(tracks);
        MainWindow.SetStatus($"已把 {tracks.Count} 首曲目加入播放队列。");
    }

    /// <summary>把曲目插入到当前曲目之后。</summary>
    public static void PlayNext(IReadOnlyList<TrackRecord> tracks)
    {
        if (tracks.Count == 0)
        {
            return;
        }

        var playback = AppState.Current.Playback;
        playback.SetDevice(AppState.Current.Device);
        playback.PlayNext(tracks);
        MainWindow.SetStatus($"已把 {tracks.Count} 首曲目排到下一首播放。");
    }

    /// <summary>打开元数据编辑器。</summary>
    public static async Task EditAsync(IReadOnlyList<TrackRecord> tracks, XamlRoot? xamlRoot)
    {
        var library = AppState.Current.Library;
        if (library is null || tracks.Count == 0)
        {
            return;
        }

        if (!library.Device.CanWrite)
        {
            await MainWindow.ShowErrorAsync("无法编辑", library.Device.RestrictionReason);
            return;
        }

        var dialog = new TrackEditorDialog(tracks, xamlRoot);
        await dialog.ShowAsync();
    }

    /// <summary>从设备上彻底移除曲目。</summary>
    public static async Task RemoveAsync(IReadOnlyList<TrackRecord> tracks)
    {
        var library = AppState.Current.Library;
        if (library is null || tracks.Count == 0)
        {
            return;
        }

        if (!library.Device.CanWrite)
        {
            await MainWindow.ShowErrorAsync("无法移除", library.Device.RestrictionReason);
            return;
        }

        var confirmed = await MainWindow.ConfirmAsync(
            "从 iPod 移除",
            $"将永久删除设备上的 {tracks.Count} 首曲目（包含媒体文件与数据库记录）。此操作不可撤销，是否继续？",
            "删除");

        if (!confirmed)
        {
            return;
        }

        library.RemoveTracks(tracks);
        var outcome = await library.SaveAsync();
        MainWindow.SetStatus(outcome.Message);
        if (!outcome.Success)
        {
            await MainWindow.ShowErrorAsync("移除失败", outcome.Message);
            return;
        }

        AppState.Current.RaiseLibraryChanged();
    }

    /// <summary>把曲目添加到指定播放列表。</summary>
    public static async Task AddToPlaylistAsync(IReadOnlyList<TrackRecord> tracks, XamlRoot? xamlRoot)
    {
        var library = AppState.Current.Library;
        if (library is null || tracks.Count == 0)
        {
            return;
        }

        var playlists = library.Database.UserPlaylists;
        if (playlists.Count == 0)
        {
            var name = await MainWindow.PromptAsync("新建播放列表", "输入播放列表名称", "新建播放列表");
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            var created = library.CreatePlaylist(name.Trim());
            library.AddToPlaylist(created, tracks);
        }
        else
        {
            var list = new ListView
            {
                ItemsSource = playlists.Select(playlist => playlist.Name).ToArray(),
                SelectionMode = ListViewSelectionMode.Single,
                Height = 260,
                Width = 320,
            };
            list.SelectedIndex = 0;

            var dialog = new ContentDialog
            {
                Title = "添加到播放列表",
                Content = list,
                PrimaryButtonText = "添加",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = xamlRoot,
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary || list.SelectedIndex < 0)
            {
                return;
            }

            library.AddToPlaylist(playlists[list.SelectedIndex], tracks);
        }

        var outcome = await library.SaveAsync();
        MainWindow.SetStatus(outcome.Message);
        if (!outcome.Success)
        {
            await MainWindow.ShowErrorAsync("添加失败", outcome.Message);
        }
    }

    /// <summary>把曲目导出到宿主文件夹。</summary>
    public static async Task ExportAsync(IReadOnlyList<TrackRecord> tracks)
    {
        var device = AppState.Current.Device;
        if (device is null || tracks.Count == 0)
        {
            return;
        }

        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.MusicLibrary;
        picker.FileTypeFilter.Add("*");

        var window = App.WindowHandle;
        if (window == IntPtr.Zero)
        {
            return;
        }

        WinRT.Interop.InitializeWithWindow.Initialize(picker, window);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null)
        {
            return;
        }

        var copied = 0;
        foreach (var track in tracks)
        {
            var relative = track.RelativePath;
            if (relative is null || !SessionPath.TryCreate(relative, out var path))
            {
                continue;
            }

            try
            {
                var name = track.DisplayFileName + "." + track.FileExtension;
                var target = Path.Combine(folder.Path, Sanitize(name));
                using var source = device.Session.OpenRead(path);
                await using var destination = File.Create(target);
                await source.CopyToAsync(destination);
                copied++;
            }
            catch (Exception error)
            {
                AppLog.Error("导出", $"导出失败：{track.Title}", error);
            }
        }

        MainWindow.SetStatus($"已导出 {copied} 个文件到 {folder.Path}。");
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder(name.Length);
        foreach (var character in name)
        {
            builder.Append(Array.IndexOf(invalid, character) >= 0 ? '_' : character);
        }

        return builder.Length == 0 ? "track" : builder.ToString();
    }
}
