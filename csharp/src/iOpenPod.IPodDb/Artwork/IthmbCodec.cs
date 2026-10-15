namespace IOpenPod.IPodDb.Artwork;

/// <summary>iTHMB 图像解码或编码失败。</summary>
public sealed class IthmbException : IPodDbException
{
    public IthmbException(string message) : base(message) { }
}

/// <summary>iTHMB 中实际出现的像素排布。</summary>
public enum IthmbPixelFormat
{
    Rgb565Le,
    Rgb565Be,
    Rgb565Be90,
    Rgb555Le,
    Rgb555Be,
    RecRgb555Le,
    Uyvy,
    UyvyFields,
    I420Le,
    Jpeg,
}

/// <summary>iTHMB 图像在文件中的定位与几何信息。</summary>
public sealed record IthmbLayout(
    int Width,
    int Height,
    int RowBytes,
    IthmbPixelFormat PixelFormat,
    int HorizontalPadding = 0,
    int VerticalPadding = 0)
{
    public int MinimumRowBytes => Width * 2;
}

/// <summary>
/// iTHMB 像素编解码。
///
/// 目录中所有带封面能力的设备使用的都是 RGB565 小端布局，
/// 因此该路径经过重点优化；其余已知布局也一并支持以保证向后兼容。
/// </summary>
public static class IthmbCodec
{
    private const int MaxDimension = 8192;
    private const long MaxPixels = 32L * 1024 * 1024;

    /// <summary>把一段有界的 iTHMB 字节解码为 RGB888 图像。</summary>
    public static RgbImage Decode(ReadOnlySpan<byte> payload, IthmbLayout layout)
    {
        if (layout.Width <= 0 || layout.Height <= 0 ||
            layout.Width > MaxDimension || layout.Height > MaxDimension ||
            (long)layout.Width * layout.Height > MaxPixels)
        {
            throw new IthmbException($"iTHMB 尺寸 {layout.Width}×{layout.Height} 超出支持范围。");
        }

        return layout.PixelFormat switch
        {
            IthmbPixelFormat.Rgb565Le => DecodePacked(payload, layout, swapBytes: false, bigEndian: false),
            IthmbPixelFormat.Rgb555Le => DecodePacked(payload, layout, swapBytes: false, bigEndian: false, is555: true),
            IthmbPixelFormat.Rgb565Be => DecodePacked(payload, layout, swapBytes: true, bigEndian: false),
            IthmbPixelFormat.Rgb555Be => DecodePacked(payload, layout, swapBytes: true, bigEndian: false, is555: true),
            IthmbPixelFormat.Rgb565Be90 => DecodePacked(payload, layout, swapBytes: true, bigEndian: true),
            IthmbPixelFormat.Uyvy => DecodeUyvy(payload, layout, fields: false),
            IthmbPixelFormat.UyvyFields => DecodeUyvy(payload, layout, fields: true),
            IthmbPixelFormat.I420Le => DecodeI420(payload, layout),
            IthmbPixelFormat.Jpeg => DecodeJpeg(payload, layout),
            _ => throw new IthmbException($"尚不支持 iTHMB 像素格式 {layout.PixelFormat}。"),
        };
    }

    /// <summary>把 RGB888 图像编码为指定 iTHMB 布局的字节。</summary>
    public static byte[] Encode(RgbImage image, IthmbLayout layout)
    {
        var fitted = image.FitTo(layout.Width, layout.Height);
        var rowBytes = layout.RowBytes > 0 ? layout.RowBytes : layout.Width * 2;
        rowBytes = Math.Max(rowBytes, layout.Width * 2);

        var buffer = new byte[rowBytes * layout.Height];

        for (var y = 0; y < layout.Height; y++)
        {
            var row = fitted.Row(y);
            var target = y * rowBytes;
            for (var x = 0; x < layout.Width; x++)
            {
                var r = row[x * 3];
                var g = row[(x * 3) + 1];
                var b = row[(x * 3) + 2];

                // 以 5-6-5 打包并保留高位精度。
                var value = (ushort)(((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3));
                buffer[target + (x * 2)] = (byte)(value & 0xFF);
                buffer[target + (x * 2) + 1] = (byte)(value >> 8);
            }
        }

        return buffer;
    }

    private static RgbImage DecodePacked(
        ReadOnlySpan<byte> payload,
        IthmbLayout layout,
        bool swapBytes,
        bool bigEndian,
        bool is555 = false)
    {
        var rowBytes = layout.RowBytes > 0 ? layout.RowBytes : layout.Width * 2;
        if (rowBytes < layout.Width * 2 || rowBytes % 2 != 0)
        {
            throw new IthmbException(
                $"iTHMB 行宽 {rowBytes} 无法容纳 {layout.Width} 个 16 位像素。");
        }

        var required = rowBytes * layout.Height;
        if (payload.Length < required)
        {
            throw new IthmbException($"iTHMB 数据长度 {payload.Length} 小于所需的 {required} 字节。");
        }

        var image = new RgbImage(layout.Width, layout.Height);

        for (var y = 0; y < layout.Height; y++)
        {
            var sourceRow = payload.Slice(y * rowBytes, layout.Width * 2);
            var targetRow = image.Row(y);

            for (var x = 0; x < layout.Width; x++)
            {
                byte low = sourceRow[x * 2];
                byte high = sourceRow[(x * 2) + 1];
                if (swapBytes)
                {
                    (low, high) = (high, low);
                }

                var value = bigEndian
                    ? (ushort)((low << 8) | high)
                    : (ushort)((high << 8) | low);

                int r, g, b;
                if (is555)
                {
                    r = (value >> 10) & 0x1F;
                    g = (value >> 5) & 0x1F;
                    b = value & 0x1F;
                    targetRow[x * 3] = Expand5(r);
                    targetRow[(x * 3) + 1] = Expand5(g);
                    targetRow[(x * 3) + 2] = Expand5(b);
                }
                else
                {
                    r = (value >> 11) & 0x1F;
                    g = (value >> 5) & 0x3F;
                    b = value & 0x1F;
                    targetRow[x * 3] = Expand5(r);
                    targetRow[(x * 3) + 1] = Expand6(g);
                    targetRow[(x * 3) + 2] = Expand5(b);
                }
            }
        }

        return image;
    }

    private static byte Expand5(int value) => (byte)((value << 3) | (value >> 2));

    private static byte Expand6(int value) => (byte)((value << 2) | (value >> 4));

    private static RgbImage DecodeUyvy(ReadOnlySpan<byte> payload, IthmbLayout layout, bool fields)
    {
        var width = layout.Width;
        var height = layout.Height;
        if (width % 2 != 0 || (fields && height % 2 != 0))
        {
            throw new IthmbException("UYVY 图像要求宽度为偶数（字段分离布局还要求高度为偶数）。");
        }

        var rowBytes = layout.RowBytes > 0 ? layout.RowBytes : width * 2;
        var required = rowBytes * height;
        if (payload.Length < required)
        {
            throw new IthmbException($"UYVY 数据长度 {payload.Length} 小于所需的 {required} 字节。");
        }

        var evenFieldRows = (height + 1) / 2;
        var image = new RgbImage(width, height);

        for (var y = 0; y < height; y++)
        {
            var sourceRowIndex = fields ? (y / 2) + (y % 2 == 1 ? evenFieldRows : 0) : y;
            var start = sourceRowIndex * rowBytes;
            var targetRow = image.Row(y);

            for (var x = 0; x < width; x += 2)
            {
                var cb = payload[start + (x * 2)];
                var y0 = payload[start + (x * 2) + 1];
                var cr = payload[start + (x * 2) + 2];
                var y1 = payload[start + (x * 2) + 3];

                WriteYCbCr(targetRow, x, y0, cb, cr);
                WriteYCbCr(targetRow, x + 1, y1, cb, cr);
            }
        }

        return image;
    }

    private static void WriteYCbCr(Span<byte> row, int x, byte y, byte cb, byte cr)
    {
        var yy = Math.Max(0, (y - 16) * 255 / 219);
        var cbb = (cb - 128) * 255 / 224;
        var crr = (cr - 128) * 255 / 224;

        var r = (int)Math.Round(yy + (1.402 * crr));
        var g = (int)Math.Round(yy - (0.344136 * cbb) - (0.714136 * crr));
        var b = (int)Math.Round(yy + (1.772 * cbb));

        row[x * 3] = (byte)Math.Clamp(r, 0, 255);
        row[(x * 3) + 1] = (byte)Math.Clamp(g, 0, 255);
        row[(x * 3) + 2] = (byte)Math.Clamp(b, 0, 255);
    }

    private static RgbImage DecodeI420(ReadOnlySpan<byte> payload, IthmbLayout layout)
    {
        var width = layout.Width;
        var height = layout.Height;
        if (width % 2 != 0 || height % 2 != 0)
        {
            throw new IthmbException("I420 图像要求宽高均为偶数。");
        }

        var lumaSize = width * height;
        var chromaSize = width / 2 * (height / 2);
        var required = lumaSize + (2 * chromaSize);
        if (payload.Length < required)
        {
            throw new IthmbException($"I420 数据长度 {payload.Length} 小于所需的 {required} 字节。");
        }

        var image = new RgbImage(width, height);
        var cbStart = lumaSize;
        var crStart = cbStart + chromaSize;

        for (var y = 0; y < height; y++)
        {
            var targetRow = image.Row(y);
            for (var x = 0; x < width; x++)
            {
                var luma = payload[(y * width) + x];
                var chromaIndex = (y / 2 * (width / 2)) + (x / 2);
                WriteYCbCr(targetRow, x, luma, payload[cbStart + chromaIndex], payload[crStart + chromaIndex]);
            }
        }

        return image;
    }

    private static RgbImage DecodeJpeg(ReadOnlySpan<byte> payload, IthmbLayout layout)
    {
        if (payload.Length == 0)
        {
            throw new IthmbException("JPEG 类型的 iTHMB 数据为空。");
        }

        var image = ImageLoader.Decode(payload.ToArray());
        return image.Width == layout.Width && image.Height == layout.Height
            ? image
            : image.FitTo(layout.Width, layout.Height);
    }
}
