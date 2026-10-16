using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using IOpenPod.App.Logging;

namespace IOpenPod.App.Rockbox;

/// <summary>安装模式。</summary>
public enum RockboxInstallMode
{
    /// <summary>只安装 Rockbox 系统文件（可逆，不触碰固件）。</summary>
    SystemOnly,

    /// <summary>安装系统文件并尝试安装引导程序。</summary>
    SystemAndBootloader,

    /// <summary>只安装引导程序。</summary>
    BootloaderOnly,

    /// <summary>卸载：删除 .rockbox，并按需移除引导程序。</summary>
    Uninstall,
}

/// <summary>安装参数。参数尽量多，便于高级用户定制。</summary>
public sealed record RockboxOptions
{
    /// <summary>镜像站根地址；留空使用 Rockbox 官方地址。</summary>
    public string MirrorBaseUrl { get; init; } = string.Empty;

    /// <summary>"release" 或 "daily"。</summary>
    public string Channel { get; init; } = "release";

    /// <summary>稳定版版本号，例如 "4.0"。</summary>
    public string ReleaseVersion { get; init; } = "4.0";

    public RockboxInstallMode Mode { get; init; } = RockboxInstallMode.SystemOnly;

    /// <summary>安装前用 ipodpatcher --read-partition 备份固件分区。</summary>
    public bool BackupFirmwarePartition { get; init; } = true;

    /// <summary>安装前用 ipodpatcher --read-firmware 备份 Apple 固件。</summary>
    public bool BackupAppleFirmware { get; init; } = true;

    /// <summary>把 apple_os.ipod 放进 .rockbox 以便双启动。</summary>
    public bool KeepAppleFirmwareDualBoot { get; init; } = true;

    /// <summary>覆盖 .rockbox 前先把已有的 .rockbox 打包备份。</summary>
    public bool BackupExistingInstallation { get; init; } = true;

    /// <summary>附加给 ipodpatcher 的参数（原样追加）。</summary>
    public string ExtraIpodpatcherArguments { get; init; } = string.Empty;

    /// <summary>不校验下载内容哈希（默认仍校验 ZIP 完整性与大小）。</summary>
    public bool SkipIntegrityCheck { get; init; }

    /// <summary>用户自备的系统文件 zip 路径（设置后不再下载）。</summary>
    public string? CustomSystemZipPath { get; init; }

    /// <summary>用户自备的引导程序文件路径。</summary>
    public string? CustomBootloaderPath { get; init; }

    /// <summary>用户自备的 ipodpatcher 路径。</summary>
    public string? CustomIpodpatcherPath { get; init; }

    /// <summary>mks5lboot 路径（仅 iPod classic 需要）。</summary>
    public string? Mks5lbootPath { get; init; }

    /// <summary>卸载时是否同时移除引导程序。</summary>
    public bool RemoveBootloaderOnUninstall { get; init; }
}

/// <summary>安装进度。</summary>
public sealed record RockboxProgress(string Phase, long Completed, long Total, string Message)
{
    public double Fraction => Total <= 0 ? 0 : Math.Clamp((double)Completed / Total, 0, 1);

    public string Display => Total <= 0 ? $"{Phase}：{Message}" : $"{Phase}（{Fraction:P0}）：{Message}";
}

/// <summary>安装结果。</summary>
public sealed record RockboxInstallResult(
    bool Success,
    string Message,
    IReadOnlyList<string> Steps,
    string? BackupDirectory,
    string? LogFile);

/// <summary>
/// Rockbox 安装/卸载。
///
/// 覆盖三条路径：系统文件（解压 zip 到设备根目录）、引导程序（ipodpatcher 或 mks5lboot）、以及卸载。
/// 除参数为空外，任何失败都以 <see cref="RockboxInstallResult.Success"/> 为 false 的中文结果返回，不抛异常。
/// </summary>
public sealed class RockboxInstaller
{
    /// <summary>设备上 Rockbox 系统文件的目录名。</summary>
    public const string RockboxDirectoryName = ".rockbox";

    /// <summary>Rockbox 版本信息文件（相对 .rockbox）。</summary>
    private const string InfoFileName = "rockbox-info.txt";

    /// <summary>iPod classic 引导程序的官方来源地址（mks5lboot 需要）。</summary>
    private const string Mks5lbootBootloaderSource = RockboxTargets.Mks5lbootSource;

    /// <summary>单次下载的最大字节数（512 MiB），防止异常响应占满磁盘。</summary>
    private const long MaxDownloadBytes = 512L * 1024 * 1024;

    private static readonly HttpClient SharedClient = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
    };

    private readonly List<string> _steps = [];

    private readonly RockboxCommandRunner _runner;

    private bool _elevationNotified;

    private string _backupDirectory = string.Empty;
    private string _logFile = string.Empty;

    public RockboxInstaller(string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            throw new ArgumentNullException(nameof(workingDirectory), "工作目录不能为空。");
        }

        WorkingDirectory = Path.GetFullPath(workingDirectory);
        CacheDirectory = Path.Combine(WorkingDirectory, "cache");
        _runner = new RockboxCommandRunner(NotifyElevation);
        Directory.CreateDirectory(CacheDirectory);
    }

    /// <summary>工作目录（备份、缓存与日志都放在它下面）。</summary>
    public string WorkingDirectory { get; }

    /// <summary>下载缓存目录。</summary>
    public string CacheDirectory { get; }

    /// <summary>最近一次使用的参数，供只读探测复用自定义路径。</summary>
    public RockboxOptions? LastOptions { get; private set; }

    // ---- 状态探测 ----

    /// <summary>查询设备上当前的 Rockbox 安装状态（只读，不写设备）。</summary>
    public async Task<RockboxStatus> InspectAsync(string mountPoint, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(mountPoint))
        {
            return new RockboxStatus(false, null, false, null, 0, "未提供设备挂载点，无法探测。");
        }

        try
        {
            var root = Path.GetFullPath(mountPoint);
            var rockboxRoot = Path.Combine(root, RockboxDirectoryName);
            var installed = Directory.Exists(rockboxRoot);
            var bytes = installed ? RockboxArchive.DirectorySize(rockboxRoot) : 0;
            var version = installed ? ReadVersion(rockboxRoot) : null;

            var (bootloader, probeOutput, probeNote) = await ProbeBootloaderAsync(cancellationToken)
                .ConfigureAwait(false);

            var noteParts = new List<string>();
            if (!installed)
            {
                noteParts.Add("设备根目录没有 .rockbox 目录，未安装 Rockbox 系统文件。");
            }
            else if (version is null)
            {
                noteParts.Add("已找到 .rockbox 目录，但无法从 rockbox-info.txt 或 rockbox.ipod 读取版本号。");
            }

            if (probeNote is not null)
            {
                noteParts.Add(probeNote);
            }

            return new RockboxStatus(
                installed,
                version,
                bootloader,
                probeOutput,
                bytes,
                noteParts.Count == 0 ? "探测完成。" : string.Join(" ", noteParts));
        }
        catch (Exception error)
        {
            AppLog.Warning("Rockbox", $"状态探测失败：{error.Message}");
            return new RockboxStatus(false, null, false, null, 0, $"状态探测失败：{error.Message}");
        }
    }

    // ---- 安装 / 卸载 ----

    /// <summary>执行安装或卸载。</summary>
    public async Task<RockboxInstallResult> RunAsync(
        string mountPoint,
        RockboxTarget target,
        RockboxOptions options,
        IProgress<RockboxProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(mountPoint))
        {
            throw new ArgumentNullException(nameof(mountPoint), "设备挂载点不能为空。");
        }

        _steps.Clear();
        LastOptions = options;
        var started = DateTime.Now;
        _backupDirectory = Path.Combine(
            WorkingDirectory,
            "backup",
            DescribeMountPoint(mountPoint),
            started.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        _logFile = Path.Combine(
            WorkingDirectory,
            "logs",
            $"rockbox-{started:yyyyMMdd-HHmmss}.log");

        AppLog.Info("Rockbox", $"开始 Rockbox 操作：模式 {options.Mode}，目标 {target.Name}，设备 {mountPoint}。");

        try
        {
            return await ExecuteAsync(mountPoint, target, options, progress, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            AddStep("操作被用户取消。");
            return Finish(false, "操作已取消。设备上可能留下不完整的安装，建议重新执行一次安装以覆盖修复。");
        }
        catch (Exception error)
        {
            AppLog.Error("Rockbox", "Rockbox 操作失败", error);
            AddStep($"发生未预期的错误：{error.GetType().Name}：{error.Message}");
            return Finish(false, $"操作失败：{error.Message}");
        }
        finally
        {
            _runner.Flush(_logFile);
        }
    }

    private async Task<RockboxInstallResult> ExecuteAsync(
        string mountPoint,
        RockboxTarget target,
        RockboxOptions options,
        IProgress<RockboxProgress>? progress,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(mountPoint);
        if (!Directory.Exists(root))
        {
            AddStep($"设备根目录不存在：{root}");
            return Finish(false, $"设备挂载点不存在：{root}，请确认 iPod 已连接并可访问。");
        }

        AddStep($"设备根目录：{root}");
        AddStep($"目标机型：{target.Name}（引导方式 {target.BootloaderMethod}）");
        if (target.IsExperimental)
        {
            AddStep($"注意：{target.Name} 属于 Rockbox 实验性支持机型。{target.Note}");
        }
        else if (!target.Supported)
        {
            return Finish(false, target.Note);
        }

        return options.Mode switch
        {
            RockboxInstallMode.SystemOnly => await RunSystemOnlyAsync(
                root, target, options, progress, cancellationToken).ConfigureAwait(false),
            RockboxInstallMode.SystemAndBootloader => await RunSystemAndBootloaderAsync(
                root, target, options, progress, cancellationToken).ConfigureAwait(false),
            RockboxInstallMode.BootloaderOnly => await RunBootloaderOnlyAsync(
                root, target, options, progress, cancellationToken).ConfigureAwait(false),
            RockboxInstallMode.Uninstall => await RunUninstallAsync(
                root, target, options, progress, cancellationToken).ConfigureAwait(false),
            _ => Finish(false, $"未知的安装模式：{options.Mode}"),
        };
    }

    private async Task<RockboxInstallResult> RunSystemOnlyAsync(
        string root,
        RockboxTarget target,
        RockboxOptions options,
        IProgress<RockboxProgress>? progress,
        CancellationToken cancellationToken)
    {
        var system = await InstallSystemAsync(root, target, options, progress, cancellationToken)
            .ConfigureAwait(false);
        if (!system.Ok)
        {
            return Finish(false, system.Error!);
        }

        var verification = VerifySystemInstalled(root);
        if (!verification.Ok)
        {
            return Finish(false, verification.Error!);
        }

        progress?.Report(new RockboxProgress("完成", 1, 1, "系统文件安装完成"));
        return Finish(true, $"Rockbox {target.Name} 系统文件已安装到 {Path.Combine(root, RockboxDirectoryName)}。" +
                            $"{target.Note} 卸载时直接删除 .rockbox 目录即可。");
    }

    private async Task<RockboxInstallResult> RunSystemAndBootloaderAsync(
        string root,
        RockboxTarget target,
        RockboxOptions options,
        IProgress<RockboxProgress>? progress,
        CancellationToken cancellationToken)
    {
        var system = await InstallSystemAsync(root, target, options, progress, cancellationToken)
            .ConfigureAwait(false);
        if (!system.Ok)
        {
            return Finish(false, system.Error!);
        }

        var verification = VerifySystemInstalled(root);
        if (!verification.Ok)
        {
            return Finish(false, verification.Error!);
        }

        var bootloader = await InstallBootloaderAsync(root, target, options, cancellationToken)
            .ConfigureAwait(false);
        if (!bootloader.Ok)
        {
            return Finish(
                false,
                $"系统文件已安装成功，但引导程序安装未完成：{bootloader.Error}");
        }

        progress?.Report(new RockboxProgress("完成", 1, 1, "安装完成"));
        return Finish(true, $"系统文件与引导程序均已安装（目标 {target.Name}）。请断开并重新连接 iPod。");
    }

    private async Task<RockboxInstallResult> RunBootloaderOnlyAsync(
        string root,
        RockboxTarget target,
        RockboxOptions options,
        IProgress<RockboxProgress>? progress,
        CancellationToken cancellationToken)
    {
        var bootloader = await InstallBootloaderAsync(root, target, options, cancellationToken)
            .ConfigureAwait(false);
        if (!bootloader.Ok)
        {
            return Finish(false, bootloader.Error!);
        }

        progress?.Report(new RockboxProgress("完成", 1, 1, "引导程序安装完成"));
        return Finish(true, $"引导程序已写入（目标 {target.Name}）。请断开并重新连接 iPod。{target.Note}");
    }

    private async Task<RockboxInstallResult> RunUninstallAsync(
        string root,
        RockboxTarget target,
        RockboxOptions options,
        IProgress<RockboxProgress>? progress,
        CancellationToken cancellationToken)
    {
        var rockboxRoot = Path.Combine(root, RockboxDirectoryName);
        var removedSomething = false;

        if (Directory.Exists(rockboxRoot))
        {
            AddStep($"确认删除设备上的 {RockboxDirectoryName} 目录（共 {RockboxArchive.DirectorySize(rockboxRoot) / 1024.0 / 1024.0:F1} MB）。");
            if (options.BackupExistingInstallation)
            {
                var zip = Path.Combine(_backupDirectory, $"rockbox-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
                await RockboxArchive.CompressDirectoryAsync(rockboxRoot, zip, cancellationToken).ConfigureAwait(false);
                AddStep($"已把现有安装备份为 {zip}");
            }

            progress?.Report(new RockboxProgress("正在删除", 0, 1, RockboxDirectoryName));
            if (!RockboxArchive.TryDeleteDirectory(rockboxRoot))
            {
                return Finish(false, $"无法删除 {rockboxRoot}，请关闭占用该目录的程序后重试。");
            }

            AddStep($"已删除 {rockboxRoot}");
            removedSomething = true;
        }
        else
        {
            AddStep("设备上不存在 .rockbox 目录，无需删除系统文件。");
        }

        if (options.RemoveBootloaderOnUninstall)
        {
            if (target.UsesIpodpatcher)
            {
                var (patcher, patcherError) = await EnsureIpodpatcherAsync(options, cancellationToken)
                    .ConfigureAwait(false);
                if (patcher is null)
                {
                    return Finish(false, $"系统文件已处理，但无法移除引导程序：{patcherError}");
                }

                var result = await RunIpodpatcherAsync(
                    patcher, "--delete-bootloader", true, cancellationToken).ConfigureAwait(false);
                if (!result.Started)
                {
                    return Finish(false, "系统文件已处理，但引导程序移除未完成：需要管理员权限，用户取消了 UAC 提权。");
                }

                if (result.ExitCode != 0)
                {
                    return Finish(false, $"系统文件已处理，但引导程序移除失败（退出码 {result.ExitCode}）：{FirstLines(result.Output)}");
                }

                AddStep("已通过 ipodpatcher 移除引导程序。");
                removedSomething = true;
            }
            else if (target.UsesMks5lboot)
            {
                return Finish(false,
                    "系统文件已处理，但 iPod classic 的引导程序必须用 mks5lboot 在 DFU 模式下卸载，" +
                    "本程序不会自动执行，请自行使用 mks5lboot 处理。");
            }
        }
        else
        {
            AddStep("按当前设置保留引导程序（RemoveBootloaderOnUninstall 为 false）。");
        }

        progress?.Report(new RockboxProgress("完成", 1, 1, "卸载完成"));
        return Finish(
            true,
            removedSomething
                ? "Rockbox 已卸载。请断开并重新连接 iPod。"
                : "没有需要删除的内容，设备未发生变化。");
    }

    // ---- 系统文件 ----

    private async Task<(bool Ok, string? Error)> InstallSystemAsync(
        string root,
        RockboxTarget target,
        RockboxOptions options,
        IProgress<RockboxProgress>? progress,
        CancellationToken cancellationToken)
    {
        var rockboxRoot = Path.Combine(root, RockboxDirectoryName);
        var hadExisting = Directory.Exists(rockboxRoot);
        if (hadExisting && options.BackupExistingInstallation)
        {
            var zip = Path.Combine(_backupDirectory, $"existing-rockbox-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
            await RockboxArchive.CompressDirectoryAsync(rockboxRoot, zip, cancellationToken).ConfigureAwait(false);
            AddStep($"已把设备上已有的安装备份为 {zip}");
        }

        string archive;
        if (!string.IsNullOrWhiteSpace(options.CustomSystemZipPath))
        {
            archive = Path.GetFullPath(options.CustomSystemZipPath);
            if (!File.Exists(archive))
            {
                return (false, $"指定的自定义系统文件压缩包不存在：{archive}");
            }

            AddStep($"使用用户自备的系统文件压缩包：{archive}");
        }
        else
        {
            var relative = RockboxArchive.SystemArchiveRelativePath(target, options);
            var url = RockboxArchive.BuildUrl(options.MirrorBaseUrl, relative);
            var fileName = Path.GetFileName(relative);
            var cached = Path.Combine(CacheDirectory, fileName);
            if (File.Exists(cached) && new FileInfo(cached).Length > 0)
            {
                // 同名缓存已存在：只要长度与镜像站一致就复用，避免重复下载。
                var expectedSize = await QueryLengthAsync(url, cancellationToken).ConfigureAwait(false);
                var cachedSize = new FileInfo(cached).Length;
                if (expectedSize <= 0 || expectedSize == cachedSize)
                {
                    archive = cached;
                    AddStep($"复用缓存中的系统文件压缩包（{cachedSize} 字节）：{cached}");
                    progress?.Report(new RockboxProgress("系统文件已缓存", cachedSize, cachedSize, "跳过下载"));
                }
                else
                {
                    AddStep($"缓存长度 {cachedSize} 与镜像站 {expectedSize} 不一致，重新下载。");
                    archive = await RockboxArchive.DownloadAsync(
                        SharedClient, url, cached, progress, "正在下载系统文件", cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                AddStep($"从 {url} 下载系统文件…");
                archive = await RockboxArchive.DownloadAsync(
                    SharedClient, url, cached, progress, "正在下载系统文件", cancellationToken).ConfigureAwait(false);
            }

            if (new FileInfo(archive).Length > MaxDownloadBytes)
            {
                RockboxArchive.TryDelete(archive);
                return (false, "下载内容异常巨大，已删除并中止（可能是镜像站返回了错误页面）。");
            }
        }

        try
        {
            var checksumsUrl = options.SkipIntegrityCheck
                ? null
                : RockboxArchive.BuildUrl(options.MirrorBaseUrl, $"{(RelativeChannelDirectory(options))}/SHA256SUMS");
            var hash = await RockboxArchive.ValidateSystemArchiveAsync(
                SharedClient, archive, checksumsUrl, options.SkipIntegrityCheck, cancellationToken).ConfigureAwait(false);
            AddStep(hash is null
                ? "已校验压缩包可读且条目数大于 0（未使用哈希校验）。"
                : $"已校验压缩包完整性，SHA-256 = {hash}");
        }
        catch (Exception error)
        {
            AddStep($"压缩包校验失败：{error.Message}");
            if (string.IsNullOrWhiteSpace(options.CustomSystemZipPath))
            {
                RockboxArchive.TryDelete(archive);
                AddStep("已删除损坏的缓存文件，请重试。");
            }

            return (false, $"系统文件压缩包校验失败：{error.Message}");
        }

        // 解压前先确认所有条目安全，避免解压到一半才失败。
        var unsafeEntry = RockboxArchive.ListEntries(archive)
            .FirstOrDefault(entry => !RockboxArchive.IsSafeEntryPath(entry));
        if (unsafeEntry is not null)
        {
            return (false, $"压缩包中存在不安全的条目路径，已拒绝解压：{unsafeEntry}");
        }

        try
        {
            if (hadExisting)
            {
                // 先清空旧目录再解压，避免旧版本残留文件造成混装；旧内容已在上面备份。
                if (!RockboxArchive.TryDeleteDirectory(rockboxRoot))
                {
                    return (false, $"无法清理已有的 {rockboxRoot}，请关闭占用该目录的程序后重试。");
                }

                AddStep($"已清空旧的 {RockboxDirectoryName} 目录，准备写入新版本。");
            }

            AddStep($"正在解压到设备根目录 {root} …");
            RockboxArchive.ExtractTo(archive, root, progress, cancellationToken);
            AddStep($"解压完成：{RockboxDirectoryName} 目录已生成。");
        }
        catch (Exception error)
        {
            return (false, $"解压失败：{error.Message}");
        }

        return await CopyAppleFirmwareAsync(root, target, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>把备份到的 apple_os.ipod 复制到 .rockbox，用于双启动。</summary>
    private async Task<(bool Ok, string? Error)> CopyAppleFirmwareAsync(
        string root,
        RockboxTarget target,
        RockboxOptions options,
        CancellationToken cancellationToken)
    {
        if (!options.KeepAppleFirmwareDualBoot)
        {
            return (true, null);
        }

        var source = Path.Combine(_backupDirectory, "apple_os.ipod");
        if (!File.Exists(source))
        {
            AddStep("没有 apple_os.ipod 可放入 .rockbox，跳过双启动配置（未安装引导程序时属正常情况）。");
            return (true, null);
        }

        try
        {
            var destination = Path.Combine(root, RockboxDirectoryName, "apple_os.ipod");
            File.Copy(source, destination, overwrite: true);
            AddStep($"已放入 apple_os.ipod，可在 Rockbox 中切换回 Apple 固件（目标 {target.Name}）。");
        }
        catch (Exception error)
        {
            return (false, $"写入 apple_os.ipod 失败：{error.Message}");
        }

        await Task.CompletedTask.ConfigureAwait(false);
        return (true, null);
    }

    /// <summary>确认 .rockbox 目录存在且非空。</summary>
    private (bool Ok, string? Error) VerifySystemInstalled(string root)
    {
        var rockboxRoot = Path.Combine(root, RockboxDirectoryName);
        if (!Directory.Exists(rockboxRoot))
        {
            return (false, $"解压后未找到 {rockboxRoot}，安装可能未成功。");
        }

        var count = Directory.EnumerateFileSystemEntries(rockboxRoot).Take(1).Count();
        if (count == 0)
        {
            return (false, $"{rockboxRoot} 为空目录，安装可能未成功。");
        }

        var size = RockboxArchive.DirectorySize(rockboxRoot);
        AddStep($"复验通过：{RockboxDirectoryName} 已存在于设备根目录，共 {size / 1024.0 / 1024.0:F1} MB。");
        return (true, null);
    }

    // ---- 引导程序 ----

    private async Task<(bool Ok, string? Error)> InstallBootloaderAsync(
        string root,
        RockboxTarget target,
        RockboxOptions options,
        CancellationToken cancellationToken)
    {
        if (!target.Supported || target.BootloaderMethod == "unsupported")
        {
            return (false, target.Note);
        }

        switch (target.BootloaderMethod)
        {
            case "ipodpatcher":
                return await InstallBootloaderWithIpodpatcherAsync(root, target, options, cancellationToken)
                    .ConfigureAwait(false);
            case "mks5lboot":
                return await InstallBootloaderWithMks5lbootAsync(target, options, cancellationToken)
                    .ConfigureAwait(false);
            default:
                return (false, $"未知的引导程序安装方式：{target.BootloaderMethod}");
        }
    }

    private async Task<(bool Ok, string? Error)> InstallBootloaderWithIpodpatcherAsync(
        string root,
        RockboxTarget target,
        RockboxOptions options,
        CancellationToken cancellationToken)
    {
        var (patcher, patcherError) = await EnsureIpodpatcherAsync(options, cancellationToken).ConfigureAwait(false);
        if (patcher is null)
        {
            return (false, patcherError);
        }

        var (bootloader, bootloaderError) = await EnsureBootloaderAsync(target, options, cancellationToken)
            .ConfigureAwait(false);
        if (bootloader is null)
        {
            return (false, bootloaderError);
        }

        if (target.Name == "ipodnano2g")
        {
            AddStep(
                "提示：iPod nano 2G 的 Rockbox 安装方式与其他机型不同，rockbox.ipod 必须用 crypt_firmware 加密，" +
                "官方提供的是已加密的 .ipodx。强烈建议改用 Rockbox Utility 完成此步骤，本程序只按通用流程写入文件。");
        }

        // 1) 探测
        var list = await RunIpodpatcherAsync(patcher, "--list", false, cancellationToken)
            .ConfigureAwait(false);
        if (!list.Started || list.ExitCode != 0)
        {
            AddStep("普通权限下 --list 未成功，将改用管理员权限重试。");
            list = await RunIpodpatcherAsync(patcher, "--list", true, cancellationToken)
                .ConfigureAwait(false);
            if (!list.Started)
            {
                return (false, "需要管理员权限运行 ipodpatcher，但用户取消了 UAC 提权请求。请以管理员身份重新执行。");
            }
        }

        AddStep($"已探测设备固件分区（ipodpatcher --list 退出码 {list.ExitCode}）。");

        // 2) 备份固件分区
        if (options.BackupFirmwarePartition)
        {
            var targetFile = Path.Combine(_backupDirectory, "firmware-partition.img");
            var result = await RunIpodpatcherAsync(
                patcher, $"--read-partition {Quote(targetFile)}", true, cancellationToken).ConfigureAwait(false);
            if (!result.Started)
            {
                return (false, "备份固件分区需要管理员权限，但用户取消了 UAC 提权请求。可在选项中关闭该备份后重试。");
            }

            if (result.ExitCode != 0 || !File.Exists(targetFile))
            {
                return (false, $"固件分区备份失败（退出码 {result.ExitCode}）：{FirstLines(result.Output)}");
            }

            AddStep($"已备份固件分区：{targetFile}");
        }
        else
        {
            AddStep("按设置跳过固件分区备份（BackupFirmwarePartition 为 false）。");
        }

        // 3) 备份 Apple 固件
        if (options.BackupAppleFirmware)
        {
            var targetFile = Path.Combine(_backupDirectory, "apple_os.ipod");
            var result = await RunIpodpatcherAsync(
                patcher, $"--read-firmware {Quote(targetFile)}", true, cancellationToken).ConfigureAwait(false);
            if (!result.Started)
            {
                return (false, "备份 Apple 固件需要管理员权限，但用户取消了 UAC 提权请求。可在选项中关闭该备份后重试。");
            }

            if (result.ExitCode != 0 || !File.Exists(targetFile))
            {
                return (false, $"Apple 固件备份失败（退出码 {result.ExitCode}）：{FirstLines(result.Output)}");
            }

            AddStep($"已备份 Apple 固件：{targetFile}");
        }
        else
        {
            AddStep("按设置跳过 Apple 固件备份（BackupAppleFirmware 为 false）。");
        }

        // 4) 写入引导程序
        var extra = options.ExtraIpodpatcherArguments?.Trim() ?? string.Empty;
        var arguments = string.IsNullOrEmpty(extra)
            ? $"--add-bootloader {Quote(bootloader)}"
            : $"--add-bootloader {Quote(bootloader)} {extra}";
        var added = await RunIpodpatcherAsync(patcher, arguments, true, cancellationToken).ConfigureAwait(false);
        if (!added.Started)
        {
            return (false, "写入引导程序需要管理员权限，但用户取消了 UAC 提权请求。");
        }

        if (added.ExitCode != 0)
        {
            return (false, $"写入引导程序失败（退出码 {added.ExitCode}）：{FirstLines(added.Output)}");
        }

        AddStep($"已执行 ipodpatcher {arguments}");

        // 5) 复验
        var verify = await RunIpodpatcherAsync(patcher, "--list", true, cancellationToken)
            .ConfigureAwait(false);
        if (verify.Started && verify.ExitCode == 0)
        {
            AddStep("复验：ipodpatcher --list 已再次执行，输出见日志文件。");
        }
        else
        {
            AddStep("复验未成功，请查看日志文件确认设备状态。");
        }

        if (target.Name == "ipodnano2g")
        {
            AddStep("提醒：nano 2G 还需要额外的加密与 DFU 步骤，若设备无法引导，请使用 Rockbox Utility 重新安装。");
        }

        return (true, null);
    }

    private async Task<(bool Ok, string? Error)> InstallBootloaderWithMks5lbootAsync(
        RockboxTarget target,
        RockboxOptions options,
        CancellationToken cancellationToken)
    {
        var mks5lboot = options.Mks5lbootPath;
        if (string.IsNullOrWhiteSpace(mks5lboot))
        {
            AddStep($"未提供 mks5lboot 路径。请从 {Mks5lbootBootloaderSource} 取得 mks5lboot 后，在选项中指定其路径。");
            return (false,
                "iPod classic 不能用 ipodpatcher 安装引导程序：需要在 DFU 模式下用 mks5lboot 写入。" +
                $"请自行获取 mks5lboot（{Mks5lbootBootloaderSource}），并在选项中填写其路径后重试。" +
                "本次仅完成系统文件安装，设备已可正常使用 Apple 固件。");
        }

        mks5lboot = Path.GetFullPath(mks5lboot);
        if (!File.Exists(mks5lboot))
        {
            return (false, $"指定的 mks5lboot 不存在：{mks5lboot}");
        }

        var (bootloader, bootloaderError) = await EnsureBootloaderAsync(target, options, cancellationToken)
            .ConfigureAwait(false);
        if (bootloader is null)
        {
            return (false, $"已找到 mks5lboot，但引导程序文件不可用：{bootloaderError}");
        }

        var scan = await _runner.RunAsync(mks5lboot, "--dfuscan -l", elevate: false, cancellationToken)
            .ConfigureAwait(false);
        if (scan.Started)
        {
            AddStep($"mks5lboot --dfuscan -l 退出码 {scan.ExitCode}。输出见日志文件。");
            if (scan.ExitCode != 0 || scan.Output.Contains("No device", StringComparison.OrdinalIgnoreCase))
            {
                return (false,
                    "mks5lboot 未在 DFU 模式下发现 iPod classic。请按以下步骤操作：" +
                    "① 断开 iPod；② 按住 Menu + Select 约 8 秒直到屏幕变黑；" +
                    "③ 立刻按住 Select + Play 直到进入 DFU 模式；④ 重新连接后再试。");
            }
        }
        else
        {
            AddStep("mks5lboot 启动失败，请检查文件是否完整（Windows 下可能需要管理员权限）。");
        }

        var command = $"--bl-inst {Quote(bootloader)}";
        var install = await _runner.RunAsync(mks5lboot, command, elevate: true, cancellationToken)
            .ConfigureAwait(false);
        if (!install.Started)
        {
            return (false, "mks5lboot 写入引导程序需要管理员权限，但用户取消了 UAC 提权请求。");
        }

        if (install.ExitCode != 0)
        {
            return (false, $"mks5lboot 写入引导程序失败（退出码 {install.ExitCode}）：{FirstLines(install.Output)}");
        }

        AddStep($"已执行 mks5lboot {command}");
        AddStep("iPod classic 引导程序安装完成：开机时同时按住 Menu + Select 可切换回 Apple 固件。");
        return (true, null);
    }

    private async Task<(string? Patcher, string? Error)> EnsureIpodpatcherAsync(
        RockboxOptions options,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(options.CustomIpodpatcherPath))
        {
            var custom = Path.GetFullPath(options.CustomIpodpatcherPath);
            if (!File.Exists(custom))
            {
                return (null, $"指定的 ipodpatcher 不存在：{custom}");
            }

            AddStep($"使用用户自备的 ipodpatcher：{custom}");
            return (custom, null);
        }

        var cached = Path.Combine(CacheDirectory, "ipodpatcher.exe");
        if (File.Exists(cached) && new FileInfo(cached).Length > 0)
        {
            AddStep($"使用缓存的 ipodpatcher：{cached}");
            return (cached, null);
        }

        var url = RockboxArchive.BuildUrl(options.MirrorBaseUrl, RockboxArchive.IpodpatcherRelativePath);
        try
        {
            AddStep($"下载 ipodpatcher：{url}");
            await RockboxArchive.DownloadAsync(
                SharedClient, url, cached, null, "正在下载 ipodpatcher", cancellationToken).ConfigureAwait(false);
            return (cached, null);
        }
        catch (Exception error)
        {
            return (null, $"下载 ipodpatcher 失败：{error.Message}。可在选项中手动指定 ipodpatcher 路径。");
        }
    }

    private async Task<(string? Bootloader, string? Error)> EnsureBootloaderAsync(
        RockboxTarget target,
        RockboxOptions options,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(options.CustomBootloaderPath))
        {
            var custom = Path.GetFullPath(options.CustomBootloaderPath);
            if (!File.Exists(custom))
            {
                return (null, $"指定的引导程序文件不存在：{custom}");
            }

            AddStep($"使用用户自备的引导程序：{custom}");
            return (custom, null);
        }

        if (string.IsNullOrWhiteSpace(target.BootloaderFileName))
        {
            return (null, "该目标没有可用的引导程序文件名，请手动指定引导程序文件。");
        }

        var cached = Path.Combine(CacheDirectory, target.BootloaderFileName);
        if (File.Exists(cached) && new FileInfo(cached).Length > 0)
        {
            AddStep($"使用缓存的引导程序：{cached}");
            return (cached, null);
        }

        var relative = target.UsesMks5lboot
            ? $"bootloader/ipod/{target.BootloaderFileName}"
            : RockboxArchive.BootloaderRelativePath(target);
        var url = RockboxArchive.BuildUrl(options.MirrorBaseUrl, relative);
        try
        {
            AddStep($"下载引导程序：{url}");
            await RockboxArchive.DownloadAsync(
                SharedClient, url, cached, null, "正在下载引导程序", cancellationToken).ConfigureAwait(false);
            if (target.UsesMks5lboot)
            {
                AddStep($"提示：以下地址也提供 iPod classic 引导程序，可作为备用来源：{Mks5lbootBootloaderSource}");
            }

            return (cached, null);
        }
        catch (Exception error)
        {
            return (null, $"下载引导程序失败：{error.Message}（{url}）。可在选项中手动指定引导程序文件路径。");
        }
    }

    private async Task<RockboxCommandResult> RunIpodpatcherAsync(
        string patcher,
        string arguments,
        bool elevate,
        CancellationToken cancellationToken) =>
        await _runner.RunAsync(patcher, arguments, elevate, cancellationToken).ConfigureAwait(false);

    // ---- 只读探测 ----

    private async Task<(bool Installed, string? Output, string? Note)> ProbeBootloaderAsync(
        CancellationToken cancellationToken)
    {
        string? patcher = null;
        if (LastOptions is not null && !string.IsNullOrWhiteSpace(LastOptions.CustomIpodpatcherPath))
        {
            patcher = LastOptions.CustomIpodpatcherPath;
        }
        else
        {
            var cached = Path.Combine(CacheDirectory, "ipodpatcher.exe");
            if (File.Exists(cached))
            {
                patcher = cached;
            }
        }

        if (patcher is null)
        {
            return (false, null, "本地没有 ipodpatcher，无法探测引导程序状态；在设置中提供 ipodpatcher 路径可启用探测。");
        }

        if (!File.Exists(patcher))
        {
            return (false, null, $"配置的 ipodpatcher 不存在（{patcher}），跳过引导程序探测。");
        }

        // 探测是只读动作：只尝试普通权限，避免在探测阶段弹出 UAC。
        var result = await RockboxCommandRunner.RunCapturedAsync(patcher, "--list", cancellationToken)
            .ConfigureAwait(false);
        if (!result.Started)
        {
            return (false, null, "无法启动 ipodpatcher 探测引导程序，引导程序状态未知。");
        }

        if (result.ExitCode != 0)
        {
            return (false, result.Output,
                "普通权限下 ipodpatcher --list 未成功（通常需要管理员权限），引导程序状态未知；" +
                "以管理员身份运行本程序即可探测。");
        }

        var output = result.Output ?? string.Empty;
        var installed = output.Contains("bootloader", StringComparison.OrdinalIgnoreCase)
            && !output.Contains("no bootloader", StringComparison.OrdinalIgnoreCase)
            && !output.Contains("No bootloader", StringComparison.Ordinal);
        return (installed, output, installed
            ? "ipodpatcher --list 输出中出现了引导程序相关条目。"
            : "ipodpatcher --list 未显示引导程序信息，可能未安装引导程序。");
    }

    // ---- 辅助 ----

    /// <summary>查询远端文件长度；失败时返回 -1（不抛异常）。</summary>
    private static async Task<long> QueryLengthAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await SharedClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return -1;
            }

            return response.Content.Headers.ContentLength ?? -1;
        }
        catch (Exception error)
        {
            AppLog.Debug("Rockbox", $"HEAD 请求失败（{url}）：{error.Message}");
            return -1;
        }
    }

    /// <summary>当前渠道对应的站点目录，用于拼 SHA256SUMS。</summary>
    private static string RelativeChannelDirectory(RockboxOptions options)
    {
        var channel = string.Equals(options.Channel?.Trim(), "daily", StringComparison.OrdinalIgnoreCase)
            ? "daily"
            : "release";
        return channel == "daily" ? "daily" : $"release/{RockboxArchive.VersionText(options.ReleaseVersion)}";
    }

    /// <summary>读取 Rockbox 版本号。</summary>
    private static string? ReadVersion(string rockboxRoot)
    {
        var infoPath = Path.Combine(rockboxRoot, InfoFileName);
        if (File.Exists(infoPath))
        {
            try
            {
                foreach (var line in File.ReadLines(infoPath))
                {
                    if (line.StartsWith("Version:", StringComparison.OrdinalIgnoreCase))
                    {
                        var value = line["Version:".Length..].Trim();
                        if (value.Length > 0)
                        {
                            return value;
                        }
                    }
                }
            }
            catch (Exception error)
            {
                AppLog.Debug("Rockbox", $"读取 {infoPath} 失败：{error.Message}");
            }
        }

        var bootImage = Path.Combine(rockboxRoot, "rockbox.ipod");
        if (File.Exists(bootImage))
        {
            try
            {
                // rockbox.ipod 的头部包含版本字符串，退化为第一个可打印文本段。
                var bytes = File.ReadAllBytes(bootImage);
                var match = Regex.Match(Encoding.ASCII.GetString(bytes), @"\d+\.\d+(\.\d+)?");
                if (match.Success)
                {
                    return $"未知（镜像版本标记 {match.Value}）";
                }
            }
            catch (Exception error)
            {
                AppLog.Debug("Rockbox", $"读取 {bootImage} 失败：{error.Message}");
            }
        }

        return null;
    }

    /// <summary>生成备份目录中使用的设备名。</summary>
    private static string DescribeMountPoint(string mountPoint)
    {
        var root = Path.GetFullPath(mountPoint);
        var volumeName = new DirectoryInfo(root).Name.Trim();
        var driveLetter = root.Length >= 2 && root[1] == ':' ? root[..1] : string.Empty;

        var name = volumeName.Length == 0
            ? driveLetter
            : driveLetter.Length == 0 || volumeName.StartsWith(driveLetter, StringComparison.OrdinalIgnoreCase)
                ? volumeName
                : $"{driveLetter}-{volumeName}";

        return Sanitize(name.Length == 0 ? "iPod" : name);
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);
        foreach (var character in name)
        {
            builder.Append(Array.IndexOf(invalid, character) >= 0 ? '_' : character);
        }

        return builder.Length == 0 ? "iPod" : builder.ToString();
    }

    private static string Quote(string path) => $"\"{path}\"";

    private static string FirstLines(string? output, int count = 4)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return "（无输出）";
        }

        var lines = output.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .Take(count)
            .ToArray();
        return lines.Length == 0 ? "（无输出）" : string.Join(" / ", lines);
    }

    private void AddStep(string step)
    {
        _steps.Add(step);
        AppLog.Info("Rockbox", step);
    }

    /// <summary>首次请求提权时向用户说明会看到 UAC 对话框。</summary>
    private void NotifyElevation(string message)
    {
        if (_elevationNotified)
        {
            return;
        }

        _elevationNotified = true;
        AddStep(string.IsNullOrWhiteSpace(message)
            ? "接下来的步骤会弹出 Windows UAC 提示，请选择“是”以允许修改设备固件。"
            : message);
    }

    private RockboxInstallResult Finish(bool success, string message)
    {
        AddStep(message);
        _runner.Flush(_logFile);
        AppLog.Info("Rockbox", $"操作结束：{(success ? "成功" : "失败")}，{message}");

        var backup = Directory.Exists(_backupDirectory) ? _backupDirectory : null;
        return new RockboxInstallResult(success, message, _steps.ToArray(), backup, _logFile);
    }
}
