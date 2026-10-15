using System.Management;

namespace IOpenPod.Storage;

/// <summary>一个盘符与物理 USB 设备之间的关联证据。</summary>
public sealed record UsbDiskLink(
    string DriveLetter,
    string DeviceInstanceId,
    string PnpDeviceId,
    ushort? VendorId,
    ushort? ProductId,
    string? SerialNumber,
    string? Model);

/// <summary>
/// 通过 Windows 管理规范（WMI）把盘符关联到物理磁盘与 USB 标识。
/// 任何一步失败都只降级为“无 USB 证据”，绝不抛异常打断设备发现。
/// </summary>
internal static class UsbTopology
{
    public static IReadOnlyList<UsbDiskLink> Query()
    {
        try
        {
            var diskToDrive = MapPartitionsToDrives();
            var usbDevices = QueryUsbDevices();
            var links = new List<UsbDiskLink>();

            using var searcher = new ManagementObjectSearcher(
                "SELECT DeviceID, PNPDeviceID, Model, InterfaceType, SerialNumber FROM Win32_DiskDrive");
            using var disks = searcher.Get();

            foreach (ManagementObject disk in disks)
            {
                using (disk)
                {
                    var pnp = disk["PNPDeviceID"] as string;
                    if (string.IsNullOrEmpty(pnp))
                    {
                        continue;
                    }

                    var interfaceType = disk["InterfaceType"] as string;
                    var isUsb = pnp.StartsWith("USBSTOR", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(interfaceType, "USB", StringComparison.OrdinalIgnoreCase);
                    if (!isUsb)
                    {
                        continue;
                    }

                    var deviceId = disk["DeviceID"] as string ?? string.Empty;
                    var model = disk["Model"] as string;
                    var diskSerial = NormalizeInstance(disk["SerialNumber"] as string);
                    var usb = MatchUsbDevice(pnp, diskSerial, usbDevices);

                    if (!diskToDrive.TryGetValue(deviceId, out var letters))
                    {
                        continue;
                    }

                    foreach (var letter in letters)
                    {
                        links.Add(new UsbDiskLink(
                            DriveLetter: letter,
                            DeviceInstanceId: usb?.DeviceId ?? pnp,
                            PnpDeviceId: pnp,
                            VendorId: usb?.VendorId,
                            ProductId: usb?.ProductId,
                            SerialNumber: usb?.Serial ?? diskSerial,
                            Model: model));
                    }
                }
            }

            return links;
        }
        catch (Exception)
        {
            return Array.Empty<UsbDiskLink>();
        }
    }

    private static Dictionary<string, List<string>> MapPartitionsToDrives()
    {
        var partitionToDisk = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        using (var searcher = new ManagementObjectSearcher(
                   "SELECT Antecedent, Dependent FROM Win32_DiskDriveToDiskPartition"))
        using (var rows = searcher.Get())
        {
            foreach (ManagementObject row in rows)
            {
                using (row)
                {
                    var disk = (row["Antecedent"] as string ?? string.Empty).Split('=').Last().Trim('"');
                    var partition = (row["Dependent"] as string ?? string.Empty).Split('=').Last().Trim('"');
                    if (disk.Length > 0 && partition.Length > 0)
                    {
                        partitionToDisk[partition] = disk;
                    }
                }
            }
        }

        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        using (var searcher = new ManagementObjectSearcher(
                   "SELECT Antecedent, Dependent FROM Win32_LogicalDiskToPartition"))
        using (var rows = searcher.Get())
        {
            foreach (ManagementObject row in rows)
            {
                using (row)
                {
                    var partition = (row["Antecedent"] as string ?? string.Empty).Split('=').Last().Trim('"');
                    var logical = (row["Dependent"] as string ?? string.Empty).Split('=').Last().Trim('"');
                    if (!partitionToDisk.TryGetValue(partition, out var disk) || logical.Length == 0)
                    {
                        continue;
                    }

                    if (!result.TryGetValue(disk, out var list))
                    {
                        list = new List<string>();
                        result[disk] = list;
                    }

                    list.Add(logical);
                }
            }
        }

        return result;
    }

    private sealed record UsbDeviceInfo(string DeviceId, ushort? VendorId, ushort? ProductId, string? Serial);

    private static List<UsbDeviceInfo> QueryUsbDevices()
    {
        var list = new List<UsbDeviceInfo>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT DeviceID FROM Win32_PnPEntity WHERE DeviceID LIKE 'USB\\\\VID_%'");
            using var rows = searcher.Get();
            foreach (ManagementObject row in rows)
            {
                using (row)
                {
                    var deviceId = row["DeviceID"] as string;
                    if (string.IsNullOrEmpty(deviceId))
                    {
                        continue;
                    }

                    var parts = deviceId.Split('\\');
                    if (parts.Length < 2)
                    {
                        continue;
                    }

                    ushort? vendor = null;
                    ushort? product = null;
                    foreach (var token in parts[1].Split('&'))
                    {
                        if (token.StartsWith("VID_", StringComparison.OrdinalIgnoreCase) &&
                            ushort.TryParse(token[4..], System.Globalization.NumberStyles.HexNumber, null, out var v))
                        {
                            vendor = v;
                        }
                        else if (token.StartsWith("PID_", StringComparison.OrdinalIgnoreCase) &&
                                 ushort.TryParse(token[4..], System.Globalization.NumberStyles.HexNumber, null, out var p))
                        {
                            product = p;
                        }
                    }

                    var serial = parts.Length > 2 ? parts[2] : null;
                    list.Add(new UsbDeviceInfo(deviceId, vendor, product, serial));
                }
            }
        }
        catch (Exception)
        {
            // 没有 USB 枚举权限时静默降级。
        }

        return list;
    }

    private static UsbDeviceInfo? MatchUsbDevice(
        string diskPnpDeviceId,
        string? diskSerial,
        List<UsbDeviceInfo> devices)
    {
        var segments = diskPnpDeviceId.Split('\\');
        var instance = segments.Length > 2 ? segments[^1] : string.Empty;
        var trimmed = NormalizeInstance(instance);

        foreach (var device in devices)
        {
            if (device.VendorId != 0x05AC)
            {
                continue;
            }

            var serial = NormalizeInstance(device.Serial);
            if (serial.Length > 0 &&
                (serial.Equals(trimmed, StringComparison.OrdinalIgnoreCase) ||
                 (diskSerial is not null && serial.Equals(diskSerial, StringComparison.OrdinalIgnoreCase))))
            {
                return device;
            }
        }

        // 退化匹配：任何 Apple USB 设备都可以作为弱证据。
        return devices.FirstOrDefault(device => device.VendorId == 0x05AC);
    }

    private static string NormalizeInstance(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var text = value.Trim().TrimEnd('&', '0');
        return text.Length > 8 && text.EndsWith("&0", StringComparison.Ordinal) ? text[..^2] : text;
    }
}
