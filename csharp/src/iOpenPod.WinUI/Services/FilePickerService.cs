using Windows.Storage;
using Windows.Storage.Pickers;

namespace IOpenPod.WinUI.Services;

/// <summary>文件与文件夹选择器。非打包应用必须显式绑定窗口句柄。</summary>
public static class FilePickerService
{
    /// <summary>选择一个文件夹。</summary>
    public static async Task<string?> PickFolderAsync(string? startPath = null)
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.MusicLibrary,
        };
        picker.FileTypeFilter.Add("*");
        Initialize(picker);

        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    /// <summary>选择多个媒体文件。</summary>
    public static async Task<IReadOnlyList<string>> PickMediaFilesAsync()
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.MusicLibrary,
            ViewMode = PickerViewMode.List,
        };
        picker.FileTypeFilter.Add(".mp3");
        picker.FileTypeFilter.Add(".m4a");
        picker.FileTypeFilter.Add(".m4b");
        picker.FileTypeFilter.Add(".aac");
        picker.FileTypeFilter.Add(".wav");
        picker.FileTypeFilter.Add(".aiff");
        picker.FileTypeFilter.Add(".flac");
        picker.FileTypeFilter.Add(".ogg");
        picker.FileTypeFilter.Add(".wma");
        picker.FileTypeFilter.Add(".mp4");
        picker.FileTypeFilter.Add(".m4v");
        Initialize(picker);

        var files = await picker.PickMultipleFilesAsync();
        return files?.Select(file => file.Path).ToArray() ?? [];
    }

    private static void Initialize(object picker)
    {
        var handle = App.WindowHandle;
        if (handle != IntPtr.Zero)
        {
            WinRT.Interop.InitializeWithWindow.Initialize(picker, handle);
        }
    }
}
