using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace IOpenPod.DeviceRegistry;

/// <summary>
/// 把 iPod 元数据载荷转换为类型化设备证据的纯函数集合。
///
/// 这些方法只接受已经由存储层读取的内容，绝不自行访问文件系统或硬件。
/// </summary>
public static partial class DeviceMetadata
{
    [GeneratedRegex(
        "<key>([^<]+)</key>\\s*(?:(?:<string>(.*?)</string>)|(?:<integer>(.*?)</integer>)|(<true\\s*/>)|(<false\\s*/>))",
        RegexOptions.Singleline)]
    private static partial Regex PlistScalarPattern();

    /// <summary>解析 <c>iPod_Control/Device/SysInfo</c> 载荷。</summary>
    public static DeviceEvidence ParseSysInfo(
        byte[] content,
        string source = "SysInfo",
        EvidenceAuthority authority = EvidenceAuthority.DeviceMetadata)
    {
        var text = FilesystemTextDecoder.Decode(content);
        var values = ParseKeyValues(text);

        var modelNumbers = TextIdentifier(
            Identifiers.NormalizeModelNumber(Get(values, "ModelNumStr")), source, authority);
        var productSerials = TextIdentifier(
            Identifiers.NormalizeSerial(Get(values, "pszSerialNumber")), source, authority);
        var transportSerials = TextIdentifier(
            Identifiers.NormalizeTransportSerial(Get(values, "FirewireGuid")), source, authority);
        var boardNames = TextIdentifier(Get(values, "BoardHwName").Trim(), source, authority);

        var firmware = FirstNonEmpty(values, "visibleBuildID", "VisibleBuildID", "BuildID");
        var firmwareVersions = TextIdentifier(firmware, source, authority);

        IReadOnlyList<DeviceIdentifier<UsbIdentifier>> usbIdentifiers = [];
        var usbProductId = ParseUsbProductId(Get(values, "USBProductID"));
        if (usbProductId is { } sysInfoProductId)
        {
            usbIdentifiers =
            [
                new DeviceIdentifier<UsbIdentifier>(
                    new UsbIdentifier(Identifiers.AppleUsbVendorId, (ushort)sysInfoProductId),
                    source,
                    authority),
            ];
        }

        return new DeviceEvidence
        {
            ModelNumbers = modelNumbers,
            ProductSerials = productSerials,
            TransportSerials = transportSerials,
            UsbIdentifiers = usbIdentifiers,
            BoardHardwareNames = boardNames,
            FirmwareVersions = firmwareVersions,
            FamilyIds = IntegerIdentifier(FirstNonEmpty(values, "FamilyID", "iPodFamily"), source, authority),
            UpdaterFamilyIds = IntegerIdentifier(Get(values, "UpdaterFamilyID"), source, authority),
        };
    }

    /// <summary>
    /// 解析 <c>iPod_Control/Device/SysInfoExtended</c> 载荷。
    ///
    /// 真实设备上是 plist；部分载荷带有传输前缀或被截断，因此优先严格解析，
    /// 失败后只在标量范围内做有界回退，绝不执行载荷内容。
    /// </summary>
    public static DeviceEvidence ParseSysInfoExtended(
        byte[] content,
        string source = "SysInfoExtended",
        EvidenceAuthority authority = EvidenceAuthority.DeviceMetadata)
    {
        var raw = ExtractPlistBytes(content);
        var plist = LoadPlistMapping(raw);

        var serial = MappingText(plist, "SerialNumber");
        if (serial.StartsWith("RAND", StringComparison.OrdinalIgnoreCase))
        {
            serial = string.Empty;
        }

        var transportSerial = Identifiers.NormalizeTransportSerial(FirstMappingText(
            plist, "FireWireGUID", "FirewireGuid", "FireWireGuid", "usb_serial"));
        var modelNumber = Identifiers.NormalizeModelNumber(MappingText(plist, "ModelNumStr"));
        var firmware = FirstMappingText(
            plist, "FireWireVersion", "scsi_revision", "VisibleBuildID", "BuildID", "visibleBuildID");
        var board = FirstMappingText(plist, "BoardHwName", "BoardHwID");

        IReadOnlyList<DeviceIdentifier<UsbIdentifier>> usbIdentifiers = [];
        var usbProductId = MappingInteger(plist, "usb_pid") ?? MappingInteger(plist, "USBProductID");
        if (usbProductId is { } extendedProductId)
        {
            var usbVendorId = MappingInteger(plist, "usb_vid") ?? Identifiers.AppleUsbVendorId;
            usbIdentifiers =
            [
                new DeviceIdentifier<UsbIdentifier>(
                    new UsbIdentifier((ushort)usbVendorId, (ushort)extendedProductId),
                    source,
                    authority),
            ];
        }

        return new DeviceEvidence
        {
            ModelNumbers = TextIdentifier(modelNumber, source, authority),
            ProductSerials = TextIdentifier(Identifiers.NormalizeSerial(serial), source, authority),
            TransportSerials = TextIdentifier(transportSerial, source, authority),
            UsbIdentifiers = usbIdentifiers,
            BoardHardwareNames = TextIdentifier(board, source, authority),
            FirmwareVersions = TextIdentifier(firmware, source, authority),
            FamilyIds = MappingIntegerIdentifier(plist, "FamilyID", source, authority),
            UpdaterFamilyIds = MappingIntegerIdentifier(plist, "UpdaterFamilyID", source, authority),
        };
    }

    private static Dictionary<string, string> ParseKeyValues(string content)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var separator = line.IndexOf(':');
            if (separator < 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            if (key.Length > 0)
            {
                values[key] = line[(separator + 1)..].Trim();
            }
        }

        return values;
    }

    private static string Get(Dictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) ? value : string.Empty;

    private static string FirstNonEmpty(Dictionary<string, string> values, params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = Get(values, key).Trim();
            if (value.Length > 0)
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static byte[] ExtractPlistBytes(byte[] raw)
    {
        var start = -1;
        foreach (var marker in new[] { "<?xml", "<plist", "bplist" })
        {
            var index = IndexOf(raw, Encoding.ASCII.GetBytes(marker));
            if (index >= 0 && (start < 0 || index < start))
            {
                start = index;
            }
        }

        var slice = start > 0 ? raw[start..] : raw;
        var end = slice.Length;
        while (end > 0 && slice[end - 1] == 0)
        {
            end--;
        }

        return slice[..end];
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
        {
            return -1;
        }

        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return i;
            }
        }

        return -1;
    }

    private static Dictionary<string, object> LoadPlistMapping(byte[] raw)
    {
        if (raw.Length > 0)
        {
            var candidates = new List<byte[]> { raw };
            var text = FilesystemTextDecoder.Decode(raw);
            if (text.Contains("<plist", StringComparison.Ordinal) &&
                !text.Contains("</plist>", StringComparison.Ordinal))
            {
                candidates.Add(Encoding.UTF8.GetBytes(text + "\n</dict>\n</plist>"));
            }

            foreach (var candidate in candidates)
            {
                var parsed = TryParseXmlPlist(candidate);
                if (parsed is not null)
                {
                    return parsed;
                }
            }
        }

        return ParsePlistScalars(raw);
    }

    private static Dictionary<string, object>? TryParseXmlPlist(byte[] candidate)
    {
        try
        {
            using var stream = new MemoryStream(candidate);
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreWhitespace = true,
            };

            using var reader = XmlReader.Create(stream, settings);
            var document = new XmlDocument { XmlResolver = null };
            document.Load(reader);

            var root = document.DocumentElement;
            if (root is null || root.Name != "plist")
            {
                return null;
            }

            var dict = root.SelectSingleNode("dict");
            if (dict is null)
            {
                return null;
            }

            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            var nodes = dict.ChildNodes;
            for (var i = 0; i + 1 < nodes.Count; i += 2)
            {
                if (nodes[i]!.Name != "key")
                {
                    continue;
                }

                var key = nodes[i]!.InnerText;
                var valueNode = nodes[i + 1]!;
                result[key] = valueNode.Name switch
                {
                    "string" => valueNode.InnerText,
                    "integer" => ParseLong(valueNode.InnerText) ?? 0L,
                    "real" => double.TryParse(valueNode.InnerText, out var d) ? d : 0d,
                    "true" => true,
                    "false" => false,
                    _ => valueNode.InnerText,
                };
            }

            return result;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static long? ParseLong(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return long.TryParse(trimmed[2..], System.Globalization.NumberStyles.HexNumber, null, out var hex)
                ? hex
                : null;
        }

        return long.TryParse(trimmed, out var value) ? value : null;
    }

    private static Dictionary<string, object> ParsePlistScalars(byte[] raw)
    {
        var text = FilesystemTextDecoder.Decode(raw);
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (Match match in PlistScalarPattern().Matches(text))
        {
            var key = match.Groups[1].Value.Trim();
            if (key.Length == 0)
            {
                continue;
            }

            if (match.Groups[2].Success)
            {
                result[key] = match.Groups[2].Value.Trim();
            }
            else if (match.Groups[3].Success)
            {
                result[key] = ParseLong(match.Groups[3].Value) ?? (object)match.Groups[3].Value.Trim();
            }
            else
            {
                result[key] = match.Groups[4].Success;
            }
        }

        return result;
    }

    private static string MappingText(Dictionary<string, object> values, string key)
    {
        if (!values.TryGetValue(key, out var value))
        {
            return string.Empty;
        }

        return value switch
        {
            string text => text.Trim(),
            long number => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            int number => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => string.Empty,
        };
    }

    private static string FirstMappingText(Dictionary<string, object> values, params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = MappingText(values, key);
            if (value.Length > 0)
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static int? MappingInteger(Dictionary<string, object> values, string key)
    {
        if (!values.TryGetValue(key, out var value))
        {
            return null;
        }

        return value switch
        {
            bool => null,
            long number when number is >= 0 and <= int.MaxValue => (int)number,
            int number when number >= 0 => number,
            string text => Identifiers.ParseUnsignedInteger(text),
            _ => null,
        };
    }

    private static IReadOnlyList<DeviceIdentifier<int>> MappingIntegerIdentifier(
        Dictionary<string, object> values,
        string key,
        string source,
        EvidenceAuthority authority)
    {
        var value = MappingInteger(values, key);
        return value is null
            ? []
            : [new DeviceIdentifier<int>(value.Value, source, authority)];
    }

    private static IReadOnlyList<DeviceIdentifier<string>> TextIdentifier(
        string value,
        string source,
        EvidenceAuthority authority) =>
        string.IsNullOrEmpty(value)
            ? []
            : [new DeviceIdentifier<string>(value, source, authority)];

    private static IReadOnlyList<DeviceIdentifier<int>> IntegerIdentifier(
        string value,
        string source,
        EvidenceAuthority authority)
    {
        var parsed = Identifiers.ParseUnsignedInteger(value);
        return parsed is null
            ? []
            : [new DeviceIdentifier<int>(parsed.Value, source, authority)];
    }

    private static int? ParseUsbProductId(string value)
    {
        var text = value.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        if (!text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && text.Length == 4)
        {
            return int.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out var hex)
                ? hex
                : null;
        }

        return Identifiers.ParseUnsignedInteger(text);
    }
}
