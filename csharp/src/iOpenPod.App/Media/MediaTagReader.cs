using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace IOpenPod.App.Media;

/// <summary>宿主媒体文件的标签与基础参数读取。</summary>
public static class MediaTagReader
{
    /// <summary>内嵌封面允许的最大字节数（8 MiB），超过则忽略。</summary>
    internal const int MaxCoverBytes = 8 * 1024 * 1024;

    /// <summary>支持的音频扩展名。</summary>
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp3", "m4a", "m4b", "m4p", "m4r", "aac", "wav", "aif", "aiff",
        "alac", "flac", "ogg", "oga", "opus", "wma", "asf", "wv", "ape",
    };

    /// <summary>支持的视频扩展名。</summary>
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp4", "m4v", "mov",
    };

    /// <summary>判断扩展名是否为软件支持扫描的音视频文件。</summary>
    public static bool IsSupportedExtension(string path)
    {
        string extension = GetExtension(path);
        return extension.Length != 0 && (AudioExtensions.Contains(extension) || VideoExtensions.Contains(extension));
    }

    /// <summary>读取一个媒体文件。文件不可读或格式未知时返回仅含文件事实的结果，不抛异常。</summary>
    public static MediaTagInfo Read(string hostPath)
    {
        ArgumentNullException.ThrowIfNull(hostPath);

        long sizeBytes = 0;
        try
        {
            FileInfo info = new(hostPath);
            if (info.Exists)
            {
                sizeBytes = info.Length;
            }
        }
        catch
        {
            // 文件事实读取失败时保持 0。
        }

        MediaTagBuilder builder = new();
        string extension = GetExtension(hostPath);
        FileSliceSource? file = null;
        try
        {
            file = FileSliceSource.Open(hostPath);
            sizeBytes = file.Length;
            ReadContent(builder, file, extension);
        }
        catch
        {
            // 任何解析失败都退化为只含文件事实的结果。
        }
        finally
        {
            file?.Dispose();
        }

        return builder.ToInfo(sizeBytes, VideoExtensions.Contains(extension));
    }

    /// <summary>按魔数优先、扩展名兜底的方式分派到具体解析器。</summary>
    private static void ReadContent(MediaTagBuilder builder, FileSliceSource file, string extension)
    {
        Span<byte> magic = stackalloc byte[16];
        int magicLength = file.Read(0, magic);
        ReadOnlySpan<byte> head = magic[..magicLength];

        if (TagBytes.IsAscii(head, "fLaC"))
        {
            FlacReader.Read(builder, file);
            return;
        }

        if (TagBytes.IsAscii(head, "OggS"))
        {
            OggReader.Read(builder, file);
            return;
        }

        if (head.Length >= 12 && TagBytes.IsAscii(head, "RIFF") && TagBytes.IsAscii(head[8..12], "WAVE"))
        {
            WavReader.Read(builder, file);
            return;
        }

        if (head.Length >= 12 && TagBytes.IsAscii(head, "FORM")
            && (TagBytes.IsAscii(head[8..12], "AIFF") || TagBytes.IsAscii(head[8..12], "AIFC")))
        {
            AiffReader.Read(builder, file);
            return;
        }

        if (head.Length >= 8 && TagBytes.IsAscii(head[4..8], "ftyp"))
        {
            Mp4Reader.Read(builder, file);
            return;
        }

        if (AsfReader.IsAsfHeader(head))
        {
            AsfReader.Read(builder, file);
            return;
        }

        if (TagBytes.IsAscii(head, "ID3"))
        {
            Id3v2Reader.Read(builder, file, 0);
            Id3v1Reader.Read(builder, file);
            MpegAudioReader.Read(builder, file);
            return;
        }

        switch (extension)
        {
            case "mp3":
            case "aac":
                MpegAudioReader.Read(builder, file);
                Id3v1Reader.Read(builder, file);
                return;
            case "wav":
                WavReader.Read(builder, file);
                return;
            case "aif":
            case "aiff":
                AiffReader.Read(builder, file);
                return;
            case "flac":
                FlacReader.Read(builder, file);
                return;
            case "ogg":
            case "oga":
            case "opus":
                OggReader.Read(builder, file);
                return;
            case "wma":
            case "asf":
                AsfReader.Read(builder, file);
                return;
            case "m4a":
            case "m4b":
            case "m4p":
            case "m4r":
            case "mp4":
            case "m4v":
            case "mov":
            case "alac":
                Mp4Reader.Read(builder, file);
                return;
            default:
                if (extension.Length != 0 && (AudioExtensions.Contains(extension) || VideoExtensions.Contains(extension)))
                {
                    // 扩展名支持但内容未知：仅尝试最廉价的 ID3v1 回退。
                    Id3v1Reader.Read(builder, file);
                }

                return;
        }
    }

    /// <summary>取小写扩展名（不含前导点）。</summary>
    internal static string GetExtension(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        try
        {
            string extension = Path.GetExtension(path);
            return extension.Length <= 1 ? string.Empty : extension[1..].ToLowerInvariant();
        }
        catch
        {
            return string.Empty;
        }
    }
}

/// <summary>基于随机访问的文件读取源，只读取需要的字节区间。</summary>
internal sealed class FileSliceSource : IDisposable
{
    private readonly SafeFileHandle _handle;

    private FileSliceSource(SafeFileHandle handle, long length)
    {
        _handle = handle;
        Length = length;
    }

    /// <summary>文件总长度。</summary>
    public long Length { get; }

    /// <summary>以只读方式打开文件。</summary>
    public static FileSliceSource Open(string path)
    {
        SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        try
        {
            long length = RandomAccess.GetLength(handle);
            return new FileSliceSource(handle, length);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>从指定偏移读取至多 <c>buffer.Length</c> 个字节，返回实际读取数。</summary>
    public int Read(long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset >= Length || buffer.Length == 0)
        {
            return 0;
        }

        int wanted = (int)Math.Min(buffer.Length, Length - offset);
        int total = 0;
        while (total < wanted)
        {
            int read = RandomAccess.Read(_handle, buffer[total..wanted], offset + total);
            if (read <= 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    /// <summary>尽量读满指定长度；不足时返回实际读到的长度。</summary>
    public bool TryRead(long offset, Span<byte> buffer) => Read(offset, buffer) == buffer.Length;

    /// <summary>读取字节数组；不足时返回实际长度的数组。</summary>
    public byte[] ReadArray(long offset, int count)
    {
        if (count <= 0 || offset < 0 || offset >= Length)
        {
            return [];
        }

        int wanted = (int)Math.Min(count, Length - offset);
        byte[] data = new byte[wanted];
        int read = Read(offset, data);
        if (read == wanted)
        {
            return data;
        }

        return read <= 0 ? [] : data[..read];
    }

    public void Dispose() => _handle.Dispose();
}

/// <summary>标签收集器：先写入者优先，最终冻结为 <see cref="MediaTagInfo"/>。</summary>
internal sealed class MediaTagBuilder
{
    public string Title { get; private set; } = string.Empty;

    public string Artist { get; private set; } = string.Empty;

    public string Album { get; private set; } = string.Empty;

    public string AlbumArtist { get; private set; } = string.Empty;

    public string Genre { get; private set; } = string.Empty;

    public string Composer { get; private set; } = string.Empty;

    public string Comment { get; private set; } = string.Empty;

    public string Grouping { get; private set; } = string.Empty;

    public string Lyrics { get; private set; } = string.Empty;

    public int TrackNumber { get; private set; }

    public int TotalTracks { get; private set; }

    public int DiscNumber { get; private set; }

    public int TotalDiscs { get; private set; }

    public int Year { get; private set; }

    public int Bpm { get; private set; }

    public bool IsCompilation { get; private set; }

    public TimeSpan Duration { get; private set; }

    public int BitrateKbps { get; private set; }

    public int SampleRate { get; private set; }

    public int Channels { get; private set; }

    public byte[]? CoverImage { get; private set; }

    public string CoverMimeType { get; private set; } = string.Empty;

    /// <summary>是否已经收集到任意文本标签。</summary>
    public bool HasTextTag =>
        Title.Length > 0 || Artist.Length > 0 || Album.Length > 0 || AlbumArtist.Length > 0
        || Genre.Length > 0 || Composer.Length > 0 || Comment.Length > 0 || Grouping.Length > 0
        || Lyrics.Length > 0;

    public void SetTitle(string? value) => Title = Merge(Title, value);

    public void SetArtist(string? value) => Artist = Merge(Artist, value);

    public void SetAlbum(string? value) => Album = Merge(Album, value);

    public void SetAlbumArtist(string? value) => AlbumArtist = Merge(AlbumArtist, value);

    public void SetGenre(string? value) => Genre = Merge(Genre, value);

    public void SetComposer(string? value) => Composer = Merge(Composer, value);

    public void SetComment(string? value) => Comment = Merge(Comment, value);

    public void SetGrouping(string? value) => Grouping = Merge(Grouping, value);

    public void SetLyrics(string? value) => Lyrics = Merge(Lyrics, value);

    public void SetTrackNumber(int value)
    {
        if (value > 0 && TrackNumber == 0)
        {
            TrackNumber = value;
        }
    }

    public void SetTotalTracks(int value)
    {
        if (value > 0 && TotalTracks == 0)
        {
            TotalTracks = value;
        }
    }

    public void SetDiscNumber(int value)
    {
        if (value > 0 && DiscNumber == 0)
        {
            DiscNumber = value;
        }
    }

    public void SetTotalDiscs(int value)
    {
        if (value > 0 && TotalDiscs == 0)
        {
            TotalDiscs = value;
        }
    }

    public void SetYear(int value)
    {
        if (value > 0 && value < 3000 && Year == 0)
        {
            Year = value;
        }
    }

    public void SetBpm(int value)
    {
        if (value > 0 && Bpm == 0)
        {
            Bpm = value;
        }
    }

    public void SetCompilation(bool value)
    {
        if (value)
        {
            IsCompilation = true;
        }
    }

    public void SetDuration(TimeSpan value)
    {
        if (value > TimeSpan.Zero && value < TimeSpan.FromHours(24) && Duration == TimeSpan.Zero)
        {
            Duration = value;
        }
    }

    /// <summary>写入比特率；<paramref name="force"/> 为真时覆盖已写入的粗略估算值。</summary>
    public void SetBitrateKbps(int value, bool force = false)
    {
        if (value > 0 && value < 100000 && (force || BitrateKbps == 0))
        {
            BitrateKbps = value;
        }
    }

    public void SetSampleRate(int value)
    {
        if (value > 0 && SampleRate == 0)
        {
            SampleRate = value;
        }
    }

    public void SetChannels(int value)
    {
        if (value > 0 && Channels == 0)
        {
            Channels = value;
        }
    }

    /// <summary>写入封面：只接受第一张、且不超过 8 MiB 的图片。</summary>
    public void SetCover(byte[]? data, string? mime)
    {
        if (data is null || data.Length == 0 || data.Length > MediaTagReader.MaxCoverBytes || CoverImage is not null)
        {
            return;
        }

        string resolved = TagText.Clean(mime);
        if (resolved.Length == 0)
        {
            resolved = TagText.SniffImageMime(data);
        }

        CoverImage = data;
        CoverMimeType = resolved;
    }

    /// <summary>用另一个收集器补齐本收集器尚未填充的字段（不与已有值冲突）。</summary>
    public void FillMissingFrom(MediaTagBuilder other)
    {
        SetTitle(other.Title);
        SetArtist(other.Artist);
        SetAlbum(other.Album);
        SetAlbumArtist(other.AlbumArtist);
        SetGenre(other.Genre);
        SetComposer(other.Composer);
        SetComment(other.Comment);
        SetGrouping(other.Grouping);
        SetLyrics(other.Lyrics);
        SetTrackNumber(other.TrackNumber);
        SetTotalTracks(other.TotalTracks);
        SetDiscNumber(other.DiscNumber);
        SetTotalDiscs(other.TotalDiscs);
        SetYear(other.Year);
        SetBpm(other.Bpm);
        SetCompilation(other.IsCompilation);
        SetDuration(other.Duration);
        SetBitrateKbps(other.BitrateKbps);
        SetSampleRate(other.SampleRate);
        SetChannels(other.Channels);
        if (CoverImage is null && other.CoverImage is not null)
        {
            SetCover(other.CoverImage, other.CoverMimeType);
        }
    }

    /// <summary>冻结为只读结果。</summary>
    public MediaTagInfo ToInfo(long sizeBytes, bool isVideo) => new()
    {
        Title = Title,
        Artist = Artist,
        Album = Album,
        AlbumArtist = AlbumArtist,
        Genre = Genre,
        Composer = Composer,
        Comment = Comment,
        Grouping = Grouping,
        Lyrics = Lyrics,
        TrackNumber = TrackNumber,
        TotalTracks = TotalTracks,
        DiscNumber = DiscNumber,
        TotalDiscs = TotalDiscs,
        Year = Year,
        Bpm = Bpm,
        IsCompilation = IsCompilation,
        Duration = Duration,
        SizeBytes = sizeBytes,
        BitrateKbps = BitrateKbps,
        SampleRate = SampleRate,
        Channels = Channels,
        IsVideo = isVideo,
        CoverImage = CoverImage,
        CoverMimeType = CoverMimeType,
    };

    private static string Merge(string current, string? value)
        => current.Length > 0 ? current : TagText.Clean(value);
}

/// <summary>字节与文本处理辅助。</summary>
internal static class TagBytes
{
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

    /// <summary>以 Latin-1 解码短标识（保留 0xA9 等高位字节）。</summary>
    public static string Latin1Text(ReadOnlySpan<byte> data) => Encoding.Latin1.GetString(data);

    /// <summary>ID3v2 synchsafe 长度（7 位一字节）。</summary>
    public static int Synchsafe(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
        {
            return 0;
        }

        return ((data[0] & 0x7F) << 21) | ((data[1] & 0x7F) << 14) | ((data[2] & 0x7F) << 7) | (data[3] & 0x7F);
    }

    public static int UInt24BigEndian(ReadOnlySpan<byte> data)
    {
        if (data.Length < 3)
        {
            return 0;
        }

        return (data[0] << 16) | (data[1] << 8) | data[2];
    }
}

/// <summary>标签文本解码与数字解析。</summary>
internal static class TagText
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false);
    private static readonly Encoding Utf8Strict = new UTF8Encoding(false, true);
    private static readonly Encoding Utf16Le = new UnicodeEncoding(false, false);
    private static readonly Encoding Utf16Be = new UnicodeEncoding(true, false);

    /// <summary>清理文本：去掉 NUL 与首尾空白。</summary>
    public static string Clean(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value.IndexOf('\0') < 0)
        {
            return value.Trim();
        }

        StringBuilder builder = new(value.Length);
        foreach (char c in value)
        {
            if (c != '\0')
            {
                builder.Append(c);
            }
        }

        return builder.ToString().Trim();
    }

    /// <summary>按 ID3 编码字节解码正文。</summary>
    public static string DecodeWithEncoding(byte encoding, ReadOnlySpan<byte> body)
    {
        switch (encoding)
        {
            case 0:
                return Encoding.Latin1.GetString(TrimNulls(body, wide: false));
            case 1:
                return DecodeUtf16(body, bigEndian: null);
            case 2:
                return DecodeUtf16(body, bigEndian: true);
            case 3:
                return Utf8.GetString(TrimNulls(body, wide: false));
            default:
                return Encoding.Latin1.GetString(TrimNulls(body, wide: false));
        }
    }

    /// <summary>解码带编码前缀的 ID3 文本帧。</summary>
    public static string DecodeTextFrame(ReadOnlySpan<byte> data)
        => data.Length == 0 ? string.Empty : DecodeWithEncoding(data[0], data[1..]);

    /// <summary>跳过以 NUL 结尾的描述串，返回消耗的字节数。</summary>
    public static int SkipNullTerminated(ReadOnlySpan<byte> data, byte encoding)
    {
        if (encoding is 1 or 2)
        {
            int i = 0;
            while (i + 1 < data.Length)
            {
                if (data[i] == 0 && data[i + 1] == 0)
                {
                    return i + 2;
                }

                i += 2;
            }

            return data.Length;
        }

        int index = data.IndexOf((byte)0);
        return index < 0 ? data.Length : index + 1;
    }

    /// <summary>UTF-8 优先、Latin-1 兜底（ID3v1 与文件名等无编码信息文本）。</summary>
    public static string DecodeUnknown(ReadOnlySpan<byte> data)
    {
        ReadOnlySpan<byte> trimmed = TrimNulls(data, wide: false);
        if (trimmed.IsEmpty)
        {
            return string.Empty;
        }

        bool hasHighByte = false;
        foreach (byte b in trimmed)
        {
            if (b >= 0x80)
            {
                hasHighByte = true;
                break;
            }
        }

        if (!hasHighByte)
        {
            return Encoding.Latin1.GetString(trimmed);
        }

        try
        {
            return Utf8Strict.GetString(trimmed);
        }
        catch
        {
            return Encoding.Latin1.GetString(trimmed);
        }
    }

    /// <summary>UTF-16LE 解码（ASF 使用），去掉结尾 NUL。</summary>
    public static string DecodeUtf16Le(ReadOnlySpan<byte> data)
        => Utf16Le.GetString(TrimNulls(data, wide: true));

    /// <summary>解析开头的十进制数字，例如 "03/12" → 3。</summary>
    public static int ParseLeadingInt(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        int index = 0;
        while (index < text.Length && !char.IsAsciiDigit(text[index]))
        {
            index++;
        }

        int value = 0;
        int digits = 0;
        while (index < text.Length && char.IsAsciiDigit(text[index]) && digits < 9)
        {
            value = (value * 10) + (text[index] - '0');
            index++;
            digits++;
        }

        return value;
    }

    /// <summary>解析 "n/m" 形式的数字对。</summary>
    public static void ParsePair(string? text, out int first, out int second)
    {
        first = 0;
        second = 0;
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        int slash = text.IndexOf('/');
        if (slash < 0)
        {
            first = ParseLeadingInt(text);
            return;
        }

        first = ParseLeadingInt(text[..slash]);
        second = ParseLeadingInt(text[(slash + 1)..]);
    }

    /// <summary>解析年份：取开头 4 位数字。</summary>
    public static int ParseYear(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        for (int i = 0; i + 4 <= text.Length; i++)
        {
            if (char.IsAsciiDigit(text[i]) && char.IsAsciiDigit(text[i + 1])
                && char.IsAsciiDigit(text[i + 2]) && char.IsAsciiDigit(text[i + 3]))
            {
                int year = int.Parse(text.AsSpan(i, 4), CultureInfo.InvariantCulture);
                if (year is > 0 and < 3000)
                {
                    return year;
                }
            }
        }

        return 0;
    }

    /// <summary>解析布尔式标记（"1"、"true"、"yes"）。</summary>
    public static bool ParseBool(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        string value = text.Trim();
        return value.StartsWith('1')
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>按图片字节魔数推断 MIME。</summary>
    public static string SniffImageMime(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (data.Length >= 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
        {
            return "image/png";
        }

        if (data.Length >= 6 && data[0] == (byte)'G' && data[1] == (byte)'I' && data[2] == (byte)'F')
        {
            return "image/gif";
        }

        if (data.Length >= 2 && data[0] == (byte)'B' && data[1] == (byte)'M')
        {
            return "image/bmp";
        }

        if (data.Length >= 12 && data[0] == (byte)'R' && data[1] == (byte)'I' && data[2] == (byte)'F'
            && data[3] == (byte)'F' && data[8] == (byte)'W')
        {
            return "image/webp";
        }

        return string.Empty;
    }

    /// <summary>去除 ID3 unsynchronisation 填充（0xFF 0x00 → 0xFF）。</summary>
    public static byte[] RemoveUnsynchronisation(ReadOnlySpan<byte> data, bool strict)
    {
        byte[] result = new byte[data.Length];
        int written = 0;
        for (int i = 0; i < data.Length; i++)
        {
            byte value = data[i];
            result[written++] = value;
            if (value != 0xFF || i + 1 >= data.Length || data[i + 1] != 0x00)
            {
                continue;
            }

            if (!strict)
            {
                i++;
                continue;
            }

            // v2.4 仅在 0xFF 0x00 后紧跟 0x00 或 ≥0xE0 时才视为填充。
            if (i + 2 >= data.Length || data[i + 2] == 0x00 || data[i + 2] >= 0xE0)
            {
                i++;
            }
        }

        return written == result.Length ? result : result[..written];
    }

    /// <summary>解析常见图片 MIME 与扩展名映射。</summary>
    public static string MimeFromPictureFormat(string format)
    {
        string value = format.Trim().ToUpperInvariant();
        return value switch
        {
            "JPG" or "JPEG" => "image/jpeg",
            "PNG" => "image/png",
            "GIF" => "image/gif",
            "BMP" => "image/bmp",
            "WEBP" => "image/webp",
            _ => string.Empty,
        };
    }

    private static string DecodeUtf16(ReadOnlySpan<byte> body, bool? bigEndian)
    {
        bool big = bigEndian ?? false;
        if (body.Length >= 2 && body[0] == 0xFF && body[1] == 0xFE)
        {
            big = false;
            body = body[2..];
        }
        else if (body.Length >= 2 && body[0] == 0xFE && body[1] == 0xFF)
        {
            big = true;
            body = body[2..];
        }

        ReadOnlySpan<byte> trimmed = TrimNulls(body, wide: true);
        if (trimmed.Length < 2)
        {
            return string.Empty;
        }

        return (big ? Utf16Be : Utf16Le).GetString(trimmed);
    }

    private static ReadOnlySpan<byte> TrimNulls(ReadOnlySpan<byte> data, bool wide)
    {
        if (wide)
        {
            while (data.Length >= 2 && data[^1] == 0 && data[^2] == 0)
            {
                data = data[..^2];
            }

            return data;
        }

        while (data.Length > 0 && data[^1] == 0)
        {
            data = data[..^1];
        }

        return data;
    }
}
/// <summary>ID3v1 流派表（0..147）。</summary>
internal static class Id3Genres
{
    private static readonly string[] Names =
    [
        "Blues", "Classic Rock", "Country", "Dance", "Disco", "Funk", "Grunge", "Hip-Hop",
        "Jazz", "Metal", "New Age", "Oldies", "Other", "Pop", "R&B", "Rap",
        "Reggae", "Rock", "Techno", "Industrial", "Alternative", "Ska", "Death Metal", "Pranks",
        "Soundtrack", "Euro-Techno", "Ambient", "Trip-Hop", "Vocal", "Jazz+Funk", "Fusion", "Trance",
        "Classical", "Instrumental", "Acid", "House", "Game", "Sound Clip", "Gospel", "Noise",
        "AlternRock", "Bass", "Soul", "Punk", "Space", "Meditative", "Instrumental Pop", "Instrumental Rock",
        "Ethnic", "Gothic", "Darkwave", "Techno-Industrial", "Electronic", "Pop-Folk", "Eurodance", "Dream",
        "Southern Rock", "Comedy", "Cult", "Gangsta", "Top 40", "Christian Rap", "Pop/Funk", "Jungle",
        "Native American", "Cabaret", "New Wave", "Psychedelic", "Rave", "Showtunes", "Trailer", "Lo-Fi",
        "Tribal", "Acid Punk", "Acid Jazz", "Polka", "Retro", "Musical", "Rock & Roll", "Hard Rock",
        "Folk", "Folk-Rock", "National Folk", "Swing", "Fast Fusion", "Bebob", "Latin", "Revival",
        "Celtic", "Bluegrass", "Avantgarde", "Gothic Rock", "Progressive Rock", "Psychedelic Rock", "Symphonic Rock", "Slow Rock",
        "Big Band", "Chorus", "Easy Listening", "Acoustic", "Humour", "Speech", "Chanson", "Opera",
        "Chamber Music", "Sonata", "Symphony", "Booty Bass", "Primus", "Porn Groove", "Satire", "Slow Jam",
        "Club", "Tango", "Samba", "Folklore", "Ballad", "Power Ballad", "Rhythmic Soul", "Freestyle",
        "Duet", "Punk Rock", "Drum Solo", "A capella", "Euro-House", "Dance Hall", "Goa", "Drum & Bass",
        "Club-House", "Hardcore", "Terror", "Indie", "BritPop", "Negerpunk", "Polsk Punk", "Beat",
        "Christian Gangsta Rap", "Heavy Metal", "Black Metal", "Crossover", "Contemporary Christian", "Christian Rock", "Merengue", "Salsa",
        "Thrash Metal", "Anime", "JPop", "SynthPop",
    ];

    /// <summary>按流派号取英文名，越界返回空串。</summary>
    public static string Name(int index)
        => index >= 0 && index < Names.Length ? Names[index] : string.Empty;

    /// <summary>把 TCON 原始值解析为流派名：处理 (NN)、纯数字、RX/CR 与自由文本。</summary>
    public static string Resolve(string? raw)
    {
        string value = TagText.Clean(raw);
        if (value.Length == 0)
        {
            return string.Empty;
        }

        // TCON 可能是 NUL 分隔的多个流派，取第一个。
        int nul = value.IndexOf('\0');
        if (nul >= 0)
        {
            value = value[..nul].Trim();
        }

        if (value.Length == 0)
        {
            return string.Empty;
        }

        if (value.Equals("RX", StringComparison.OrdinalIgnoreCase))
        {
            return "Remix";
        }

        if (value.Equals("CR", StringComparison.OrdinalIgnoreCase))
        {
            return "Cover";
        }

        if (value[0] == '(')
        {
            int close = value.IndexOf(')');
            if (close > 1)
            {
                string inner = value[1..close];
                int number = TagText.ParseLeadingInt(inner);
                if (inner.Length > 0 && char.IsAsciiDigit(inner[0]))
                {
                    string name = Name(number);
                    if (name.Length > 0)
                    {
                        // "(17)Rock" 形式保留括号后的补充文本。
                        string rest = value[(close + 1)..].Trim();
                        return rest.Length > 0 ? rest : name;
                    }
                }
            }

            return value;
        }

        if (char.IsAsciiDigit(value[0]))
        {
            int number = TagText.ParseLeadingInt(value);
            string name = Name(number);
            if (name.Length > 0)
            {
                return name;
            }
        }

        return value;
    }
}

/// <summary>ID3v2（2.2 / 2.3 / 2.4）标签解析。</summary>
internal static class Id3v2Reader
{
    /// <summary>单次读取标签正文的字节上限（防止畸形头导致巨量读取）。</summary>
    private const int MaxTagBytes = 40 * 1024 * 1024;

    /// <summary>从指定偏移尝试解析 ID3v2 标签，失败静默返回。</summary>
    public static void Read(MediaTagBuilder builder, FileSliceSource file, long offset)
    {
        Span<byte> header = stackalloc byte[10];
        if (!file.TryRead(offset, header) || !TagBytes.IsAscii(header, "ID3"))
        {
            return;
        }

        int major = header[3];
        if (major is < 2 or > 4)
        {
            return;
        }

        byte flags = header[5];
        if (major == 2 && (flags & 0x40) != 0)
        {
            // v2.2 压缩标签无法处理。
            return;
        }

        int bodySize = TagBytes.Synchsafe(header[6..10]);
        if (bodySize <= 0)
        {
            return;
        }

        long bodyStart = offset + 10;
        if (bodyStart >= file.Length)
        {
            return;
        }

        long bodyEnd = Math.Min(bodyStart + bodySize, file.Length);
        if (bodyEnd - bodyStart > MaxTagBytes)
        {
            bodyEnd = bodyStart + MaxTagBytes;
        }

        bool hasExtendedHeader = major >= 3 && (flags & 0x40) != 0;
        bool unsynchronised = (flags & 0x80) != 0;

        // 主路径：在“未还原”的标签体上顺序解析帧（帧长度字段描述的是存储长度），
        // 逐帧做 unsynchronisation 还原——与主流解码器一致。
        ParseFrames(builder, major, bodyStart, bodyEnd, hasExtendedHeader, unsynchronised, (position, length) => file.ReadArray(position, length));

        // 兜底：少数编码器把整个标签体统一填充（帧长度描述的是还原后的长度），
        // 此时主路径会错位；仅在主路径一无所获时再按整段还原解析一次。
        if (unsynchronised && !builder.HasTextTag)
        {
            byte[] raw = file.ReadArray(bodyStart, (int)(bodyEnd - bodyStart));
            byte[] clean = TagText.RemoveUnsynchronisation(raw, strict: major == 4);
            MediaTagBuilder fallback = new();
            ParseFrames(fallback, major, 0, clean.Length, hasExtendedHeader, false, (position, length) => Slice(clean, position, length));
            builder.FillMissingFrom(fallback);
        }
    }

    private static byte[] Slice(byte[] source, long position, int length)
    {
        if (position < 0 || position >= source.Length)
        {
            return [];
        }

        int start = (int)position;
        int count = Math.Min(length, source.Length - start);
        return count <= 0 ? [] : source[start..(start + count)];
    }

    /// <summary>遍历帧头；只读取关心帧的正文。</summary>
    private static void ParseFrames(
        MediaTagBuilder builder,
        int major,
        long start,
        long end,
        bool extendedHeader,
        bool globalUnsync,
        Func<long, int, byte[]> read)
    {
        bool version22 = major == 2;
        int headerLength = version22 ? 6 : 10;
        long cursor = start;

        // 扩展头：v2.3 为 4 字节长度（不含自身）+6 字节；v2.4 为 synchsafe 长度（含自身）。
        if (extendedHeader && major == 3)
        {
            byte[] ext = read(start, 10);
            if (ext.Length >= 4)
            {
                int extendedSize = BinaryPrimitives.ReadInt32BigEndian(ext.AsSpan(0, 4));
                if (extendedSize is > 0 and < 1024 * 1024)
                {
                    cursor = start + 4 + extendedSize;
                }
            }
        }
        else if (extendedHeader && major == 4)
        {
            byte[] ext = read(start, 6);
            if (ext.Length >= 4)
            {
                int extendedSize = TagBytes.Synchsafe(ext.AsSpan(0, 4));
                if (extendedSize is > 0 and < 1024 * 1024)
                {
                    cursor = start + extendedSize;
                }
            }
        }

        byte[] headerBuffer = new byte[10];
        int guard = 0;
        while (cursor + headerLength <= end && guard++ < 4096)
        {
            byte[] head = read(cursor, headerLength);
            if (head.Length < headerLength)
            {
                break;
            }

            Array.Copy(head, headerBuffer, headerLength);

            bool padding = true;
            int idLength = version22 ? 3 : 4;
            for (int i = 0; i < idLength; i++)
            {
                if (headerBuffer[i] != 0)
                {
                    padding = false;
                    break;
                }
            }

            if (padding)
            {
                break;
            }

            string id = TagBytes.Latin1Text(headerBuffer.AsSpan(0, idLength));
            int size;
            int frameFlags = 0;
            if (version22)
            {
                size = TagBytes.UInt24BigEndian(headerBuffer.AsSpan(3, 3));
            }
            else if (major == 3)
            {
                size = BinaryPrimitives.ReadInt32BigEndian(headerBuffer.AsSpan(4, 4));
                frameFlags = (headerBuffer[8] << 8) | headerBuffer[9];
            }
            else
            {
                size = TagBytes.Synchsafe(headerBuffer.AsSpan(4, 4));
                frameFlags = (headerBuffer[8] << 8) | headerBuffer[9];
            }

            if (size <= 0)
            {
                break;
            }

            long bodyPosition = cursor + headerLength;
            if (bodyPosition + size > end)
            {
                // 帧被截断，停止解析。
                break;
            }

            bool compressed = false;
            bool encrypted = false;
            bool grouped = false;
            bool frameUnsync = false;
            bool hasDataLength = false;
            if (major == 3)
            {
                compressed = (frameFlags & 0x0080) != 0;
                encrypted = (frameFlags & 0x0040) != 0;
                grouped = (frameFlags & 0x0020) != 0;
            }
            else if (major == 4)
            {
                grouped = (frameFlags & 0x0040) != 0;
                compressed = (frameFlags & 0x0008) != 0;
                encrypted = (frameFlags & 0x0004) != 0;
                frameUnsync = (frameFlags & 0x0002) != 0;
                hasDataLength = (frameFlags & 0x0001) != 0;
            }

            if (compressed || encrypted || !IsWantedFrame(id))
            {
                cursor = bodyPosition + size;
                continue;
            }

            // 过大的封面直接跳过，避免把数十兆字节读进内存。
            if ((id is "APIC" or "PIC") && size > MediaTagReader.MaxCoverBytes + 1024)
            {
                cursor = bodyPosition + size;
                continue;
            }

            byte[] body = read(bodyPosition, size);
            if (body.Length < size)
            {
                break;
            }

            int skip = (grouped ? 1 : 0) + (hasDataLength ? 4 : 0);
            if (skip > 0)
            {
                body = skip >= body.Length ? [] : body[skip..];
            }

            if (frameUnsync || globalUnsync)
            {
                body = TagText.RemoveUnsynchronisation(body, strict: major == 4);
            }

            if (body.Length > 0)
            {
                ProcessFrame(builder, major, id, body);
            }

            cursor = bodyPosition + size;
        }
    }

    private static bool IsWantedFrame(string id) => id switch
    {
        "TIT2" or "TT2" or "TPE1" or "TP1" or "TALB" or "TAL" or "TPE2" or "TP2"
            or "TCON" or "TCO" or "TCOM" or "TCM" or "TIT1" or "TT1" or "TRCK" or "TRK"
            or "TPOS" or "TPA" or "TYER" or "TYE" or "TDRC" or "TDRL" or "TBPM" or "TBP"
            or "TCMP" or "TCP" or "COMM" or "COM" or "USLT" or "ULT" or "APIC" or "PIC" => true,
        _ => false,
    };

    private static void ProcessFrame(MediaTagBuilder builder, int major, string id, byte[] body)
    {
        switch (id)
        {
            case "TIT2":
            case "TT2":
                builder.SetTitle(TagText.DecodeTextFrame(body));
                break;
            case "TPE1":
            case "TP1":
                builder.SetArtist(TagText.DecodeTextFrame(body));
                break;
            case "TALB":
            case "TAL":
                builder.SetAlbum(TagText.DecodeTextFrame(body));
                break;
            case "TPE2":
            case "TP2":
                builder.SetAlbumArtist(TagText.DecodeTextFrame(body));
                break;
            case "TCON":
            case "TCO":
                builder.SetGenre(Id3Genres.Resolve(TagText.DecodeTextFrame(body)));
                break;
            case "TCOM":
            case "TCM":
                builder.SetComposer(TagText.DecodeTextFrame(body));
                break;
            case "TIT1":
            case "TT1":
                builder.SetGrouping(TagText.DecodeTextFrame(body));
                break;
            case "TRCK":
            case "TRK":
            {
                TagText.ParsePair(TagText.DecodeTextFrame(body), out int track, out int total);
                builder.SetTrackNumber(track);
                builder.SetTotalTracks(total);
                break;
            }

            case "TPOS":
            case "TPA":
            {
                TagText.ParsePair(TagText.DecodeTextFrame(body), out int disc, out int discs);
                builder.SetDiscNumber(disc);
                builder.SetTotalDiscs(discs);
                break;
            }

            case "TYER":
            case "TYE":
            case "TDRC":
            case "TDRL":
                builder.SetYear(TagText.ParseYear(TagText.DecodeTextFrame(body)));
                break;
            case "TBPM":
            case "TBP":
                builder.SetBpm(TagText.ParseLeadingInt(TagText.DecodeTextFrame(body)));
                break;
            case "TCMP":
            case "TCP":
                builder.SetCompilation(TagText.ParseBool(TagText.DecodeTextFrame(body)));
                break;
            case "COMM":
            case "COM":
                builder.SetComment(ParseComment(body));
                break;
            case "USLT":
            case "ULT":
                builder.SetLyrics(ParseLyrics(body));
                break;
            case "APIC":
            case "PIC":
                ParsePicture(builder, id, body);
                break;
        }
    }

    /// <summary>COMM：编码(1) + 语言(3) + 描述(NUL 结尾) + 正文。</summary>
    private static string ParseComment(byte[] body)
    {
        if (body.Length < 4)
        {
            return string.Empty;
        }

        byte encoding = body[0];
        ReadOnlySpan<byte> rest = body.AsSpan(4);
        int skipped = TagText.SkipNullTerminated(rest, encoding);
        return skipped >= rest.Length ? string.Empty : TagText.DecodeWithEncoding(encoding, rest[skipped..]);
    }

    /// <summary>USLT：编码(1) + 语言(3) + 内容描述(NUL 结尾) + 歌词。</summary>
    private static string ParseLyrics(byte[] body)
    {
        if (body.Length < 4)
        {
            return string.Empty;
        }

        byte encoding = body[0];
        ReadOnlySpan<byte> rest = body.AsSpan(4);
        int skipped = TagText.SkipNullTerminated(rest, encoding);
        return skipped >= rest.Length ? string.Empty : TagText.DecodeWithEncoding(encoding, rest[skipped..]);
    }

    /// <summary>APIC / PIC：取第一张封面。</summary>
    private static void ParsePicture(MediaTagBuilder builder, string id, byte[] body)
    {
        if (builder.CoverImage is not null || body.Length < 5)
        {
            return;
        }

        byte encoding = body[0];
        if (id == "PIC")
        {
            string format = TagBytes.Latin1Text(body.AsSpan(1, 3));
            int position = 5;
            if (position > body.Length)
            {
                return;
            }

            int skipped = TagText.SkipNullTerminated(body.AsSpan(position), encoding);
            position += skipped;
            if (position >= body.Length)
            {
                return;
            }

            builder.SetCover(body[position..], TagText.MimeFromPictureFormat(format));
            return;
        }

        int mimeEnd = Array.IndexOf(body, (byte)0, 1);
        if (mimeEnd < 0)
        {
            return;
        }

        string mime = TagBytes.Latin1Text(body.AsSpan(1, mimeEnd - 1));
        int cursor = mimeEnd + 2;
        if (cursor > body.Length)
        {
            return;
        }

        cursor += TagText.SkipNullTerminated(body.AsSpan(cursor), encoding);
        if (cursor >= body.Length)
        {
            return;
        }

        builder.SetCover(body[cursor..], mime);
    }
}

/// <summary>ID3v1 / ID3v1.1 回退解析（文件末尾 128 字节）。</summary>
internal static class Id3v1Reader
{
    public static void Read(MediaTagBuilder builder, FileSliceSource file)
    {
        if (file.Length < 128)
        {
            return;
        }

        Span<byte> buffer = stackalloc byte[128];
        if (!file.TryRead(file.Length - 128, buffer))
        {
            return;
        }

        if (buffer[0] != (byte)'T' || buffer[1] != (byte)'A' || buffer[2] != (byte)'G')
        {
            return;
        }

        builder.SetTitle(TagText.DecodeUnknown(buffer[3..33]));
        builder.SetArtist(TagText.DecodeUnknown(buffer[33..63]));
        builder.SetAlbum(TagText.DecodeUnknown(buffer[63..93]));
        builder.SetYear(TagText.ParseYear(TagText.DecodeUnknown(buffer[93..97])));

        // ID3v1.1：备注 28 字节 + 0x00 + 音轨号。
        bool version11 = buffer[125] == 0 && buffer[126] != 0;
        int commentEnd = version11 ? 125 : 127;
        builder.SetComment(TagText.DecodeUnknown(buffer[97..commentEnd]));
        if (version11)
        {
            builder.SetTrackNumber(buffer[126]);
        }

        byte genre = buffer[127];
        if (genre < 148)
        {
            builder.SetGenre(Id3Genres.Name(genre));
        }
    }
}

/// <summary>MPEG 音频帧头信息。</summary>
internal readonly record struct MpegFrameInfo(
    int Version,
    int Layer,
    int BitrateKbps,
    int SampleRate,
    int Channels,
    int SamplesPerFrame,
    int FrameLength,
    int SideInfoSize);

/// <summary>MP3 / AAC(ADTS 之外) 的时长与比特率估算。</summary>
internal static class MpegAudioReader
{
    private static readonly int[] Mpeg1Layer1 = [0, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448];
    private static readonly int[] Mpeg1Layer2 = [0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384];
    private static readonly int[] Mpeg1Layer3 = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];
    private static readonly int[] Mpeg2Layer1 = [0, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256];
    private static readonly int[] Mpeg2Layer2And3 = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160];
    private static readonly int[] SampleRatesMpeg1 = [44100, 48000, 32000];
    private static readonly int[] SampleRatesMpeg2 = [22050, 24000, 16000];
    private static readonly int[] SampleRatesMpeg25 = [11025, 12000, 8000];

    public static void Read(MediaTagBuilder builder, FileSliceSource file)
    {
        long searchStart = 0;
        Span<byte> id3 = stackalloc byte[10];
        if (file.TryRead(0, id3) && TagBytes.IsAscii(id3, "ID3"))
        {
            searchStart = 10 + TagBytes.Synchsafe(id3[6..10]);
            if ((id3[5] & 0x10) != 0)
            {
                searchStart += 10;
            }
        }

        long frameStart = FindFirstFrame(file, searchStart, out MpegFrameInfo frame);
        if (frameStart < 0)
        {
            return;
        }

        builder.SetSampleRate(frame.SampleRate);
        builder.SetChannels(frame.Channels);
        builder.SetBitrateKbps(frame.BitrateKbps);

        if (TryReadVbrHeaders(file, frameStart, frame, out int frames, out long streamBytes))
        {
            double seconds = (double)frames * frame.SamplesPerFrame / frame.SampleRate;
            builder.SetDuration(TimeSpan.FromSeconds(seconds));
            if (seconds > 0.1)
            {
                if (streamBytes <= 0)
                {
                    streamBytes = file.Length - frameStart;
                }

                builder.SetBitrateKbps((int)Math.Round(streamBytes * 8.0 / seconds / 1000.0), force: true);
            }

            return;
        }

        if (frame.BitrateKbps > 0)
        {
            double seconds = (file.Length - frameStart) * 8.0 / (frame.BitrateKbps * 1000.0);
            builder.SetDuration(TimeSpan.FromSeconds(seconds));
        }
    }

    /// <summary>在前 1 MiB 内寻找连续两个合法帧头，返回首帧偏移。</summary>
    private static long FindFirstFrame(FileSliceSource file, long start, out MpegFrameInfo frame)
    {
        frame = default;
        const int ChunkSize = 64 * 1024;
        const long MaxScan = 1024 * 1024;
        byte[] buffer = new byte[ChunkSize + 8];
        byte[] probe = new byte[4];
        long position = Math.Max(0, start);
        long scanned = 0;

        while (position < file.Length && scanned < MaxScan)
        {
            int read = file.Read(position, buffer);
            if (read < 4)
            {
                break;
            }

            for (int i = 0; i + 4 <= read; i++)
            {
                if (!TryParseFrameHeader(buffer.AsSpan(i, 4), out MpegFrameInfo candidate))
                {
                    continue;
                }

                int next = i + candidate.FrameLength;
                if (next + 4 <= read)
                {
                    if (!TryParseFrameHeader(buffer.AsSpan(next, 4), out _))
                    {
                        continue;
                    }
                }
                else if (position + next < file.Length - 4)
                {
                    if (!file.TryRead(position + next, probe) || !TryParseFrameHeader(probe, out _))
                    {
                        continue;
                    }
                }

                frame = candidate;
                return position + i;
            }

            if (read < 4)
            {
                break;
            }

            position += read - 3;
            scanned += read - 3;
            if (read < buffer.Length)
            {
                break;
            }
        }

        return -1;
    }

    private static bool TryParseFrameHeader(ReadOnlySpan<byte> data, out MpegFrameInfo info)
    {
        info = default;
        if (data.Length < 4)
        {
            return false;
        }

        uint header = BinaryPrimitives.ReadUInt32BigEndian(data[..4]);
        if ((header & 0xFFE00000) != 0xFFE00000)
        {
            return false;
        }

        int versionBits = (int)((header >> 19) & 0x3);
        int layerBits = (int)((header >> 17) & 0x3);
        int bitrateIndex = (int)((header >> 12) & 0xF);
        int sampleRateIndex = (int)((header >> 10) & 0x3);
        int padding = (int)((header >> 9) & 0x1);
        int channelMode = (int)((header >> 6) & 0x3);

        if (versionBits == 1 || layerBits == 0 || bitrateIndex is 0 or 15 || sampleRateIndex == 3)
        {
            return false;
        }

        int version = versionBits == 3 ? 1 : versionBits == 2 ? 2 : 25;
        int layer = 4 - layerBits;

        int bitrate;
        if (version == 1)
        {
            bitrate = layer switch
            {
                1 => Mpeg1Layer1[bitrateIndex],
                2 => Mpeg1Layer2[bitrateIndex],
                _ => Mpeg1Layer3[bitrateIndex],
            };
        }
        else
        {
            bitrate = layer == 1 ? Mpeg2Layer1[bitrateIndex] : Mpeg2Layer2And3[bitrateIndex];
        }

        if (bitrate <= 0)
        {
            return false;
        }

        int sampleRate = version switch
        {
            1 => SampleRatesMpeg1[sampleRateIndex],
            2 => SampleRatesMpeg2[sampleRateIndex],
            _ => SampleRatesMpeg25[sampleRateIndex],
        };

        int samplesPerFrame = layer switch
        {
            1 => 384,
            2 => 1152,
            _ => version == 1 ? 1152 : 576,
        };

        int frameLength = ((samplesPerFrame / 8) * bitrate * 1000 / sampleRate) + padding;
        if (frameLength <= 4)
        {
            return false;
        }

        int channels = channelMode == 3 ? 1 : 2;
        int sideInfo = layer == 1
            ? channels == 1 ? 17 : 32
            : version == 1
                ? channels == 1 ? 17 : 32
                : channels == 1 ? 9 : 17;

        info = new MpegFrameInfo(version, layer, bitrate, sampleRate, channels, samplesPerFrame, frameLength, sideInfo);
        return true;
    }

    /// <summary>读取 Xing/Info 或 VBRI 头；仅在 Layer 3 下有效。</summary>
    private static bool TryReadVbrHeaders(FileSliceSource file, long frameStart, in MpegFrameInfo frame, out int frames, out long streamBytes)
    {
        frames = 0;
        streamBytes = 0;
        if (frame.Layer != 3)
        {
            return false;
        }

        Span<byte> probe = stackalloc byte[16];
        long xingPosition = frameStart + 4 + frame.SideInfoSize;
        if (file.TryRead(xingPosition, probe)
            && (TagBytes.IsAscii(probe, "Xing") || TagBytes.IsAscii(probe, "Info")))
        {
            uint flags = BinaryPrimitives.ReadUInt32BigEndian(probe[4..8]);
            Span<byte> value = stackalloc byte[8];
            int cursor = 8;
            if ((flags & 0x1) != 0)
            {
                if (!file.TryRead(xingPosition + cursor, value[..4]))
                {
                    return false;
                }

                frames = (int)BinaryPrimitives.ReadUInt32BigEndian(value[..4]);
                cursor += 4;
            }

            if ((flags & 0x2) != 0 && file.TryRead(xingPosition + cursor, value[..4]))
            {
                streamBytes = BinaryPrimitives.ReadUInt32BigEndian(value[..4]);
            }

            return frames > 0;
        }

        // Fraunhofer VBRI 固定位于首帧后 36 字节。
        if (file.TryRead(frameStart + 36, probe) && TagBytes.IsAscii(probe, "VBRI"))
        {
            Span<byte> vbri = stackalloc byte[18];
            if (file.TryRead(frameStart + 36, vbri))
            {
                streamBytes = BinaryPrimitives.ReadUInt32BigEndian(vbri[10..14]);
                frames = (int)BinaryPrimitives.ReadUInt32BigEndian(vbri[14..18]);
                return frames > 0;
            }
        }

        return false;
    }
}
/// <summary>MP4 原子引用。</summary>
internal readonly record struct AtomRef(string Type, long DataPos, long DataLength);

/// <summary>MP4 / M4A / MOV 原子解析（moov/udta/meta/ilst）。</summary>
internal static class Mp4Reader
{
    public static void Read(MediaTagBuilder builder, FileSliceSource file)
    {
        long position = 0;
        int guard = 0;
        while (position + 8 <= file.Length && guard++ < 512)
        {
            if (!TryReadAtomHeader(file, position, file.Length, out AtomRef atom, out long atomEnd))
            {
                break;
            }

            if (atom.Type == "moov")
            {
                ParseMoov(builder, file, atom.DataPos, atom.DataPos + atom.DataLength);
            }

            if (atomEnd <= position)
            {
                break;
            }

            position = atomEnd;
        }
    }

    /// <summary>顺序读取原子头（支持 64 位长度与 size==0 到文件尾）。</summary>
    private static bool TryReadAtomHeader(FileSliceSource file, long position, long limit, out AtomRef atom, out long atomEnd)
    {
        atom = default;
        atomEnd = 0;
        Span<byte> header = stackalloc byte[16];
        if (!file.TryRead(position, header[..8]))
        {
            return false;
        }

        long size = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
        string type = TagBytes.Latin1Text(header[4..8]);
        int headerLength = 8;
        if (size == 1)
        {
            if (!file.TryRead(position + 8, header[8..16]))
            {
                return false;
            }

            size = (long)BinaryPrimitives.ReadUInt64BigEndian(header[8..16]);
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

        long dataPos = position + headerLength;
        long dataLength = size - headerLength;
        if (dataPos + dataLength > limit)
        {
            dataLength = limit - dataPos;
        }

        if (dataLength < 0)
        {
            return false;
        }

        atom = new AtomRef(type, dataPos, dataLength);
        atomEnd = position + size;
        if (atomEnd > limit)
        {
            atomEnd = limit;
        }

        return true;
    }

    /// <summary>枚举区间内的直接子原子。</summary>
    private static List<AtomRef> Children(FileSliceSource file, long start, long end)
    {
        List<AtomRef> children = [];
        long position = start;
        int guard = 0;
        while (position + 8 <= end && guard++ < 4096)
        {
            if (!TryReadAtomHeader(file, position, end, out AtomRef atom, out long atomEnd))
            {
                break;
            }

            if (atom.DataPos + atom.DataLength > end)
            {
                break;
            }

            children.Add(atom);
            if (atomEnd <= position)
            {
                break;
            }

            position = atomEnd;
        }

        return children;
    }

    private static void ParseMoov(MediaTagBuilder builder, FileSliceSource file, long start, long end)
    {
        foreach (AtomRef atom in Children(file, start, end))
        {
            switch (atom.Type)
            {
                case "mvhd":
                    ParseMvhd(builder, file, atom.DataPos, atom.DataLength);
                    break;
                case "trak":
                    ParseTrak(builder, file, atom.DataPos, atom.DataPos + atom.DataLength);
                    break;
                case "udta":
                    ParseUdta(builder, file, atom.DataPos, atom.DataPos + atom.DataLength);
                    break;
            }
        }
    }

    private static void ParseMvhd(MediaTagBuilder builder, FileSliceSource file, long position, long length)
    {
        if (length < 20)
        {
            return;
        }

        Span<byte> version = stackalloc byte[4];
        if (!file.TryRead(position, version))
        {
            return;
        }

        Span<byte> buffer = stackalloc byte[12];
        if (version[0] == 1)
        {
            // version/flags(4) + created(8) + modified(8) + timescale(4) + duration(8)
            Span<byte> wide = stackalloc byte[12];
            if (!file.TryRead(position + 20, wide))
            {
                return;
            }

            uint timescale = BinaryPrimitives.ReadUInt32BigEndian(wide[..4]);
            ulong duration = BinaryPrimitives.ReadUInt64BigEndian(wide[4..12]);
            ApplyDuration(builder, timescale, duration);
            return;
        }

        // version/flags(4) + created(4) + modified(4) + timescale(4) + duration(4)
        if (!file.TryRead(position + 12, buffer))
        {
            return;
        }

        uint scale = BinaryPrimitives.ReadUInt32BigEndian(buffer[..4]);
        uint value = BinaryPrimitives.ReadUInt32BigEndian(buffer[4..8]);
        ApplyDuration(builder, scale, value);
    }

    private static void ApplyDuration(MediaTagBuilder builder, uint timescale, ulong duration)
    {
        if (timescale == 0 || duration == 0 || duration == uint.MaxValue || duration == ulong.MaxValue)
        {
            return;
        }

        builder.SetDuration(TimeSpan.FromSeconds((double)duration / timescale));
    }

    private static void ParseUdta(MediaTagBuilder builder, FileSliceSource file, long start, long end)
    {
        foreach (AtomRef atom in Children(file, start, end))
        {
            if (atom.Type == "meta")
            {
                ParseMeta(builder, file, atom.DataPos, atom.DataPos + atom.DataLength);
            }
        }
    }

    /// <summary>meta 可能是完整盒（4 字节版本+标志），也可能没有；两种布局都识别。</summary>
    private static void ParseMeta(MediaTagBuilder builder, FileSliceSource file, long start, long end)
    {
        long childStart = start;
        Span<byte> probe = stackalloc byte[8];
        if (file.TryRead(start, probe))
        {
            string first = TagBytes.Latin1Text(probe[4..8]);
            bool plain = first is "hdlr" or "ilst" or "keys" or "mdta" or "free";
            childStart = plain ? start : start + 4;
        }

        if (childStart >= end)
        {
            return;
        }

        foreach (AtomRef atom in Children(file, childStart, end))
        {
            if (atom.Type == "ilst")
            {
                ParseIlst(builder, file, atom.DataPos, atom.DataPos + atom.DataLength);
            }
        }
    }

    private static void ParseIlst(MediaTagBuilder builder, FileSliceSource file, long start, long end)
    {
        foreach (AtomRef item in Children(file, start, end))
        {
            foreach (AtomRef data in Children(file, item.DataPos, item.DataPos + item.DataLength))
            {
                // data 原子的负载从 DataPos 起为：版本(1)+类型(3) + locale(4) + 负载。
                if (data.Type != "data" || data.DataLength < 8)
                {
                    continue;
                }

                Span<byte> head = stackalloc byte[8];
                if (!file.TryRead(data.DataPos, head))
                {
                    break;
                }

                int dataType = head[3];
                byte[] payload = file.ReadArray(data.DataPos + 8, (int)Math.Min(data.DataLength - 8, int.MaxValue));
                if (payload.Length == 0)
                {
                    continue;
                }

                ApplyItem(builder, item.Type, dataType, payload);
                break;
            }
        }
    }

    private static void ApplyItem(MediaTagBuilder builder, string type, int dataType, byte[] payload)
    {
        switch (type)
        {
            case "\u00A9nam":
                builder.SetTitle(DecodeIlstText(dataType, payload));
                break;
            case "\u00A9ART":
                builder.SetArtist(DecodeIlstText(dataType, payload));
                break;
            case "aART":
                builder.SetAlbumArtist(DecodeIlstText(dataType, payload));
                break;
            case "\u00A9alb":
                builder.SetAlbum(DecodeIlstText(dataType, payload));
                break;
            case "\u00A9gen":
                builder.SetGenre(DecodeIlstText(dataType, payload));
                break;
            case "gnre":
                if (payload.Length >= 2)
                {
                    builder.SetGenre(Id3Genres.Name(BinaryPrimitives.ReadUInt16BigEndian(payload) - 1));
                }

                break;
            case "\u00A9wrt":
                builder.SetComposer(DecodeIlstText(dataType, payload));
                break;
            case "\u00A9cmt":
                builder.SetComment(DecodeIlstText(dataType, payload));
                break;
            case "\u00A9grp":
                builder.SetGrouping(DecodeIlstText(dataType, payload));
                break;
            case "\u00A9lyr":
                builder.SetLyrics(DecodeIlstText(dataType, payload));
                break;
            case "\u00A9day":
                builder.SetYear(TagText.ParseYear(DecodeIlstText(dataType, payload)));
                break;
            case "trkn":
                if (payload.Length >= 6)
                {
                    builder.SetTrackNumber(BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(2, 2)));
                    builder.SetTotalTracks(BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(4, 2)));
                }

                break;
            case "disk":
                if (payload.Length >= 6)
                {
                    builder.SetDiscNumber(BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(2, 2)));
                    builder.SetTotalDiscs(BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(4, 2)));
                }

                break;
            case "tmpo":
                if (payload.Length >= 2)
                {
                    builder.SetBpm(BinaryPrimitives.ReadUInt16BigEndian(payload));
                }

                break;
            case "cpil":
                builder.SetCompilation(payload[0] != 0);
                break;
            case "covr":
            {
                string mime = dataType switch
                {
                    13 => "image/jpeg",
                    14 => "image/png",
                    _ => TagText.SniffImageMime(payload),
                };
                builder.SetCover(payload, mime);
                break;
            }
        }
    }

    private static string DecodeIlstText(int dataType, byte[] payload)
        => dataType == 2 ? TagText.DecodeWithEncoding(2, payload) : TagText.DecodeUnknown(payload);

    private static void ParseTrak(MediaTagBuilder builder, FileSliceSource file, long start, long end)
    {
        string handler = string.Empty;
        long stsdPos = -1;
        long stsdLength = 0;

        foreach (AtomRef atom in Children(file, start, end))
        {
            if (atom.Type != "mdia")
            {
                continue;
            }

            foreach (AtomRef media in Children(file, atom.DataPos, atom.DataPos + atom.DataLength))
            {
                if (media.Type == "hdlr")
                {
                    handler = ReadHandlerType(file, media.DataPos, media.DataLength);
                }
                else if (media.Type == "minf")
                {
                    foreach (AtomRef info in Children(file, media.DataPos, media.DataPos + media.DataLength))
                    {
                        if (info.Type != "stbl")
                        {
                            continue;
                        }

                        foreach (AtomRef table in Children(file, info.DataPos, info.DataPos + info.DataLength))
                        {
                            if (table.Type == "stsd")
                            {
                                stsdPos = table.DataPos;
                                stsdLength = table.DataLength;
                            }
                        }
                    }
                }
            }
        }

        if (handler == "soun" && stsdPos >= 0)
        {
            ParseStsd(builder, file, stsdPos, stsdLength);
        }
    }

    private static string ReadHandlerType(FileSliceSource file, long position, long length)
    {
        // version/flags(4) + pre_defined(4) + handler_type(4)
        if (length < 12)
        {
            return string.Empty;
        }

        Span<byte> buffer = stackalloc byte[4];
        return file.TryRead(position + 8, buffer) ? TagBytes.Latin1Text(buffer) : string.Empty;
    }

    /// <summary>从 stsd 中取声道数、采样率与 esds 平均比特率。</summary>
    private static void ParseStsd(MediaTagBuilder builder, FileSliceSource file, long position, long length)
    {
        if (length < 16)
        {
            return;
        }

        Span<byte> head = stackalloc byte[8];
        if (!file.TryRead(position, head))
        {
            return;
        }

        int entries = BinaryPrimitives.ReadInt32BigEndian(head[4..8]);
        if (entries <= 0 || entries > 64)
        {
            entries = 1;
        }

        long cursor = position + 8;
        int guard = 0;
        Span<byte> sample = stackalloc byte[28];
        while (guard++ < entries)
        {
            if (!TryReadAtomHeader(file, cursor, position + length, out AtomRef entry, out long entryEnd))
            {
                return;
            }

            // AudioSampleEntry：reserved(6)+dataRefIndex(2)+version(2)+revision(2)+vendor(4)
            //                   +channels(2)+sampleSize(2)+compression(2)+packetSize(2)+sampleRate(4)
            if (entry.DataLength >= 28 && file.TryRead(entry.DataPos, sample))
            {
                int channels = BinaryPrimitives.ReadUInt16BigEndian(sample[16..18]);
                uint sampleRate = BinaryPrimitives.ReadUInt32BigEndian(sample[24..28]);
                builder.SetChannels(channels);
                if (sampleRate > 0)
                {
                    builder.SetSampleRate((int)(sampleRate >> 16));
                }

                if (entry.DataLength > 28)
                {
                    foreach (AtomRef child in Children(file, entry.DataPos + 28, entry.DataPos + entry.DataLength))
                    {
                        if (child.Type == "esds")
                        {
                            ParseEsds(builder, file, child.DataPos, child.DataLength);
                        }
                    }
                }
            }

            if (entryEnd <= cursor)
            {
                return;
            }

            cursor = entryEnd;
        }
    }

    /// <summary>esds：定位 DecoderConfigDescriptor(0x04)，取其平均比特率。</summary>
    private static void ParseEsds(MediaTagBuilder builder, FileSliceSource file, long position, long length)
    {
        if (length < 8 || length > 4096)
        {
            return;
        }

        byte[] data = file.ReadArray(position, (int)length);
        int cursor = 4; // version/flags
        int guard = 0;
        while (cursor < data.Length && guard++ < 32)
        {
            if (!TryReadDescriptorHeader(data, ref cursor, out byte tag, out int size))
            {
                return;
            }

            if (tag == 0x03)
            {
                // ES_Descriptor：跳过 ES_ID(2) 与 flags(1) 及可选字段，交给下一轮继续扫描。
                if (size < 3)
                {
                    return;
                }

                cursor += 3;
                if (cursor <= data.Length && (data[cursor - 1] & 0x80) != 0)
                {
                    cursor += 2; // streamDependenceFlag
                }

                if (cursor <= data.Length && (data[cursor - 1] & 0x40) != 0)
                {
                    cursor += 1; // URL_Flag
                }

                if (cursor <= data.Length && (data[cursor - 1] & 0x20) != 0)
                {
                    cursor += 2; // OCRstreamFlag
                }

                continue;
            }

            if (tag == 0x04 && size >= 13 && cursor + size <= data.Length)
            {
                int average = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(cursor + 9, 4));
                builder.SetBitrateKbps((int)Math.Round(average / 1000.0));
                return;
            }

            if (size <= 0 || cursor + size > data.Length)
            {
                return;
            }

            cursor += size;
        }
    }

    private static bool TryReadDescriptorHeader(ReadOnlySpan<byte> data, ref int cursor, out byte tag, out int size)
    {
        tag = 0;
        size = 0;
        if (cursor >= data.Length)
        {
            return false;
        }

        tag = data[cursor++];
        int length = 0;
        int count = 0;
        while (cursor < data.Length && count < 4)
        {
            byte value = data[cursor++];
            length = (length << 7) | (value & 0x7F);
            count++;
            if ((value & 0x80) == 0)
            {
                break;
            }
        }

        size = length;
        return count > 0;
    }
}

/// <summary>FLAC 元数据块解析。</summary>
internal static class FlacReader
{
    public static void Read(MediaTagBuilder builder, FileSliceSource file)
    {
        Span<byte> marker = stackalloc byte[4];
        if (!file.TryRead(0, marker) || !TagBytes.IsAscii(marker, "fLaC"))
        {
            return;
        }

        long position = 4;
        long audioStart = file.Length;
        bool last = false;
        int guard = 0;
        Span<byte> head = stackalloc byte[4];
        while (!last && guard++ < 1024 && position + 4 <= file.Length)
        {
            if (!file.TryRead(position, head))
            {
                break;
            }

            last = (head[0] & 0x80) != 0;
            int type = head[0] & 0x7F;
            int length = TagBytes.UInt24BigEndian(head[1..4]);
            long dataPos = position + 4;
            if (dataPos + length > file.Length)
            {
                length = (int)Math.Max(0, file.Length - dataPos);
            }

            switch (type)
            {
                case 0:
                    ParseStreamInfo(builder, file, dataPos, length);
                    break;
                case 4 when length is > 0 and <= 4 * 1024 * 1024:
                    VorbisComment.Parse(builder, file.ReadArray(dataPos, length));
                    break;
                case 6 when length is > 0 and <= MediaTagReader.MaxCoverBytes + 4096:
                    FlacPicture.Parse(builder, file.ReadArray(dataPos, length));
                    break;
            }

            position = dataPos + length;
            audioStart = position;
        }

        double seconds = builder.Duration.TotalSeconds;
        if (seconds > 0.5 && file.Length > audioStart)
        {
            builder.SetBitrateKbps((int)Math.Round((file.Length - audioStart) * 8.0 / seconds / 1000.0));
        }
    }

    private static void ParseStreamInfo(MediaTagBuilder builder, FileSliceSource file, long position, int length)
    {
        if (length < 18)
        {
            return;
        }

        // min/max block(4) + min/max frame(6) = 10 字节后为 20 位采样率 / 3 位声道 / 5 位位深 / 36 位总样本数。
        Span<byte> buffer = stackalloc byte[18];
        if (!file.TryRead(position, buffer))
        {
            return;
        }

        ulong packed = BinaryPrimitives.ReadUInt64BigEndian(buffer[10..18]);
        int sampleRate = (int)((packed >> 44) & 0xFFFFF);
        int channels = (int)((packed >> 41) & 0x7) + 1;
        long totalSamples = (long)(packed & 0xF_FFFF_FFFF);

        builder.SetSampleRate(sampleRate);
        builder.SetChannels(channels);
        if (sampleRate > 0 && totalSamples > 0)
        {
            builder.SetDuration(TimeSpan.FromSeconds((double)totalSamples / sampleRate));
        }
    }
}

/// <summary>FLAC PICTURE 块（也用于 Ogg 的 METADATA_BLOCK_PICTURE）。</summary>
internal static class FlacPicture
{
    public static void Parse(MediaTagBuilder builder, ReadOnlySpan<byte> data)
    {
        if (builder.CoverImage is not null || data.Length < 32)
        {
            return;
        }

        int cursor = 4; // picture type
        if (!TryReadUInt32(data, ref cursor, out uint mimeLength) || mimeLength > (uint)data.Length - (uint)cursor)
        {
            return;
        }

        string mime = TagBytes.Latin1Text(data.Slice(cursor, (int)mimeLength));
        cursor += (int)mimeLength;

        if (!TryReadUInt32(data, ref cursor, out uint descriptionLength) || descriptionLength > (uint)data.Length - (uint)cursor)
        {
            return;
        }

        cursor += (int)descriptionLength;

        // width(4) + height(4) + depth(4) + colors(4)
        if (!TryReadUInt32(data, ref cursor, out _) || !TryReadUInt32(data, ref cursor, out _)
            || !TryReadUInt32(data, ref cursor, out _) || !TryReadUInt32(data, ref cursor, out _))
        {
            return;
        }

        if (!TryReadUInt32(data, ref cursor, out uint dataLength) || dataLength == 0)
        {
            return;
        }

        if (dataLength > (uint)(data.Length - cursor) || dataLength > MediaTagReader.MaxCoverBytes)
        {
            return;
        }

        builder.SetCover(data.Slice(cursor, (int)dataLength).ToArray(), mime);
    }

    private static bool TryReadUInt32(ReadOnlySpan<byte> data, ref int cursor, out uint value)
    {
        value = 0;
        if (cursor + 4 > data.Length)
        {
            return false;
        }

        value = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(cursor, 4));
        cursor += 4;
        return true;
    }
}

/// <summary>Vorbis Comment（FLAC / Ogg Vorbis / Opus 共用）。</summary>
internal static class VorbisComment
{
    public static void Parse(MediaTagBuilder builder, ReadOnlySpan<byte> data)
    {
        int cursor = 0;
        if (!TryReadUInt32LittleEndian(data, ref cursor, out uint vendorLength) || vendorLength > (uint)(data.Length - cursor))
        {
            return;
        }

        cursor += (int)vendorLength;
        if (!TryReadUInt32LittleEndian(data, ref cursor, out uint count))
        {
            return;
        }

        if (count > 4096)
        {
            count = 4096;
        }

        for (uint i = 0; i < count; i++)
        {
            if (!TryReadUInt32LittleEndian(data, ref cursor, out uint length) || length > (uint)(data.Length - cursor))
            {
                return;
            }

            string entry = Encoding.UTF8.GetString(data.Slice(cursor, (int)length));
            cursor += (int)length;

            int equals = entry.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            Apply(builder, entry[..equals].Trim().ToUpperInvariant(), entry[(equals + 1)..]);
        }
    }

    private static void Apply(MediaTagBuilder builder, string key, string value)
    {
        switch (key)
        {
            case "TITLE":
                builder.SetTitle(value);
                break;
            case "ARTIST":
                builder.SetArtist(value);
                break;
            case "ALBUM":
                builder.SetAlbum(value);
                break;
            case "ALBUMARTIST":
            case "ALBUM ARTIST":
            case "ENSEMBLE":
                builder.SetAlbumArtist(value);
                break;
            case "GENRE":
                builder.SetGenre(value);
                break;
            case "COMPOSER":
                builder.SetComposer(value);
                break;
            case "COMMENT":
            case "DESCRIPTION":
                builder.SetComment(value);
                break;
            case "GROUPING":
                builder.SetGrouping(value);
                break;
            case "LYRICS":
            case "UNSYNCEDLYRICS":
                builder.SetLyrics(value);
                break;
            case "TRACKNUMBER":
                TagText.ParsePair(value, out int track, out _);
                builder.SetTrackNumber(track > 0 ? track : TagText.ParseLeadingInt(value));
                break;
            case "TRACKTOTAL":
            case "TOTALTRACKS":
                builder.SetTotalTracks(TagText.ParseLeadingInt(value));
                break;
            case "DISCNUMBER":
                TagText.ParsePair(value, out int disc, out _);
                builder.SetDiscNumber(disc > 0 ? disc : TagText.ParseLeadingInt(value));
                break;
            case "DISCTOTAL":
            case "TOTALDISCS":
                builder.SetTotalDiscs(TagText.ParseLeadingInt(value));
                break;
            case "DATE":
            case "YEAR":
                builder.SetYear(TagText.ParseYear(value));
                break;
            case "BPM":
                builder.SetBpm(TagText.ParseLeadingInt(value));
                break;
            case "COMPILATION":
                builder.SetCompilation(TagText.ParseBool(value));
                break;
            case "METADATA_BLOCK_PICTURE":
                ParseBase64Picture(builder, value);
                break;
        }
    }

    private static void ParseBase64Picture(MediaTagBuilder builder, string value)
    {
        if (builder.CoverImage is not null || value.Length == 0 || value.Length > 12 * 1024 * 1024)
        {
            return;
        }

        try
        {
            byte[] decoded = Convert.FromBase64String(value.Trim());
            FlacPicture.Parse(builder, decoded);
        }
        catch (FormatException)
        {
            // 非法的 base64 直接忽略。
        }
    }

    private static bool TryReadUInt32LittleEndian(ReadOnlySpan<byte> data, ref int cursor, out uint value)
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

/// <summary>Ogg Vorbis / Opus 解析。</summary>
internal static class OggReader
{
    public static void Read(MediaTagBuilder builder, FileSliceSource file)
    {
        Span<byte> head = stackalloc byte[27];
        if (!file.TryRead(0, head) || !TagBytes.IsAscii(head, "OggS"))
        {
            return;
        }

        uint serial = BinaryPrimitives.ReadUInt32LittleEndian(head[14..18]);
        long position = 0;
        long maxScan = Math.Min(file.Length, 4L * 1024 * 1024);
        List<byte> packet = [];
        int packetIndex = 0;
        int pages = 0;
        Span<byte> pageHeader = stackalloc byte[27];

        while (position + 27 <= maxScan && pages++ < 512 && packetIndex < 2)
        {
            if (!file.TryRead(position, pageHeader) || !TagBytes.IsAscii(pageHeader, "OggS"))
            {
                break;
            }

            byte headerType = pageHeader[5];
            uint pageSerial = BinaryPrimitives.ReadUInt32LittleEndian(pageHeader[14..18]);
            int segments = pageHeader[26];
            byte[] segmentTable = file.ReadArray(position + 27, segments);
            if (segmentTable.Length < segments)
            {
                break;
            }

            int payloadLength = 0;
            foreach (byte segment in segmentTable)
            {
                payloadLength += segment;
            }

            long payloadPosition = position + 27 + segments;
            byte[] payload = file.ReadArray(payloadPosition, payloadLength);
            long next = payloadPosition + payloadLength;

            if (pageSerial == serial && payload.Length > 0)
            {
                int offset = 0;
                foreach (byte segment in segmentTable)
                {
                    int take = Math.Min(segment, payload.Length - offset);
                    if (take > 0)
                    {
                        packet.AddRange(payload.AsSpan(offset, take).ToArray());
                        offset += take;
                    }

                    if (segment < 255)
                    {
                        HandlePacket(builder, packet, packetIndex);
                        packetIndex++;
                        packet.Clear();
                        if (packetIndex >= 2)
                        {
                            break;
                        }
                    }
                }
            }

            if ((headerType & 0x04) != 0)
            {
                break;
            }

            if (next <= position)
            {
                break;
            }

            position = next;
        }

        ApplyDuration(builder, file);
    }

    private static void HandlePacket(MediaTagBuilder builder, List<byte> packet, int index)
    {
        if (packet.Count == 0)
        {
            return;
        }

        byte[] data = [.. packet];
        if (index == 0)
        {
            if (data.Length >= 30 && data[0] == 0x01 && TagBytes.IsAscii(data.AsSpan(1, 6), "vorbis"))
            {
                builder.SetChannels(data[11]);
                builder.SetSampleRate(BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(12, 4)));
                int nominal = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(20, 4));
                if (nominal > 0)
                {
                    builder.SetBitrateKbps(nominal / 1000);
                }
            }
            else if (data.Length >= 19 && TagBytes.IsAscii(data.AsSpan(0, 8), "OpusHead"))
            {
                builder.SetChannels(data[9]);
                builder.SetSampleRate(48000);
            }

            return;
        }

        if (data.Length > 7 && data[0] == 0x03 && TagBytes.IsAscii(data.AsSpan(1, 6), "vorbis"))
        {
            VorbisComment.Parse(builder, data.AsSpan(7));
        }
        else if (data.Length > 8 && TagBytes.IsAscii(data.AsSpan(0, 8), "OpusTags"))
        {
            VorbisComment.Parse(builder, data.AsSpan(8));
        }
    }

    /// <summary>用最后一页的 granule position 推算时长。</summary>
    private static void ApplyDuration(MediaTagBuilder builder, FileSliceSource file)
    {
        int sampleRate = builder.SampleRate > 0 ? builder.SampleRate : 48000;
        if (file.Length < 27)
        {
            return;
        }

        int tailLength = (int)Math.Min(file.Length, 64 * 1024);
        long tailStart = file.Length - tailLength;
        byte[] tail = file.ReadArray(tailStart, tailLength);
        for (int i = tail.Length - 27; i >= 0; i--)
        {
            if (tail[i] != (byte)'O' || tail[i + 1] != (byte)'g' || tail[i + 2] != (byte)'g' || tail[i + 3] != (byte)'S'
                || tail[i + 4] != 0)
            {
                continue;
            }

            long granule = BinaryPrimitives.ReadInt64LittleEndian(tail.AsSpan(i + 6, 8));
            if (granule <= 0)
            {
                continue;
            }

            builder.SetDuration(TimeSpan.FromSeconds((double)granule / sampleRate));
            return;
        }
    }
}
/// <summary>WAV（RIFF）解析。</summary>
internal static class WavReader
{
    public static void Read(MediaTagBuilder builder, FileSliceSource file)
    {
        Span<byte> header = stackalloc byte[12];
        if (!file.TryRead(0, header) || !TagBytes.IsAscii(header, "RIFF") || !TagBytes.IsAscii(header[8..12], "WAVE"))
        {
            return;
        }

        long position = 12;
        long byteRate = 0;
        long dataLength = 0;
        int guard = 0;
        Span<byte> chunk = stackalloc byte[8];
        while (position + 8 <= file.Length && guard++ < 4096)
        {
            if (!file.TryRead(position, chunk))
            {
                break;
            }

            string id = TagBytes.Latin1Text(chunk[..4]);
            long declared = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..8]);
            long dataPos = position + 8;
            long length = Math.Min(declared, file.Length - dataPos);

            switch (id)
            {
                case "fmt ":
                    byteRate = ParseFmt(builder, file, dataPos, (int)Math.Min(length, 64));
                    break;
                case "data":
                    dataLength = length;
                    break;
                case "LIST":
                    ParseList(builder, file, dataPos, (int)Math.Min(length, 4 * 1024 * 1024));
                    break;
                case "id3 ":
                case "ID3 ":
                    TryReadId3(builder, file, dataPos, (int)Math.Min(length, 1024));
                    break;
            }

            long next = dataPos + length + (length & 1);
            if (next <= position)
            {
                break;
            }

            position = next;
        }

        if (byteRate > 0)
        {
            if (dataLength <= 0)
            {
                dataLength = Math.Max(0, file.Length - 44);
            }

            builder.SetDuration(TimeSpan.FromSeconds((double)dataLength / byteRate));
        }
    }

    private static long ParseFmt(MediaTagBuilder builder, FileSliceSource file, long position, int length)
    {
        if (length < 16)
        {
            return 0;
        }

        Span<byte> buffer = stackalloc byte[16];
        if (!file.TryRead(position, buffer))
        {
            return 0;
        }

        // audioFormat(2) + channels(2) + sampleRate(4) + byteRate(4) + blockAlign(2) + bitsPerSample(2)
        int channels = BinaryPrimitives.ReadUInt16LittleEndian(buffer[2..4]);
        int sampleRate = BinaryPrimitives.ReadInt32LittleEndian(buffer[4..8]);
        long byteRate = BinaryPrimitives.ReadUInt32LittleEndian(buffer[8..12]);

        builder.SetChannels(channels);
        builder.SetSampleRate(sampleRate);
        if (byteRate > 0)
        {
            builder.SetBitrateKbps((int)Math.Round(byteRate * 8.0 / 1000.0));
        }

        return byteRate;
    }

    private static void ParseList(MediaTagBuilder builder, FileSliceSource file, long position, int length)
    {
        if (length < 4)
        {
            return;
        }

        Span<byte> type = stackalloc byte[4];
        if (!file.TryRead(position, type) || !TagBytes.IsAscii(type, "INFO"))
        {
            return;
        }

        long cursor = position + 4;
        long end = position + length;
        int guard = 0;
        Span<byte> chunk = stackalloc byte[8];
        while (cursor + 8 <= end && guard++ < 512)
        {
            if (!file.TryRead(cursor, chunk))
            {
                break;
            }

            string id = TagBytes.Latin1Text(chunk[..4]);
            long declared = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..8]);
            int size = (int)Math.Min(declared, end - cursor - 8);
            if (size < 0)
            {
                break;
            }

            string text = TagText.DecodeUnknown(file.ReadArray(cursor + 8, Math.Min(size, 64 * 1024)));
            switch (id)
            {
                case "INAM":
                    builder.SetTitle(text);
                    break;
                case "IART":
                    builder.SetArtist(text);
                    break;
                case "IPRD":
                    builder.SetAlbum(text);
                    break;
                case "IGNR":
                    builder.SetGenre(text);
                    break;
                case "ICMT":
                    builder.SetComment(text);
                    break;
                case "ICRD":
                    builder.SetYear(TagText.ParseYear(text));
                    break;
                case "ITRK":
                case "IPRT":
                    TagText.ParsePair(text, out int track, out int total);
                    builder.SetTrackNumber(track);
                    builder.SetTotalTracks(total);
                    break;
            }

            long next = cursor + 8 + size + (size & 1);
            if (next <= cursor)
            {
                break;
            }

            cursor = next;
        }
    }

    private static void TryReadId3(MediaTagBuilder builder, FileSliceSource file, long position, int length)
    {
        Span<byte> probe = stackalloc byte[8];
        if (!file.TryRead(position, probe))
        {
            return;
        }

        if (TagBytes.IsAscii(probe, "ID3"))
        {
            Id3v2Reader.Read(builder, file, position);
            return;
        }

        if (probe.Length >= 7 && TagBytes.IsAscii(probe[4..], "ID3"))
        {
            Id3v2Reader.Read(builder, file, position + 4);
        }
    }
}

/// <summary>AIFF / AIFF-C 解析。</summary>
internal static class AiffReader
{
    public static void Read(MediaTagBuilder builder, FileSliceSource file)
    {
        Span<byte> header = stackalloc byte[12];
        if (!file.TryRead(0, header) || !TagBytes.IsAscii(header, "FORM"))
        {
            return;
        }

        long position = 12;
        int guard = 0;
        Span<byte> chunk = stackalloc byte[8];
        while (position + 8 <= file.Length && guard++ < 4096)
        {
            if (!file.TryRead(position, chunk))
            {
                break;
            }

            string id = TagBytes.Latin1Text(chunk[..4]);
            long declared = BinaryPrimitives.ReadUInt32BigEndian(chunk[4..8]);
            long dataPos = position + 8;
            long length = Math.Min(declared, file.Length - dataPos);

            switch (id)
            {
                case "COMM":
                    ParseCommon(builder, file, dataPos, (int)Math.Min(length, 64));
                    break;
                case "NAME":
                    builder.SetTitle(ReadChunkText(file, dataPos, length));
                    break;
                case "AUTH":
                    builder.SetArtist(ReadChunkText(file, dataPos, length));
                    break;
                case "ANNO":
                    builder.SetComment(ReadChunkText(file, dataPos, length));
                    break;
                case "ID3 ":
                case "id3 ":
                    Id3v2Reader.Read(builder, file, dataPos);
                    break;
            }

            long next = dataPos + length + (length & 1);
            if (next <= position)
            {
                break;
            }

            position = next;
        }
    }

    private static string ReadChunkText(FileSliceSource file, long position, long length)
        => TagText.DecodeUnknown(file.ReadArray(position, (int)Math.Min(length, 64 * 1024)));

    private static void ParseCommon(MediaTagBuilder builder, FileSliceSource file, long position, int length)
    {
        if (length < 18)
        {
            return;
        }

        Span<byte> buffer = stackalloc byte[18];
        if (!file.TryRead(position, buffer))
        {
            return;
        }

        // channels(2) + numSampleFrames(4) + sampleSize(2) + sampleRate(10，80 位扩展浮点)
        int channels = BinaryPrimitives.ReadUInt16BigEndian(buffer[..2]);
        long frames = BinaryPrimitives.ReadUInt32BigEndian(buffer[2..6]);
        double sampleRate = ReadExtended80(buffer[8..18]);

        builder.SetChannels(channels);
        if (sampleRate > 0)
        {
            builder.SetSampleRate((int)Math.Round(sampleRate));
        }

        if (sampleRate > 0 && frames > 0)
        {
            builder.SetDuration(TimeSpan.FromSeconds(frames / sampleRate));
        }
    }

    /// <summary>解码 80 位 IEEE 扩展浮点（AIFF 采样率）。</summary>
    private static double ReadExtended80(ReadOnlySpan<byte> data)
    {
        if (data.Length < 10)
        {
            return 0;
        }

        int exponent = ((data[0] & 0x7F) << 8) | data[1];
        ulong mantissa = BinaryPrimitives.ReadUInt64BigEndian(data[2..10]);
        if (exponent == 0 && mantissa == 0)
        {
            return 0;
        }

        double value = mantissa * Math.Pow(2, exponent - 16383 - 63);
        return (data[0] & 0x80) != 0 ? -value : value;
    }
}

/// <summary>WMA / ASF 尽力而为解析（Content Description 与 Extended Content Description）。</summary>
internal static class AsfReader
{
    private static readonly byte[] HeaderGuid =
        [0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11, 0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C];

    private static readonly byte[] ContentDescriptionGuid =
        [0x33, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11, 0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C];

    private static readonly byte[] ExtendedContentDescriptionGuid =
        [0x40, 0xA4, 0xD0, 0xD2, 0x07, 0xE3, 0xD2, 0x11, 0x97, 0xF0, 0x00, 0xA0, 0xC9, 0x5E, 0xA8, 0x50];

    private static readonly byte[] FilePropertiesGuid =
        [0xA1, 0xDC, 0xAB, 0x8C, 0x47, 0xA9, 0xCF, 0x11, 0x8E, 0xE4, 0x00, 0xC0, 0x0C, 0x20, 0x53, 0x65];

    public static bool IsAsfHeader(ReadOnlySpan<byte> data)
        => data.Length >= 16 && data[..16].SequenceEqual(HeaderGuid);

    public static void Read(MediaTagBuilder builder, FileSliceSource file)
    {
        Span<byte> header = stackalloc byte[30];
        if (!file.TryRead(0, header) || !IsAsfHeader(header))
        {
            return;
        }

        long headerSize = BinaryPrimitives.ReadInt64LittleEndian(header[16..24]);
        int objectCount = BinaryPrimitives.ReadInt32LittleEndian(header[24..28]);
        long end = headerSize > 0 ? Math.Min(headerSize, file.Length) : file.Length;
        long position = 30;
        int limit = objectCount is > 0 and < 512 ? objectCount : 128;
        int guard = 0;
        Span<byte> objectHeader = stackalloc byte[24];

        while (position + 24 <= end && guard++ < limit)
        {
            if (!file.TryRead(position, objectHeader))
            {
                break;
            }

            long size = BinaryPrimitives.ReadInt64LittleEndian(objectHeader[16..24]);
            if (size < 24)
            {
                break;
            }

            long dataPos = position + 24;
            long dataLength = Math.Min(size - 24, end - dataPos);
            if (dataLength < 0)
            {
                break;
            }

            ReadOnlySpan<byte> guid = objectHeader[..16];
            if (guid.SequenceEqual(ContentDescriptionGuid))
            {
                ParseContentDescription(builder, file, dataPos, (int)Math.Min(dataLength, 1024 * 1024));
            }
            else if (guid.SequenceEqual(ExtendedContentDescriptionGuid))
            {
                ParseExtendedContentDescription(builder, file, dataPos, (int)Math.Min(dataLength, 4 * 1024 * 1024));
            }
            else if (guid.SequenceEqual(FilePropertiesGuid))
            {
                ParseFileProperties(builder, file, dataPos, (int)Math.Min(dataLength, 128));
            }

            position = dataPos + (size - 24);
        }
    }

    private static void ParseContentDescription(MediaTagBuilder builder, FileSliceSource file, long position, int length)
    {
        if (length < 10)
        {
            return;
        }

        byte[] data = file.ReadArray(position, length);
        if (data.Length < 10)
        {
            return;
        }

        // title / author / copyright / description / rating 的 5 个长度字段，随后是 UTF-16LE 串。
        int cursor = 10;
        builder.SetTitle(ReadUtf16(data, ref cursor, BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(0, 2))));
        builder.SetArtist(ReadUtf16(data, ref cursor, BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2, 2))));
        cursor = Math.Min(data.Length, cursor + BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4, 2)));
        builder.SetComment(ReadUtf16(data, ref cursor, BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(6, 2))));
    }

    private static void ParseExtendedContentDescription(MediaTagBuilder builder, FileSliceSource file, long position, int length)
    {
        if (length < 2)
        {
            return;
        }

        byte[] data = file.ReadArray(position, length);
        if (data.Length < 2)
        {
            return;
        }

        int count = BinaryPrimitives.ReadUInt16LittleEndian(data);
        int cursor = 2;
        for (int i = 0; i < count && i < 1024; i++)
        {
            if (cursor + 2 > data.Length)
            {
                return;
            }

            int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(cursor, 2));
            cursor += 2;
            if (nameLength <= 0 || cursor + nameLength > data.Length)
            {
                return;
            }

            string name = TagText.DecodeUtf16Le(data.AsSpan(cursor, nameLength));
            cursor += nameLength;

            if (cursor + 4 > data.Length)
            {
                return;
            }

            int valueType = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(cursor, 2));
            cursor += 2;
            int valueLength = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(cursor, 2));
            cursor += 2;
            if (valueLength < 0 || cursor + valueLength > data.Length)
            {
                return;
            }

            ReadOnlySpan<byte> value = data.AsSpan(cursor, valueLength);
            cursor += valueLength;
            ApplyDescriptor(builder, name, valueType, value);
        }
    }

    private static void ApplyDescriptor(MediaTagBuilder builder, string name, int valueType, ReadOnlySpan<byte> value)
    {
        if (name.Equals("WM/Picture", StringComparison.OrdinalIgnoreCase))
        {
            ParsePicture(builder, value);
            return;
        }

        string text = valueType == 0 ? TagText.DecodeUtf16Le(value) : TagText.DecodeUnknown(value);
        if (name.Equals("Title", StringComparison.OrdinalIgnoreCase))
        {
            builder.SetTitle(text);
        }
        else if (name.Equals("Author", StringComparison.OrdinalIgnoreCase))
        {
            builder.SetArtist(text);
        }
        else if (name.Equals("Description", StringComparison.OrdinalIgnoreCase))
        {
            builder.SetComment(text);
        }
        else if (name.Equals("WM/AlbumTitle", StringComparison.OrdinalIgnoreCase))
        {
            builder.SetAlbum(text);
        }
        else if (name.Equals("WM/AlbumArtist", StringComparison.OrdinalIgnoreCase))
        {
            builder.SetAlbumArtist(text);
        }
        else if (name.Equals("WM/Genre", StringComparison.OrdinalIgnoreCase))
        {
            builder.SetGenre(text);
        }
        else if (name.Equals("WM/Year", StringComparison.OrdinalIgnoreCase))
        {
            builder.SetYear(TagText.ParseYear(text));
        }
        else if (name.Equals("WM/Composer", StringComparison.OrdinalIgnoreCase))
        {
            builder.SetComposer(text);
        }
        else if (name.Equals("WM/TrackNumber", StringComparison.OrdinalIgnoreCase)
            || name.Equals("WM/Track", StringComparison.OrdinalIgnoreCase))
        {
            int track = TagText.ParseLeadingInt(text);
            if (name.Equals("WM/Track", StringComparison.OrdinalIgnoreCase) && valueType != 0 && track > 0)
            {
                // WM/Track 为 0 基序号。
                track++;
            }

            builder.SetTrackNumber(track);
        }
        else if (name.Equals("WM/TrackTotal", StringComparison.OrdinalIgnoreCase)
            || name.Equals("TrackTotal", StringComparison.OrdinalIgnoreCase))
        {
            builder.SetTotalTracks(TagText.ParseLeadingInt(text));
        }
    }

    private static void ParsePicture(MediaTagBuilder builder, ReadOnlySpan<byte> value)
    {
        if (value.Length < 5 || builder.CoverImage is not null)
        {
            return;
        }

        int dataLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(value[1..5]);
        if (dataLength <= 0 || dataLength > MediaTagReader.MaxCoverBytes)
        {
            return;
        }

        string mime = ReadUtf16ZeroTerminated(value[5..], out int mimeBytes);
        int offset = 5 + mimeBytes;
        if (offset >= value.Length)
        {
            return;
        }

        ReadUtf16ZeroTerminated(value[offset..], out int descriptionBytes);
        offset += descriptionBytes;
        if (offset + dataLength > value.Length)
        {
            return;
        }

        builder.SetCover(value.Slice(offset, dataLength).ToArray(), mime);
    }

    private static string ReadUtf16(byte[] data, ref int cursor, int byteLength)
    {
        if (cursor < 0 || cursor >= data.Length || byteLength <= 0)
        {
            return string.Empty;
        }

        int count = Math.Min(byteLength, data.Length - cursor);
        string text = TagText.DecodeUtf16Le(data.AsSpan(cursor, count));
        cursor += count;
        return text;
    }

    private static string ReadUtf16ZeroTerminated(ReadOnlySpan<byte> data, out int consumed)
    {
        int index = 0;
        while (index + 1 < data.Length)
        {
            if (data[index] == 0 && data[index + 1] == 0)
            {
                break;
            }

            index += 2;
        }

        string text = TagText.DecodeUtf16Le(data[..index]);
        consumed = Math.Min(index + 2, data.Length);
        return text;
    }

    private static void ParseFileProperties(MediaTagBuilder builder, FileSliceSource file, long position, int length)
    {
        if (length < 80)
        {
            return;
        }

        Span<byte> buffer = stackalloc byte[80];
        if (!file.TryRead(position, buffer))
        {
            return;
        }

        long playDuration = BinaryPrimitives.ReadInt64LittleEndian(buffer[40..48]);
        long preroll = BinaryPrimitives.ReadInt64LittleEndian(buffer[56..64]);
        uint maxBitrate = BinaryPrimitives.ReadUInt32LittleEndian(buffer[76..80]);

        if (playDuration > 0)
        {
            double seconds = (playDuration / 10000000.0) - (preroll / 1000.0);
            if (seconds > 0)
            {
                builder.SetDuration(TimeSpan.FromSeconds(seconds));
            }
        }

        if (maxBitrate > 0)
        {
            builder.SetBitrateKbps((int)(maxBitrate / 1000));
        }
    }
}
