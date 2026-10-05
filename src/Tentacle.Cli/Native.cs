using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Tentacle.Cli;

/// <summary>
/// The few libc calls the shim needs.
/// </summary>
internal static unsafe partial class Native
{
    private const int SigPipe = 13;
    private const int SigSetMask = 2;
    private const int FSetFd = 2;
    private const int PrioProcess = 0;

    /// <summary>
    /// Replaces this process with <paramref name="path"/>, keeping the PID, the
    /// environment and every fd without CLOEXEC. Only returns on failure.
    /// Jellyfin's Process object then *is* the real ffmpeg: stdin keys, Kill()
    /// and the pipes behave exactly as without Tentacle.
    /// </summary>
    /// <param name="path">The executable.</param>
    /// <param name="argv">The full argv, argv[0] included.</param>
    /// <returns>The errno of the failed execv.</returns>
    public static int Exec(string path, IReadOnlyList<string> argv)
    {
        ResetSignalsForExec();

        var pathBytes = NullTerminated(path);
        var buffers = new byte[argv.Count][];
        for (var i = 0; i < argv.Count; i++)
        {
            buffers[i] = NullTerminated(argv[i]);
        }

        var handles = new GCHandle[argv.Count];
        try
        {
            var pointers = new IntPtr[argv.Count + 1];
            for (var i = 0; i < argv.Count; i++)
            {
                handles[i] = GCHandle.Alloc(buffers[i], GCHandleType.Pinned);
                pointers[i] = handles[i].AddrOfPinnedObject();
            }

            fixed (byte* p = pathBytes)
            {
                fixed (IntPtr* a = pointers)
                {
                    _ = ExecV(p, (byte**)a);
                }
            }

            return Marshal.GetLastPInvokeError();
        }
        finally
        {
            foreach (var handle in handles)
            {
                if (handle.IsAllocated)
                {
                    handle.Free();
                }
            }
        }
    }

    /// <summary>
    /// Clears FD_CLOEXEC so the fd survives exec. .NET opens every fd with CLOEXEC.
    /// </summary>
    /// <param name="fd">The file descriptor.</param>
    public static void ClearCloseOnExec(int fd) => _ = Fcntl(fd, FSetFd, 0);

    /// <summary>
    /// Sends a signal to a process (or, with a negative pid, a process group).
    /// </summary>
    /// <param name="pid">The pid.</param>
    /// <param name="signal">The signal number.</param>
    /// <returns>True on success.</returns>
    public static bool SendSignal(int pid, int signal) => KillProcess(pid, signal) == 0;

    /// <summary>
    /// Gets the uid this process runs as.
    /// </summary>
    /// <returns>The uid.</returns>
    public static int CurrentUid() => (int)GetUid();

    /// <summary>
    /// Sets a process's nice value.
    /// </summary>
    /// <param name="pid">The pid (0 for this process).</param>
    /// <param name="nice">The nice value.</param>
    /// <returns>True on success.</returns>
    public static bool SetNice(int pid, int nice) => SetPriority(PrioProcess, (uint)pid, nice) == 0;

    /// <summary>
    /// Ignored signal dispositions survive execve, and the .NET runtime ignores
    /// SIGPIPE: without this the real ffmpeg would not die on a closed pipe.
    /// Also clear the signal mask so ffmpeg starts with nothing blocked.
    /// </summary>
    private static void ResetSignalsForExec()
    {
        _ = Signal(SigPipe, IntPtr.Zero); // SIG_DFL
        var emptySet = stackalloc byte[128]; // sizeof(sigset_t) on glibc
        new Span<byte>(emptySet, 128).Clear();
        _ = SigProcMask(SigSetMask, emptySet, null);
    }

    /// <summary>
    /// The canonical path: every symlink followed, "." and ".." resolved (realpath(3)).
    /// </summary>
    /// <param name="path">A path that exists.</param>
    /// <returns>The resolved path, or null when it does not exist or cannot be read.</returns>
    public static string? RealPath(string path)
    {
        var bytes = NullTerminated(path);
        byte* resolved;
        fixed (byte* p = bytes)
        {
            resolved = RealPathC(p, null);
        }

        if (resolved is null)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUTF8((IntPtr)resolved);
        }
        finally
        {
            Free(resolved);
        }
    }

    private static byte[] NullTerminated(string value)
    {
        var bytes = new byte[Encoding.UTF8.GetByteCount(value) + 1];
        Encoding.UTF8.GetBytes(value, bytes);
        return bytes;
    }

    [LibraryImport("libc", EntryPoint = "execv", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int ExecV(byte* path, byte** argv);

    [LibraryImport("libc", EntryPoint = "realpath")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial byte* RealPathC(byte* path, byte* resolved);

    [LibraryImport("libc", EntryPoint = "free")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial void Free(byte* pointer);

    [LibraryImport("libc", EntryPoint = "fcntl")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int Fcntl(int fd, int cmd, int arg);

    [LibraryImport("libc", EntryPoint = "getuid")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial uint GetUid();

    [LibraryImport("libc", EntryPoint = "setpriority")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int SetPriority(int which, uint who, int prio);

    [LibraryImport("libc", EntryPoint = "kill")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int KillProcess(int pid, int signal);

    [LibraryImport("libc", EntryPoint = "signal")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial IntPtr Signal(int signum, IntPtr handler);

    [LibraryImport("libc", EntryPoint = "sigprocmask")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int SigProcMask(int how, byte* set, byte* oldset);
}
