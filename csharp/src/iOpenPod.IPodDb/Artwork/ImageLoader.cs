using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace IOpenPod.IPodDb.Artwork;

/// <summary>图像解码失败。</summary>
public sealed class ImageDecodeException : IPodDbException
{
    public ImageDecodeException(string message) : base(message) { }

    public ImageDecodeException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// 使用 Windows 内置图像管线解码宿主图片（JPEG、PNG、BMP、GIF、TIFF、HEIF）。
///
/// 只依赖系统组件，因此不需要任何第三方图像库。
/// </summary>
public static class ImageLoader
{
    /// <summary>把编码后的图片字节解码为 RGB888。</summary>
    public static RgbImage Decode(byte[] encoded)
    {
        if (encoded.Length == 0)
        {
            throw new ImageDecodeException("图片内容为空。");
        }

        return DecodeAsync(encoded).GetAwaiter().GetResult();
    }

    /// <summary>从宿主文件解码图片。</summary>
    public static RgbImage DecodeFile(string hostPath)
    {
        try
        {
            return Decode(File.ReadAllBytes(hostPath));
        }
        catch (IOException error)
        {
            throw new ImageDecodeException($"无法读取图片文件：{hostPath}", error);
        }
    }

    private static async Task<RgbImage> DecodeAsync(byte[] encoded)
    {
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(encoded);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }

            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream);

            var pixelProvider = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Straight,
                new BitmapTransform(),
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.ColorManageToSRgb);

            var bgra = pixelProvider.DetachPixelData();
            var width = (int)decoder.PixelWidth;
            var height = (int)decoder.PixelHeight;

            if (width <= 0 || height <= 0)
            {
                throw new ImageDecodeException("图片尺寸无效。");
            }

            return FromBgra(bgra, width, height);
        }
        catch (ImageDecodeException)
        {
            throw;
        }
        catch (Exception error)
        {
            throw new ImageDecodeException($"无法解码图片：{error.Message}", error);
        }
    }

    /// <summary>把 BGRA8 缓冲区转换为 RGB888。</summary>
    public static RgbImage FromBgra(byte[] bgra, int width, int height)
    {
        if (bgra.Length < width * height * 4)
        {
            throw new ImageDecodeException("BGRA 缓冲区长度与图像尺寸不匹配。");
        }

        var image = new RgbImage(width, height);
        var target = image.Pixels;

        for (var i = 0; i < width * height; i++)
        {
            var source = i * 4;
            var destination = i * 3;
            var b = bgra[source];
            var g = bgra[source + 1];
            var r = bgra[source + 2];
            var a = bgra[source + 3];

            if (a != 255 && a != 0)
            {
                // 与白底合成，避免半透明封面出现黑边。
                r = (byte)(((r * a) + (255 * (255 - a))) / 255);
                g = (byte)(((g * a) + (255 * (255 - a))) / 255);
                b = (byte)(((b * a) + (255 * (255 - a))) / 255);
            }
            else if (a == 0)
            {
                r = g = b = 255;
            }

            target[destination] = r;
            target[destination + 1] = g;
            target[destination + 2] = b;
        }

        return image;
    }

    /// <summary>把 RGB888 编码为 PNG 字节，供界面显示使用。</summary>
    public static byte[] EncodePng(RgbImage image)
    {
        var bgra = new byte[image.Width * image.Height * 4];
        for (var i = 0; i < image.Width * image.Height; i++)
        {
            bgra[i * 4] = image.Pixels[(i * 3) + 2];
            bgra[(i * 4) + 1] = image.Pixels[(i * 3) + 1];
            bgra[(i * 4) + 2] = image.Pixels[i * 3];
            bgra[(i * 4) + 3] = 255;
        }

        return EncodePngAsync(bgra, image.Width, image.Height).GetAwaiter().GetResult();
    }

    /// <summary>把 BGRA8 缓冲区编码为 PNG 字节。</summary>
    public static byte[] EncodePng(byte[] bgra, int width, int height) =>
        EncodePngAsync(bgra, width, height).GetAwaiter().GetResult();

    private static async Task<byte[]> EncodePngAsync(byte[] bgra, int width, int height)
    {
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            (uint)width,
            (uint)height,
            96,
            96,
            bgra);
        await encoder.FlushAsync();

        stream.Seek(0);
        var buffer = new byte[stream.Size];
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)stream.Size);
        reader.ReadBytes(buffer);
        return buffer;
    }
}
