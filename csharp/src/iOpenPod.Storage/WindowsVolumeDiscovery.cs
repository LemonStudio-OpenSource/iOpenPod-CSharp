using System.Runtime.InteropServices;

namespace IOpenPod.Storage;

/// <summary>Windows 原生卷枚举。使用 Win32 卷 API，并在可用时关联 USB 标识。</summary>
public sealed class WindowsVolumeDiscovery : IVolumeDiscovery
{
    private const int MaxPath = 260;
    private const uint DriveRemovable = 2;
    private const uint DriveFixed = 3;
    private const uint DriveRemote = 4;
    private const uint DriveCdRom = 5;
    private const uint DriveRamdisk = 6;
    private const uint DriveNoRootDir = 7;

    public IReadOnlyList<VolumeObservation> Discover()
    {
        var results = new List<VolumeObservation>();
        var buffer = new char[512];
        var length = GetLogicalDriveStrings(buffer.Length, buffer);
        if (length == 0)
        {
            return results;
        }

        var driveMap = UsbTopology.Query();

        var roots = new string(buffer, 0, (int)length)
            .Split('\0', StringSplitOptions.RemoveEmptyEntries);

        foreach (var root in roots)
        {
            var observation = Describe(root, driveMap);
            if (observation is not null)
            {
                results.Add(observation);
            }
        }

        return results;
    }

    private static VolumeObservation? Describe(string root, IReadOnlyList<UsbDiskLink> usbLinks)
    {
        var driveType = GetDriveType(root);
        if (driveType is DriveCdRom or DriveRamdisk or DriveNoRootDir)
        {
            return null;
        }

        var volumeName = new char[MaxPath + 1];
        var fileSystemName = new char[MaxPath + 1];
        uint serialNumber = 0;
        uint maxComponentLength = 0;
        uint fileSystemFlags = 0;

        var ok = GetVolumeInformation(
            root,
            volumeName,
            volumeName.Length,
            ref serialNumber,
            ref maxComponentLength,
            ref fileSystemFlags,
            fileSystemName,
            fileSystemName.Length);

        if (!ok)
        {
            return null;
        }

        var label = new string(volumeName).TrimEnd('\0');
        var fileSystem = new string(fileSystemName).TrimEnd('\0');

        long totalBytes = 0;
        long freeBytes = 0;
        if (GetDiskFreeSpaceEx(root, out var freeToCaller, out var total, out _))
        {
            totalBytes = (long)total;
            freeBytes = (long)freeToCaller;
        }

        var isRemovable = driveType == DriveRemovable;
        var isNetwork = driveType == DriveRemote;
        var devicePath = usbLinks
            .FirstOrDefault(link => string.Equals(link.DriveLetter, root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            ?.DeviceInstanceId ?? string.Empty;

        var identity = new VolumeIdentity(
            Serial: serialNumber == 0 ? string.Empty : serialNumber.ToString("X8"),
            Label: label,
            FileSystem: fileSystem,
            MountPoint: root,
            DevicePath: devicePath,
            TotalBytes: totalBytes,
            FreeBytes: freeBytes);

        string? diagnostic = null;
        var writeSafe = true;

        if ((fileSystemFlags & 0x00080000) != 0)
        {
            writeSafe = false;
            diagnostic = "文件系统已标记为只读。";
        }
        else if (isNetwork)
        {
            diagnostic = "网络驱动器：可以浏览，但不视为 iPod 连接。";
        }

        return new VolumeObservation(
            identity,
            IsRemovable: isRemovable,
            IsWriteSafe: writeSafe,
            IsReady: true,
            Diagnostic: diagnostic);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLogicalDriveStrings(int length, char[] buffer);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetDriveType(string rootPathName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeInformation(
        string rootPathName,
        char[] volumeNameBuffer,
        int volumeNameSize,
        ref uint volumeSerialNumber,
        ref uint maximumComponentLength,
        ref uint fileSystemFlags,
        char[] fileSystemNameBuffer,
        int fileSystemNameSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetDiskFreeSpaceEx(
        string directoryName,
        out ulong freeBytesAvailableToCaller,
        out ulong totalNumberOfBytes,
        out ulong totalNumberOfFreeBytes);
}
