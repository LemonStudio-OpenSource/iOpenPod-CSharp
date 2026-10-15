using System.Globalization;
using System.Text.RegularExpressions;

namespace IOpenPod.DeviceRegistry;

/// <summary>标识符归一化。所有比较都在归一化之后进行。</summary>
public static partial class Identifiers
{
    public const ushort AppleUsbVendorId = 0x05AC;

    [GeneratedRegex("^M[A-Z0-9]{4}")]
    private static partial Regex ModelNumberPattern();

    /// <summary>
    /// 归一化 iPod 元数据里出现的型号编号写法。
    /// 形如 <c>X1234</c>、<c>P1234</c> 的前缀会被统一为 <c>M</c>。
    /// </summary>
    public static string NormalizeModelNumber(string? value)
    {
        var text = (value ?? string.Empty).Trim().ToUpperInvariant();
        if (text.Length >= 2 && (text[0] == 'X' || text[0] == 'P'))
        {
            text = "M" + text[1..];
        }

        var match = ModelNumberPattern().Match(text);
        return match.Success ? match.Value : text;
    }

    /// <summary>归一化序列号，只保留字母数字字符并转为大写。</summary>
    public static string NormalizeSerial(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var buffer = new char[value.Length];
        var length = 0;
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                buffer[length++] = char.ToUpperInvariant(character);
            }
        }

        return new string(buffer, 0, length);
    }

    /// <summary>把 USB 标识渲染为 <c>05AC:1263</c> 形式。</summary>
    public static string FormatUsbIdentifier(UsbIdentifier identifier) =>
        $"{identifier.VendorId:X4}:{identifier.ProductId:X4}";

    /// <summary>解析 SysInfo 中使用的十进制或显式十六进制整数。</summary>
    public static int? ParseUnsignedInteger(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return null;
        }

        var space = text.IndexOfAny([' ', '\t']);
        var token = space < 0 ? text : text[..space];

        if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return int.TryParse(token[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex)
                ? hex
                : null;
        }

        return int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;
    }

    /// <summary>归一化传输序列号：去掉 <c>0x</c> 前缀，全零视为无效。</summary>
    public static string NormalizeTransportSerial(string? value)
    {
        var text = (value ?? string.Empty)
            .Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase);
        var normalized = NormalizeSerial(text);
        if (normalized.Length == 0 || normalized.All(character => character == '0'))
        {
            return string.Empty;
        }

        return normalized;
    }
}
