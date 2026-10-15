using IOpenPod.App.Logging;
using IOpenPod.IPodDb;
using IOpenPod.WinUI.Converters;
using IOpenPod.WinUI.Services;
using IOpenPod.WinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace IOpenPod.WinUI.Pages;

/// <summary>播放列表管理页面。</summary>
public sealed partial class PlaylistsPage : Page, IRefreshable
{
    private PlaylistRecord? _current;

    public PlaylistsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    /// <summary>重新构建播放列表。</summary>
    public void Refresh()
    {
        var library = AppState.Current.Library;
        if (library is null)
        {
            PlaylistList.ItemsSource = null;
            SummaryText.Text = "尚未加载设备。请先在“我的 iPod”页面加载音乐库。";
            DetailTracks.ItemsSource = null;
            DetailName.Text = "未选择播放列表";
            DetailMeta.Text = string.Empty;
            return;
        }

        var playlists = library.Database.UserPlaylists;
        PlaylistList.ItemsSource = playlists.Select(playlist => playlist.Name).ToArray();
        SummaryText.Text = $"共 {playlists.Count} 个播放列表。" +
                           (library.IsDirty ? " 有未保存的修改。" : string.Empty);

        if (_current is not null && playlists.Contains(_current))
        {
            var index = playlists.ToList().IndexOf(_current);
            if (index >= 0 && PlaylistList.SelectedIndex != index)
            {
                PlaylistList.SelectedIndex = index;
            }

            ShowPlaylist(_current);
            return;
        }

        _current = null;
        DetailName.Text = "未选择播放列表";
        DetailMeta.Text = playlists.Count == 0 ? "点击“新建播放列表”开始创建。" : string.Empty;
        DetailTracks.ItemsSource = null;
    }

    private void ShowPlaylist(PlaylistRecord playlist)
    {
        _current = playlist;
        DetailName.Text = playlist.Name.Length > 0 ? playlist.Name : "未命名播放列表";

        var tracks = playlist.Tracks().ToArray();
        DetailMeta.Text =
            $"{tracks.Length} 首 · " +
            $"{DurationConverter.FormatDuration(TimeSpan.FromMilliseconds(tracks.Sum(t => (double)t.LengthMs)))}";

        DetailTracks.ItemsSource = tracks.Select(track => new TrackRow(track, playlist.Name)).ToArray();
    }

    private void OnPlaylistSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var library = AppState.Current.Library;
        if (library is null || PlaylistList.SelectedIndex < 0)
        {
            return;
        }

        var playlists = library.Database.UserPlaylists;
        if (PlaylistList.SelectedIndex < playlists.Count)
        {
            ShowPlaylist(playlists[PlaylistList.SelectedIndex]);
        }
    }

    private async void OnCreatePlaylistClick(object sender, RoutedEventArgs e)
    {
        var library = AppState.Current.Library;
        if (library is null)
        {
            await MainWindow.ShowErrorAsync("无法创建", "请先加载一个 iPod。");
            return;
        }

        var name = await MainWindow.PromptAsync("新建播放列表", "输入播放列表名称", "新建播放列表");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        library.CreatePlaylist(name.Trim());
        var outcome = await library.SaveAsync();
        MainWindow.SetStatus(outcome.Message);
        AppState.Current.RaiseLibraryChanged();
        Refresh();
    }

    private async void OnRenamePlaylistClick(object sender, RoutedEventArgs e)
    {
        var library = AppState.Current.Library;
        if (library is null || _current is null)
        {
            await MainWindow.ShowErrorAsync("无法重命名", "请先选择一个播放列表。");
            return;
        }

        var name = await MainWindow.PromptAsync("重命名播放列表", "输入新的名称", _current.Name);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        library.RenamePlaylist(_current, name.Trim());
        var outcome = await library.SaveAsync();
        MainWindow.SetStatus(outcome.Message);
        AppState.Current.RaiseLibraryChanged();
        Refresh();
    }

    private async void OnDeletePlaylistClick(object sender, RoutedEventArgs e)
    {
        var library = AppState.Current.Library;
        if (library is null || _current is null)
        {
            await MainWindow.ShowErrorAsync("无法删除", "请先选择一个播放列表。");
            return;
        }

        if (!await MainWindow.ConfirmAsync("删除播放列表",
                $"确定要删除播放列表「{_current.Name}」吗？设备上的曲目不会被删除。", "删除"))
        {
            return;
        }

        library.DeletePlaylist(_current);
        _current = null;
        var outcome = await library.SaveAsync();
        MainWindow.SetStatus(outcome.Message);
        AppState.Current.RaiseLibraryChanged();
        Refresh();
    }

    private IReadOnlyList<TrackRecord> SelectedTracks() =>
        DetailTracks.SelectedItems.OfType<TrackRow>().Select(row => row.Track).ToArray();

    private void OnPlayPlaylistClick(object sender, RoutedEventArgs e)
    {
        var tracks = _current?.Tracks().ToArray() ?? [];
        TrackActions.PlayNow(tracks);
    }

    private async void OnAddTracksClick(object sender, RoutedEventArgs e)
    {
        var library = AppState.Current.Library;
        var view = AppState.Current.View;
        if (library is null || view is null || _current is null)
        {
            await MainWindow.ShowErrorAsync("无法添加", "请先选择一个播放列表。");
            return;
        }

        var picker = new TrackPickerDialog(view.Tracks, XamlRoot);
        if (await picker.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        library.AddToPlaylist(_current, picker.SelectedTracks);
        var outcome = await library.SaveAsync();
        MainWindow.SetStatus(outcome.Message);
        AppState.Current.RaiseLibraryChanged();
        Refresh();
    }

    private async void OnRemoveFromPlaylistClick(object sender, RoutedEventArgs e)
    {
        var library = AppState.Current.Library;
        var tracks = SelectedTracks();
        if (library is null || _current is null || tracks.Count == 0)
        {
            return;
        }

        library.RemoveFromPlaylist(_current, tracks);
        var outcome = await library.SaveAsync();
        MainWindow.SetStatus(outcome.Message);
        AppState.Current.RaiseLibraryChanged();
        Refresh();
    }

    private void OnTrackDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var tracks = SelectedTracks();
        if (tracks.Count > 0)
        {
            TrackActions.PlayNow(tracks);
        }
    }

    private void OnTrackPlayClick(object sender, RoutedEventArgs e) => TrackActions.PlayNow(SelectedTracks());

    private void OnTrackEnqueueClick(object sender, RoutedEventArgs e) => TrackActions.Enqueue(SelectedTracks());
}
