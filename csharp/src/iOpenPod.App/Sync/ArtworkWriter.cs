using IOpenPod.App.Devices;
using IOpenPod.App.Library;
using IOpenPod.App.Logging;
using IOpenPod.DeviceRegistry;
using IOpenPod.IPodDb;
using IOpenPod.IPodDb.Artwork;
using IOpenPod.Storage;

namespace IOpenPod.App.Sync;

/// <summary>
/// 同步过程中的封面写入。
///
/// 策略是**只新增、不覆写**：本批封面写入新的 iTHMB 分片（或在最后一个分片尾部
/// 追加），已有分片与已有插图记录一律保持原样，因此不可能破坏设备上原有的封面。
/// </summary>
public sealed class ArtworkWriter
{
    private const long ShardCapacityBytes = 4L * 1024 * 1024;
    private const long MaxReadBytes = 64L * 1024 * 1024;

    private readonly ActiveDevice _device;
    private readonly LibrarySession _library;
    private readonly Dictionary<int, ShardState> _shards = [];
    private ArtworkDatabase? _database;

    private sealed class ShardState
    {
        public required string FileName { get; set; }

        public required long Offset { get; set; }

        public MemoryStream Buffer { get; } = new();
    }

    public ArtworkWriter(ActiveDevice device, LibrarySession library)
    {
        _device = device;
        _library = library;
    }

    /// <summary>本次同步是否改动了插图库。</summary>
    public bool HasChanges { get; private set; }

    private ArtworkDatabase EnsureDatabase()
    {
        if (_database is not null)
        {
            return _database;
        }

        var existing = _library.Artwork;
        if (existing is null)
        {
            var rootValue = _device.Profile?.Capabilities.Artwork.ArtworkRootValue ?? 1u;
            existing = ArtworkDatabase.CreateEmpty(rootValue);
            AppLog.Info("插图", "设备上没有插图库，已创建新的 ArtworkDB。");
        }

        _database = existing;
        return _database;
    }

    /// <summary>
    /// 为曲目写入封面；返回新的插图图像 ID，未写入时返回 <c>null</c>。
    /// </summary>
    public uint? AttachCover(StorageTransaction transaction, TrackRecord track, byte[]? coverBytes)
    {
        if (coverBytes is null || coverBytes.Length == 0)
        {
            return null;
        }

        var profile = _device.Profile;
        if (profile is null || !profile.Capabilities.Artwork.SupportsCoverArt ||
            profile.Capabilities.Artwork.CoverFormats.Count == 0)
        {
            return null;
        }

        try
        {
            var source = ImageLoader.Decode(coverBytes);
            var database = EnsureDatabase();
            var locations = new List<(uint FormatId, string FileName, long Offset, int ByteLength, int Width, int Height)>();

            foreach (var format in profile.Capabilities.Artwork.CoverFormats)
            {
                var encoded = IthmbCodec.Encode(source, new IthmbLayout(
                    format.Width, format.Height, format.RowBytes, Convert(format.PixelFormat)));

                var state = AllocateShard(format, encoded.Length);
                var offset = state.Offset + state.Buffer.Length;
                state.Buffer.Write(encoded);

                database.EnsureFormatFile((uint)format.FormatId, (uint)encoded.Length, state.FileName);
                locations.Add(((uint)format.FormatId, state.FileName, offset, encoded.Length, format.Width, format.Height));
            }

            var imageId = database.AddImage(track.DbTrackId, (uint)coverBytes.Length, locations);
            HasChanges = true;
            _ = transaction;
            return imageId;
        }
        catch (Exception error)
        {
            AppLog.Error("插图", $"封面写入失败：{track.Title}", error);
            return null;
        }
    }

    /// <summary>把本批生成的分片与插图库提交到事务中。</summary>
    public void Flush(StorageTransaction transaction)
    {
        if (!HasChanges)
        {
            return;
        }

        foreach (var state in _shards.Values)
        {
            if (state.Buffer.Length == 0)
            {
                continue;
            }

            var path = SessionPath.Create($"{DevicePaths.ArtworkDirectory}/{state.FileName}");
            if (state.Offset <= 0)
            {
                transaction.Stage(path, state.Buffer.ToArray());
            }
            else
            {
                var existing = _device.Session.ReadAllBytes(path, MaxReadBytes);
                var appended = state.Buffer.ToArray();
                var merged = new byte[existing.Length + appended.Length];
                Array.Copy(existing, merged, existing.Length);
                Array.Copy(appended, 0, merged, existing.Length, appended.Length);
                transaction.Stage(path, merged);
            }

            state.Buffer.SetLength(0);
            state.Offset = 0;
        }

        var database = EnsureDatabase();
        transaction.Stage(SessionPath.Create(DevicePaths.ArtworkDb), database.Serialize());
        _device.Artwork = database;
        AppLog.Info("插图", $"已为 {database.Items.Count} 张插图写入插图库。");
    }

    private ShardState AllocateShard(ArtworkFormat format, int size)
    {
        if (_shards.TryGetValue(format.FormatId, out var existing) &&
            existing.Buffer.Length + size <= ShardCapacityBytes)
        {
            return existing;
        }

        var fileName = NextShardName(format.FormatId);
        var offset = 0L;

        var candidates = EnumerateShards(format.FormatId);
        if (candidates.Count > 0)
        {
            var last = candidates[^1];
            if (SessionPath.TryCreate($"{DevicePaths.ArtworkDirectory}/{last}", out var lastPath))
            {
                var length = _device.Session.GetFileLength(lastPath);
                if (length > 0 && length + size <= ShardCapacityBytes)
                {
                    fileName = last;
                    offset = length;
                }
            }
        }

        var state = new ShardState { FileName = fileName, Offset = offset };
        _shards[format.FormatId] = state;
        return state;
    }

    private List<string> EnumerateShards(int formatId)
    {
        var results = new List<string>();
        var directory = SessionPath.Create(DevicePaths.ArtworkDirectory);
        if (!_device.Session.DirectoryExists(directory))
        {
            return results;
        }

        foreach (var path in _device.Session.EnumerateFiles(directory))
        {
            var fileName = path.FileName;
            if (fileName.StartsWith($"F{formatId}_", StringComparison.OrdinalIgnoreCase) &&
                fileName.EndsWith(".ithmb", StringComparison.OrdinalIgnoreCase))
            {
                results.Add(fileName);
            }
        }

        results.Sort(StringComparer.OrdinalIgnoreCase);
        return results;
    }

    private string NextShardName(int formatId)
    {
        var existing = new HashSet<string>(EnumerateShards(formatId), StringComparer.OrdinalIgnoreCase);
        for (var index = 1; index < 100000; index++)
        {
            var name = $"F{formatId}_{index}.ithmb";
            if (!existing.Contains(name))
            {
                return name;
            }
        }

        return $"F{formatId}_{Guid.NewGuid():N}.ithmb";
    }

    private static IthmbPixelFormat Convert(ArtworkPixelFormat format) => format switch
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
}
