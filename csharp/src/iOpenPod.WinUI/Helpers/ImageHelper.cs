using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace IOpenPod.WinUI.Helpers;

/// <summary>界面图像辅助方法。</summary>
public static class ImageHelper
{
    /// <summary>把内存中的图片字节（PNG/JPEG）转换为可绑定的位图。</summary>
    public static async Task<BitmapImage?> ToBitmapAsync(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return null;
        }

        try
        {
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }

            stream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            return bitmap;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>把宿主文件转换为位图。</summary>
    public static async Task<BitmapImage?> FromFileAsync(string hostPath)
    {
        try
        {
            return await ToBitmapAsync(await File.ReadAllBytesAsync(hostPath));
        }
        catch (Exception)
        {
            return null;
        }
    }
}
