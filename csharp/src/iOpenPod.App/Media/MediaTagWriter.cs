using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace IOpenPod.App.Media;

/// <summary>一次写回源文件的标签修改。为 null 的字段表示不修改。</summary>
public sealed record TagWriteRequest
{
    public string? Title { get; init; }
    public string? Artist { get; init; }
    public string? AlbumArtist { get; init; }
    public string? Album { get; init; }
    public string? Genre { get; init; }
    public string? Composer { get; init; }
    public string? Comment { get; init; }
    public string? Grouping { get; init; }
    public string? Lyrics { get; init; }
    public string? SortTitle { get; init; }
    public string? SortArtist { get; init; }
    public string? SortAlbum { get; init; }
    public int? TrackNumber { get; init; }
    public int? TotalTracks { get; init; }
    public int? DiscNumber { get; init; }
    public int? TotalDiscs { get; init; }
    public int? Year { get; init; }
    public int? Bpm { get; init; }
    public bool? IsCompilation { get; init; }
    public byte[]? CoverImage { get; init; }
    public string? CoverMimeType { get; init; }
    /// <summary>是否删除已有封面。</summary>
    public bool RemoveCover { get; init; }
}

/// <summary>写入结果。</summary>
public sealed record TagWriteResult(
    bool Success,
    bool Changed,
    IReadOnlyList<string> WrittenFields,
    IReadOnlyList<string> UnsupportedFields,
    string? BackupPath,
    string Message);

/// <summary>宿主媒体文件的标签写入。写入过程原子化：同目录临时文件 + 原子替换。</summary>
public static class MediaTagWriter
{
    /// <summary>写入时预留的 ID3v2 空闲字节数。</summary>
    internal const int PaddingBytes = 1024;

    /// <summary>该文件格式能够写入的字段（中文名称，用于界面提示）。</summary>
    public static IReadOnlyList<string> SupportedFields(string hostPath)
    {
        ArgumentNullException.ThrowIfNull(hostPath);

        TagContainer container = TagContainerDetect.Detect(hostPath);
        return container switch
        {
            TagContainer.Id3 => Id3WriteSupport.SupportedFieldNames(),
            TagContainer.Mp4 => Mp4WriteSupport.SupportedFieldNames(),
            TagContainer.Flac => FlacWriteSupport.SupportedFieldNames(),
            TagContainer.Ogg => FlacWriteSupport.SupportedFieldNames(),
            TagContainer.Wav => TagContainerDetect.WavUsesInfo(hostPath)
                ? WavInfoWriter.SupportedFieldNames()
                : Id3WriteSupport.SupportedFieldNames(),
            TagContainer.Aiff => TagContainerDetect.AiffUsesText(hostPath)
                ? AiffTextWriter.SupportedFieldNames()
                : Id3WriteSupport.SupportedFieldNames(),
            _ => TagFields.ReaderFieldNames(),
        };
    }

    /// <summary>把给定的标签写回源文件。永不抛异常（除 ArgumentNullException）。</summary>
    /// <param name="hostPath">宿主文件绝对路径。</param>
    /// <param name="request">要写入的字段；null 表示不修改。</param>
    /// <param name="createBackup">是否在同目录生成 <c>&lt;原文件名&gt;.iopenpod.bak</c> 备份。</param>
    /// <param name="cancellationToken">取消标记。</param>
    public static async Task<TagWriteResult> WriteAsync(
        string hostPath,
        TagWriteRequest request,
        bool createBackup = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hostPath);
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            return await WriteCoreAsync(hostPath, request, createBackup, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Failure("写入已取消，原文件未被修改。");
        }
        catch (Exception ex)
        {
            return Failure($"写入标签失败：{ex.Message}（原文件未被修改）");
        }
    }

    /// <summary>写入主流程：解析 → 比较 → 生成临时文件 → 原子替换。</summary>
    private static async Task<TagWriteResult> WriteCoreAsync(
        string hostPath,
        TagWriteRequest request,
        bool createBackup,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(hostPath))
        {
            return Failure("未提供有效的文件路径。");
        }

        string path;
        try
        {
            path = Path.GetFullPath(hostPath);
        }
        catch (Exception ex)
        {
            return Failure($"文件路径无效：{ex.Message}");
        }

        if (!File.Exists(path))
        {
            return Failure("文件不存在，无法写入标签。");
        }

        TagContainer container = TagContainerDetect.Detect(path);
        if (container == TagContainer.Unsupported)
        {
            return new TagWriteResult(
                false,
                false,
                [],
                TagFields.DescribeAll(request),
                null,
                "该格式暂不支持写入标签。");
        }

        FileAttributes attributes;
        DateTime creationTime;
        DateTime writeTime;
        try
        {
            FileInfo info = new(path);
            attributes = info.Attributes;
            creationTime = info.CreationTime;
            writeTime = info.LastWriteTime;
        }
        catch (Exception ex)
        {
            return Failure($"无法读取文件属性：{ex.Message}");
        }

        if ((attributes & FileAttributes.Directory) != 0)
        {
            return Failure("目标是目录，无法写入标签。");
        }

        if ((attributes & FileAttributes.ReadOnly) != 0)
        {
            return Failure("文件为只读，无法写入标签。");
        }

        MediaTagInfo current = MediaTagReader.Read(path);
        TagMetadata existing = TagMetadata.From(current);
        TagFieldMapping.FillFromContainer(container, path, existing);
        TagWritePlan plan = TagFieldMapping.Build(container, path, request, existing);

        if (plan.SupportedChanges.Count == 0 && plan.UnsupportedFields.Count > 0)
        {
            return new TagWriteResult(
                false,
                false,
                [],
                plan.UnsupportedFields,
                null,
                $"该格式无法写入请求中的任何字段（{string.Join('、', plan.UnsupportedFields)}），未修改文件。");
        }

        if (plan.ChangedFields.Count == 0)
        {
            string unchangedMessage = plan.UnsupportedFields.Count == 0
                ? "标签已是目标值，未修改文件。"
                : $"标签已是目标值，未修改文件。以下字段该格式无法表达，已跳过：{string.Join('、', plan.UnsupportedFields)}。";
            return new TagWriteResult(true, false, [], plan.UnsupportedFields, null, unchangedMessage);
        }

        string? backupPath = null;
        if (createBackup)
        {
            backupPath = path + ".iopenpod.bak";
            try
            {
                await CopyFileAsync(path, backupPath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                TryDelete(backupPath);
                return Failure($"创建备份失败：{ex.Message}（原文件未被修改）");
            }
        }

        string tempPath = path + ".iopenpod.tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            bool written = await TagContainerDispatch.WriteAsync(
                container, path, request, plan.ChangedFields, existing, tempPath, cancellationToken).ConfigureAwait(false);

            if (!written)
            {
                TryDelete(tempPath);
                if (backupPath is not null)
                {
                    TryDelete(backupPath);
                }

                return Failure("该文件的容器结构无法写入标签，未修改文件。");
            }

            await CommitAsync(path, tempPath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryDelete(tempPath);
            if (backupPath is not null)
            {
                TryDelete(backupPath);
            }

            return Failure("写入已取消，原文件未被修改。");
        }
        catch (Exception ex)
        {
            TryDelete(tempPath);
            if (backupPath is not null)
            {
                TryDelete(backupPath);
            }

            return Failure($"写入标签失败：{ex.Message}（原文件未被修改）");
        }

        // 写回原文件的时间戳；失败不影响整体结果。
        try
        {
            File.SetCreationTime(path, creationTime);
            File.SetLastWriteTime(path, writeTime);
        }
        catch
        {
            // 时间戳恢复失败不是致命问题。
        }

        string message = plan.UnsupportedFields.Count == 0
            ? $"已写入 {plan.WrittenFields.Count} 个字段。"
            : $"已写入 {plan.WrittenFields.Count} 个字段；该格式无法表达，已跳过：{string.Join('、', plan.UnsupportedFields)}。";

        return new TagWriteResult(true, true, [.. plan.WrittenFields], plan.UnsupportedFields, backupPath, message);
    }

    /// <summary>用临时文件原子替换原文件。</summary>
    private static async Task CommitAsync(string path, string tempPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReadOnly) != 0)
        {
            throw new IOException("文件为只读。");
        }

        FileAttributes tempAttributes = File.GetAttributes(tempPath);
        if ((tempAttributes & FileAttributes.ReadOnly) != 0)
        {
            File.SetAttributes(tempPath, attributes & ~FileAttributes.ReadOnly);
        }

        // 让临时文件的数据真正落盘，再替换。
        using (FileStream flush = new(tempPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1, useAsync: false))
        {
            await flush.FlushAsync(cancellationToken).ConfigureAwait(false);
            flush.Flush(flushToDisk: true);
        }

        try
        {
            File.Replace(tempPath, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            return;
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or IOException or UnauthorizedAccessException)
        {
            // 某些卷不支持 File.Replace，退化为「删除后移动」；此时临时文件已完整落盘。
        }

        File.Delete(path);
        File.Move(tempPath, path);
    }

    private static TagWriteResult Failure(string message) => new(false, false, [], [], null, message);

    /// <summary>复制文件（异步、可取消）。</summary>
    internal static async Task CopyFileAsync(string source, string destination, CancellationToken cancellationToken)
    {
        await using FileStream input = OpenRead(source);
        await using FileStream output = new(
            destination, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true);
        await input.CopyToAsync(output, 128 * 1024, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>以只读方式打开宿主文件。</summary>
    internal static FileStream OpenRead(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: true);

    /// <summary>创建写入用的临时文件流。</summary>
    internal static FileStream CreateWrite(string path)
        => new(path, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true);

    /// <summary>尽量删除临时文件，忽略失败。</summary>
    internal static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
        }
        catch
        {
            // 清理失败不影响结果。
        }
    }
}

/// <summary>按容器分派到具体写入器。</summary>
internal static class TagContainerDispatch
{
    /// <summary>把变更写入临时文件；返回 false 表示该文件结构无法写入。</summary>
    public static Task<bool> WriteAsync(
        TagContainer container,
        string sourcePath,
        TagWriteRequest request,
        IReadOnlyList<TagFieldId> changes,
        TagMetadata existing,
        string tempPath,
        CancellationToken cancellationToken)
        => container switch
        {
            TagContainer.Id3 => Id3FileWriter.WriteAsync(sourcePath, request, changes, existing, tempPath, cancellationToken),
            TagContainer.Mp4 => Mp4FileWriter.WriteAsync(sourcePath, request, changes, tempPath, cancellationToken),
            TagContainer.Flac => FlacFileWriter.WriteAsync(sourcePath, request, changes, tempPath, cancellationToken),
            TagContainer.Ogg => OggFileWriter.WriteAsync(sourcePath, request, changes, tempPath, cancellationToken),
            TagContainer.Wav => WavFileWriter.WriteAsync(sourcePath, request, changes, tempPath, cancellationToken),
            TagContainer.Aiff => AiffFileWriter.WriteAsync(sourcePath, request, changes, tempPath, cancellationToken),
            _ => Task.FromResult(false),
        };
}

/// <summary>宿主容器类型。</summary>
internal enum TagContainer
{
    /// <summary>无法写入的格式。</summary>
    Unsupported,

    /// <summary>ID3v2 宿主（MP3、带 ID3 块的 WAV / AIFF）。</summary>
    Id3,

    /// <summary>MP4 家族。</summary>
    Mp4,

    /// <summary>FLAC。</summary>
    Flac,

    /// <summary>Ogg Vorbis / Opus。</summary>
    Ogg,

    /// <summary>WAV。</summary>
    Wav,

    /// <summary>AIFF / AIFC。</summary>
    Aiff,
}

/// <summary>按魔数与扩展名判断宿主容器。</summary>
internal static class TagContainerDetect
{
    /// <summary>读取文件头判断容器。</summary>
    public static TagContainer Detect(string path)
    {
        string extension = MediaTagReader.GetExtension(path);
        byte[] head = ReadHead(path, 16);

        if (head.Length >= 4 && TagByteOps.IsAscii(head, "fLaC"))
        {
            return TagContainer.Flac;
        }

        if (head.Length >= 4 && TagByteOps.IsAscii(head, "OggS"))
        {
            return TagContainer.Ogg;
        }

        if (head.Length >= 12 && TagByteOps.IsAscii(head, "RIFF") && TagByteOps.IsAscii(head.AsSpan(8, 4), "WAVE"))
        {
            return TagContainer.Wav;
        }

        if (head.Length >= 12 && TagByteOps.IsAscii(head, "FORM")
            && (TagByteOps.IsAscii(head.AsSpan(8, 4), "AIFF") || TagByteOps.IsAscii(head.AsSpan(8, 4), "AIFC")))
        {
            return TagContainer.Aiff;
        }

        if (head.Length >= 8 && TagByteOps.IsAscii(head.AsSpan(4, 4), "ftyp"))
        {
            return TagContainer.Mp4;
        }

        if (head.Length >= 3 && TagByteOps.IsAscii(head, "ID3"))
        {
            return TagContainer.Id3;
        }

        return extension switch
        {
            "mp3" or "aac" or "mp2" => TagContainer.Id3,
            "wav" => TagContainer.Wav,
            "aif" or "aiff" or "aifc" => TagContainer.Aiff,
            "flac" => TagContainer.Flac,
            "ogg" or "oga" or "opus" or "spx" => TagContainer.Ogg,
            "m4a" or "m4b" or "m4p" or "m4r" or "m4v" or "mp4" or "mov" or "alac" => TagContainer.Mp4,
            _ => TagContainer.Unsupported,
        };
    }

    /// <summary>WAV 是否走 LIST/INFO 写入器（不存在 <c>id3 </c> 块）。</summary>
    public static bool WavUsesInfo(string path)
    {
        try
        {
            return !RiffChunkScan.HasChunk(path, "RIFF", isAiff: false, "id3 ", "ID3 ");
        }
        catch
        {
            return false;
        }
    }

    /// <summary>AIFF 是否走 NAME/AUTH/ANNO 写入器（不存在 <c>ID3 </c> 块）。</summary>
    public static bool AiffUsesText(string path)
    {
        try
        {
            return !RiffChunkScan.HasChunk(path, "FORM", isAiff: true, "ID3 ", "id3 ");
        }
        catch
        {
            return false;
        }
    }

    /// <summary>读取文件开头的若干字节。</summary>
    private static byte[] ReadHead(string path, int count)
    {
        try
        {
            using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            long length = RandomAccess.GetLength(handle);
            int wanted = (int)Math.Min(count, Math.Max(0, length));
            if (wanted <= 0)
            {
                return [];
            }

            byte[] buffer = new byte[wanted];
            int read = RandomAccess.Read(handle, buffer, 0);
            return read == wanted ? buffer : buffer[..Math.Max(0, read)];
        }
        catch
        {
            return [];
        }
    }
}

/// <summary>字节与缓冲区辅助。</summary>
internal static class TagByteOps
{
    /// <summary>比较前缀是否为给定 ASCII 串。</summary>
    public static bool IsAscii(ReadOnlySpan<byte> data, string ascii)
    {
        if (data.Length < ascii.Length)
        {
            return false;
        }

        for (int i = 0; i < ascii.Length; i++)
        {
            if (data[i] != (byte)ascii[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>不区分大小写比较两个字符串（null 视为空串）。</summary>
    public static bool TextEquals(string? left, string? right)
        => string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.OrdinalIgnoreCase);

    /// <summary>写入 1 字节。</summary>
    public static void WriteByte(IBufferWriter<byte> writer, byte value)
    {
        Span<byte> span = writer.GetSpan(1);
        span[0] = value;
        writer.Advance(1);
    }

    /// <summary>写入大端 16 位整数。</summary>
    public static void WriteUInt16BigEndian(IBufferWriter<byte> writer, ushort value)
    {
        Span<byte> span = writer.GetSpan(2);
        BinaryPrimitives.WriteUInt16BigEndian(span, value);
        writer.Advance(2);
    }

    /// <summary>写入大端 32 位整数。</summary>
    public static void WriteUInt32BigEndian(IBufferWriter<byte> writer, uint value)
    {
        Span<byte> span = writer.GetSpan(4);
        BinaryPrimitives.WriteUInt32BigEndian(span, value);
        writer.Advance(4);
    }

    /// <summary>写入大端 64 位整数。</summary>
    public static void WriteUInt64BigEndian(IBufferWriter<byte> writer, ulong value)
    {
        Span<byte> span = writer.GetSpan(8);
        BinaryPrimitives.WriteUInt64BigEndian(span, value);
        writer.Advance(8);
    }

    /// <summary>写入小端 32 位整数。</summary>
    public static void WriteUInt32LittleEndian(IBufferWriter<byte> writer, uint value)
    {
        Span<byte> span = writer.GetSpan(4);
        BinaryPrimitives.WriteUInt32LittleEndian(span, value);
        writer.Advance(4);
    }

    /// <summary>写入小端 64 位整数。</summary>
    public static void WriteUInt64LittleEndian(IBufferWriter<byte> writer, ulong value)
    {
        Span<byte> span = writer.GetSpan(8);
        BinaryPrimitives.WriteUInt64LittleEndian(span, value);
        writer.Advance(8);
    }

    /// <summary>写入 ASCII 串。</summary>
    public static void WriteAscii(IBufferWriter<byte> writer, string text)
    {
        Span<byte> span = writer.GetSpan(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            span[i] = (byte)text[i];
        }

        writer.Advance(text.Length);
    }

    /// <summary>写入任意字节。</summary>
    public static void WriteBytes(IBufferWriter<byte> writer, ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return;
        }

        writer.Write(data);
    }

    /// <summary>写入指定长度的 0。</summary>
    public static void WriteZeros(IBufferWriter<byte> writer, int count)
    {
        if (count <= 0)
        {
            return;
        }

        Span<byte> span = writer.GetSpan(count);
        span[..count].Clear();
        writer.Advance(count);
    }

    /// <summary>把 4 字节 ID3 帧/原子标识编码为 ASCII 字节。</summary>
    public static byte[] AsciiBytes(string id)
    {
        byte[] bytes = new byte[id.Length];
        for (int i = 0; i < id.Length; i++)
        {
            bytes[i] = (byte)id[i];
        }

        return bytes;
    }

    /// <summary>安全地把文件内容复制到输出流。</summary>
    public static async Task CopyRangeAsync(
        FileStream source,
        long offset,
        long length,
        FileStream destination,
        CancellationToken cancellationToken)
    {
        if (length <= 0)
        {
            return;
        }

        source.Position = offset;
        byte[] buffer = new byte[128 * 1024];
        long remaining = length;
        while (remaining > 0)
        {
            int wanted = (int)Math.Min(buffer.Length, remaining);
            int read = await source.ReadAsync(buffer.AsMemory(0, wanted), cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                throw new EndOfStreamException("源文件在读取过程中被截断。");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            remaining -= read;
        }
    }

    /// <summary>同步地把文件内容复制到输出流（用于已打开的文件）。</summary>
    public static void CopyRange(FileStream source, long offset, long length, FileStream destination)
    {
        if (length <= 0)
        {
            return;
        }

        source.Position = offset;
        byte[] buffer = new byte[128 * 1024];
        long remaining = length;
        while (remaining > 0)
        {
            int wanted = (int)Math.Min(buffer.Length, remaining);
            int read = source.Read(buffer, 0, wanted);
            if (read <= 0)
            {
                throw new EndOfStreamException("源文件在读取过程中被截断。");
            }

            destination.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    /// <summary>把整个文件读入内存（用于 MP4 的 moov 等中小体量区域）。</summary>
    public static byte[] ReadRange(FileStream source, long offset, int length)
    {
        if (length <= 0)
        {
            return [];
        }

        source.Position = offset;
        byte[] buffer = new byte[length];
        int total = 0;
        while (total < length)
        {
            int read = source.Read(buffer, total, length - total);
            if (read <= 0)
            {
                break;
            }

            total += read;
        }

        return total == length ? buffer : buffer[..total];
    }

    /// <summary>以宽松方式把字节解码为文本（UTF-8 优先，Latin-1 兜底）。</summary>
    public static string DecodeText(ReadOnlySpan<byte> data) => TagText.DecodeUnknown(data);
}

/// <summary>需要写入的字段标识。</summary>
internal enum TagFieldId
{
    Title,
    Artist,
    AlbumArtist,
    Album,
    Genre,
    Composer,
    Comment,
    Grouping,
    Lyrics,
    SortTitle,
    SortArtist,
    SortAlbum,
    TrackNumber,
    TotalTracks,
    DiscNumber,
    TotalDiscs,
    Year,
    Bpm,
    Compilation,
    Cover,
    RemoveCover,
}

/// <summary>字段中文名与取值器。</summary>
internal readonly record struct TagFieldDescriptor(TagFieldId Id, string Name, Func<TagWriteRequest, string?> Text);

/// <summary>字段清单与差异计算。</summary>
internal static class TagFields
{
    /// <summary>顺序固定的字段描述表，索引与 <see cref="TagFieldId"/> 对应。</summary>
    public static readonly TagFieldDescriptor[] Descriptors =
    [
        new(TagFieldId.Title, "标题", r => r.Title),
        new(TagFieldId.Artist, "艺术家", r => r.Artist),
        new(TagFieldId.AlbumArtist, "专辑艺术家", r => r.AlbumArtist),
        new(TagFieldId.Album, "专辑", r => r.Album),
        new(TagFieldId.Genre, "流派", r => r.Genre),
        new(TagFieldId.Composer, "作曲者", r => r.Composer),
        new(TagFieldId.Comment, "备注", r => r.Comment),
        new(TagFieldId.Grouping, "分组", r => r.Grouping),
        new(TagFieldId.Lyrics, "歌词", r => r.Lyrics),
        new(TagFieldId.SortTitle, "排序用标题", r => r.SortTitle),
        new(TagFieldId.SortArtist, "排序用艺术家", r => r.SortArtist),
        new(TagFieldId.SortAlbum, "排序用专辑", r => r.SortAlbum),
        new(TagFieldId.TrackNumber, "音轨号", r => r.TrackNumber?.ToString(CultureInfo.InvariantCulture)),
        new(TagFieldId.TotalTracks, "音轨总数", r => r.TotalTracks?.ToString(CultureInfo.InvariantCulture)),
        new(TagFieldId.DiscNumber, "碟号", r => r.DiscNumber?.ToString(CultureInfo.InvariantCulture)),
        new(TagFieldId.TotalDiscs, "碟总数", r => r.TotalDiscs?.ToString(CultureInfo.InvariantCulture)),
        new(TagFieldId.Year, "年份", r => r.Year?.ToString(CultureInfo.InvariantCulture)),
        new(TagFieldId.Bpm, "BPM", r => r.Bpm?.ToString(CultureInfo.InvariantCulture)),
        new(TagFieldId.Compilation, "合辑", r => r.IsCompilation is { } value ? (value ? "1" : "0") : null),
        new(TagFieldId.Cover, "封面", _ => null),
        new(TagFieldId.RemoveCover, "删除封面", _ => null),
    ];

    /// <summary>所有字段的中文名（按枚举顺序）。</summary>
    public static string NameOf(TagFieldId id) => Descriptors[(int)id].Name;

    /// <summary>只读读取器能够返回的字段（用于不支持写入的格式）。</summary>
    public static IReadOnlyList<string> ReaderFieldNames() =>
    [
        "标题", "艺术家", "专辑艺术家", "专辑", "流派", "作曲者", "备注", "分组", "歌词",
        "音轨号", "音轨总数", "碟号", "碟总数", "年份", "BPM", "合辑", "封面",
    ];

    /// <summary>描述请求中的所有非空字段（用于不支持格式的提示）。</summary>
    public static List<string> DescribeAll(TagWriteRequest request)
    {
        List<string> names = [];
        foreach (TagFieldDescriptor descriptor in Descriptors)
        {
            if (descriptor.Id == TagFieldId.Cover)
            {
                if (request.CoverImage is { Length: > 0 })
                {
                    names.Add(descriptor.Name);
                }

                continue;
            }

            if (descriptor.Id == TagFieldId.RemoveCover)
            {
                if (request.RemoveCover)
                {
                    names.Add(descriptor.Name);
                }

                continue;
            }

            if (!string.IsNullOrEmpty(descriptor.Text(request)))
            {
                names.Add(descriptor.Name);
            }
        }

        return names;
    }

    /// <summary>取「已请求字段」的规范化取值（封面/删除封面用占位符）。</summary>
    public static Dictionary<TagFieldId, string> RequestedValues(TagWriteRequest request)
    {
        Dictionary<TagFieldId, string> values = [];
        foreach (TagFieldDescriptor descriptor in Descriptors)
        {
            switch (descriptor.Id)
            {
                case TagFieldId.Cover when request.CoverImage is { Length: > 0 }:
                    values[descriptor.Id] = "1";
                    break;
                case TagFieldId.RemoveCover when request.RemoveCover:
                    values[descriptor.Id] = "1";
                    break;
                case TagFieldId.Cover:
                case TagFieldId.RemoveCover:
                    break;
                default:
                    string? text = descriptor.Text(request);
                    if (!string.IsNullOrEmpty(text))
                    {
                        values[descriptor.Id] = text;
                    }

                    break;
            }
        }

        return values;
    }
}

/// <summary>一次写入的差异计划。</summary>
internal sealed class TagWritePlan
{
    /// <summary>当前容器支持的请求字段。</summary>
    public List<TagFieldId> SupportedChanges { get; } = [];

    /// <summary>实际需要改动的字段（支持且与现值不同）。</summary>
    public List<TagFieldId> ChangedFields { get; } = [];

    /// <summary>该容器无法表达的请求字段（中文名）。</summary>
    public List<string> UnsupportedFields { get; } = [];

    /// <summary>成功写入的字段（中文名）。</summary>
    public List<string> WrittenFields { get; } = [];
}

/// <summary>把请求映射到具体容器的字段能力，并计算差异。</summary>
internal static class TagFieldMapping
{
    /// <summary>补齐读取器不返回的字段现值（排序字段等），并按容器权威解析覆盖。</summary>
    public static void FillFromContainer(TagContainer container, string path, TagMetadata values)
    {
        switch (container)
        {
            case TagContainer.Id3:
                values.SupportsSortFrames = Id3FileWriter.DetectVersion(path) == 4;
                values.SortTitle = Id3FileWriter.ReadSortField(path, "TSOT");
                values.SortArtist = Id3FileWriter.ReadSortField(path, "TSOP");
                values.SortAlbum = Id3FileWriter.ReadSortField(path, "TSOA");
                break;
            case TagContainer.Flac:
                FlacFileWriter.FillMetadata(path, values);
                break;
            case TagContainer.Ogg:
                OggFileWriter.FillMetadata(path, values);
                break;
            case TagContainer.Wav:
                if (TagContainerDetect.WavUsesInfo(path))
                {
                    WavInfoWriter.FillMetadata(path, values);
                }
                else
                {
                    Id3ChunkWriter.FillMetadata(path, values, isAiff: false);
                }

                break;
            case TagContainer.Aiff:
                if (TagContainerDetect.AiffUsesText(path))
                {
                    AiffTextWriter.FillMetadata(path, values);
                }
                else
                {
                    Id3ChunkWriter.FillMetadata(path, values, isAiff: true);
                }

                break;
            case TagContainer.Mp4:
                Mp4FileWriter.FillMetadata(path, values);
                break;
        }
    }

    /// <summary>构造差异计划。</summary>
    public static TagWritePlan Build(TagContainer container, string path, TagWriteRequest request, TagMetadata current)
    {
        TagWritePlan plan = new();
        Dictionary<TagFieldId, string> requested = TagFields.RequestedValues(request);
        HashSet<TagFieldId> supported = container switch
        {
            TagContainer.Id3 => Id3WriteSupport.SupportedFields(current.SupportsSortFrames),
            TagContainer.Mp4 => Mp4WriteSupport.SupportedFields(),
            TagContainer.Flac => FlacWriteSupport.SupportedFields(),
            TagContainer.Ogg => FlacWriteSupport.SupportedFields(),
            TagContainer.Wav => TagContainerDetect.WavUsesInfo(path)
                ? WavInfoWriter.SupportedFields()
                : Id3WriteSupport.SupportedFields(current.SupportsSortFrames),
            TagContainer.Aiff => TagContainerDetect.AiffUsesText(path)
                ? AiffTextWriter.SupportedFields()
                : Id3WriteSupport.SupportedFields(current.SupportsSortFrames),
            _ => [],
        };

        TagMetadata values = current;
        foreach (TagFieldDescriptor descriptor in TagFields.Descriptors)
        {
            if (!requested.TryGetValue(descriptor.Id, out string? wanted))
            {
                continue;
            }

            bool writable = supported.Contains(descriptor.Id);
            if (writable && container == TagContainer.Id3 && !current.SupportsSortFrames
                && descriptor.Id is TagFieldId.SortTitle or TagFieldId.SortArtist or TagFieldId.SortAlbum)
            {
                // ID3v2.3 没有排序帧。
                writable = false;
            }

            if (!writable)
            {
                plan.UnsupportedFields.Add(descriptor.Name);
                continue;
            }

            plan.SupportedChanges.Add(descriptor.Id);
            if (!HasChanged(descriptor.Id, wanted, values))
            {
                continue;
            }

            plan.ChangedFields.Add(descriptor.Id);
            plan.WrittenFields.Add(descriptor.Name);
        }

        return plan;
    }

    /// <summary>判断某字段的目标值是否与文件现值不同。</summary>
    private static bool HasChanged(TagFieldId id, string wanted, TagMetadata current)
    {
        switch (id)
        {
            case TagFieldId.Title:
                return !TagByteOps.TextEquals(wanted, current.Title);
            case TagFieldId.Artist:
                return !TagByteOps.TextEquals(wanted, current.Artist);
            case TagFieldId.AlbumArtist:
                return !TagByteOps.TextEquals(wanted, current.AlbumArtist);
            case TagFieldId.Album:
                return !TagByteOps.TextEquals(wanted, current.Album);
            case TagFieldId.Genre:
                return !TagByteOps.TextEquals(wanted, current.Genre);
            case TagFieldId.Composer:
                return !TagByteOps.TextEquals(wanted, current.Composer);
            case TagFieldId.Comment:
                return !TagByteOps.TextEquals(wanted, current.Comment);
            case TagFieldId.Grouping:
                return !TagByteOps.TextEquals(wanted, current.Grouping);
            case TagFieldId.Lyrics:
                return !TagByteOps.TextEquals(wanted, current.Lyrics);
            case TagFieldId.SortTitle:
                return !TagByteOps.TextEquals(wanted, current.SortTitle);
            case TagFieldId.SortArtist:
                return !TagByteOps.TextEquals(wanted, current.SortArtist);
            case TagFieldId.SortAlbum:
                return !TagByteOps.TextEquals(wanted, current.SortAlbum);
            case TagFieldId.TrackNumber:
                return ParseInt(wanted) != current.TrackNumber;
            case TagFieldId.TotalTracks:
                return ParseInt(wanted) != current.TotalTracks;
            case TagFieldId.DiscNumber:
                return ParseInt(wanted) != current.DiscNumber;
            case TagFieldId.TotalDiscs:
                return ParseInt(wanted) != current.TotalDiscs;
            case TagFieldId.Year:
                return ParseInt(wanted) != current.Year;
            case TagFieldId.Bpm:
                return ParseInt(wanted) != current.Bpm;
            case TagFieldId.Compilation:
                return TagText.ParseBool(wanted) != current.IsCompilation;
            case TagFieldId.Cover:
                return current.CoverImage is null;
            case TagFieldId.RemoveCover:
                return current.CoverImage is not null;
            default:
                return false;
        }
    }

    private static int ParseInt(string value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : 0;
}

/// <summary>文件当前的标签现值（用于差异判断）。</summary>
internal sealed class TagMetadata
{
    public string Title { get; set; } = string.Empty;

    public string Artist { get; set; } = string.Empty;

    public string AlbumArtist { get; set; } = string.Empty;

    public string Album { get; set; } = string.Empty;

    public string Genre { get; set; } = string.Empty;

    public string Composer { get; set; } = string.Empty;

    public string Comment { get; set; } = string.Empty;

    public string Grouping { get; set; } = string.Empty;

    public string Lyrics { get; set; } = string.Empty;

    public string SortTitle { get; set; } = string.Empty;

    public string SortArtist { get; set; } = string.Empty;

    public string SortAlbum { get; set; } = string.Empty;

    public int TrackNumber { get; set; }

    public int TotalTracks { get; set; }

    public int DiscNumber { get; set; }

    public int TotalDiscs { get; set; }

    public int Year { get; set; }

    public int Bpm { get; set; }

    public bool IsCompilation { get; set; }

    public byte[]? CoverImage { get; set; }

    /// <summary>ID3 宿主是否支持排序帧（仅 v2.4 支持 TSOT/TSOA/TSOP）。</summary>
    public bool SupportsSortFrames { get; set; } = true;

    /// <summary>从读取结果初始化（排序字段由各容器解析器补充）。</summary>
    public static TagMetadata From(MediaTagInfo info) => new()    {
        Title = info.Title,
        Artist = info.Artist,
        AlbumArtist = info.AlbumArtist,
        Album = info.Album,
        Genre = info.Genre,
        Composer = info.Composer,
        Comment = info.Comment,
        Grouping = info.Grouping,
        Lyrics = info.Lyrics,
        TrackNumber = info.TrackNumber,
        TotalTracks = info.TotalTracks,
        DiscNumber = info.DiscNumber,
        TotalDiscs = info.TotalDiscs,
        Year = info.Year,
        Bpm = info.Bpm,
        IsCompilation = info.IsCompilation,
        CoverImage = info.CoverImage,
    };

    /// <summary>补进一个字段的现值（读取器不返回的字段用此补全）。</summary>
    public void SetField(TagFieldId id, string? value)
    {
        string text = TagText.Clean(value);
        if (text.Length == 0)
        {
            return;
        }

        switch (id)
        {
            case TagFieldId.Title:
                Title = Pick(Title, text);
                break;
            case TagFieldId.Artist:
                Artist = Pick(Artist, text);
                break;
            case TagFieldId.AlbumArtist:
                AlbumArtist = Pick(AlbumArtist, text);
                break;
            case TagFieldId.Album:
                Album = Pick(Album, text);
                break;
            case TagFieldId.Genre:
                Genre = Pick(Genre, text);
                break;
            case TagFieldId.Composer:
                Composer = Pick(Composer, text);
                break;
            case TagFieldId.Comment:
                Comment = Pick(Comment, text);
                break;
            case TagFieldId.Grouping:
                Grouping = Pick(Grouping, text);
                break;
            case TagFieldId.Lyrics:
                Lyrics = Pick(Lyrics, text);
                break;
            case TagFieldId.SortTitle:
                SortTitle = Pick(SortTitle, text);
                break;
            case TagFieldId.SortArtist:
                SortArtist = Pick(SortArtist, text);
                break;
            case TagFieldId.SortAlbum:
                SortAlbum = Pick(SortAlbum, text);
                break;
            case TagFieldId.TrackNumber:
                TrackNumber = PickInt(TrackNumber, text);
                break;
            case TagFieldId.TotalTracks:
                TotalTracks = PickInt(TotalTracks, text);
                break;
            case TagFieldId.DiscNumber:
                DiscNumber = PickInt(DiscNumber, text);
                break;
            case TagFieldId.TotalDiscs:
                TotalDiscs = PickInt(TotalDiscs, text);
                break;
            case TagFieldId.Year:
                Year = Year == 0 ? TagText.ParseYear(text) : Year;
                break;
            case TagFieldId.Bpm:
                Bpm = PickInt(Bpm, text);
                break;
            case TagFieldId.Compilation:
                IsCompilation = TagText.ParseBool(text);
                break;
        }
    }

    private static string Pick(string current, string value) => current.Length == 0 ? value : current;

    private static int PickInt(int current, string value) => current == 0 ? TagText.ParseLeadingInt(value) : current;
}

/// <summary>ID3v1 序号反查。</summary>
internal static class Id3GenreLookup
{
    private static readonly Dictionary<string, byte> Index = BuildIndex();

    /// <summary>按流派名反查 ID3v1 序号；查不到返回 255（无）。</summary>
    public static byte IndexOf(string? name)
    {
        string key = Normalize(name);
        if (key.Length == 0)
        {
            return 255;
        }

        return Index.TryGetValue(key, out byte value) ? value : (byte)255;
    }

    private static Dictionary<string, byte> BuildIndex()
    {
        Dictionary<string, byte> map = new(StringComparer.Ordinal);
        for (int i = 0; i < 148; i++)
        {
            string name = Id3Genres.Name(i);
            if (name.Length == 0)
            {
                continue;
            }

            map.TryAdd(Normalize(name), (byte)i);
        }

        return map;
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        StringBuilder builder = new(value.Length);
        foreach (char c in value)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        return builder.ToString();
    }
}

/// <summary>ID3v2（2.3 / 2.4）标签重建。</summary>
internal static class Id3v2TagBuilder
{
    private const int FrameHeaderV23 = 10;
    private const int FrameHeaderV24 = 10;

    /// <summary>构造新的 ID3v2 标签字节；文本统一使用 UTF-16 带 BOM。</summary>
    public static byte[] Build(
        IReadOnlyList<Id3Frame> originalFrames,
        int originalVersion,
        int targetVersion,
        TagWriteRequest request,
        IReadOnlyList<TagFieldId> changes)
    {
        int version = targetVersion == 4 ? 4 : 3;
        List<Id3Frame> frames = [];
        List<string> removed = [];

        ApplyChanges(changes, request, version, frames, removed);

        ArrayBufferWriter<byte> writer = new(4096);
        int bodyStart = writer.WrittenCount + 10;
        writer.Advance(10);

        List<string> present = [];
        foreach (Id3Frame frame in originalFrames)
        {
            if (IsStale(frame.Id, version) || removed.Contains(frame.Id) || frames.Exists(f => f.Id == frame.Id))
            {
                continue;
            }

            WriteFrame(writer, frame.Id, frame.Body, version);
            present.Add(frame.Id);
        }

        foreach (Id3Frame frame in frames)
        {
            if (!present.Contains(frame.Id))
            {
                WriteFrame(writer, frame.Id, frame.Body, version);
                present.Add(frame.Id);
            }
        }

        int bodySize = writer.WrittenCount - bodyStart;
        writer.Write(PaddingBuffer);

        byte[] file = writer.WrittenSpan.ToArray();
        Span<byte> header = file.AsSpan(0, 10);
        header[0] = (byte)'I';
        header[1] = (byte)'D';
        header[2] = (byte)'3';
        header[3] = (byte)version;
        header[4] = 0;
        header[5] = 0;
        WriteSynchsafe(header[6..], bodySize);

        _ = originalVersion;
        return file;
    }

    private static readonly byte[] PaddingBuffer = new byte[MediaTagWriter.PaddingBytes];

    /// <summary>把目标变更编码为帧，并给出需要删除的帧 ID。</summary>
    private static void ApplyChanges(
        IReadOnlyList<TagFieldId> changes,
        TagWriteRequest request,
        int version,
        List<Id3Frame> frames,
        List<string> removed)
    {
        bool has = changes.Contains(TagFieldId.Title);
        if (has)
        {
            AddText(frames, removed, "TIT2", Id3WriteSupport.Value(request, TagFieldId.Title));
        }

        if (changes.Contains(TagFieldId.Artist))
        {
            AddText(frames, removed, "TPE1", Id3WriteSupport.Value(request, TagFieldId.Artist));
        }

        if (changes.Contains(TagFieldId.AlbumArtist))
        {
            AddText(frames, removed, "TPE2", Id3WriteSupport.Value(request, TagFieldId.AlbumArtist));
        }

        if (changes.Contains(TagFieldId.Album))
        {
            AddText(frames, removed, "TALB", Id3WriteSupport.Value(request, TagFieldId.Album));
        }

        if (changes.Contains(TagFieldId.Genre))
        {
            AddText(frames, removed, "TCON", Id3WriteSupport.Value(request, TagFieldId.Genre));
        }

        if (changes.Contains(TagFieldId.Composer))
        {
            AddText(frames, removed, "TCOM", Id3WriteSupport.Value(request, TagFieldId.Composer));
        }

        if (changes.Contains(TagFieldId.Grouping))
        {
            AddText(frames, removed, "TIT1", Id3WriteSupport.Value(request, TagFieldId.Grouping));
        }

        if (changes.Contains(TagFieldId.Comment))
        {
            AddComment(frames, removed, "COMM", Id3WriteSupport.Value(request, TagFieldId.Comment));
        }

        if (changes.Contains(TagFieldId.Lyrics))
        {
            AddComment(frames, removed, "USLT", Id3WriteSupport.Value(request, TagFieldId.Lyrics));
        }

        if (version >= 4)
        {
            if (changes.Contains(TagFieldId.SortTitle))
            {
                AddText(frames, removed, "TSOT", Id3WriteSupport.Value(request, TagFieldId.SortTitle));
            }

            if (changes.Contains(TagFieldId.SortArtist))
            {
                AddText(frames, removed, "TSOP", Id3WriteSupport.Value(request, TagFieldId.SortArtist));
            }

            if (changes.Contains(TagFieldId.SortAlbum))
            {
                AddText(frames, removed, "TSOA", Id3WriteSupport.Value(request, TagFieldId.SortAlbum));
            }
        }

        if (changes.Contains(TagFieldId.TrackNumber) || changes.Contains(TagFieldId.TotalTracks))
        {
            AddPair(frames, removed, "TRCK", request.TrackNumber, request.TotalTracks);
        }

        if (changes.Contains(TagFieldId.DiscNumber) || changes.Contains(TagFieldId.TotalDiscs))
        {
            AddPair(frames, removed, "TPOS", request.DiscNumber, request.TotalDiscs);
        }

        if (changes.Contains(TagFieldId.Year))
        {
            if (version >= 4)
            {
                removed.Add("TYER");
                removed.Add("TDAT");
                AddText(frames, removed, "TDRC", Id3WriteSupport.Value(request, TagFieldId.Year));
            }
            else
            {
                removed.Add("TDRC");
                removed.Add("TDRL");
                AddText(frames, removed, "TYER", Id3WriteSupport.Value(request, TagFieldId.Year));
            }
        }

        if (changes.Contains(TagFieldId.Bpm))
        {
            AddText(frames, removed, "TBPM", Id3WriteSupport.Value(request, TagFieldId.Bpm));
        }

        if (changes.Contains(TagFieldId.Compilation))
        {
            AddText(frames, removed, "TCMP", Id3WriteSupport.Value(request, TagFieldId.Compilation));
        }

        if (changes.Contains(TagFieldId.RemoveCover))
        {
            removed.Add("APIC");
            removed.Add("PIC");
        }
        else if (changes.Contains(TagFieldId.Cover) && request.CoverImage is { Length: > 0 } cover)
        {
            removed.Add("APIC");
            removed.Add("PIC");
            frames.Add(new Id3Frame("APIC", BuildApic(cover, request.CoverMimeType)));
        }
    }

    private static void AddPair(
        List<Id3Frame> frames,
        List<string> removed,
        string id,
        int? first,
        int? second)
    {
        int a = first ?? 0;
        int b = second ?? 0;
        if (a <= 0 && b <= 0)
        {
            removed.Add(id);
            return;
        }

        string text = b > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{a}/{b}")
            : a.ToString(CultureInfo.InvariantCulture);
        AddText(frames, removed, id, text);
    }

    private static void AddText(List<Id3Frame> frames, List<string> removed, string id, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            removed.Add(id);
            return;
        }

        frames.Add(new Id3Frame(id, BuildTextFrame(value)));
    }

    private static void AddComment(List<Id3Frame> frames, List<string> removed, string id, string? value)
    {
        if (value is null)
        {
            return;
        }

        if (value.Length == 0)
        {
            removed.Add(id);
            return;
        }

        frames.Add(new Id3Frame(id, BuildCommentFrame(value)));
    }

    /// <summary>文本帧正文：编码(1=UTF-16 BOM) + UTF-16LE 文本 + 结束符。</summary>
    private static byte[] BuildTextFrame(string value)
    {
        byte[] encoded = Encoding.Unicode.GetBytes(value);
        byte[] body = new byte[1 + 2 + encoded.Length + 2];
        body[0] = 0x01;
        body[1] = 0xFF;
        body[2] = 0xFE;
        encoded.CopyTo(body, 3);
        return body;
    }
    /// <summary>COMM / USLT 正文：编码 + 语言(XXX) + 空描述(NUL 结尾) + 文本(NUL 结尾)。</summary>
    private static byte[] BuildCommentFrame(string value)
    {
        byte[] encoded = Encoding.Unicode.GetBytes(value);
        byte[] body = new byte[1 + 3 + 2 + encoded.Length + 2];
        body[0] = 0x01;
        body[1] = (byte)'X';
        body[2] = (byte)'X';
        body[3] = (byte)'X';
        body[4] = 0xFF;
        body[5] = 0xFE;
        body[6] = 0x00;
        body[7] = 0x00;
        encoded.CopyTo(body, 8);
        return body;
    }

    /// <summary>APIC 正文：编码 + MIME(NUL) + 图片类型(3) + 空描述 + 图片数据。</summary>
    private static byte[] BuildApic(byte[] image, string? mime)
    {
        string resolved = TagText.Clean(mime);
        if (resolved.Length == 0)
        {
            resolved = TagText.SniffImageMime(image);
        }

        if (resolved.Length == 0)
        {
            resolved = "image/jpeg";
        }

        int size = 1 + resolved.Length + 1 + 1 + 2 + image.Length;
        byte[] body = new byte[size];
        int cursor = 0;
        body[cursor++] = 0x01;
        for (int i = 0; i < resolved.Length; i++)
        {
            body[cursor++] = (byte)resolved[i];
        }

        body[cursor++] = 0;
        body[cursor++] = 3;
        body[cursor++] = 0x00;
        body[cursor++] = 0x00;
        image.CopyTo(body, cursor);
        return body;
    }

    /// <summary>v2.3 没有排序帧；v2.4 的 TDRC 取代 TYER/TDAT；旧版帧在新版下作废。</summary>
    private static bool IsStale(string id, int version)
    {
        if (version >= 4)
        {
            return id is "TSOT" or "TSOA" or "TSOP" ? false : id is "TYER" or "TDAT" or "TIME" or "TRDA" or "TORY";
        }

        return id is "TSOT" or "TSOA" or "TSOP" or "TDRC" or "TDRL" or "TDEN" or "TDTG";
    }

    private static void WriteFrame(ArrayBufferWriter<byte> writer, string id, byte[] body, int version)
    {
        int headerSize = version >= 4 ? FrameHeaderV24 : FrameHeaderV23;
        Span<byte> header = stackalloc byte[10];
        header.Clear();
        byte[] ascii = TagByteOps.AsciiBytes(id);
        ascii.AsSpan(0, Math.Min(4, ascii.Length)).CopyTo(header);

        if (version >= 4)
        {
            WriteSynchsafe(header[4..], body.Length);
        }
        else
        {
            BinaryPrimitives.WriteUInt32BigEndian(header[4..], (uint)body.Length);
        }

        writer.Write(header[..headerSize]);
        writer.Write(body);
    }

    private static void WriteSynchsafe(Span<byte> destination, int value)
    {
        destination[0] = (byte)((value >> 21) & 0x7F);
        destination[1] = (byte)((value >> 14) & 0x7F);
        destination[2] = (byte)((value >> 7) & 0x7F);
        destination[3] = (byte)(value & 0x7F);
    }
}

/// <summary>一个已解析的 ID3v2 帧。</summary>
internal readonly record struct Id3Frame(string Id, byte[] Body);

/// <summary>ID3v2 写入能力。</summary>
internal static class Id3WriteSupport
{
    private static readonly string[] Names =
    [
        "标题", "艺术家", "专辑艺术家", "专辑", "流派", "作曲者", "备注", "分组", "歌词",
        "排序用标题", "排序用艺术家", "排序用专辑", "音轨号", "音轨总数", "碟号", "碟总数",
        "年份", "BPM", "合辑", "封面", "删除封面",
    ];

    /// <summary>可写入字段的中文名。</summary>
    public static IReadOnlyList<string> SupportedFieldNames() => Names;

    /// <summary>可写入字段集合；ID3v2.3 不支持排序帧。</summary>
    public static HashSet<TagFieldId> SupportedFields(bool supportsSortFrames)
    {
        HashSet<TagFieldId> fields =
        [
            TagFieldId.Title, TagFieldId.Artist, TagFieldId.AlbumArtist, TagFieldId.Album, TagFieldId.Genre,
            TagFieldId.Composer, TagFieldId.Comment, TagFieldId.Grouping, TagFieldId.Lyrics,
            TagFieldId.TrackNumber, TagFieldId.TotalTracks, TagFieldId.DiscNumber, TagFieldId.TotalDiscs,
            TagFieldId.Year, TagFieldId.Bpm, TagFieldId.Compilation, TagFieldId.Cover,
            TagFieldId.RemoveCover,
        ];
        if (supportsSortFrames)
        {
            fields.Add(TagFieldId.SortTitle);
            fields.Add(TagFieldId.SortArtist);
            fields.Add(TagFieldId.SortAlbum);
        }

        return fields;
    }

    /// <summary>可写入字段集合（默认按 v2.4 计）。</summary>
    public static HashSet<TagFieldId> SupportedFields() => SupportedFields(supportsSortFrames: true);

    /// <summary>取某字段的目标文本；返回 null 表示无目标值。</summary>
    public static string? Value(TagWriteRequest request, TagFieldId id)
        => TagFields.RequestedValues(request).TryGetValue(id, out string? value) ? value : null;
}

/// <summary>ID3v2.3 / 2.4 宿主文件的写入（MP3）。</summary>
internal static class Id3FileWriter
{
    /// <summary>把变更写入临时文件。</summary>
    public static async Task<bool> WriteAsync(
        string sourcePath,
        TagWriteRequest request,
        IReadOnlyList<TagFieldId> changes,
        TagMetadata existing,
        string tempPath,
        CancellationToken cancellationToken)
    {
        await using FileStream source = MediaTagWriter.OpenRead(sourcePath);
        (List<Id3Frame> frames, int version) = Id3v2TagReader.ReadFrames(source);
        int targetVersion = version == 4 ? 4 : 3;

        byte[] tag = Id3v2TagBuilder.Build(frames, version, targetVersion, request, changes);
        long end = Id3v2TagReader.ReadTagEnd(source);

        byte[]? trailer = null;
        long trailerStart = 0;
        long? id3v1Start = FindId3v1Start(source);
        if (id3v1Start is { } start)
        {
            trailerStart = start;
            trailer = BuildTrailer(source, start, request, changes, existing);
        }

        await using (FileStream output = MediaTagWriter.CreateWrite(tempPath))
        {
            await output.WriteAsync(tag, cancellationToken).ConfigureAwait(false);
            long audioEnd = trailerStart > 0 ? trailerStart : source.Length;
            await TagByteOps.CopyRangeAsync(source, end, audioEnd - end, output, cancellationToken).ConfigureAwait(false);
            if (trailer is not null)
            {
                await output.WriteAsync(trailer, cancellationToken).ConfigureAwait(false);
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>探测文件 ID3v2 的主版本号；不存在时按 3 处理。</summary>
    public static int DetectVersion(string sourcePath)
    {
        try
        {
            using FileStream source = MediaTagWriter.OpenRead(sourcePath);
            byte[] header = TagByteOps.ReadRange(source, 0, 10);
            if (header.Length == 10 && TagByteOps.IsAscii(header, "ID3"))
            {
                return header[3] == 4 ? 4 : 3;
            }
        }
        catch
        {
            // 探测失败按 3 处理。
        }

        return 3;
    }

    /// <summary>读取单个 ID3v2 排序帧的现值。</summary>
    public static string ReadSortField(string sourcePath, string frameId)
    {
        try
        {
            using FileStream source = MediaTagWriter.OpenRead(sourcePath);
            (List<Id3Frame> frames, _) = Id3v2TagReader.ReadFrames(source);
            foreach (Id3Frame frame in frames)
            {
                if (frame.Id == frameId)
                {
                    return TagText.DecodeTextFrame(frame.Body);
                }
            }
        }
        catch
        {
            // 读取失败视为无值。
        }

        return string.Empty;
    }

    /// <summary>定位文件末尾 ID3v1 标签的起始偏移。</summary>
    private static long? FindId3v1Start(FileStream source)
    {
        if (source.Length < 128)
        {
            return null;
        }

        byte[] buffer = new byte[128];
        source.Position = source.Length - 128;
        if (source.Read(buffer, 0, 128) != 128)
        {
            return null;
        }

        return buffer[0] == (byte)'T' && buffer[1] == (byte)'A' && buffer[2] == (byte)'G'
            ? source.Length - 128
            : null;
    }

    /// <summary>重建 ID3v1 区域（保持「原来没有就不新增」的规则）。</summary>
    private static byte[] BuildTrailer(
        FileStream source,
        long id3v1Start,
        TagWriteRequest request,
        IReadOnlyList<TagFieldId> changes,
        TagMetadata existing)
    {
        byte[] old = TagByteOps.ReadRange(source, id3v1Start, (int)(source.Length - id3v1Start));
        byte[] updated = (byte[])old.Clone();

        string? title = changes.Contains(TagFieldId.Title) ? Id3WriteSupport.Value(request, TagFieldId.Title) : null;
        string? artist = changes.Contains(TagFieldId.Artist) ? Id3WriteSupport.Value(request, TagFieldId.Artist) : null;
        string? album = changes.Contains(TagFieldId.Album) ? Id3WriteSupport.Value(request, TagFieldId.Album) : null;
        string? comment = changes.Contains(TagFieldId.Comment) ? Id3WriteSupport.Value(request, TagFieldId.Comment) : null;

        if (title is not null)
        {
            WriteFixed(updated, 3, 30, title);
        }

        if (artist is not null)
        {
            WriteFixed(updated, 33, 30, artist);
        }

        if (album is not null)
        {
            WriteFixed(updated, 63, 30, album);
        }

        if (changes.Contains(TagFieldId.Year))
        {
            string year = request.Year is { } value and > 0
                ? value.ToString("D4", CultureInfo.InvariantCulture)
                : string.Empty;
            WriteFixed(updated, 93, 4, year);
        }

        int commentLength = old[125] == 0 && old[126] != 0 ? 28 : 30;
        if (comment is not null)
        {
            WriteFixed(updated, 97, commentLength, comment);
        }

        if (changes.Contains(TagFieldId.TrackNumber))
        {
            int track = request.TrackNumber ?? TagText.ParseLeadingInt(Id3WriteSupport.Value(request, TagFieldId.TrackNumber));
            WriteTrack(updated, track);
        }

        if (changes.Contains(TagFieldId.Genre))
        {
            string? genre = Id3WriteSupport.Value(request, TagFieldId.Genre);
            updated[127] = genre is null ? (byte)255 : Id3GenreLookup.IndexOf(genre);
        }

        _ = existing;
        return updated;
    }

    private static void WriteTrack(byte[] tag, int track)
    {
        if (track is > 0 and < 256)
        {
            tag[125] = 0;
            tag[126] = (byte)track;
            return;
        }

        // 无音轨号：清空备注区的版本标记。
        tag[125] = 0;
        tag[126] = 0;
    }

    /// <summary>以 Latin-1 写入定长字段并补 0。</summary>
    private static void WriteFixed(byte[] destination, int offset, int length, string value)
    {
        Span<byte> field = destination.AsSpan(offset, length);
        field.Clear();
        for (int i = 0; i < length && i < value.Length; i++)
        {
            field[i] = value[i] < 0x100 ? (byte)value[i] : (byte)'?';
        }
    }
}

/// <summary>ID3v2 标签的随机访问解析（写入器专用）。</summary>
internal static class Id3v2TagReader
{
    /// <summary>读取已存在的 ID3v2 帧与版本号。</summary>
    public static (List<Id3Frame> Frames, int Version) ReadFrames(FileStream source)
    {
        Span<byte> header = stackalloc byte[10];
        source.Position = 0;
        if (source.Read(header) != 10 || !TagByteOps.IsAscii(header, "ID3"))
        {
            return ([], 3);
        }

        return ReadFramesFromHeader(source, header);
    }

    /// <summary>从独立缓冲区（容器内的 ID3 块）读取 ID3v2 帧与版本号。</summary>
    public static (List<Id3Frame> Frames, int Version) ReadFramesFromBuffer(ReadOnlySpan<byte> data)
    {
        if (data.Length < 10 || !TagByteOps.IsAscii(data, "ID3"))
        {
            return ([], 3);
        }

        return ParseFrames(data[..10], data.Length >= 10 ? data[10..] : default, data[5]);
    }

    private static (List<Id3Frame> Frames, int Version) ReadFramesFromHeader(FileStream source, ReadOnlySpan<byte> header)
    {
        int major = header[3];
        if (major is < 3 or > 4)
        {
            return ([], 3);
        }

        int bodySize = TagSynchsafe.Read(header[6..10]);
        if (bodySize <= 0)
        {
            return ([], major);
        }

        byte[] body = TagByteOps.ReadRange(source, 10, bodySize);
        return ParseFrames(header, body, header[5]);
    }

    /// <summary>解析标签正文为帧列表。</summary>
    private static (List<Id3Frame> Frames, int Version) ParseFrames(
        ReadOnlySpan<byte> header,
        ReadOnlySpan<byte> body,
        byte flags)
    {
        List<Id3Frame> frames = [];
        int major = header[3];
        if (major is < 3 or > 4)
        {
            return (frames, 3);
        }

        int cursor = 0;
        bool extended = (flags & 0x40) != 0;
        if (extended && body.Length >= 4)
        {
            int extendedSize = major == 3
                ? BinaryPrimitives.ReadInt32BigEndian(body[..4]) + 4
                : TagSynchsafe.Read(body[..4]);
            if (extendedSize is > 0 and < 1024 * 1024 && extendedSize <= body.Length)
            {
                cursor = extendedSize;
            }
        }

        while (cursor + 10 <= body.Length)
        {
            if (body[cursor] == 0)
            {
                break;
            }

            string id = TagBytes.Latin1Text(body.Slice(cursor, 4));
            int size = major == 3
                ? BinaryPrimitives.ReadInt32BigEndian(body.Slice(cursor + 4, 4))
                : TagSynchsafe.Read(body.Slice(cursor + 4, 4));
            if (size <= 0 || cursor + 10 + size > body.Length)
            {
                break;
            }

            int frameFlags = (body[cursor + 8] << 8) | body[cursor + 9];
            bool compressed = major == 3 ? (frameFlags & 0x0080) != 0 : (frameFlags & 0x0008) != 0;
            bool encrypted = major == 3 ? (frameFlags & 0x0040) != 0 : (frameFlags & 0x0004) != 0;
            bool grouped = major == 3 ? (frameFlags & 0x0020) != 0 : (frameFlags & 0x0040) != 0;

            int skip = grouped ? 1 : 0;
            if (major == 4 && (frameFlags & 0x0001) != 0)
            {
                skip += 4;
            }

            if (!compressed && !encrypted && size > skip && IsValidFrameId(id))
            {
                frames.Add(new Id3Frame(id, body.Slice(cursor + 10 + skip, size - skip).ToArray()));
            }

            cursor += 10 + size;
        }

        return (frames, major);
    }

    /// <summary>定位 ID3v2 标签之后音频数据的起始偏移。</summary>
    public static long ReadTagEnd(FileStream source)
    {
        Span<byte> header = stackalloc byte[10];
        source.Position = 0;
        if (source.Read(header) != 10 || !TagByteOps.IsAscii(header, "ID3"))
        {
            return 0;
        }

        int bodySize = TagSynchsafe.Read(header[6..10]);
        long end = 10 + Math.Max(0, bodySize);
        if ((header[5] & 0x10) != 0)
        {
            end += 10;
        }

        return Math.Min(end, source.Length);
    }

    /// <summary>从帧中补齐读取器无法返回的排序字段。</summary>
    public static void FillMetadata(IReadOnlyList<Id3Frame> frames, TagMetadata metadata)
    {
        foreach (Id3Frame frame in frames)
        {
            switch (frame.Id)
            {
                case "TSOT":
                    if (metadata.SortTitle.Length == 0)
                    {
                        metadata.SortTitle = DecodeText(frame.Body);
                    }

                    break;
                case "TSOP":
                    if (metadata.SortArtist.Length == 0)
                    {
                        metadata.SortArtist = DecodeText(frame.Body);
                    }

                    break;
                case "TSOA":
                    if (metadata.SortAlbum.Length == 0)
                    {
                        metadata.SortAlbum = DecodeText(frame.Body);
                    }

                    break;
            }
        }
    }

    private static string DecodeText(byte[] body) => TagText.DecodeTextFrame(body);

    private static bool IsValidFrameId(string id)
    {
        if (id.Length != 4)
        {
            return false;
        }

        foreach (char c in id)
        {
            if (!char.IsAsciiLetterOrDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>ID3v2 synchsafe 整数。</summary>
internal static class TagSynchsafe
{
    /// <summary>读取 4 字节 synchsafe 整数。</summary>
    public static int Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
        {
            return 0;
        }

        return ((data[0] & 0x7F) << 21) | ((data[1] & 0x7F) << 14) | ((data[2] & 0x7F) << 7) | (data[3] & 0x7F);
    }

    /// <summary>写入 4 字节 synchsafe 整数。</summary>
    public static void Write(Span<byte> destination, int value)
    {
        destination[0] = (byte)((value >> 21) & 0x7F);
        destination[1] = (byte)((value >> 14) & 0x7F);
        destination[2] = (byte)((value >> 7) & 0x7F);
        destination[3] = (byte)(value & 0x7F);
    }
}

/// <summary>FLAC 写入能力（同时用于 Ogg 的 Vorbis / Opus 注释）。</summary>
internal static class FlacWriteSupport
{
    private static readonly string[] Names =
    [
        "标题", "艺术家", "专辑艺术家", "专辑", "流派", "作曲者", "备注", "分组", "歌词",
        "排序用标题", "排序用艺术家", "排序用专辑", "音轨号", "音轨总数", "碟号", "碟总数",
        "年份", "BPM", "合辑", "封面", "删除封面",
    ];

    /// <summary>可写入字段的中文名。</summary>
    public static IReadOnlyList<string> SupportedFieldNames() => Names;

    /// <summary>可写入字段集合。</summary>
    public static HashSet<TagFieldId> SupportedFields() =>
    [
        TagFieldId.Title, TagFieldId.Artist, TagFieldId.AlbumArtist, TagFieldId.Album, TagFieldId.Genre,
        TagFieldId.Composer, TagFieldId.Comment, TagFieldId.Grouping, TagFieldId.Lyrics,
        TagFieldId.SortTitle, TagFieldId.SortArtist, TagFieldId.SortAlbum,
        TagFieldId.TrackNumber, TagFieldId.TotalTracks, TagFieldId.DiscNumber, TagFieldId.TotalDiscs,
        TagFieldId.Year, TagFieldId.Bpm, TagFieldId.Compilation, TagFieldId.Cover,
        TagFieldId.RemoveCover,
    ];

    /// <summary>字段 → Vorbis 注释键。</summary>
    public static string KeyFor(TagFieldId id) => id switch
    {
        TagFieldId.Title => "TITLE",
        TagFieldId.Artist => "ARTIST",
        TagFieldId.AlbumArtist => "ALBUMARTIST",
        TagFieldId.Album => "ALBUM",
        TagFieldId.Genre => "GENRE",
        TagFieldId.Composer => "COMPOSER",
        TagFieldId.Comment => "COMMENT",
        TagFieldId.Grouping => "GROUPING",
        TagFieldId.Lyrics => "LYRICS",
        TagFieldId.SortTitle => "SORTTITLE",
        TagFieldId.SortArtist => "ARTIST_SORT",
        TagFieldId.SortAlbum => "ALBUMART_SORT",
        TagFieldId.TrackNumber => "TRACKNUMBER",
        TagFieldId.TotalTracks => "TRACKTOTAL",
        TagFieldId.DiscNumber => "DISCNUMBER",
        TagFieldId.TotalDiscs => "DISCTOTAL",
        TagFieldId.Year => "DATE",
        TagFieldId.Bpm => "BPM",
        TagFieldId.Compilation => "COMPILATION",
        _ => string.Empty,
    };

    /// <summary>该字段在 Vorbis 注释中的所有可能键（读取时用于清理）。</summary>
    public static string[] AliasesFor(TagFieldId id) => id switch
    {
        TagFieldId.AlbumArtist => ["ALBUMARTIST", "ALBUM ARTIST", "ENSEMBLE"],
        TagFieldId.Comment => ["COMMENT", "DESCRIPTION"],
        TagFieldId.Lyrics => ["LYRICS", "UNSYNCEDLYRICS"],
        TagFieldId.TotalTracks => ["TRACKTOTAL", "TOTALTRACKS"],
        TagFieldId.TotalDiscs => ["DISCTOTAL", "TOTALDISCS"],
        TagFieldId.Year => ["DATE", "YEAR"],
        _ => [KeyFor(id)],
    };

    /// <summary>取某字段的目标值。</summary>
    public static string? Value(TagWriteRequest request, TagFieldId id)
        => TagFields.RequestedValues(request).TryGetValue(id, out string? value) ? value : null;

    /// <summary>把所有变更应用到注释列表（原地更新已有键、删除值为空的键、追加新键）。</summary>
    public static List<VorbisEntry> Apply(
        IReadOnlyList<VorbisEntry> original,
        TagWriteRequest request,
        IReadOnlyList<TagFieldId> changes)
    {
        List<VorbisEntry> entries = [.. original];
        foreach (TagFieldId id in changes)
        {
            if (id is TagFieldId.Cover or TagFieldId.RemoveCover)
            {
                continue;
            }

            string key = KeyFor(id);
            if (key.Length == 0)
            {
                continue;
            }

            string[] aliases = AliasesFor(id);
            entries.RemoveAll(entry => Matches(entry.Key, aliases));

            string? value = Value(request, id);
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            entries.Add(new VorbisEntry(key, value));
        }

        return entries;
    }

    /// <summary>键名是否属于给定别名集合（不区分大小写）。</summary>
    public static bool Matches(string key, string[] aliases)
    {
        foreach (string alias in aliases)
        {
            if (string.Equals(key, alias, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>把注释现值补进元数据（覆盖读取器不返回的排序与合辑字段）。</summary>
    public static void ApplyToMetadata(IReadOnlyList<VorbisEntry> entries, TagMetadata metadata)
    {
        foreach ((TagFieldId id, string alias) in MetadataAliases)
        {
            foreach (VorbisEntry entry in entries)
            {
                if (string.Equals(entry.Key, alias, StringComparison.OrdinalIgnoreCase))
                {
                    metadata.SetField(id, entry.Value);
                    break;
                }
            }
        }
    }

    private static readonly (TagFieldId Id, string Alias)[] MetadataAliases =
    [
        (TagFieldId.SortTitle, "SORTTITLE"),
        (TagFieldId.SortArtist, "ARTIST_SORT"),
        (TagFieldId.SortAlbum, "ALBUMART_SORT"),
        (TagFieldId.Compilation, "COMPILATION"),
        (TagFieldId.TrackNumber, "TRACKNUMBER"),
        (TagFieldId.TotalTracks, "TRACKTOTAL"),
        (TagFieldId.DiscNumber, "DISCNUMBER"),
        (TagFieldId.TotalDiscs, "DISCTOTAL"),
        (TagFieldId.Year, "DATE"),
        (TagFieldId.Bpm, "BPM"),
    ];

    /// <summary>把注释列表序列化为 VORBIS_COMMENT 正文（不含 FLAC 块头）。</summary>
    public static byte[] SerializeFrameBody(string? vendor, IReadOnlyList<VorbisEntry> entries)
    {
        ArrayBufferWriter<byte> writer = new(1024);
        byte[] vendorBytes = Encoding.UTF8.GetBytes(vendor ?? "iOpenPod");
        TagByteOps.WriteUInt32LittleEndian(writer, (uint)vendorBytes.Length);
        writer.Write(vendorBytes);
        TagByteOps.WriteUInt32LittleEndian(writer, (uint)entries.Count);
        foreach (VorbisEntry entry in entries)
        {
            byte[] line = Encoding.UTF8.GetBytes(entry.Key + "=" + entry.Value);
            TagByteOps.WriteUInt32LittleEndian(writer, (uint)line.Length);
            writer.Write(line);
        }

        return writer.WrittenSpan.ToArray();
    }

    /// <summary>解析 VORBIS_COMMENT 正文。</summary>
    public static List<VorbisEntry> ParseFrameBody(ReadOnlySpan<byte> data, out string vendor)
    {
        List<VorbisEntry> entries = [];
        vendor = string.Empty;
        int cursor = 0;
        if (!TryReadUInt32(data, ref cursor, out uint vendorLength) || vendorLength > (uint)(data.Length - cursor))
        {
            return entries;
        }

        vendor = Encoding.UTF8.GetString(data.Slice(cursor, (int)vendorLength));
        cursor += (int)vendorLength;
        if (!TryReadUInt32(data, ref cursor, out uint count))
        {
            return entries;
        }

        if (count > 65535)
        {
            count = 65535;
        }

        for (uint i = 0; i < count; i++)
        {
            if (!TryReadUInt32(data, ref cursor, out uint length) || length > (uint)(data.Length - cursor))
            {
                break;
            }

            string line = Encoding.UTF8.GetString(data.Slice(cursor, (int)length));
            cursor += (int)length;
            int equals = line.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            entries.Add(new VorbisEntry(line[..equals], line[(equals + 1)..]));
        }

        return entries;
    }

    private static bool TryReadUInt32(ReadOnlySpan<byte> data, ref int cursor, out uint value)
    {
        value = 0;
        if (cursor + 4 > data.Length)
        {
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(cursor, 4));
        cursor += 4;
        return true;
    }
}

/// <summary>一条 Vorbis 注释（键 = 值）。</summary>
internal readonly record struct VorbisEntry(string Key, string Value);

/// <summary>FLAC PICTURE 块构造与解析。</summary>
internal static class FlacPictureBlock
{
    /// <summary>构造 PICTURE 块正文。</summary>
    public static byte[] Build(byte[] image, string? mime, int pictureType)
    {
        string resolved = TagText.Clean(mime);
        if (resolved.Length == 0)
        {
            resolved = TagText.SniffImageMime(image);
        }

        if (resolved.Length == 0)
        {
            resolved = "image/jpeg";
        }

        ArrayBufferWriter<byte> writer = new(image.Length + 64);
        TagByteOps.WriteUInt32BigEndian(writer, (uint)pictureType);
        byte[] mimeBytes = Encoding.ASCII.GetBytes(resolved);
        TagByteOps.WriteUInt32BigEndian(writer, (uint)mimeBytes.Length);
        writer.Write(mimeBytes);
        TagByteOps.WriteUInt32BigEndian(writer, 0);
        TagByteOps.WriteUInt32BigEndian(writer, 0);
        TagByteOps.WriteUInt32BigEndian(writer, 0);
        TagByteOps.WriteUInt32BigEndian(writer, 0);
        TagByteOps.WriteUInt32BigEndian(writer, 0);
        TagByteOps.WriteUInt32BigEndian(writer, (uint)image.Length);
        writer.Write(image);
        return writer.WrittenSpan.ToArray();
    }

    /// <summary>读取 PICTURE 块的图片类型。</summary>
    public static int ReadType(ReadOnlySpan<byte> data)
        => data.Length >= 4 ? (int)BinaryPrimitives.ReadUInt32BigEndian(data[..4]) : -1;

    /// <summary>把 PICTURE 块正文编码为 base64（Ogg 的 METADATA_BLOCK_PICTURE）。</summary>
    public static string ToBase64(ReadOnlySpan<byte> picture)
        => Convert.ToBase64String(picture);
}

/// <summary>FLAC 文件写入。</summary>
internal static class FlacFileWriter
{
    /// <summary>把变更写入临时文件。</summary>
    public static async Task<bool> WriteAsync(
        string sourcePath,
        TagWriteRequest request,
        IReadOnlyList<TagFieldId> changes,
        string tempPath,
        CancellationToken cancellationToken)
    {
        await using FileStream source = MediaTagWriter.OpenRead(sourcePath);
        Span<byte> header = stackalloc byte[4];
        source.Position = 0;
        if (source.Read(header) != 4 || !TagByteOps.IsAscii(header, "fLaC"))
        {
            return false;
        }

        List<FlacBlock> blocks = [];
        byte[] streamInfo = [];
        long position = 4;
        bool last = false;
        int guard = 0;
        Span<byte> blockHeader = stackalloc byte[4];
        while (!last && guard++ < 4096 && position + 4 <= source.Length)
        {
            source.Position = position;
            if (source.Read(blockHeader) != 4)
            {
                break;
            }

            last = (blockHeader[0] & 0x80) != 0;
            int type = blockHeader[0] & 0x7F;
            int length = (blockHeader[1] << 16) | (blockHeader[2] << 8) | blockHeader[3];
            long dataPos = position + 4;
            if (dataPos + length > source.Length)
            {
                return false;
            }

            if (type == 0)
            {
                streamInfo = TagByteOps.ReadRange(source, dataPos, length);
            }
            else
            {
                blocks.Add(new FlacBlock(type, TagByteOps.ReadRange(source, dataPos, length)));
            }

            position = dataPos + length;
        }

        if (streamInfo.Length == 0)
        {
            return false;
        }

        long audioStart = position;
        List<FlacBlock> output = RebuildBlocks(blocks, request, changes);

        await using (FileStream destination = MediaTagWriter.CreateWrite(tempPath))
        {
            await destination.WriteAsync(header.ToArray(), cancellationToken).ConfigureAwait(false);
            WriteBlock(destination, 0, streamInfo, output.Count == 0);
            for (int i = 0; i < output.Count; i++)
            {
                WriteBlock(destination, output[i].Type, output[i].Data, i == output.Count - 1);
            }

            await TagByteOps.CopyRangeAsync(source, audioStart, source.Length - audioStart, destination, cancellationToken)
                .ConfigureAwait(false);
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>重建元数据块序列：STREAMINFO 固定第一，其余块保持相对顺序。</summary>
    private static List<FlacBlock> RebuildBlocks(
        IReadOnlyList<FlacBlock> blocks,
        TagWriteRequest request,
        IReadOnlyList<TagFieldId> changes)
    {
        bool touchComment = false;
        foreach (TagFieldId id in changes)
        {
            if (id is not (TagFieldId.Cover or TagFieldId.RemoveCover))
            {
                touchComment = true;
                break;
            }
        }
        List<FlacBlock> result = [];
        bool commentWritten = false;
        bool paddingKept = false;

        foreach (FlacBlock block in blocks)
        {
            if (block.Type == 1)
            {
                // 重新计算 padding；只保留一个、且不超过 64 KiB。
                if (paddingKept || touchComment)
                {
                    continue;
                }

                paddingKept = true;
                int size = Math.Clamp(block.Data.Length, 0, 64 * 1024);
                result.Add(new FlacBlock(1, new byte[size]));
                continue;
            }

            if (block.Type == 4)
            {
                if (commentWritten)
                {
                    continue;
                }

                commentWritten = true;
                List<VorbisEntry> entries = FlacWriteSupport.ParseFrameBody(block.Data, out string vendor);
                result.Add(new FlacBlock(4, FlacWriteSupport.SerializeFrameBody(
                    vendor, FlacWriteSupport.Apply(entries, request, changes))));
                continue;
            }

            if (block.Type == 6)
            {
                if (changes.Contains(TagFieldId.RemoveCover))
                {
                    if (FlacPictureBlock.ReadType(block.Data) == 3)
                    {
                        continue;
                    }
                }

                if (changes.Contains(TagFieldId.Cover) && FlacPictureBlock.ReadType(block.Data) == 3)
                {
                    continue;
                }

                result.Add(block);
                continue;
            }

            result.Add(block);
        }

        if (!commentWritten && touchComment)
        {
            List<VorbisEntry> entries = FlacWriteSupport.Apply([], request, changes);
            result.Insert(0, new FlacBlock(4, FlacWriteSupport.SerializeFrameBody(null, entries)));
        }

        if (changes.Contains(TagFieldId.Cover) && request.CoverImage is { Length: > 0 } cover)
        {
            result.Add(new FlacBlock(6, FlacPictureBlock.Build(cover, request.CoverMimeType, 3)));
        }

        if (!paddingKept)
        {
            result.Add(new FlacBlock(1, new byte[MediaTagWriter.PaddingBytes]));
        }

        return result;
    }

    private static void WriteBlock(Stream destination, int type, byte[] data, bool isLast)
    {
        byte[] header = new byte[4];
        header[0] = (byte)(type | (isLast ? 0x80 : 0));
        header[1] = (byte)((data.Length >> 16) & 0xFF);
        header[2] = (byte)((data.Length >> 8) & 0xFF);
        header[3] = (byte)(data.Length & 0xFF);
        destination.Write(header, 0, 4);
        destination.Write(data, 0, data.Length);
    }

    /// <summary>把 FLAC 的注释现值补进元数据。</summary>
    public static void FillMetadata(string sourcePath, TagMetadata metadata)
    {
        try
        {
            using FileStream source = MediaTagWriter.OpenRead(sourcePath);
            Span<byte> marker = stackalloc byte[4];
            source.Position = 0;
            if (source.Read(marker) != 4 || !TagByteOps.IsAscii(marker, "fLaC"))
            {
                return;
            }

            long position = 4;
            bool last = false;
            int guard = 0;
            Span<byte> head = stackalloc byte[4];
            while (!last && guard++ < 4096 && position + 4 <= source.Length)
            {
                source.Position = position;
                if (source.Read(head) != 4)
                {
                    return;
                }

                last = (head[0] & 0x80) != 0;
                int type = head[0] & 0x7F;
                int length = (head[1] << 16) | (head[2] << 8) | head[3];
                if (type == 4 && length is > 0 and <= 4 * 1024 * 1024)
                {
                    List<VorbisEntry> entries = FlacWriteSupport.ParseFrameBody(
                        TagByteOps.ReadRange(source, position + 4, length), out _);
                    FlacWriteSupport.ApplyToMetadata(entries, metadata);
                    return;
                }

                position += 4 + length;
            }
        }
        catch
        {
            // 读取失败视为无现值。
        }
    }
}

/// <summary>一个 FLAC 元数据块。</summary>
internal readonly record struct FlacBlock(int Type, byte[] Data);

/// <summary>Ogg 页写入（Vorbis / Opus）。</summary>
internal static class OggFileWriter
{
    /// <summary>把变更写入临时文件。</summary>
    public static async Task<bool> WriteAsync(
        string sourcePath,
        TagWriteRequest request,
        IReadOnlyList<TagFieldId> changes,
        string tempPath,
        CancellationToken cancellationToken)
    {
        await using FileStream source = MediaTagWriter.OpenRead(sourcePath);
        byte[] data = TagByteOps.ReadRange(source, 0, (int)Math.Min(source.Length, int.MaxValue));
        if (data.Length < 27 || !TagByteOps.IsAscii(data, "OggS"))
        {
            return false;
        }

        List<OggPacket> packets = OggPacketReader.Read(data);
        if (packets.Count < 2)
        {
            return false;
        }

        byte[] commentPacket = BuildCommentPacket(packets[1], request, changes, out bool isOpus);
        if (commentPacket.Length == 0)
        {
            return false;
        }

        _ = isOpus;
        List<OggPagePlan> pages = BuildPages(packets, commentPacket);
        if (pages.Count > 0)
        {
            // 码流首页不可能续包。
            pages[0] = pages[0] with { IsBos = true, Continuation = false };
            pages[^1] = pages[^1] with { IsEos = true };
        }

        await using (FileStream destination = MediaTagWriter.CreateWrite(tempPath))
        {
            foreach (OggPagePlan page in pages)
            {
                await destination.WriteAsync(
                    OggPageCodec.Encode(page, packets[0].Serial), cancellationToken).ConfigureAwait(false);
            }

            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>构造替换用的注释头数据包。</summary>
    private static byte[] BuildCommentPacket(
        OggPacket original,
        TagWriteRequest request,
        IReadOnlyList<TagFieldId> changes,
        out bool isOpus)
    {
        isOpus = false;
        ReadOnlySpan<byte> body = original.Data;
        byte[] prefix;
        if (body.Length > 7 && body[0] == 0x03 && TagByteOps.IsAscii(body[1..7], "vorbis"))
        {
            prefix = [0x03, (byte)'v', (byte)'o', (byte)'r', (byte)'b', (byte)'i', (byte)'s'];
            body = body[7..];
        }
        else if (body.Length > 8 && TagByteOps.IsAscii(body[..8], "OpusTags"))
        {
            isOpus = true;
            prefix = [(byte)'O', (byte)'p', (byte)'u', (byte)'s', (byte)'T', (byte)'a', (byte)'g', (byte)'s'];
            body = body[8..];
        }
        else
        {
            return [];
        }

        List<VorbisEntry> entries = FlacWriteSupport.ParseFrameBody(body, out string vendor);
        entries.RemoveAll(entry => string.Equals(entry.Key, "METADATA_BLOCK_PICTURE", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entry.Key, "COVERART", StringComparison.OrdinalIgnoreCase));
        List<VorbisEntry> updated = FlacWriteSupport.Apply(entries, request, changes);
        if (changes.Contains(TagFieldId.Cover) && request.CoverImage is { Length: > 0 } cover)
        {
            updated.Add(new VorbisEntry(
                "METADATA_BLOCK_PICTURE",
                FlacPictureBlock.ToBase64(FlacPictureBlock.Build(cover, request.CoverMimeType, 3))));
        }

        byte[] frameBody = FlacWriteSupport.SerializeFrameBody(vendor, updated);
        byte[] packet = new byte[prefix.Length + frameBody.Length];
        prefix.CopyTo(packet, 0);
        frameBody.CopyTo(packet, prefix.Length);
        return packet;
    }

    /// <summary>把 Ogg 注释头现值补进元数据。</summary>
    public static void FillMetadata(string sourcePath, TagMetadata metadata)
    {
        try
        {
            using FileStream source = MediaTagWriter.OpenRead(sourcePath);
            byte[] data = TagByteOps.ReadRange(source, 0, (int)Math.Min(source.Length, int.MaxValue));
            if (data.Length < 27 || !TagByteOps.IsAscii(data, "OggS"))
            {
                return;
            }

            List<OggPacket> packets = OggPacketReader.Read(data);
            if (packets.Count < 2)
            {
                return;
            }

            ReadOnlySpan<byte> body = packets[1].Data;
            if (body.Length > 7 && body[0] == 0x03 && TagByteOps.IsAscii(body[1..7], "vorbis"))
            {
                body = body[7..];
            }
            else if (body.Length > 8 && TagByteOps.IsAscii(body[..8], "OpusTags"))
            {
                body = body[8..];
            }
            else
            {
                return;
            }

            FlacWriteSupport.ApplyToMetadata(FlacWriteSupport.ParseFrameBody(body, out _), metadata);
        }
        catch
        {
            // 读取失败视为无现值。
        }
    }

    /// <summary>重新分页：保持包序、granule 与 BOS/EOS，必要时把一个包拆到多页。</summary>
    private static List<OggPagePlan> BuildPages(IReadOnlyList<OggPacket> packets, byte[] commentPacket)
    {
        List<OggPagePlan> pages = [];

        // 1) 把数据包按原页切分成组，并取回该页的 granule position。
        List<(List<byte[]> Payloads, long Granule)> groups = [];
        long currentOffset = long.MinValue;
        List<byte[]>? current = null;
        foreach (OggPacket packet in packets)
        {
            if (current is null || packet.OriginalOffset != currentOffset)
            {
                currentOffset = packet.OriginalOffset;
                current = [];
                groups.Add((current, unchecked((long)packet.PageGranule)));
            }

            current.Add(packet.Index == 1 ? commentPacket : packet.Data);
        }

        if (groups.Count == 0)
        {
            return pages;
        }

        // 2) 把逻辑页逐页展开成物理页；单页最多 255 个段。
        for (int g = 0; g < groups.Count; g++)
        {
            List<byte[]> queue = [.. groups[g].Payloads];
            long granule = groups[g].Granule;
            List<byte[]> batch = [];
            int segments = 0;
            bool emittedSplit = false;
            while (queue.Count > 0)
            {
                byte[] packet = queue[0];
                int neededSegments = (packet.Length / 255) + 1;
                if (segments + neededSegments <= 255)
                {
                    batch.Add(packet);
                    segments += neededSegments;
                    queue.RemoveAt(0);
                    continue;
                }

                int allowedSegments = 255 - segments;
                if (allowedSegments > 0)
                {
                    int take = Math.Min(allowedSegments * 255, packet.Length);
                    batch.Add(packet[..take]);
                    queue[0] = packet[take..];
                }

                emittedSplit = true;
                pages.Add(new OggPagePlan(batch, granule, Continuation: true));
                batch = [];
                segments = 0;
            }

            bool moreGroups = g < groups.Count - 1;
            if (batch.Count > 0)
            {
                pages.Add(new OggPagePlan(batch, granule, Continuation: moreGroups));
            }
            else if (emittedSplit && pages.Count > 0)
            {
                // 上一页只是「为放不下的包」而强制拆页，组内并未真正跨页续包。
                OggPagePlan last = pages[^1];
                pages[^1] = last with { Continuation = moreGroups };
            }
        }

        return pages;
    }
}

/// <summary>一个 Ogg 数据包及其原页信息。</summary>
internal readonly record struct OggPacket(
    int Index,
    byte[] Data,
    uint Serial,
    uint PageGranule,
    long OriginalOffset,
    bool IsBos,
    bool IsEos);

/// <summary>待输出的 Ogg 页。</summary>
internal sealed record OggPagePlan(List<byte[]> Packets, long Granule, bool Continuation)
{
    /// <summary>是否为整个码流的第一个页（BOS）。</summary>
    public bool IsBos { get; init; }

    /// <summary>是否为整个码流的最后一个页（EOS）。</summary>
    public bool IsEos { get; init; }
}

/// <summary>Ogg 页序列解析。</summary>
internal static class OggPacketReader
{
    /// <summary>解析整个文件的包序列。</summary>
    public static List<OggPacket> Read(byte[] data)
    {
        List<OggPacket> packets = [];
        List<byte> current = [];
        long position = 0;
        long pageOffset = 0;
        uint serial = 0;
        uint pageGranule = 0;
        bool bos = false;
        bool eos = false;
        bool spanning = false;
        int index = 0;

        while (position + 27 <= data.Length && index < 100000)
        {
            ReadOnlySpan<byte> header = data.AsSpan((int)position, 27);
            if (!TagByteOps.IsAscii(header, "OggS") || header[4] != 0)
            {
                break;
            }

            serial = BinaryPrimitives.ReadUInt32LittleEndian(header[14..18]);
            pageGranule = unchecked((uint)BinaryPrimitives.ReadInt64LittleEndian(header[6..14]));
            int segments = header[26];
            long tableStart = position + 27;
            long payloadStart = tableStart + segments;
            if (payloadStart > data.Length)
            {
                break;
            }

            int payloadLength = 0;
            for (int i = 0; i < segments; i++)
            {
                payloadLength += data[(int)tableStart + i];
            }

            long next = payloadStart + payloadLength;
            if (next > data.Length)
            {
                break;
            }

            pageOffset = position;
            int offset = 0;
            for (int i = 0; i < segments; i++)
            {
                int take = data[(int)tableStart + i];
                current.AddRange(data.AsSpan((int)payloadStart + offset, take));
                offset += take;
                if (take < 255)
                {
                    packets.Add(new OggPacket(
                        index++,
                        [.. current],
                        serial,
                        pageGranule,
                        pageOffset,
                        (header[5] & 0x02) != 0,
                        (header[5] & 0x04) != 0));
                    current.Clear();
                }
            }

            spanning = segments > 0 && data[(int)tableStart + segments - 1] == 255;
            bos = (header[5] & 0x02) != 0;
            eos = (header[5] & 0x04) != 0;
            position = next;
        }

        _ = spanning;
        _ = bos;
        _ = eos;
        return packets;
    }
}

/// <summary>Ogg 页编解码与 CRC-32。</summary>
internal static class OggPageCodec
{
    private static readonly uint[] CrcTable = BuildTable();

    /// <summary>把一组包编码为一页。</summary>
    public static byte[] Encode(OggPagePlan page, uint serial)
    {
        List<byte> segments = [];
        foreach (byte[] packet in page.Packets)
        {
            int remaining = packet.Length;
            while (remaining >= 255)
            {
                segments.Add(255);
                remaining -= 255;
            }

            segments.Add((byte)remaining);
        }

        int payloadLength = 0;
        foreach (byte[] packet in page.Packets)
        {
            payloadLength += packet.Length;
        }

        byte[] output = new byte[27 + segments.Count + payloadLength];
        Span<byte> span = output;
        "OggS"u8.CopyTo(span);
        span[4] = 0;
        span[5] = (byte)((page.Continuation ? 0x01 : 0x00) | (page.IsBos ? 0x02 : 0x00) | (page.IsEos ? 0x04 : 0x00));
        BinaryPrimitives.WriteInt64LittleEndian(span[6..14], page.Granule);
        BinaryPrimitives.WriteUInt32LittleEndian(span[14..18], serial);
        BinaryPrimitives.WriteUInt32LittleEndian(span[18..22], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(span[22..26], 0);
        span[26] = (byte)segments.Count;
        for (int i = 0; i < segments.Count; i++)
        {
            span[27 + i] = segments[i];
        }

        int cursor = 27 + segments.Count;
        foreach (byte[] packet in page.Packets)
        {
            packet.CopyTo(span[cursor..]);
            cursor += packet.Length;
        }

        uint crc = Compute(span);
        BinaryPrimitives.WriteUInt32LittleEndian(span[22..26], crc);
        return output;
    }

    /// <summary>Ogg CRC-32（多项式 0x04C11DB7，初值 0，无反射，无最终异或）。</summary>
    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0;
        foreach (byte value in data)
        {
            crc = (crc << 8) ^ CrcTable[(byte)((crc >> 24) ^ value)];
        }

        return crc;
    }

    private static uint[] BuildTable()
    {
        uint[] table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint value = i << 24;
            for (int bit = 0; bit < 8; bit++)
            {
                value = (value & 0x80000000) != 0 ? (value << 1) ^ 0x04C11DB7 : value << 1;
            }

            table[i] = value;
        }

        return table;
    }
}

/// <summary>WAV（RIFF）块扫描辅助。</summary>
internal static class RiffChunkScan
{
    /// <summary>判断文件是否包含给定 ID 的块。</summary>
    public static bool HasChunk(string path, string formId, bool isAiff, params string[] ids)
    {
        using FileStream stream = MediaTagWriter.OpenRead(path);
        Span<byte> header = stackalloc byte[12];
        stream.Position = 0;
        if (stream.Read(header) != 12 || !TagByteOps.IsAscii(header, formId))
        {
            return false;
        }

        long position = 12;
        int guard = 0;
        Span<byte> chunk = stackalloc byte[8];
        while (position + 8 <= stream.Length && guard++ < 4096)
        {
            stream.Position = position;
            if (stream.Read(chunk) != 8)
            {
                break;
            }

            string id = TagBytes.Latin1Text(chunk[..4]);
            long declared = isAiff
                ? BinaryPrimitives.ReadUInt32BigEndian(chunk[4..8])
                : BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..8]);
            long length = Math.Min(declared, stream.Length - position - 8);
            foreach (string wanted in ids)
            {
                if (string.Equals(id, wanted, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            long next = position + 8 + length + (length & 1);
            if (next <= position)
            {
                break;
            }

            position = next;
        }

        return false;
    }
}

/// <summary>WAV 写入分派。</summary>
internal static class WavFileWriter
{
    /// <summary>按是否存在 <c>id3 </c> 块选择写入器。</summary>
    public static Task<bool> WriteAsync(
        string sourcePath,
        TagWriteRequest request,
        IReadOnlyList<TagFieldId> changes,
        string tempPath,
        CancellationToken cancellationToken)
        => TagContainerDetect.WavUsesInfo(sourcePath)
            ? WavInfoWriter.WriteAsync(sourcePath, request, changes, tempPath, cancellationToken)
            : Id3ChunkWriter.WriteAsync(sourcePath, request, changes, tempPath, cancellationToken, isAiff: false);
}

/// <summary>WAV 的 LIST/INFO 写入。</summary>
internal static class WavInfoWriter
{
    private static readonly string[] Names = ["标题", "艺术家", "专辑", "流派", "备注", "年份", "音轨号"];

    private static readonly TagFieldId[] Supported =
    [
        TagFieldId.Title, TagFieldId.Artist, TagFieldId.Album, TagFieldId.Genre,
        TagFieldId.Comment, TagFieldId.Year, TagFieldId.TrackNumber,
    ];

    /// <summary>可写入字段的中文名。</summary>
    public static IReadOnlyList<string> SupportedFieldNames() => Names;

    /// <summary>可写入字段集合。</summary>
    public static HashSet<TagFieldId> SupportedFields() => [.. Supported];

    /// <summary>把变更写入临时文件。</summary>
    public static async Task<bool> WriteAsync(
        string sourcePath,
        TagWriteRequest request,
        IReadOnlyList<TagFieldId> changes,
        string tempPath,
        CancellationToken cancellationToken)
    {
        await using FileStream source = MediaTagWriter.OpenRead(sourcePath);
        Span<byte> header = stackalloc byte[12];
        source.Position = 0;
        if (source.Read(header) != 12 || !TagByteOps.IsAscii(header, "RIFF") || !TagByteOps.IsAscii(header[8..12], "WAVE"))
        {
            return false;
        }

        List<RiffChunk> chunks = ReadChunks(source, isAiff: false, out long end);
        RiffChunk? info = null;
        RiffChunk? id3 = null;
        foreach (RiffChunk chunk in chunks)
        {
            switch (chunk.Id)
            {
                case "LIST" when chunk.TypeId == "INFO":
                    info ??= chunk;
                    break;
                case "id3 ":
                case "ID3 ":
                    id3 ??= chunk;
                    break;
            }
        }

        byte[]? infoBytes = changes.Count == 0 ? null : BuildInfoChunk(source, info, request, changes);
        if (changes.Count == 0)
        {
            infoBytes = info is { } existing ? RawBytes(source, existing) : null;
        }

        await using (FileStream destination = MediaTagWriter.CreateWrite(tempPath))
        {
            byte[] riffHeader = new byte[12];
            header.CopyTo(riffHeader);
            await destination.WriteAsync(riffHeader, cancellationToken).ConfigureAwait(false);

            long body = 4;
            foreach (RiffChunk chunk in chunks)
            {
                if (chunk.Id == "LIST" && chunk.TypeId == "INFO")
                {
                    continue;
                }

                if (chunk.Id is "id3 " or "ID3 ")
                {
                    continue;
                }

                byte[] emitted = EmitChunk(source, chunk, isAiff: false);
                await destination.WriteAsync(emitted, cancellationToken).ConfigureAwait(false);
                body += emitted.Length;
            }

            if (infoBytes is { Length: > 0 })
            {
                await destination.WriteAsync(infoBytes, cancellationToken).ConfigureAwait(false);
                body += infoBytes.Length;
            }

            if (id3 is { } id3Chunk)
            {
                byte[] emitted = EmitChunk(source, id3Chunk, isAiff: false);
                await destination.WriteAsync(emitted, cancellationToken).ConfigureAwait(false);
                body += emitted.Length;
            }

            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            WriteRiffSize(destination, body);
        }

        _ = end;
        return true;
    }

    /// <summary>构造 LIST/INFO 块（含 8 字节块头）。</summary>
    private static byte[] BuildInfoChunk(
        FileStream source,
        RiffChunk? existing,
        TagWriteRequest request,
        IReadOnlyList<TagFieldId> changes)
    {
        Dictionary<string, string> values = [];
        if (existing is { } chunk)
        {
            foreach ((string id, string text) in ReadInfoEntries(source, chunk))
            {
                values[id] = text;
            }
        }

        foreach (TagFieldId id in changes)
        {
            switch (id)
            {
                case TagFieldId.Title:
                    Set(values, "INAM", request.Title);
                    break;
                case TagFieldId.Artist:
                    Set(values, "IART", request.Artist);
                    break;
                case TagFieldId.Album:
                    Set(values, "IPRD", request.Album);
                    break;
                case TagFieldId.Genre:
                    Set(values, "IGNR", request.Genre);
                    break;
                case TagFieldId.Comment:
                    Set(values, "ICMT", request.Comment);
                    break;
                case TagFieldId.Year:
                    Set(
                        values,
                        "ICRD",
                        request.Year is { } year and > 0
                            ? year.ToString("D4", CultureInfo.InvariantCulture)
                            : null);
                    break;
                case TagFieldId.TrackNumber:
                    Set(
                        values,
                        "ITRK",
                        request.TrackNumber is { } track and > 0
                            ? track.ToString(CultureInfo.InvariantCulture)
                            : null);
                    break;
            }
        }

        ArrayBufferWriter<byte> payload = new(256);
        TagByteOps.WriteAscii(payload, "INFO");
        foreach ((string id, string text) in values)
        {
            byte[] encoded = Encoding.Latin1.GetBytes(text);
            TagByteOps.WriteAscii(payload, id);
            TagByteOps.WriteUInt32LittleEndian(payload, (uint)(encoded.Length + 1));
            payload.Write(encoded);
            TagByteOps.WriteByte(payload, 0);
        }

        byte[] body = payload.WrittenSpan.ToArray();
        ArrayBufferWriter<byte> writer = new(body.Length + 8);
        TagByteOps.WriteAscii(writer, "LIST");
        TagByteOps.WriteUInt32LittleEndian(writer, (uint)body.Length);
        writer.Write(body);
        return writer.WrittenSpan.ToArray();
    }

    private static void Set(Dictionary<string, string> values, string id, string? text)
    {
        string value = TagText.Clean(text);
        if (value.Length == 0)
        {
            values.Remove(id);
            return;
        }

        values[id] = value;
    }

    /// <summary>把 LIST/INFO 的现值补进元数据。</summary>
    public static void FillMetadata(string sourcePath, TagMetadata metadata)
    {
        try
        {
            using FileStream source = MediaTagWriter.OpenRead(sourcePath);
            List<RiffChunk> chunks = ReadChunks(source, isAiff: false, out _);
            foreach (RiffChunk chunk in chunks)
            {
                if (chunk.Id != "LIST" || chunk.TypeId != "INFO")
                {
                    continue;
                }

                foreach ((string id, string text) in ReadInfoEntries(source, chunk))
                {
                    TagFieldId? field = InfoFieldOf(id);
                    if (field is { } id2)
                    {
                        metadata.SetField(id2, text);
                    }
                }

                return;
            }
        }
        catch
        {
            // 读取失败视为无现值。
        }
    }

    private static TagFieldId? InfoFieldOf(string id) => id switch
    {
        "INAM" => TagFieldId.Title,
        "IART" => TagFieldId.Artist,
        "IPRD" => TagFieldId.Album,
        "IGNR" => TagFieldId.Genre,
        "ICMT" => TagFieldId.Comment,
        "ICRD" => TagFieldId.Year,
        "ITRK" or "IPRT" => TagFieldId.TrackNumber,
        _ => null,
    };

    private static IEnumerable<(string Id, string Text)> ReadInfoEntries(FileStream source, RiffChunk chunk)
    {
        List<(string, string)> entries = [];
        byte[] data = TagByteOps.ReadRange(source, chunk.DataPos, chunk.Length);
        int cursor = 4;
        while (cursor + 8 <= data.Length)
        {
            string id = TagBytes.Latin1Text(data.AsSpan(cursor, 4));
            int size = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(cursor + 4, 4));
            if (size < 0 || cursor + 8 + size > data.Length)
            {
                break;
            }

            string text = TagText.DecodeUnknown(data.AsSpan(cursor + 8, size));
            entries.Add((id, text));

            // 每个子块同样按偶数字节对齐，与写入时保持一致。
            cursor += 8 + size + (size & 1);
        }

        return entries;
    }

    private static void WriteRiffSize(FileStream destination, long body)
    {
        long position = destination.Position;
        destination.Position = 4;
        Span<byte> size = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)Math.Clamp(body, 0, uint.MaxValue));
        destination.Write(size);
        destination.Position = position;
    }

    /// <summary>按块声明长度原样输出（含补齐字节）；AIFF 使用大端长度字段。</summary>
    internal static byte[] EmitChunk(FileStream source, RiffChunk chunk, bool isAiff)
    {
        int total = 8 + chunk.Length + ((chunk.Length & 1) != 0 ? 1 : 0);
        byte[] output = new byte[total];
        for (int i = 0; i < 4; i++)
        {
            output[i] = (byte)chunk.Id[i];
        }

        if (isAiff)
        {
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(4, 4), (uint)chunk.Length);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(4, 4), (uint)chunk.Length);
        }

        source.Position = chunk.DataPos;
        ReadFully(source, output, 8, chunk.Length);
        return output;
    }

    /// <summary>原样读取块的全部字节（含块头）。</summary>
    private static byte[] RawBytes(FileStream source, RiffChunk chunk)
    {
        int total = 8 + chunk.Length + ((chunk.Length & 1) != 0 ? 1 : 0);
        return TagByteOps.ReadRange(source, chunk.Position, total);
    }

    internal static void ReadFully(FileStream source, byte[] buffer, int offset, int count)
    {
        int total = 0;
        while (total < count)
        {
            int read = source.Read(buffer, offset + total, count - total);
            if (read <= 0)
            {
                break;
            }

            total += read;
        }
    }

    /// <summary>枚举 RIFF 直接子块。</summary>
    internal static List<RiffChunk> ReadChunks(FileStream source, bool isAiff, out long end)
    {
        List<RiffChunk> chunks = [];
        long position = 12;
        end = 12;
        int guard = 0;
        Span<byte> head = stackalloc byte[8];
        while (position + 8 <= source.Length && guard++ < 8192)
        {
            source.Position = position;
            if (source.Read(head) != 8)
            {
                break;
            }

            string id = TagBytes.Latin1Text(head[..4]);
            long declared = isAiff
                ? BinaryPrimitives.ReadUInt32BigEndian(head[4..8])
                : BinaryPrimitives.ReadUInt32LittleEndian(head[4..8]);
            long length = Math.Min(declared, source.Length - position - 8);
            string typeId = string.Empty;
            if (id == "LIST" && length >= 4)
            {
                typeId = TagBytes.Latin1Text(TagByteOps.ReadRange(source, position + 8, 4));
            }

            chunks.Add(new RiffChunk(id, position, position + 8, (int)length, typeId));
            long next = position + 8 + length + (length & 1);
            if (next <= position)
            {
                break;
            }

            position = next;
            end = position;
        }

        return chunks;
    }
}

/// <summary>一个 RIFF / AIFF 块。</summary>
internal readonly record struct RiffChunk(string Id, long Position, long DataPos, int Length, string TypeId);

/// <summary>带 ID3 块的容器写入（WAV <c>id3 </c> / AIFF <c>ID3 </c>）：把新标签追加为独立块。</summary>
internal static class Id3ChunkWriter
{
    /// <summary>把新的 ID3 块追加到容器末尾并修正外层长度。</summary>
    public static async Task<bool> WriteAsync(
        string sourcePath,
        TagWriteRequest request,
        IReadOnlyList<TagFieldId> changes,
        string tempPath,
        CancellationToken cancellationToken,
        bool isAiff)
    {
        await using FileStream source = MediaTagWriter.OpenRead(sourcePath);
        Span<byte> header = stackalloc byte[12];
        source.Position = 0;
        if (source.Read(header) != 12)
        {
            return false;
        }

        bool valid = isAiff
            ? TagByteOps.IsAscii(header, "FORM")
            : TagByteOps.IsAscii(header, "RIFF") && TagByteOps.IsAscii(header[8..12], "WAVE");
        if (!valid)
        {
            return false;
        }

        List<RiffChunk> chunks = WavInfoWriter.ReadChunks(source, isAiff, out _);

        // 读取首个 ID3 块中的现有帧（可能为空）。
        List<Id3Frame> frames = [];
        int version = 3;
        foreach (RiffChunk chunk in chunks)
        {
            if (chunk.Id is "ID3 " or "id3 ")
            {
                byte[] payload = TagByteOps.ReadRange(source, chunk.DataPos, chunk.Length);
                int offset = TagByteOps.IsAscii(payload, "ID3") ? 0 : (payload.Length > 4 && TagByteOps.IsAscii(payload.AsSpan(4), "ID3") ? 4 : -1);
                if (offset >= 0)
                {
                    (frames, version) = Id3v2TagReader.ReadFramesFromBuffer(payload.AsSpan(offset));
                    if (version != 4)
                    {
                        version = 3;
                    }
                }

                break;
            }
        }

        int targetVersion = version == 4 ? 4 : 3;
        byte[] tag = Id3v2TagBuilder.Build(frames, version, targetVersion, request, changes);

        string chunkId = isAiff ? "ID3 " : "id3 ";
        int padding = tag.Length & 1;
        await using (FileStream destination = MediaTagWriter.CreateWrite(tempPath))
        {
            byte[] containerHeader = new byte[12];
            header.CopyTo(containerHeader);
            await destination.WriteAsync(containerHeader, cancellationToken).ConfigureAwait(false);

            long body = 4;
            foreach (RiffChunk chunk in chunks)
            {
                if (chunk.Id is "ID3 " or "id3 ")
                {
                    continue;
                }

                byte[] emitted = WavInfoWriter.EmitChunk(source, chunk, isAiff);
                await destination.WriteAsync(emitted, cancellationToken).ConfigureAwait(false);
                body += emitted.Length;
            }

            byte[] block = new byte[8 + tag.Length + padding];
            for (int i = 0; i < 4; i++)
            {
                block[i] = (byte)chunkId[i];
            }

            BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(4, 4), (uint)tag.Length);
            tag.CopyTo(block, 8);
            await destination.WriteAsync(block, cancellationToken).ConfigureAwait(false);
            body += block.Length;

            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            destination.Position = 4;
            Span<byte> size = stackalloc byte[4];
            if (isAiff)
            {
                BinaryPrimitives.WriteUInt32BigEndian(size, (uint)Math.Clamp(body, 0, uint.MaxValue));
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)Math.Clamp(body, 0, uint.MaxValue));
            }

            destination.Write(size);
        }

        return true;
    }

    /// <summary>读取容器内 ID3 块中的排序帧现值。</summary>
    public static void FillMetadata(string sourcePath, TagMetadata metadata, bool isAiff)
    {
        try
        {
            using FileStream source = MediaTagWriter.OpenRead(sourcePath);
            foreach (RiffChunk chunk in WavInfoWriter.ReadChunks(source, isAiff, out _))
            {
                if (chunk.Id is not ("ID3 " or "id3 "))
                {
                    continue;
                }

                byte[] payload = TagByteOps.ReadRange(source, chunk.DataPos, chunk.Length);
                ReadOnlySpan<byte> tag = payload;
                if (!TagByteOps.IsAscii(tag, "ID3") && tag.Length > 4 && TagByteOps.IsAscii(tag[4..], "ID3"))
                {
                    tag = tag[4..];
                }

                (List<Id3Frame> frames, _) = Id3v2TagReader.ReadFramesFromBuffer(tag);
                foreach (Id3Frame frame in frames)
                {
                    switch (frame.Id)
                    {
                        case "TSOT":
                            metadata.SetField(TagFieldId.SortTitle, TagText.DecodeTextFrame(frame.Body));
                            break;
                        case "TSOP":
                            metadata.SetField(TagFieldId.SortArtist, TagText.DecodeTextFrame(frame.Body));
                            break;
                        case "TSOA":
                            metadata.SetField(TagFieldId.SortAlbum, TagText.DecodeTextFrame(frame.Body));
                            break;
                    }
                }

                return;
            }
        }
        catch
        {
            // 读取失败视为无现值。
        }
    }
}

/// <summary>AIFF / AIFC 的 NAME / AUTH / ANNO 写入。</summary>
internal static class AiffTextWriter
{
    private static readonly string[] Names = ["标题", "艺术家", "备注"];

    private static readonly TagFieldId[] Supported = [TagFieldId.Title, TagFieldId.Artist, TagFieldId.Comment];

    /// <summary>可写入字段的中文名。</summary>
    public static IReadOnlyList<string> SupportedFieldNames() => Names;

    /// <summary>可写入字段集合。</summary>
    public static HashSet<TagFieldId> SupportedFields() => [.. Supported];

    /// <summary>把变更写入临时文件。</summary>
    public static async Task<bool> WriteAsync(
        string sourcePath,
        TagWriteRequest request,
        IReadOnlyList<TagFieldId> changes,
        string tempPath,
        CancellationToken cancellationToken)
    {
        await using FileStream source = MediaTagWriter.OpenRead(sourcePath);
        Span<byte> header = stackalloc byte[12];
        source.Position = 0;
        if (source.Read(header) != 12 || !TagByteOps.IsAscii(header, "FORM"))
        {
            return false;
        }

        List<RiffChunk> chunks = WavInfoWriter.ReadChunks(source, isAiff: true, out _);
        Dictionary<string, byte[]> replacements = [];
        foreach (TagFieldId id in changes)
        {
            string? target = id switch
            {
                TagFieldId.Title => request.Title,
                TagFieldId.Artist => request.Artist,
                TagFieldId.Comment => request.Comment,
                _ => null,
            };
            string chunkId = id switch
            {
                TagFieldId.Title => "NAME",
                TagFieldId.Artist => "AUTH",
                _ => "ANNO",
            };
            string text = TagText.Clean(target);
            if (text.Length == 0)
            {
                replacements[chunkId] = [];
                continue;
            }

            replacements[chunkId] = Encoding.Latin1.GetBytes(text);
        }

        await using (FileStream destination = MediaTagWriter.CreateWrite(tempPath))
        {
            byte[] containerHeader = new byte[12];
            header.CopyTo(containerHeader);
            await destination.WriteAsync(containerHeader, cancellationToken).ConfigureAwait(false);

            long body = 4;
            foreach (RiffChunk chunk in chunks)
            {
                if (replacements.TryGetValue(chunk.Id, out byte[]? replacement))
                {
                    replacements.Remove(chunk.Id);
                    if (replacement.Length > 0)
                    {
                        byte[] block = BuildChunk(chunk.Id, replacement);
                        await destination.WriteAsync(block, cancellationToken).ConfigureAwait(false);
                        body += block.Length;
                    }

                    continue;
                }

                byte[] emitted = WavInfoWriter.EmitChunk(source, chunk, isAiff: true);
                await destination.WriteAsync(emitted, cancellationToken).ConfigureAwait(false);
                body += emitted.Length;
            }

            foreach ((string id, byte[] value) in replacements)
            {
                if (value.Length == 0)
                {
                    continue;
                }

                byte[] block = BuildChunk(id, value);
                await destination.WriteAsync(block, cancellationToken).ConfigureAwait(false);
                body += block.Length;
            }

            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            destination.Position = 4;
            Span<byte> size = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(size, (uint)Math.Clamp(body, 0, uint.MaxValue));
            destination.Write(size);
        }

        return true;
    }

    private static byte[] BuildChunk(string id, byte[] data)
    {
        int padding = data.Length & 1;
        byte[] block = new byte[8 + data.Length + padding];
        for (int i = 0; i < 4; i++)
        {
            block[i] = (byte)id[i];
        }

        BinaryPrimitives.WriteUInt32BigEndian(block.AsSpan(4, 4), (uint)data.Length);
        data.CopyTo(block, 8);
        return block;
    }

    /// <summary>把 NAME / AUTH / ANNO 的现值补进元数据。</summary>
    public static void FillMetadata(string sourcePath, TagMetadata metadata)
    {
        try
        {
            using FileStream source = MediaTagWriter.OpenRead(sourcePath);
            foreach (RiffChunk chunk in WavInfoWriter.ReadChunks(source, isAiff: true, out _))
            {
                TagFieldId? field = chunk.Id switch
                {
                    "NAME" => TagFieldId.Title,
                    "AUTH" => TagFieldId.Artist,
                    "ANNO" => TagFieldId.Comment,
                    _ => null,
                };
                if (field is { } id)
                {
                    metadata.SetField(id, TagByteOps.DecodeText(TagByteOps.ReadRange(source, chunk.DataPos, chunk.Length)));
                }
            }
        }
        catch
        {
            // 读取失败视为无现值。
        }
    }
}

/// <summary>AIFF 写入分派。</summary>
internal static class AiffFileWriter
{
    /// <summary>按是否存在 <c>ID3 </c> 块选择写入器。</summary>
    public static Task<bool> WriteAsync(
        string sourcePath,
        TagWriteRequest request,
        IReadOnlyList<TagFieldId> changes,
        string tempPath,
        CancellationToken cancellationToken)
        => TagContainerDetect.AiffUsesText(sourcePath)
            ? AiffTextWriter.WriteAsync(sourcePath, request, changes, tempPath, cancellationToken)
            : Id3ChunkWriter.WriteAsync(sourcePath, request, changes, tempPath, cancellationToken, isAiff: true);
}

/// <summary>MP4 写入能力。</summary>
internal static class Mp4WriteSupport
{
    private static readonly string[] Names =
    [
        "标题", "艺术家", "专辑艺术家", "专辑", "流派", "作曲者", "备注", "分组", "歌词",
        "排序用标题", "排序用艺术家", "排序用专辑", "音轨号", "音轨总数", "碟号", "碟总数",
        "年份", "BPM", "合辑", "封面", "删除封面",
    ];

    /// <summary>可写入字段的中文名。</summary>
    public static IReadOnlyList<string> SupportedFieldNames() => Names;

    /// <summary>可写入字段集合。</summary>
    public static HashSet<TagFieldId> SupportedFields() =>
    [
        TagFieldId.Title, TagFieldId.Artist, TagFieldId.AlbumArtist, TagFieldId.Album, TagFieldId.Genre,
        TagFieldId.Composer, TagFieldId.Comment, TagFieldId.Grouping, TagFieldId.Lyrics,
        TagFieldId.SortTitle, TagFieldId.SortArtist, TagFieldId.SortAlbum,
        TagFieldId.TrackNumber, TagFieldId.TotalTracks, TagFieldId.DiscNumber, TagFieldId.TotalDiscs,
        TagFieldId.Year, TagFieldId.Bpm, TagFieldId.Compilation, TagFieldId.Cover,
        TagFieldId.RemoveCover,
    ];
}

/// <summary>MP4 原子树工具（大端布局）。</summary>
internal static class Mp4Atoms
{
    /// <summary>创建一个完整盒（size + type + 负载）。</summary>
    public static byte[] Box(string type, ReadOnlySpan<byte> payload)
    {
        byte[] output = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(0, 4), (uint)output.Length);
        WriteType(output.AsSpan(4, 4), type);
        payload.CopyTo(output.AsSpan(8));
        return output;
    }

    /// <summary>把 4 字节 ASCII 原子类型写入目标。</summary>
    public static void WriteType(Span<byte> destination, string type)
    {
        if (type.Length == 4)
        {
            for (int i = 0; i < 4; i++)
            {
                destination[i] = (byte)type[i];
            }

            return;
        }

        string latin1 = type;
        for (int i = 0; i < 4; i++)
        {
            destination[i] = i < latin1.Length ? (byte)latin1[i] : (byte)0;
        }
    }

    /// <summary>读取 4 字节原子类型为字符串（Latin-1，保留 0xA9）。</summary>
    public static string ReadType(ReadOnlySpan<byte> data) => TagBytes.Latin1Text(data[..4]);
}

/// <summary>MP4 文件中需要更新的 chunk offset 表。</summary>
internal sealed class Mp4ChunkStore
{
    private readonly List<Patch> _patches = [];

    /// <summary>是否为空（没有 stco / co64）。</summary>
    public bool IsEmpty => _patches.Count == 0;

    /// <summary>记录一个待更新的偏移表。</summary>
    public void Add(string type, long position, List<long> offsets)
        => _patches.Add(new Patch(type, position, offsets));

    /// <summary>用「移动 moov 到文件尾」的规则重写所有偏移。</summary>
    public void Apply(
        byte[] data,
        long oldMoovStart,
        long oldMoovEnd,
        long moovSize,
        long mdatPayloadStart,
        long mdatPayloadEnd)
    {
        foreach (Patch patch in _patches)
        {
            bool wide = patch.Type == "co64";
            int cursor = (int)patch.Position;
            foreach (long value in patch.Offsets)
            {
                long updated = Shift(value, oldMoovStart, oldMoovEnd, moovSize, mdatPayloadStart, mdatPayloadEnd);
                if (wide)
                {
                    if (cursor + 8 > data.Length)
                    {
                        break;
                    }

                    BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(cursor, 8), (ulong)updated);
                    cursor += 8;
                }
                else
                {
                    if (cursor + 4 > data.Length)
                    {
                        break;
                    }

                    if (updated >= uint.MaxValue)
                    {
                        // 超出 32 位范围：由调用方在写入前升级为 co64。
                        updated = uint.MaxValue;
                    }

                    BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(cursor, 4), (uint)updated);
                    cursor += 4;
                }
            }
        }
    }

    /// <summary>偏移平移规则。</summary>
    public static long Shift(
        long offset,
        long oldMoovStart,
        long oldMoovEnd,
        long moovSize,
        long mdatPayloadStart,
        long mdatPayloadEnd)
    {
        if (offset <= 0)
        {
            return offset;
        }

        if (offset >= oldMoovStart && offset < oldMoovEnd)
        {
            // 指向 moov 内部的偏移不移动。
            return offset;
        }

        bool insideMdat = mdatPayloadEnd > mdatPayloadStart
            && offset >= mdatPayloadStart
            && offset <= mdatPayloadEnd;
        if (insideMdat)
        {
            // 标准文件使用绝对偏移；此处 moov 前移，chunk 数据整体前移一个 moov 大小。
            return offset + moovSize;
        }

        return offset < oldMoovStart ? offset + moovSize : offset + moovSize - (oldMoovEnd - oldMoovStart);
    }

    /// <summary>是否所有偏移都能放进 32 位。</summary>
    public bool FitsIn32Bit(
        long oldMoovStart,
        long oldMoovEnd,
        long moovSize,
        long mdatPayloadStart,
        long mdatPayloadEnd)
    {
        foreach (Patch patch in _patches)
        {
            if (patch.Type != "stco")
            {
                continue;
            }

            foreach (long value in patch.Offsets)
            {
                if (Shift(value, oldMoovStart, oldMoovEnd, moovSize, mdatPayloadStart, mdatPayloadEnd) >= uint.MaxValue)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private readonly record struct Patch(string Type, long Position, List<long> Offsets);
}

/// <summary>MP4 文件的 moov 重建与搬运。</summary>
internal static class Mp4FileWriter
{
    /// <summary>把变更写入临时文件（把 moov 移到文件末尾并修正 chunk offset）。</summary>
    public static async Task<bool> WriteAsync(
        string sourcePath,
        TagWriteRequest request,
        IReadOnlyList<TagFieldId> changes,
        string tempPath,
        CancellationToken cancellationToken)
    {
        await using FileStream source = MediaTagWriter.OpenRead(sourcePath);
        if (source.Length < 16)
        {
            return false;
        }

        byte[] head = TagByteOps.ReadRange(source, 0, 16);
        if (head.Length < 8 || !TagByteOps.IsAscii(head.AsSpan(4, 4), "ftyp"))
        {
            return false;
        }

        List<Mp4TopAtom> atoms = ReadTopLevel(source);
        Mp4TopAtom? moov = null;
        Mp4TopAtom? mdat = null;
        foreach (Mp4TopAtom atom in atoms)
        {
            if (atom.Type == "moov")
            {
                moov ??= atom;
            }
            else if (atom.Type == "mdat")
            {
                mdat ??= atom;
            }
        }

        if (moov is not { } moovAtom)
        {
            return false;
        }

        long moovSize = moovAtom.Size;
        if (moovSize < 8 || moovSize > int.MaxValue)
        {
            return false;
        }

        byte[] moovBytes = TagByteOps.ReadRange(source, moovAtom.Position, (int)moovSize);
        if (moovBytes.Length != moovSize)
        {
            return false;
        }

        List<Mp4Box> top = Mp4BoxReader.ReadChildren(moovBytes, 8, moovBytes.Length);
        if (top.Count == 0)
        {
            return false;
        }

        Mp4ChunkStore stores = new();
        byte[] newMoov = RewriteMoov(moovBytes, top, request, changes, stores, out long sizeDelta);
        long newMoovSize = moovSize + sizeDelta;
        if (newMoovSize < 8)
        {
            return false;
        }

        long mdatPayloadStart = 0;
        long mdatPayloadEnd = 0;
        if (mdat is { } mdatInfo && !stores.IsEmpty)
        {
            mdatPayloadStart = mdatInfo.DataStart;
            mdatPayloadEnd = Math.Min(mdatInfo.DataStart + mdatInfo.DataLengthDeclared, source.Length);
        }

        if (!stores.IsEmpty
            && !stores.FitsIn32Bit(moovAtom.Position, moovAtom.Position + moovSize, newMoovSize, mdatPayloadStart, mdatPayloadEnd))
        {
            // stco 溢出：升级为 co64 后重做一次，偏移表位置可能整体移动。
            stores = new Mp4ChunkStore();
            newMoov = UpgradeToCo64(moovBytes, top, request, changes, stores);
            newMoovSize = newMoov.Length;
        }

        await using (FileStream destination = MediaTagWriter.CreateWrite(tempPath))
        {
            List<(long Offset, long Length)> plan = [];
            foreach (Mp4TopAtom atom in atoms)
            {
                if (atom.Type == "moov")
                {
                    continue;
                }

                long offset = atom.Position;
                long length = atom.Size;
                if (atom.Type == "mdat" && atom.DataLengthDeclared == 0)
                {
                    length = source.Length - atom.Position;
                }

                plan.Add((offset, length));
            }

            foreach ((long offset, long length) in plan)
            {
                await TagByteOps.CopyRangeAsync(source, offset, length, destination, cancellationToken).ConfigureAwait(false);
            }

            stores.Apply(newMoov, moovAtom.Position, moovAtom.Position + moovSize, newMoovSize, mdatPayloadStart, mdatPayloadEnd);
            await destination.WriteAsync(newMoov, cancellationToken).ConfigureAwait(false);
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>重建 moov：仅替换 udta 中的 ilst，其余字节与 chunk offset 表保持不变。</summary>
    private static byte[] RewriteMoov(
        byte[] moov,
        IReadOnlyList<Mp4Box> top,
        TagWriteRequest request,
        IReadOnlyList<TagFieldId> changes,
        Mp4ChunkStore stores,
        out long sizeDelta)
    {
        ArrayBufferWriter<byte> writer = new(moov.Length + 8192);
        TagByteOps.WriteBytes(writer, moov.AsSpan(0, 8));
        bool udtaFound = false;
        foreach (Mp4Box box in top)
        {
            ReadOnlySpan<byte> full = moov.AsSpan((int)box.Position, (int)box.Size);
            if (box.Type == "udta")
            {
                udtaFound = true;
                byte[] rebuilt = RewriteUdta(moov, box, request, changes);
                TagByteOps.WriteBytes(writer, Mp4Atoms.Box("udta", rebuilt));
                continue;
            }

            if (box.Type is "stco" or "co64")
            {
                stores.Add(box.Type, box.DataPos + 8, ReadOffsets(full.Slice(8), box.Type));
            }

            TagByteOps.WriteBytes(writer, full);
        }

        if (!udtaFound)
        {
            TagByteOps.WriteBytes(writer, Mp4Atoms.Box("udta", RewriteUdta(moov, null, request, changes)));
        }

        sizeDelta = writer.WrittenCount - moov.Length;
        return writer.WrittenSpan.ToArray();
    }

    /// <summary>重建 udta（保留未知原子与未修改条目）。</summary>
    private static byte[] RewriteUdta(
        byte[] moov,
        Mp4Box? udta,
        TagWriteRequest request,
        IReadOnlyList<TagFieldId> changes)
    {
        List<Mp4Box> children = udta is { } box
            ? Mp4BoxReader.ReadChildren(moov, (int)box.DataPos, (int)(box.DataPos + box.DataLength))
            : [];

        ArrayBufferWriter<byte> writer = new(4096);
        bool metaFound = false;
        foreach (Mp4Box child in children)
        {
            if (child.Type == "meta")
            {
                metaFound = true;
                byte[] rebuilt = RewriteMeta(moov, child, request, changes);
                TagByteOps.WriteBytes(writer, Mp4Atoms.Box("meta", rebuilt));
                continue;
            }

            TagByteOps.WriteBytes(writer, moov.AsSpan((int)child.Position, (int)child.Size));
        }

        if (!metaFound)
        {
            TagByteOps.WriteBytes(writer, Mp4Atoms.Box("meta", BuildMeta(handler: null, request, changes)));
        }

        return writer.WrittenSpan.ToArray();
    }

    /// <summary>重建 meta（保留 hdlr 等子原子与原有布局）。</summary>
    private static byte[] RewriteMeta(byte[] moov, Mp4Box meta, TagWriteRequest request, IReadOnlyList<TagFieldId> changes)
    {
        ReadOnlySpan<byte> payload = moov.AsSpan((int)meta.DataPos, (int)meta.DataLength);
        if (payload.Length < 8)
        {
            throw new InvalidDataException("meta 原子过短。");
        }

        string firstType = Mp4Atoms.ReadType(payload[4..8]);
        bool plain = firstType is "hdlr" or "ilst" or "keys" or "mdta" or "free";
        int childStart = plain ? 0 : 4;
        List<Mp4Box> children = Mp4BoxReader.ReadChildren(
            moov, (int)(meta.DataPos + childStart), (int)(meta.DataPos + payload.Length));
        if (children.Count == 0)
        {
            throw new InvalidDataException("meta 原子没有子原子。");
        }

        Mp4Box? ilst = null;
        foreach (Mp4Box child in children)
        {
            if (child.Type == "ilst")
            {
                ilst = child;
                break;
            }
        }

        ArrayBufferWriter<byte> writer = new(payload.Length + 4096);
        if (!plain)
        {
            TagByteOps.WriteBytes(writer, payload[..4]);
        }

        bool wroteIlst = false;
        foreach (Mp4Box child in children)
        {
            if (child.Type == "ilst")
            {
                wroteIlst = true;
                byte[] rebuilt = ilst is { } existing
                    ? RewriteIlst(moov, existing, request, changes)
                    : BuildIlst(request, changes);
                TagByteOps.WriteBytes(writer, Mp4Atoms.Box("ilst", rebuilt));
                continue;
            }

            TagByteOps.WriteBytes(writer, moov.AsSpan((int)child.Position, (int)child.Size));
        }

        if (!wroteIlst)
        {
            TagByteOps.WriteBytes(writer, Mp4Atoms.Box("ilst", BuildIlst(request, changes)));
        }

        return writer.WrittenSpan.ToArray();
    }

    /// <summary>重建 ilst：保留未知条目与未修改条目，只替换目标条目。</summary>
    private static byte[] RewriteIlst(byte[] moov, Mp4Box ilst, TagWriteRequest request, IReadOnlyList<TagFieldId> changes)
    {
        List<Mp4Box> items = Mp4BoxReader.ReadChildren(
            moov, (int)ilst.DataPos, (int)(ilst.DataPos + ilst.DataLength));
        HashSet<string> targets = Mp4ItemBuilder.TargetTypes(changes);

        ArrayBufferWriter<byte> writer = new((int)ilst.DataLength + 4096);
        foreach (Mp4Box item in items)
        {
            if (targets.Contains(item.Type))
            {
                continue;
            }

            TagByteOps.WriteBytes(writer, moov.AsSpan((int)item.Position, (int)item.Size));
        }

        AppendNewItems(writer, request, changes);
        return writer.WrittenSpan.ToArray();
    }

    /// <summary>从零构造 ilst（用于原本没有 ilst 的文件）。</summary>
    private static byte[] BuildIlst(TagWriteRequest request, IReadOnlyList<TagFieldId> changes)
    {
        ArrayBufferWriter<byte> writer = new(1024);
        AppendNewItems(writer, request, changes);
        return writer.WrittenSpan.ToArray();
    }

    /// <summary>写入所有目标条目（每个原子类型只写一次）。</summary>
    private static void AppendNewItems(
        ArrayBufferWriter<byte> writer,
        TagWriteRequest request,
        IReadOnlyList<TagFieldId> changes)
    {
        HashSet<string> written = [];
        foreach (TagFieldId id in changes)
        {
            string type = Mp4ItemBuilder.ItemType(id);
            if (type.Length == 0 || !written.Add(type))
            {
                continue;
            }

            byte[]? item = Mp4ItemBuilder.BuildItem(id, request);
            if (item is { Length: > 0 })
            {
                TagByteOps.WriteBytes(writer, item);
            }
        }
    }

    /// <summary>构造标准布局的新 meta 负载（version/flags + hdlr + ilst）。</summary>
    private static byte[] BuildMeta(Mp4HdlrInfo? handler, TagWriteRequest request, IReadOnlyList<TagFieldId> changes)
    {
        ArrayBufferWriter<byte> writer = new(1024);
        TagByteOps.WriteUInt32BigEndian(writer, 0);

        ArrayBufferWriter<byte> hdlrPayload = new(64);
        TagByteOps.WriteUInt32BigEndian(hdlrPayload, 0);
        TagByteOps.WriteUInt32BigEndian(hdlrPayload, 0);
        TagByteOps.WriteAscii(hdlrPayload, handler?.HandlerType ?? "mdir");
        TagByteOps.WriteAscii(hdlrPayload, handler?.Name ?? "appl");
        TagByteOps.WriteZeros(hdlrPayload, 9);
        TagByteOps.WriteBytes(writer, Mp4Atoms.Box("hdlr", hdlrPayload.WrittenSpan));
        TagByteOps.WriteBytes(writer, Mp4Atoms.Box("ilst", BuildIlst(request, changes)));
        return writer.WrittenSpan.ToArray();
    }

    /// <summary>把 stco 升级为 co64（保留其它字节不变，仅加宽偏移并改写原子类型）。</summary>
    private static byte[] UpgradeToCo64(
        byte[] moov,
        IReadOnlyList<Mp4Box> top,
        TagWriteRequest request,
        IReadOnlyList<TagFieldId> changes,
        Mp4ChunkStore stores)
    {
        ArrayBufferWriter<byte> writer = new(moov.Length + 4096);
        TagByteOps.WriteBytes(writer, moov.AsSpan(0, 8));
        foreach (Mp4Box box in top)
        {
            if (box.Type == "udta")
            {
                byte[] rebuilt = RewriteUdta(moov, box, request, changes);
                TagByteOps.WriteBytes(writer, Mp4Atoms.Box("udta", rebuilt));
                continue;
            }

            if (box.Type == "stco")
            {
                List<long> offsets = ReadOffsets(
                    moov.AsSpan((int)(box.DataPos + 8), (int)Math.Max(0, box.DataLength - 8)), "stco");
                byte[] payload = new byte[8 + (offsets.Count * 8)];
                BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(0, 4), 0);
                BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4, 4), (uint)offsets.Count);
                int cursor = 8;
                foreach (long offset in offsets)
                {
                    BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(cursor, 8), (ulong)Math.Max(0, offset));
                    cursor += 8;
                }

                stores.Add("co64", writer.WrittenCount + 8 + 8, offsets);
                TagByteOps.WriteBytes(writer, Mp4Atoms.Box("co64", payload));
                continue;
            }

            if (box.Type == "co64")
            {
                List<long> offsets = ReadOffsets(
                    moov.AsSpan((int)(box.DataPos + 8), (int)Math.Max(0, box.DataLength - 8)), "co64");
                stores.Add("co64", writer.WrittenCount + 8 + 8, offsets);
                TagByteOps.WriteBytes(writer, moov.AsSpan((int)box.Position, (int)box.Size));
                continue;
            }

            TagByteOps.WriteBytes(writer, moov.AsSpan((int)box.Position, (int)box.Size));
        }

        return writer.WrittenSpan.ToArray();
    }

    /// <summary>读取 stco / co64 的偏移列表。</summary>
    private static List<long> ReadOffsets(ReadOnlySpan<byte> payload, string type)
    {
        List<long> offsets = [];
        if (payload.Length < 8)
        {
            return offsets;
        }

        uint count = BinaryPrimitives.ReadUInt32BigEndian(payload[4..8]);
        int width = type == "co64" ? 8 : 4;
        int available = (payload.Length - 8) / width;
        int total = Math.Min((int)Math.Min(count, int.MaxValue), Math.Max(0, available));
        for (int i = 0; i < total; i++)
        {
            offsets.Add(width == 8
                ? (long)BinaryPrimitives.ReadUInt64BigEndian(payload.Slice(8 + (i * 8), 8))
                : BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(8 + (i * 4), 4)));
        }

        return offsets;
    }

    /// <summary>枚举文件顶层原子。</summary>
    private static List<Mp4TopAtom> ReadTopLevel(FileStream source)
    {
        List<Mp4TopAtom> atoms = [];
        long position = 0;
        int guard = 0;
        while (position + 8 <= source.Length && guard++ < 4096)
        {
            if (!Mp4BoxReader.TryReadHeader(source, position, source.Length, out Mp4TopAtom atom))
            {
                break;
            }

            atoms.Add(atom);
            if (atom.Position + atom.Size <= position)
            {
                break;
            }

            position = atom.Position + atom.Size;
        }

        return atoms;
    }

    /// <summary>把 ilst 现值补进元数据（覆盖读取器不返回的排序与合辑字段）。</summary>
    public static void FillMetadata(string sourcePath, TagMetadata metadata)
    {
        try
        {
            using FileStream source = MediaTagOps.OpenRead(sourcePath);
            long moovStart = Mp4TagOps.FindTopLevel(source, "moov");            if (moovStart < 0)
            {
                return;
            }

            int moovHeader = Mp4TagOps.ReadAtomHeader(source, moovStart);
            if (moovHeader <= 0 || moovStart + moovHeader > source.Length)
            {
                return;
            }

            byte[] moov = TagByteOps.ReadRange(source, moovStart, (int)Math.Min(source.Length - moovStart, int.MaxValue));
            int moovSize = BinaryPrimitives.ReadInt32BigEndian(moov.AsSpan(0, 4));
            long ilst = Mp4TagOps.FindNested(moov, "ilst", moovHeader, Math.Min(moovSize, moov.Length));
            if (ilst < 0)
            {
                return;
            }

            Mp4TagOps.ApplyItems(moov, ilst + 8, Math.Min(moovSize, moov.Length), metadata);
        }
        catch
        {
            // 读取失败视为无现值。
        }
    }
}

/// <summary>宿主文件读取辅助。</summary>
internal static class MediaTagOps
{
    /// <summary>以只读方式打开文件（同步读取，用于写入前的现值探测）。</summary>
    public static FileStream OpenRead(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: false);
}

/// <summary>MP4 读取辅助（写入器的现值探测）。</summary>
internal static class Mp4TagOps
{
    /// <summary>以只读方式打开文件。</summary>
    public static FileStream OpenRead(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: false);

    /// <summary>在 <paramref name="data"/> 的 [start,end) 区间内查找给定原子，返回其在数组中的偏移。</summary>
    public static long FindAtom(ReadOnlySpan<byte> data, string type, long start, long end)
    {
        long position = start;
        int guard = 0;
        while (position + 8 <= end && position + 8 <= data.Length && guard++ < 4096)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(data.Slice((int)position, 4));
            string id = Mp4Atoms.ReadType(data.Slice((int)position + 4, 4));
            if (size == 1)
            {
                if (position + 16 > data.Length)
                {
                    return -1;
                }

                size = (long)BinaryPrimitives.ReadUInt64BigEndian(data.Slice((int)position + 8, 8));
            }
            else if (size == 0)
            {
                size = end - position;
            }

            if (size < 8 || position + size > end)
            {
                return -1;
            }

            if (id == type)
            {
                return position;
            }

            position += size;
        }

        return -1;
    }

    /// <summary>递归查找嵌套原子（跳过 meta 的 4 字节版本/标志）。</summary>
    public static long FindNested(ReadOnlySpan<byte> data, string type, long start, long end, int depth = 0)
    {
        if (depth > 8)
        {
            return -1;
        }

        long position = start;
        while (position + 8 <= end && position + 8 <= data.Length)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(data.Slice((int)position, 4));
            string id = Mp4Atoms.ReadType(data.Slice((int)position + 4, 4));
            if (size == 1)
            {
                if (position + 16 > data.Length)
                {
                    return -1;
                }

                size = (long)BinaryPrimitives.ReadUInt64BigEndian(data.Slice((int)position + 8, 8));
            }
            else if (size == 0)
            {
                size = end - position;
            }

            if (size < 8 || position + size > end)
            {
                return -1;
            }

            if (id == type)
            {
                return position;
            }

            if (id is "udta" or "meta" or "ilst")
            {
                long childStart = position + 8 + (id == "meta" ? 4 : 0);
                long found = FindNested(data, type, childStart, position + size, depth + 1);
                if (found >= 0)
                {
                    return found;
                }
            }

            position += size;
        }

        return -1;
    }

    /// <summary>扫描文件的顶层原子，返回指定原子在文件中的偏移。</summary>
    public static long FindTopLevel(FileStream source, string type)
    {
        long position = 0;
        int guard = 0;
        while (position + 8 <= source.Length && guard++ < 4096)
        {
            int header = ReadAtomHeader(source, position);
            if (header <= 0)
            {
                return -1;
            }

            long size = ReadAtomSize(source, position, header);
            if (size < header)
            {
                return -1;
            }

            byte[] id = TagByteOps.ReadRange(source, position + 4, 4);
            if (id.Length == 4 && Mp4Atoms.ReadType(id) == type)
            {
                return position;
            }

            position += size;
        }

        return -1;
    }

    /// <summary>读取原子头的长度（8 或 16）。</summary>
    public static int ReadAtomHeader(FileStream source, long position)
    {
        byte[] head = TagByteOps.ReadRange(source, position, 8);
        if (head.Length < 8)
        {
            return -1;
        }

        return BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(0, 4)) == 1 ? 16 : 8;
    }

    /// <summary>读取原子总长度。</summary>
    public static long ReadAtomSize(FileStream source, long position, int header)
    {
        byte[] head = TagByteOps.ReadRange(source, position, header);
        if (head.Length < header)
        {
            return -1;
        }

        long size = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(0, 4));
        if (size == 1 && header >= 16)
        {
            return (long)BinaryPrimitives.ReadUInt64BigEndian(head.AsSpan(8, 8));
        }

        return size == 0 ? source.Length - position : size;
    }

    /// <summary>把 ilst 条目的值补进元数据。</summary>
    public static void ApplyItems(ReadOnlySpan<byte> moov, long start, long end, TagMetadata metadata)
    {
        long position = start;
        int guard = 0;
        while (position + 8 <= end && end <= moov.Length && guard++ < 512)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(moov.Slice((int)position, 4));
            string id = Mp4Atoms.ReadType(moov.Slice((int)position + 4, 4));
            if (size < 8 || position + size > end)
            {
                return;
            }

            long data = FindAtom(moov, "data", position + 8, position + size);
            if (data > 0)
            {
                long dataSize = BinaryPrimitives.ReadUInt32BigEndian(moov.Slice((int)data, 4));
                long payloadStart = data + 16;
                long payloadLength = dataSize - 8;
                if (payloadLength > 0 && payloadStart + payloadLength <= moov.Length)
                {
                    ReadOnlySpan<byte> payload = moov.Slice((int)payloadStart, (int)payloadLength);
                    ApplyItem(id, payload, metadata);
                }
            }

            position += size;
        }
    }

    private static void ApplyItem(string id, ReadOnlySpan<byte> payload, TagMetadata metadata)
    {
        switch (id)
        {
            case "sonm":
                metadata.SetField(TagFieldId.SortTitle, TagText.DecodeUnknown(payload));
                break;
            case "soar":
                metadata.SetField(TagFieldId.SortArtist, TagText.DecodeUnknown(payload));
                break;
            case "soal":
                metadata.SetField(TagFieldId.SortAlbum, TagText.DecodeUnknown(payload));
                break;
            case "cpil":
                metadata.SetField(TagFieldId.Compilation, payload.Length > 0 && payload[0] != 0 ? "1" : "0");
                break;
            case "trkn":
                if (payload.Length >= 6)
                {
                    metadata.SetField(
                        TagFieldId.TrackNumber,
                        BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(2, 2)).ToString(CultureInfo.InvariantCulture));
                    metadata.SetField(
                        TagFieldId.TotalTracks,
                        BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(4, 2)).ToString(CultureInfo.InvariantCulture));
                }

                break;
            case "disk":
                if (payload.Length >= 6)
                {
                    metadata.SetField(
                        TagFieldId.DiscNumber,
                        BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(2, 2)).ToString(CultureInfo.InvariantCulture));
                    metadata.SetField(
                        TagFieldId.TotalDiscs,
                        BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(4, 2)).ToString(CultureInfo.InvariantCulture));
                }

                break;
            case "tmpo":
                if (payload.Length >= 2)
                {
                    metadata.SetField(
                        TagFieldId.Bpm,
                        BinaryPrimitives.ReadUInt16BigEndian(payload).ToString(CultureInfo.InvariantCulture));
                }

                break;
            case "\u00A9day":
                metadata.SetField(TagFieldId.Year, TagText.DecodeUnknown(payload));
                break;
        }
    }
}

/// <summary>MP4 构建 ilst 条目的映射。</summary>
internal static class Mp4ItemBuilder
{
    /// <summary>字段 → ilst 条目类型。</summary>
    public static string ItemType(TagFieldId id) => id switch
    {
        TagFieldId.Title => "\u00A9nam",
        TagFieldId.Artist => "\u00A9ART",
        TagFieldId.AlbumArtist => "aART",
        TagFieldId.Album => "\u00A9alb",
        TagFieldId.Genre => "\u00A9gen",
        TagFieldId.Composer => "\u00A9wrt",
        TagFieldId.Comment => "\u00A9cmt",
        TagFieldId.Grouping => "\u00A9grp",
        TagFieldId.Lyrics => "\u00A9lyr",
        TagFieldId.SortTitle => "sonm",
        TagFieldId.SortArtist => "soar",
        TagFieldId.SortAlbum => "soal",
        TagFieldId.TrackNumber => "trkn",
        TagFieldId.TotalTracks => "trkn",
        TagFieldId.DiscNumber => "disk",
        TagFieldId.TotalDiscs => "disk",
        TagFieldId.Year => "\u00A9day",
        TagFieldId.Bpm => "tmpo",
        TagFieldId.Compilation => "cpil",
        TagFieldId.Cover => "covr",
        _ => string.Empty,
    };

    /// <summary>本次变更涉及的条目类型集合。</summary>
    public static HashSet<string> TargetTypes(IReadOnlyList<TagFieldId> changes)
    {
        HashSet<string> types = [];
        foreach (TagFieldId id in changes)
        {
            string type = ItemType(id);
            if (type.Length > 0)
            {
                types.Add(type);
            }
        }

        return types;
    }

    /// <summary>构造一个 ilst 条目（含 8 字节原子头）；返回 null 表示无需写入。</summary>
    public static byte[]? BuildItem(TagFieldId id, TagWriteRequest request)
    {
        string type = ItemType(id);
        if (type.Length == 0)
        {
            return null;
        }

        switch (id)
        {
            case TagFieldId.TrackNumber:
                return BuildPairItem("trkn", request.TrackNumber, request.TotalTracks);
            case TagFieldId.TotalTracks:
                return BuildPairItem("trkn", request.TrackNumber, request.TotalTracks);
            case TagFieldId.DiscNumber:
                return BuildPairItem("disk", request.DiscNumber, request.TotalDiscs);
            case TagFieldId.TotalDiscs:
                return BuildPairItem("disk", request.DiscNumber, request.TotalDiscs);
            case TagFieldId.Bpm:
                if (request.Bpm is not > 0)
                {
                    return null;
                }

                return BuildItemCore(type, 21, BuildInt16(request.Bpm.Value));
            case TagFieldId.Compilation:
                if (request.IsCompilation is not { } compilation)
                {
                    return null;
                }

                return BuildItemCore(type, 21, [compilation ? (byte)1 : (byte)0]);
            case TagFieldId.Year:
                if (request.Year is not > 0)
                {
                    return null;
                }

                return BuildItemCore(
                    type,
                    1,
                    BuildUtf8(request.Year.Value.ToString("D4", CultureInfo.InvariantCulture)));
            case TagFieldId.Cover:
                if (request.CoverImage is not { Length: > 0 } image)
                {
                    return null;
                }

                return BuildItemCore(type, Mp4CoverType(image, request.CoverMimeType), image);
            default:
            {
                string? text = Mp4TextValue(request, id);
                return string.IsNullOrEmpty(text) ? null : BuildItemCore(type, 1, BuildUtf8(text));
            }
        }
    }

    private static string? Mp4TextValue(TagWriteRequest request, TagFieldId id) => id switch
    {
        TagFieldId.Title => TagText.Clean(request.Title),
        TagFieldId.Artist => TagText.Clean(request.Artist),
        TagFieldId.AlbumArtist => TagText.Clean(request.AlbumArtist),
        TagFieldId.Album => TagText.Clean(request.Album),
        TagFieldId.Genre => TagText.Clean(request.Genre),
        TagFieldId.Composer => TagText.Clean(request.Composer),
        TagFieldId.Comment => TagText.Clean(request.Comment),
        TagFieldId.Grouping => TagText.Clean(request.Grouping),
        TagFieldId.Lyrics => TagText.Clean(request.Lyrics),
        TagFieldId.SortTitle => TagText.Clean(request.SortTitle),
        TagFieldId.SortArtist => TagText.Clean(request.SortArtist),
        TagFieldId.SortAlbum => TagText.Clean(request.SortAlbum),
        _ => null,
    };

    private static int Mp4CoverType(byte[] image, string? mime)
    {
        string resolved = TagText.Clean(mime);
        if (resolved.Length == 0)
        {
            resolved = TagText.SniffImageMime(image);
        }

        return resolved.Contains("png", StringComparison.OrdinalIgnoreCase) ? 14 : 13;
    }

    private static byte[] BuildPairItem(string type, int? first, int? second)
    {
        int a = first ?? 0;
        int b = second ?? 0;
        if (a <= 0 && b <= 0)
        {
            return [];
        }

        byte[] payload = new byte[8];
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(2, 2), (ushort)Math.Clamp(a, 0, ushort.MaxValue));
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(4, 2), (ushort)Math.Clamp(b, 0, ushort.MaxValue));
        return BuildItemCore(type, 0, payload);
    }

    private static byte[] BuildInt16(int value)
    {
        byte[] payload = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(payload, (ushort)Math.Clamp(value, 0, ushort.MaxValue));
        return payload;
    }

    private static byte[] BuildUtf8(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>构造 data 原子并包进条目。</summary>
    private static byte[] BuildItemCore(string itemType, int dataType, byte[] payload)
    {
        byte[] data = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0, 4), (uint)data.Length);
        "data"u8.CopyTo(data.AsSpan(4, 4));
        data[8] = 0;
        data[9] = 0;
        data[10] = 0;
        data[11] = (byte)dataType;
        payload.CopyTo(data, 16);
        return Mp4Atoms.Box(itemType, data);
    }
}

/// <summary>解析出的 MP4 盒（相对某个缓冲区）。</summary>
internal readonly record struct Mp4Box(
    string Type,
    long Position,
    long Size,
    long DataPos,
    long DataLength);

/// <summary>MP4 顶层原子（相对整个文件）。</summary>
internal readonly record struct Mp4TopAtom(
    string Type,
    long Position,
    long Size,
    long DataStart,
    long DataLengthDeclared);

/// <summary>MP4 原子头读取。</summary>
internal static class Mp4BoxReader
{
    /// <summary>从字节缓冲区枚举子原子。</summary>
    public static List<Mp4Box> ReadChildren(byte[] data, int start, int end)
    {
        List<Mp4Box> boxes = [];
        int position = start;
        int guard = 0;
        while (position + 8 <= end && position + 8 <= data.Length && guard++ < 65536)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(position, 4));
            string type = Mp4Atoms.ReadType(data.AsSpan(position + 4, 4));
            int headerLength = 8;
            if (size == 1)
            {
                if (position + 16 > data.Length)
                {
                    break;
                }

                size = (long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(position + 8, 8));
                headerLength = 16;
            }
            else if (size == 0)
            {
                size = end - position;
            }

            if (size < headerLength || position + size > end)
            {
                break;
            }

            boxes.Add(new Mp4Box(type, position, size, position + headerLength, size - headerLength));
            position += (int)size;
        }

        return boxes;
    }

    /// <summary>从文件读取一个原子头。</summary>
    public static bool TryReadHeader(FileStream source, long position, long limit, out Mp4TopAtom atom)
    {
        atom = default;
        byte[] header = TagByteOps.ReadRange(source, position, 16);
        if (header.Length < 8)
        {
            return false;
        }

        long size = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));
        string type = Mp4Atoms.ReadType(header.AsSpan(4, 4));
        int headerLength = 8;
        if (size == 1)
        {
            if (header.Length < 16)
            {
                return false;
            }

            size = (long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8, 8));
            headerLength = 16;
        }
        else if (size == 0)
        {
            size = limit - position;
        }

        if (size < headerLength)
        {
            return false;
        }

        long dataStart = position + headerLength;
        long declared = size - headerLength;
        long available = Math.Max(0, limit - dataStart);
        atom = new Mp4TopAtom(type, position, size, dataStart, Math.Min(declared, available));
        return true;
    }
}

/// <summary>MP4 meta 的 hdlr 信息。</summary>
internal sealed record Mp4HdlrInfo(string HandlerType, string Name);
