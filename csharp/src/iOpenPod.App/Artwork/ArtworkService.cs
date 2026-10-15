using System.Collections.Concurrent;
using IOpenPod.App.Devices;
using IOpenPod.App.Logging;
using IOpenPod.DeviceRegistry;
using IOpenPod.IPodDb;
using IOpenPod.IPodDb.Artwork;
using IOpenPod.Storage;

namespace IOpenPod.App.Artwork;

/// <summary>
/// 专辑封面服务。
///
/// 只按需读取被引用的 iTHMB 字节范围，并在内存中缓存已解码的缩略图，
/// 因此浏览大型音乐库时不会一次性读入全部插图。
/// </summary>
public sealed class ArtworkService : IDisposable
{
    private const int MaxIthmbImageBytes = 16 * 1024 * 1024;
    private static readonly byte[] Empty = [];

    private readonly ActiveDevice _device;
    private readonly ConcurrentDictionary<string, byte[]> _cache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(2, 2);
    private bool _disposed;

    public ArtworkService(ActiveDevice device)
    {
        _device = device;
    }

    /// <summary>缓存中已有多少张封面。</summary>
    public int CachedCount => _cache.Count;

    /// <summary>取得指定曲目的专辑封面（PNG 字节）；没有封面时返回 <c>null</c>。</summary>
    public async Task<byte[]?> GetCoverPngAsync(
        TrackRecord track,
        int targetSize = 220,
        CancellationToken cancellationToken = default)
    {
        if (_disposed || track is null)
        {
            return null;
        }

        var artwork = _device.Artwork;
        if (artwork is null)
        {
            return null;
        }

        var item = artwork.FindForTrack(track.DbTrackId, track.ArtworkIdRef);
        if (item is null)
        {
            return null;
        }

        var location = ChooseLocation(item);
        if (location is null)
        {
            return null;
        }

        var key = $"{item.ImageId}:{location.FormatId}:{targetSize}";
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cache.TryGetValue(key, out cached))
            {
                return cached;
            }

            var png = await LoadCoverAsync(location, targetSize, cancellationToken).ConfigureAwait(false);
            if (png is not null)
            {
                _cache[key] = png;
            }

            return png;
        }
        catch (Exception error)
        {
            AppLog.Warning("插图", $"读取封面失败：{error.Message}");
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>直接读取某个图像 ID 的封面。</summary>
    public async Task<byte[]?> GetCoverPngByIdAsync(
        uint imageId,
        int targetSize = 220,
        CancellationToken cancellationToken = default)
    {
        var artwork = _device.Artwork;
        if (artwork is null)
        {
            return null;
        }

        var item = artwork.ItemForImageId(imageId);
        if (item is null)
        {
            return null;
        }

        var location = ChooseLocation(item);
        if (location is null)
        {
            return null;
        }

        var key = $"{item.ImageId}:{location.FormatId}:{targetSize}";
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var png = await LoadCoverAsync(location, targetSize, cancellationToken).ConfigureAwait(false);
        if (png is not null)
        {
            _cache[key] = png;
        }

        return png;
    }

    private async Task<byte[]?> LoadCoverAsync(
        IthmbLocation location,
        int targetSize,
        CancellationToken cancellationToken)
    {
        if (location.ByteLength <= 0 || location.ByteLength > MaxIthmbImageBytes)
        {
            return null;
        }

        if (string.IsNullOrEmpty(location.FileName) ||
            !SessionPath.TryCreate($"{DevicePaths.ArtworkDirectory}/{location.FileName}", out var path))
        {
            return null;
        }

        if (!_device.Session.FileExists(path))
        {
            return null;
        }

        byte[] payload;
        await using (var stream = await _device.Session.OpenReadAsync(path, cancellationToken).ConfigureAwait(false))
        {
            if (stream.Length < location.Offset + location.ByteLength)
            {
                return null;
            }

            stream.Seek(location.Offset, SeekOrigin.Begin);
            payload = new byte[location.ByteLength];
            await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        }

        var layout = BuildLayout(location);
        var image = IthmbCodec.Decode(payload, layout);

        var cover = targetSize > 0 ? image.SquareCropAndFit(targetSize) : image;
        return ImageLoader.EncodePng(cover);
    }

    private IthmbLayout BuildLayout(IthmbLocation location)
    {
        var catalogFormat = _device.Profile?.Capabilities.Artwork.CoverFormat((int)location.FormatId);
        if (catalogFormat is not null)
        {
            return new IthmbLayout(
                location.Width > 0 ? location.Width : catalogFormat.Width,
                location.Height > 0 ? location.Height : catalogFormat.Height,
                catalogFormat.RowBytes,
                ConvertPixelFormat(catalogFormat.PixelFormat),
                location.HorizontalPadding,
                location.VerticalPadding);
        }

        // 目录中没有该格式时按最保守的布局推断：RGB565 小端、行紧打包。
        var width = location.Width > 0 ? location.Width : 128;
        var height = location.Height > 0 ? location.Height : width;
        var rowBytes = location.ByteLength >= (long)width * height * 2
            ? (int)(location.ByteLength / Math.Max(1, height))
            : width * 2;

        return new IthmbLayout(width, height, rowBytes, IthmbPixelFormat.Rgb565Le,
            location.HorizontalPadding, location.VerticalPadding);
    }

    private static IthmbPixelFormat ConvertPixelFormat(ArtworkPixelFormat format) => format switch
    {
        ArtworkPixelFormat.Rgb565Le => IthmbPixelFormat.Rgb565Le,
        ArtworkPixelFormat.Rgb565Be => IthmbPixelFormat.Rgb565Be,
        ArtworkPixelFormat.Rgb565Be90 => IthmbPixelFormat.Rgb565Be90,
        ArtworkPixelFormat.Rgb555Le => IthmbPixelFormat.Rgb555Le,
        ArtworkPixelFormat.Rgb555Be => IthmbPixelFormat.Rgb555Be,
        ArtworkPixelFormat.RecRgb555Le => IthmbPixelFormat.RecRgb555Le,
        ArtworkPixelFormat.Uyvy => IthmbPixelFormat.Uyvy,
        ArtworkPixelFormat.UyvyFields => IthmbPixelFormat.UyvyFields,
        ArtworkPixelFormat.I420Le => IthmbPixelFormat.I420Le,
        ArtworkPixelFormat.Jpeg => IthmbPixelFormat.Jpeg,
        _ => IthmbPixelFormat.Rgb565Le,
    };

    /// <summary>在多种表示中挑选最适合列表展示的一种。</summary>
    private IthmbLocation? ChooseLocation(ArtworkItem item)
    {
        if (item.Locations.Count == 0)
        {
            return null;
        }

        // 优先选择目录中登记为“封面”的格式，其次选择尺寸最大的。
        var covers = item.Locations
            .Where(location => _device.Profile?.Capabilities.Artwork.CoverFormat((int)location.FormatId) is not null)
            .ToArray();

        var pool = covers.Length > 0 ? covers : item.Locations.ToArray();
        return pool
            .OrderBy(location => Math.Abs(((long)location.Width * location.Height) - (128L * 128L)))
            .First();
    }

    /// <summary>清空封面缓存（例如保存后插图发生了变化）。</summary>
    public void Clear() => _cache.Clear();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cache.Clear();
        _gate.Dispose();
    }
}
