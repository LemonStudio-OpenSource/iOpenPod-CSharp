namespace IOpenPod.IPodDb.Artwork;

/// <summary>
/// 紧打包的 RGB888 图像缓冲区。
/// 所有像素按 R、G、B 顺序逐行存放，行之间没有填充。
/// </summary>
public sealed class RgbImage
{
    public RgbImage(int width, int height)
        : this(width, height, new byte[width * height * 3])
    {
    }

    public RgbImage(int width, int height, byte[] pixels)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "图像尺寸必须为正数。");
        }

        if (pixels.Length != width * height * 3)
        {
            throw new ArgumentException(
                $"RGB 缓冲区长度 {pixels.Length} 与 {width}×{height} 不匹配。",
                nameof(pixels));
        }

        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>RGB888 像素数据。</summary>
    public byte[] Pixels { get; }

    public Span<byte> Row(int y) => Pixels.AsSpan(y * Width * 3, Width * 3);

    /// <summary>
    /// 等比例缩放并居中放置到目标尺寸（留黑边）。
    /// 缩小时使用面积平均（盒式滤波），放大时使用双线性插值。
    /// </summary>
    public RgbImage FitTo(int targetWidth, int targetHeight)
    {
        var scale = Math.Min((double)targetWidth / Width, (double)targetHeight / Height);
        var drawWidth = Math.Max(1, (int)Math.Round(Width * scale));
        var drawHeight = Math.Max(1, (int)Math.Round(Height * scale));

        var scaled = Resize(drawWidth, drawHeight);
        var result = new RgbImage(targetWidth, targetHeight);
        var offsetX = (targetWidth - drawWidth) / 2;
        var offsetY = (targetHeight - drawHeight) / 2;

        for (var y = 0; y < drawHeight; y++)
        {
            var sourceRow = scaled.Row(y);
            var targetRow = result.Row(offsetY + y)[(offsetX * 3)..];
            sourceRow.CopyTo(targetRow);
        }

        return result;
    }

    /// <summary>缩放到指定尺寸。</summary>
    public RgbImage Resize(int newWidth, int newHeight)
    {
        if (newWidth == Width && newHeight == Height)
        {
            return new RgbImage(Width, Height, (byte[])Pixels.Clone());
        }

        var downscaling = newWidth < Width || newHeight < Height;
        return downscaling
            ? ResizeBox(newWidth, newHeight)
            : ResizeBilinear(newWidth, newHeight);
    }

    /// <summary>面积平均缩放，适合产生高质量缩略图。</summary>
    private RgbImage ResizeBox(int newWidth, int newHeight)
    {
        var result = new RgbImage(newWidth, newHeight);
        var scaleX = (double)Width / newWidth;
        var scaleY = (double)Height / newHeight;

        for (var y = 0; y < newHeight; y++)
        {
            var sourceTop = (int)(y * scaleY);
            var sourceBottom = Math.Max(sourceTop + 1, (int)((y + 1) * scaleY));
            sourceBottom = Math.Min(sourceBottom, Height);

            for (var x = 0; x < newWidth; x++)
            {
                var sourceLeft = (int)(x * scaleX);
                var sourceRight = Math.Max(sourceLeft + 1, (int)((x + 1) * scaleX));
                sourceRight = Math.Min(sourceRight, Width);

                long red = 0, green = 0, blue = 0;
                var count = 0;

                for (var sy = sourceTop; sy < sourceBottom; sy++)
                {
                    var row = (sy * Width * 3) + (sourceLeft * 3);
                    for (var sx = sourceLeft; sx < sourceRight; sx++)
                    {
                        red += Pixels[row];
                        green += Pixels[row + 1];
                        blue += Pixels[row + 2];
                        row += 3;
                        count++;
                    }
                }

                if (count == 0)
                {
                    count = 1;
                }

                var target = ((y * newWidth) + x) * 3;
                result.Pixels[target] = (byte)(red / count);
                result.Pixels[target + 1] = (byte)(green / count);
                result.Pixels[target + 2] = (byte)(blue / count);
            }
        }

        return result;
    }

    /// <summary>双线性插值缩放。</summary>
    private RgbImage ResizeBilinear(int newWidth, int newHeight)
    {
        var result = new RgbImage(newWidth, newHeight);
        var scaleX = (double)(Width - 1) / Math.Max(1, newWidth - 1);
        var scaleY = (double)(Height - 1) / Math.Max(1, newHeight - 1);

        for (var y = 0; y < newHeight; y++)
        {
            var sourceY = y * scaleY;
            var y0 = (int)sourceY;
            var y1 = Math.Min(y0 + 1, Height - 1);
            var fy = sourceY - y0;

            for (var x = 0; x < newWidth; x++)
            {
                var sourceX = x * scaleX;
                var x0 = (int)sourceX;
                var x1 = Math.Min(x0 + 1, Width - 1);
                var fx = sourceX - x0;

                var index00 = ((y0 * Width) + x0) * 3;
                var index01 = ((y0 * Width) + x1) * 3;
                var index10 = ((y1 * Width) + x0) * 3;
                var index11 = ((y1 * Width) + x1) * 3;
                var target = ((y * newWidth) + x) * 3;

                for (var channel = 0; channel < 3; channel++)
                {
                    var top = (Pixels[index00 + channel] * (1 - fx)) + (Pixels[index01 + channel] * fx);
                    var bottom = (Pixels[index10 + channel] * (1 - fx)) + (Pixels[index11 + channel] * fx);
                    result.Pixels[target + channel] = (byte)Math.Clamp(
                        (int)Math.Round((top * (1 - fy)) + (bottom * fy)), 0, 255);
                }
            }
        }

        return result;
    }

    /// <summary>按正方形中心裁剪后再缩放到目标尺寸（用于专辑封面）。</summary>
    public RgbImage SquareCropAndFit(int size)
    {
        var side = Math.Min(Width, Height);
        var offsetX = (Width - side) / 2;
        var offsetY = (Height - side) / 2;
        var cropped = new RgbImage(side, side);

        for (var y = 0; y < side; y++)
        {
            var source = Pixels.AsSpan((((y + offsetY) * Width) + offsetX) * 3, side * 3);
            source.CopyTo(cropped.Row(y));
        }

        return cropped.Resize(size, size);
    }
}
