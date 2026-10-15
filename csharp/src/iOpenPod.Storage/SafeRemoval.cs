using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace IOpenPod.Storage;

/// <summary>
/// 原生的安全弹出。
///
/// 遵循标准的可移动介质卸载顺序：加锁卷、卸载卷、解除介质保持、弹出介质。
/// 绝不强制卸载，也绝不掩盖原生拒绝原因。
/// </summary>
public static class SafeRemoval
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FsctlLockVolume = 0x00090018;
    private const uint FsctlUnlockVolume = 0x0009001C;
    private const uint FsctlDismountVolume = 0x00090020;
    private const uint IoctlStorageMediaRemoval = 0x002D4804;
    private const uint IoctlStorageEjectMedia = 0x002D4808;

    /// <summary>尝试安全弹出挂载点对应的卷。</summary>
    public static bool TryEject(string mountPoint, out string message)
    {
        var volume = mountPoint.TrimEnd('\\', '/');
        if (volume.Length == 2 && volume[1] == ':')
        {
            volume += "\\";
        }

        if (!volume.EndsWith('\\'))
        {
            volume += "\\";
        }

        var devicePath = @"\\.\" + volume.TrimEnd('\\');

        SafeFileHandle handle;
        try
        {
            handle = CreateFile(
                devicePath,
                GenericRead | GenericWrite,
                FileShareRead | FileShareWrite,
                IntPtr.Zero,
                OpenExisting,
                0,
                IntPtr.Zero);
        }
        catch (Exception error)
        {
            message = $"无法打开卷 {volume}：{error.Message}";
            return false;
        }

        if (handle.IsInvalid)
        {
            var code = Marshal.GetLastWin32Error();
            handle.Dispose();
            message = code == 5
                ? $"系统拒绝访问卷 {volume}（错误 5：拒绝访问）。请关闭正在使用该设备的程序后重试。"
                : $"无法打开卷 {volume}（Win32 错误 {code}）。";
            return false;
        }

        using (handle)
        {
            FlushFileBuffers(handle);

            if (!DeviceIoControl(handle, FsctlLockVolume, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
            {
                var code = Marshal.GetLastWin32Error();
                message = code switch
                {
                    5 => "卷正在被其他程序使用，无法加锁。请关闭资源管理器窗口或其他音乐软件后重试。",
                    21 => "设备尚未就绪，请稍后重试。",
                    _ => $"卷加锁失败（Win32 错误 {code}）。",
                };
                return false;
            }

            DeviceIoControl(handle, FsctlDismountVolume, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);

            var preventRemoval = IntPtr.Zero;
            DeviceIoControl(handle, IoctlStorageMediaRemoval, preventRemoval, sizeof(int), IntPtr.Zero, 0, out _, IntPtr.Zero);

            if (!DeviceIoControl(handle, IoctlStorageEjectMedia, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
            {
                var code = Marshal.GetLastWin32Error();
                DeviceIoControl(handle, FsctlUnlockVolume, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
                message = $"介质弹出被系统拒绝（Win32 错误 {code}）。设备可能仍在写入，请稍后重试。";
                return false;
            }

            message = $"设备 {volume} 已安全弹出，现在可以拔下。";
            return true;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint controlCode,
        IntPtr inBuffer,
        int inBufferSize,
        IntPtr outBuffer,
        int outBufferSize,
        out int bytesReturned,
        IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FlushFileBuffers(SafeFileHandle handle);
}
