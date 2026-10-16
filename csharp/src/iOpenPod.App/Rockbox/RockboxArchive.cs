using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using IOpenPod.App.Logging;

namespace IOpenPod.App.Rockbox;

/// <summary>下载与归档辅助。</summary>
internal static class RockboxArchive
{
    /// <summary>下载缓冲区大小（128 KiB）。</summary>
    private const int BufferSize = 128 * 1024;

    /// <summary>把镜像站根地址与相对路径拼成完整 URL。</summary>
    public static string BuildUrl(string? mirrorBaseUrl, string relativePath)
    {
        var path = relativePath.TrimStart('/');
        if (string.IsNullOrWhiteSpace(mirrorBaseUrl))
        {
            return $"{RockboxTargets.OfficialBaseUrl}/{path}";
        }

        return $"{mirrorBaseUrl.TrimEnd('/')}/{path}";
    }

    /// <summary>系统文件压缩包的相对路径（相对站点根）。</summary>
    public static string SystemArchiveRelativePath(RockboxTarget target, RockboxOptions options)
    {
        var channel = string.Equals(options.Channel?.Trim(), "daily", StringComparison.OrdinalIgnoreCase)
            ? "daily"
            : "release";

        return channel == "daily"
            ? $"daily/{target.Name}/{target.DailyArchiveName}"
            : $"release/{VersionText(options.ReleaseVersion)}/{target.ReleaseArchiveName(VersionText(options.ReleaseVersion))}";
    }

    /// <summary>引导程序文件的相对路径（相对站点根）。</summary>
    public static string BootloaderRelativePath(RockboxTarget target) =>
        $"bootloader/ipod/{target.BootloaderFileName}";

    /// <summary>ipodpatcher 的相对路径（相对站点根）。</summary>
    public const string IpodpatcherRelativePath = "bootloader/ipod/ipodpatcher/win32/ipodpatcher.exe";

    /// <summary>规范版本号文本，默认 4.0。</summary>
    public static string VersionText(string? version) =>
        string.IsNullOrWhiteSpace(version) ? "4.0" : version.Trim();

    /// <summary>
    /// 下载一个文件到缓存目录。
    ///
    /// 同名且字节数一致时直接复用；存在同名 .part 残片时尝试断点续传。
    /// </summary>
    public static async Task<string> DownloadAsync(
        HttpClient client,
        string url,
        string targetPath,
        IProgress<RockboxProgress>? progress,
        string phase,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

        var partialPath = targetPath + ".part";
        long existing = 0;
        if (File.Exists(partialPath))
        {
            // 上次下载留下的残片，尝试断点续传。
            existing = new FileInfo(partialPath).Length;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (existing > 0)
        {
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existing, null);
        }

        using var response = await client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        var resume = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        var append = resume;
        if (existing > 0 && !resume)
        {
            // 服务器不支持续传，从头开始。
            existing = 0;
            TryDelete(partialPath);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"下载失败，服务器返回 {(int)response.StatusCode} {response.ReasonPhrase}：{url}");
        }

        var total = response.Content.Headers.ContentLength is { } length ? length + existing : -1;
        AppLog.Info("Rockbox", $"开始下载 {url}，{(total > 0 ? $"{total} 字节" : "长度未知")}，续传起点 {existing}。");
        progress?.Report(new RockboxProgress(phase, existing, total > 0 ? total : 0, Path.GetFileName(targetPath)));

        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var target = new FileStream(
                         partialPath,
                         append ? FileMode.Append : FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         BufferSize,
                         useAsync: true))
        {
            var buffer = new byte[BufferSize];
            var completed = append ? existing : 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                completed += read;
                progress?.Report(new RockboxProgress(
                    phase,
                    completed,
                    total > 0 ? total : completed,
                    $"{Path.GetFileName(targetPath)}（{completed / 1024.0 / 1024.0:F1} MB）"));
            }
        }

        File.Move(partialPath, targetPath, overwrite: true);
        var size = new FileInfo(targetPath).Length;
        AppLog.Info("Rockbox", $"下载完成 {targetPath}，共 {size} 字节。");
        progress?.Report(new RockboxProgress(phase, size, size, $"{Path.GetFileName(targetPath)} 下载完成"));
        return targetPath;
    }

    /// <summary>
    /// 校验 ZIP：可被 ZipArchive 打开、条目数大于 0、全部条目路径安全。
    /// 可选校验 SHA256SUMS 中的哈希（文件不存在时跳过）。
    /// </summary>
    public static async Task<string?> ValidateSystemArchiveAsync(
        HttpClient client,
        string archivePath,
        string? checksumsUrl,
        bool skipHash,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException("压缩包不存在。", archivePath);
        }

        var info = new FileInfo(archivePath);
        if (info.Length == 0)
        {
            throw new InvalidDataException("压缩包长度为 0，下载可能被中断。");
        }

        var entries = ListEntries(archivePath);
        if (entries.Count == 0)
        {
            throw new InvalidDataException("压缩包内没有任何条目，文件可能已损坏。");
        }

        if (skipHash || string.IsNullOrWhiteSpace(checksumsUrl))
        {
            return null;
        }

        var expected = await TryReadExpectedHashAsync(client, checksumsUrl, Path.GetFileName(archivePath), cancellationToken)
            .ConfigureAwait(false);
        if (expected is null)
        {
            AppLog.Info("Rockbox", "镜像站未提供 SHA256SUMS 或缺少数值，跳过哈希校验。");
            return null;
        }

        var actual = await ComputeSha256Async(archivePath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"SHA-256 校验失败：期望 {expected}，实际 {actual}。");
        }

        AppLog.Info("Rockbox", $"SHA-256 校验通过：{actual}");
        return actual;
    }

    /// <summary>列出 ZIP 条目的相对路径。</summary>
    public static IReadOnlyList<string> ListEntries(string archivePath)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        return archive.Entries.Select(entry => entry.FullName).ToArray();
    }

    /// <summary>
    /// 校验条目路径安全（防 Zip Slip）：拒绝绝对路径、盘符、.. 与空目录段。
    /// </summary>
    public static bool IsSafeEntryPath(string? entryPath)
    {
        if (string.IsNullOrWhiteSpace(entryPath))
        {
            return false;
        }

        var normalized = entryPath.Replace('\\', '/');
        if (normalized.StartsWith('/'))
        {
            // Unix 绝对路径。
            return false;
        }

        if (normalized.Contains(':', StringComparison.Ordinal))
        {
            // Windows 盘符（C:\...）或 NTFS 备用数据流。
            return false;
        }

        // 结尾的 '/' 是目录条目的正常写法，检查时忽略空段。
        var trimmed = normalized.TrimEnd('/');
        if (trimmed.Length == 0)
        {
            return false;
        }

        foreach (var segment in trimmed.Split('/'))
        {
            if (segment.Length == 0 || segment is ".." or ".")
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>把 ZIP 解压到目标根目录（先校验，再逐个条目写出）。</summary>
    public static void ExtractTo(
        string archivePath,
        string destinationRoot,
        IProgress<RockboxProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var entries = archive.Entries.ToArray();
        var totalBytes = entries.Sum(entry => Math.Max(0, entry.Length));
        long written = 0;
        var root = Path.GetFullPath(destinationRoot);
        Directory.CreateDirectory(root);

        var index = 0;
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            index++;

            if (!IsSafeEntryPath(entry.FullName))
            {
                throw new InvalidDataException($"压缩包中存在不安全的条目路径，已中止解压：{entry.FullName}");
            }

            var relative = entry.FullName.Replace('\\', '/');
            var destination = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(destination, root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"压缩包条目会写到目标目录之外，已中止解压：{entry.FullName}");
            }

            if (relative.EndsWith('/'))
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
            written += Math.Max(0, entry.Length);

            progress?.Report(new RockboxProgress(
                "正在解压系统文件",
                written,
                totalBytes,
                $"{index}/{entries.Length} {relative}"));

            AppLog.Debug("Rockbox", $"已解压：{relative}");
        }

        AppLog.Info("Rockbox", $"解压完成：{entries.Length} 个条目，{written} 字节 → {root}。");
    }

    /// <summary>把一个目录压缩为 ZIP（用于备份已有的 .rockbox）。</summary>
    public static async Task<string> CompressDirectoryAsync(
        string sourceDirectory,
        string destinationZip,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationZip)!);
        if (File.Exists(destinationZip))
        {
            File.Delete(destinationZip);
        }

        await Task.Run(
            () =>
            {
                ZipFile.CreateFromDirectory(
                    sourceDirectory,
                    destinationZip,
                    CompressionLevel.Fastest,
                    includeBaseDirectory: false);
            },
            cancellationToken).ConfigureAwait(false);

        AppLog.Info("Rockbox", $"已备份目录 {sourceDirectory} → {destinationZip}");
        return destinationZip;
    }

    /// <summary>计算目录内所有文件的字节数。</summary>
    public static long DirectorySize(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Sum(file => new FileInfo(file).Length);
        }
        catch (Exception error)
        {
            AppLog.Warning("Rockbox", $"统计目录大小失败：{directory}（{error.Message}）");
            return 0;
        }
    }

    /// <summary>递归删除目录，目录不存在时视为成功。</summary>
    public static bool TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }

            return true;
        }
        catch (Exception error)
        {
            AppLog.Warning("Rockbox", $"删除目录失败：{directory}（{error.Message}）");
            return false;
        }
    }

    /// <summary>删除文件，忽略失败。</summary>
    public static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception error)
        {
            AppLog.Debug("Rockbox", $"删除文件失败：{path}（{error.Message}）");
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    /// <summary>从 SHA256SUMS 文本中挑选目标文件的哈希。</summary>
    internal static string? ParseChecksumLine(string? content, string fileName)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim().TrimStart('\\').Trim();
            if (line.Length == 0 || !line.EndsWith(fileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var head = line[..^fileName.Length].Trim();
            if (head.StartsWith('*'))
            {
                head = head[1..].Trim();
            }

            var token = head.Split([' ', '\t', '*'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (token is { Length: 64 } && token.All(Uri.IsHexDigit))
            {
                return token.ToUpperInvariant();
            }
        }

        return null;
    }

    private static async Task<string?> TryReadExpectedHashAsync(
        HttpClient client,
        string checksumsUrl,
        string fileName,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync(checksumsUrl, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ParseChecksumLine(content, fileName);
        }
        catch (Exception error)
        {
            AppLog.Info("Rockbox", $"读取 SHA256SUMS 失败，跳过哈希校验：{error.Message}");
            return null;
        }
    }
}
