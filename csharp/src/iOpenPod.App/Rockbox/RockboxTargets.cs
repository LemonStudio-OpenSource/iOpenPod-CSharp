using System.Globalization;
using System.Text;
using IOpenPod.DeviceRegistry;

namespace IOpenPod.App.Rockbox;

/// <summary>一个 Rockbox 目标机型的描述。</summary>
public sealed record RockboxTarget(
    string Name,
    bool Supported,
    bool IsExperimental,
    string BootloaderMethod,
    string BootloaderFileName,
    string Note)
{
    /// <summary>引导程序是由 ipodpatcher 写入的。</summary>
    public bool UsesIpodpatcher => string.Equals(BootloaderMethod, "ipodpatcher", StringComparison.Ordinal);

    /// <summary>引导程序需要 mks5lboot 在 DFU 模式下写入（iPod classic）。</summary>
    public bool UsesMks5lboot => string.Equals(BootloaderMethod, "mks5lboot", StringComparison.Ordinal);

    /// <summary>下载每日构建时使用的系统文件压缩包名。</summary>
    public string DailyArchiveName => $"rockbox-{Name}.zip";

    /// <summary>下载稳定版时使用的系统文件压缩包名。</summary>
    public string ReleaseArchiveName(string version) => $"rockbox-{Name}-{version}.zip";

    /// <summary>中文显示名，例如 <c>iPod video（目标 ipodvideo）</c>。</summary>
    public string Display => Supported ? $"{Name}" : "不支持";

    public override string ToString() => Name;
}

/// <summary>设备上的 Rockbox 状态。</summary>
public sealed record RockboxStatus(
    bool SystemInstalled,
    string? SystemVersion,
    bool BootloaderInstalled,
    string? BootloaderProbeOutput,
    long InstalledBytes,
    string Note)
{
    /// <summary>面向用户的中文摘要。</summary>
    public string Display => SystemInstalled
        ? $"已安装 Rockbox 系统文件{(SystemVersion is null ? string.Empty : $"（{SystemVersion}）")}，" +
          $"占用 {InstalledBytes / 1024.0 / 1024.0:F1} MB；引导程序：{BootloaderText}。"
        : $"未在设备根目录发现 .rockbox 目录；引导程序：{BootloaderText}。";

    /// <summary>引导程序状态的中文描述。</summary>
    public string BootloaderText => BootloaderInstalled ? "已安装" : "未检测到";
}

/// <summary>
/// Rockbox 目标的识别。
///
/// 映射依据 Rockbox 官方 target 命名；只接受后缀可判断的目标，家族与世代均不区分大小写。
/// 不支持的机型返回 <see cref="RockboxTarget"/>，其 <c>Supported</c> 为 false，Note 说明原因。
/// </summary>
public static class RockboxTargets
{
    /// <summary>Rockbox 引导程序与系统文件的官方下载根地址。</summary>
    public const string OfficialBaseUrl = "https://download.rockbox.org";

    /// <summary>iPod classic 引导程序的备用来源（mks5lboot 使用）。</summary>
    public const string Mks5lbootSource = "https://files.freemyipod.org/~user890104/bootloader-ipodclassic-v1_0";

    /// <summary>不支持的机型共用的占位目标名。</summary>
    private const string UnsupportedName = "unsupported";

    private static readonly RockboxTarget Unsupported = new(
        UnsupportedName,
        false,
        false,
        "unsupported",
        string.Empty,
        "该机型不在 Rockbox 官方支持列表中，无法安装 Rockbox。");

    private static readonly IReadOnlyList<RockboxTarget> Targets =
    [
        new RockboxTarget(
            "ipod1g2g",
            true,
            false,
            "ipodpatcher",
            "bootloader-ipod1g2g.ipod",
            "iPod 第 1/2 代使用 ipodpatcher 写入引导程序。安装完成后请断开并重新连接设备。"),

        new RockboxTarget(
            "ipod3g",
            true,
            false,
            "ipodpatcher",
            "bootloader-ipod3g.ipod",
            "iPod 第 3 代使用 ipodpatcher 写入引导程序。第 3 代需要先执行一次 Apple 固件恢复流程，Rockbox 才能被引导。"),

        new RockboxTarget(
            "ipod4g",
            true,
            false,
            "ipodpatcher",
            "bootloader-ipod4g.ipod",
            "iPod 第 4 代（单色）使用 ipodpatcher 写入引导程序。"),

        new RockboxTarget(
            "ipodcolor",
            true,
            false,
            "ipodpatcher",
            "bootloader-ipodcolor.ipod",
            "iPod 第 4 代（照片/彩色）与 iPod photo 使用 ipodpatcher 写入引导程序。"),

        new RockboxTarget(
            "ipodvideo",
            true,
            false,
            "ipodpatcher",
            "bootloader-ipodvideo.ipod",
            "iPod 第 5/5.5 代（video）使用 ipodpatcher 写入引导程序，是支持最完善的机型。"),

        new RockboxTarget(
            "ipod6g",
            true,
            false,
            "mks5lboot",
            "bootloader-ipod6g.ipod",
            "iPod classic（第 6/6.5/7 代）不能用 ipodpatcher 安装引导程序，必须在 DFU 模式下用 mks5lboot 写入。"),

        new RockboxTarget(
            "ipodmini1g",
            true,
            false,
            "ipodpatcher",
            "bootloader-ipodmini1g.ipod",
            "iPod mini 第 1 代使用 ipodpatcher 写入引导程序。"),

        new RockboxTarget(
            "ipodmini2g",
            true,
            false,
            "ipodpatcher",
            "bootloader-ipodmini2g.ipod",
            "iPod mini 第 2 代使用 ipodpatcher 写入引导程序。"),

        new RockboxTarget(
            "ipodnano1g",
            true,
            false,
            "ipodpatcher",
            "bootloader-ipodnano1g.ipod",
            "iPod nano 第 1 代使用 ipodpatcher 写入引导程序。"),

        new RockboxTarget(
            "ipodnano2g",
            true,
            true,
            "ipodpatcher",
            "bootloader-ipodnano2g.ipodx",
            "iPod nano 第 2 代是实验性支持：其引导与系统文件安装方式与其他机型不同，rockbox.ipod 需要经 crypt_firmware 加密，" +
            "官方下载页提供的是已加密的 .ipodx。本程序可以下载并写入该文件，但强烈建议改用 Rockbox Utility，并自行完成加密与 DFU 步骤。"),

        new RockboxTarget(
            "ipodnano3g",
            true,
            true,
            "ipodpatcher",
            "bootloader-ipodnano3g.ipod",
            "iPod nano 第 3 代是实验性支持：引导程序写入后设备可能无法正常引导，且需要依赖第三方工具完成初始刷写，" +
            "请仅在已充分备份的前提下尝试。"),

        new RockboxTarget(
            UnsupportedName,
            false,
            false,
            "unsupported",
            string.Empty,
            "iPod nano 第 4/5/6/7 代目前没有可用的 Rockbox 支持。"),
    ];

    private static readonly Dictionary<string, RockboxTarget> ByName =
        Targets.ToDictionary(target => target.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>目录中的全部目标，含“不支持”占位项。</summary>
    public static IReadOnlyList<RockboxTarget> All => Targets;

    /// <summary>iPod nano 第 4 代的“不支持”结论（供族谱未覆盖的世代复用）。</summary>
    private static RockboxTarget UnsupportedFor(string reason) =>
        Unsupported with { Note = reason };

    /// <summary>按家族与世代解析目标。<paramref name="generation"/> 可以为空。</summary>
    public static RockboxTarget Resolve(string family, string generation)
    {
        var key = BuildKey(family, generation);
        return key is null ? UnsupportedFor(FamilyReason(family, generation)) : ResolveKey(key);
    }

    /// <summary>按设备档案解析目标。档案为空时返回 null。</summary>
    public static RockboxTarget? Resolve(DeviceProfile? profile)
    {
        if (profile is null)
        {
            return null;
        }

        return Resolve(profile.Family, profile.Generation);
    }

    /// <summary>按目标名取得目标定义。</summary>
    public static RockboxTarget? ByTargetName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return ByName.TryGetValue(name.Trim(), out var target) ? target : null;
    }

    /// <summary>
    /// 描述本次操作会使用的下载地址，供界面提前展示（不发起任何网络请求）。
    /// </summary>
    /// <returns>系统文件地址与引导程序地址的中文说明。</returns>
    public static (string System, string Bootloader) DescribeDownloads(RockboxTarget target, RockboxOptions options)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(options);

        string system;
        if (!target.Supported)
        {
            system = $"无：{target.Note}";
        }
        else if (!string.IsNullOrWhiteSpace(options.CustomSystemZipPath))
        {
            system = $"使用本地文件 {options.CustomSystemZipPath}";
        }
        else
        {
            var relative = RockboxArchive.SystemArchiveRelativePath(target, options);
            system = RockboxArchive.BuildUrl(options.MirrorBaseUrl, relative);
        }

        string bootloader;
        if (!target.Supported || string.IsNullOrWhiteSpace(target.BootloaderFileName))
        {
            bootloader = $"无：{target.Note}";
        }
        else if (!string.IsNullOrWhiteSpace(options.CustomBootloaderPath))
        {
            bootloader = $"使用本地文件 {options.CustomBootloaderPath}";
        }
        else if (target.UsesMks5lboot)
        {
            bootloader =
                $"{RockboxArchive.BuildUrl(options.MirrorBaseUrl, $"bootloader/ipod/{target.BootloaderFileName}")}" +
                $"（需自行获取 mks5lboot 并在 DFU 模式下写入，备用来源 {Mks5lbootSource}）";
        }
        else
        {
            bootloader = RockboxArchive.BuildUrl(
                options.MirrorBaseUrl, RockboxArchive.BootloaderRelativePath(target));
        }

        return (system, bootloader);
    }

    private static RockboxTarget ResolveKey(string key) => key switch
    {
        "ipod1" or "ipod2" => ByName["ipod1g2g"],
        "ipod3" => ByName["ipod3g"],
        "ipod4mono" => ByName["ipod4g"],
        "ipod4color" or "ipod4photo" => ByName["ipodcolor"],
        "ipod5" or "ipod5.5" => ByName["ipodvideo"],
        "classic6" or "classic6.5" or "classic7" => ByName["ipod6g"],
        "mini1" => ByName["ipodmini1g"],
        "mini2" => ByName["ipodmini2g"],
        "nano1" => ByName["ipodnano1g"],
        "nano2" => ByName["ipodnano2g"],
        "nano3" => ByName["ipodnano3g"],
        "nano4" or "nano5" or "nano6" or "nano7" =>
            UnsupportedFor("iPod nano 第 4/5/6/7 代目前没有可用的 Rockbox 支持。"),
        "shuffle" => UnsupportedFor("iPod shuffle 没有屏幕与可刷写的固件分区，Rockbox 不支持该机型。"),
        "touch" => UnsupportedFor("iPod touch 使用 iOS，Rockbox 不支持该机型。"),
        "iphone" => UnsupportedFor("iPhone 使用 iOS，Rockbox 不支持该机型。"),
        _ => UnsupportedFor(FamilyReason(key, string.Empty)),
    };

    /// <summary>调试用：暴露内部匹配键（仅用于验证）。</summary>
    internal static string? DebugKey(string family, string generation) => BuildKey(family, generation);


    /// <summary>
    /// 把家族与世代规范化为内部匹配键，例如 <c>nano3</c>、<c>classic6.5</c>、<c>ipod5.5</c>。
    /// </summary>
    private static string? BuildKey(string family, string generation)
    {
        // 家族名必须在原始文本上判断：归一化会改写罗马数字（i → 1），"iPod" 会因此被破坏。
        var familyToken = MatchFamily(NormalizeFamilyText(family));
        if (familyToken is null)
        {
            return null;
        }

        if (familyToken is "iphone" or "shuffle" or "touch")
        {
            return familyToken;
        }

        var number = NormalizeGeneration(generation);
        if (number is null)
        {
            return null;
        }

        return familyToken + number;
    }

    /// <summary>把已经去掉空白与 "gen" 字样的家族名归一化为 ipod / classic / mini / nano / shuffle / touch / iphone。</summary>
    private static string? MatchFamily(string text)
    {
        if (text.Length == 0)
        {
            return null;
        }

        // 更具体的家族必须先判断，避免 "ipodnano" 被 "ipod" 抢先匹配。
        if (text.Contains("classic", StringComparison.Ordinal))
        {
            return "classic";
        }

        if (text.Contains("mini", StringComparison.Ordinal))
        {
            return "mini";
        }

        if (text.Contains("nano", StringComparison.Ordinal))
        {
            return "nano";
        }

        if (text.Contains("shuffle", StringComparison.Ordinal))
        {
            return "shuffle";
        }

        if (text.Contains("touch", StringComparison.Ordinal))
        {
            return "touch";
        }

        if (text.Contains("iphone", StringComparison.Ordinal))
        {
            return "iphone";
        }

        if (text.Contains("ipod", StringComparison.Ordinal))
        {
            return "ipod";
        }

        return null;
    }

    /// <summary>把世代表达式归一化为 "1"、"5.5"、"6.5"、"4mono"、"4photo"、"4color" 等后缀。</summary>
    private static string? NormalizeGeneration(string generation)
    {
        var text = Squash(generation);
        if (text.Length == 0)
        {
            return "1";
        }
        if (text.Contains("mono", StringComparison.Ordinal))
        {
            return "4mono";
        }

        if (text.Contains("photo", StringComparison.Ordinal))
        {
            return "4photo";
        }

        if (text.Contains("color", StringComparison.Ordinal) || text.Contains("colour", StringComparison.Ordinal))
        {
            return "4color";
        }

        var number = ExtractNumber(text);
        return number ?? (text is "unknown" or "unidentified" ? "1" : null);
    }

    /// <summary>从世代文本中提取前导数字（支持 5.5、6.5 这类小数）。</summary>
    private static string? ExtractNumber(string text)
    {
        var index = 0;
        while (index < text.Length && !char.IsAsciiDigit(text[index]))
        {
            index++;
        }

        if (index >= text.Length)
        {
            return null;
        }

        var builder = new StringBuilder(4);
        var seenDot = false;
        for (var position = index; position < text.Length; position++)
        {
            var character = text[position];
            if (char.IsAsciiDigit(character))
            {
                builder.Append(character);
                continue;
            }

            if (character == '.' && !seenDot && builder.Length > 0)
            {
                // 小数部分必须紧跟数字，避免把 "4th gen 2" 里的句点误当成小数。
                if (position + 1 < text.Length && char.IsAsciiDigit(text[position + 1]))
                {
                    seenDot = true;
                    builder.Append('.');
                    continue;
                }
            }

            break;
        }

        if (builder.Length == 0)
        {
            return null;
        }

        var value = builder.ToString().TrimEnd('.');
        return value.Length == 0 ? null : value;
    }

    /// <summary>
    /// 家族名归一化：去空白与连字符、去掉 "gen" 字样、转小写，但<em>不</em>改写罗马数字，
    /// 以免把 "iPod" 变成 "1Pod"。
    /// </summary>
    private static string NormalizeFamilyText(string? value) => Flatten(value);

    /// <summary>去掉空白、连字符、"gen" 字样与罗马数字，并转为小写（仅用于世代）。</summary>
    private static string Squash(string? value)
    {
        var text = Flatten(value);

        // 罗马数字世代（iPod 5G / 6G 这类写法）。
        text = text.Replace("vii", "7", StringComparison.Ordinal)
            .Replace("iii", "3", StringComparison.Ordinal)
            .Replace("vi", "6", StringComparison.Ordinal)
            .Replace("iv", "4", StringComparison.Ordinal)
            .Replace("ii", "2", StringComparison.Ordinal)
            .Replace("v", "5", StringComparison.Ordinal)
            .Replace("i", "1", StringComparison.Ordinal);

        return text;
    }

    /// <summary>去掉空白、连字符与 "gen" 字样，并转为小写。</summary>
    private static string Flatten(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(character) || character == '.')
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString().Replace("gen", string.Empty, StringComparison.Ordinal);
    }

    /// <summary>生成“不支持”时的中文原因。</summary>
    private static string FamilyReason(string family, string generation)
    {
        var name = string.IsNullOrWhiteSpace(generation) ? family : $"{family} {generation}";
        name = name.Trim();
        return name.Length == 0
            ? "无法确定设备型号，无法判断 Rockbox 支持情况。请先在设备页确认型号。"
            : $"无法为“{name}”确定 Rockbox 目标机型，可能是不受支持的设备（iPod shuffle / touch / iPhone 或非 iPod 播放器）。";
    }
}
