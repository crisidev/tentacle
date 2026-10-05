using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Tentacle.Broker;

/// <summary>
/// Reads the reply a tentacle leaves on a shared root. The tentacle creates that
/// file, so it must not be able to make the server read a link to /dev/zero or a
/// large file (memory) or a FIFO (a thread blocked for good): no symlinks, no
/// blocking open, and at most a few bytes.
/// </summary>
public static partial class ReplyFile
{
    /// <summary>The most the server reads; a reply is a 32-character nonce.</summary>
    public const int MaxBytes = 128;

    private const int ORdOnly = 0;
    private const int ONonBlock = 0x800;
    private const int OCloExec = 0x80000;
    private const int ELoop = 40;

    // O_NOFOLLOW differs: 0400000 on x86-64, 0100000 on arm64.
    private static readonly int ONoFollow = RuntimeInformation.ProcessArchitecture is Architecture.Arm64 or Architecture.Arm ? 0x8000 : 0x20000;

    /// <summary>
    /// Reads at most <see cref="MaxBytes"/> bytes of a file, without following a symlink.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <returns>Its text.</returns>
    /// <exception cref="IOException">Missing, or a symlink.</exception>
    public static string Read(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!OperatingSystem.IsLinux())
        {
            var info = new FileInfo(path);
            if (info.LinkTarget is not null || info.Length > MaxBytes)
            {
                throw new IOException($"{path} is not a short regular file");
            }

            return File.ReadAllText(path);
        }

        var fd = Open(path, ORdOnly | ONoFollow | ONonBlock | OCloExec);
        if (fd < 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            throw errno == ELoop
                ? new IOException($"{path} is a symbolic link")
                : new IOException($"{path}: {Marshal.GetPInvokeErrorMessage(errno)}");
        }

        // Non-blocking and bounded, a FIFO or a device here reads as nothing or as
        // MaxBytes of noise: either way not the nonce.
        using var handle = new Microsoft.Win32.SafeHandles.SafeFileHandle(fd, ownsHandle: true);
        var buffer = new byte[MaxBytes];
        var total = 0;
        while (total < buffer.Length)
        {
            var n = ReadFd(fd, buffer.AsSpan(total));
            if (n <= 0)
            {
                break;
            }

            total += n;
        }

        return Encoding.UTF8.GetString(buffer, 0, total);
    }

    private static unsafe int ReadFd(int fd, Span<byte> buffer)
    {
        fixed (byte* p = buffer)
        {
            return (int)ReadNative(fd, p, (nuint)buffer.Length);
        }
    }

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "read", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static unsafe partial nint ReadNative(int fd, byte* buffer, nuint count);
}
