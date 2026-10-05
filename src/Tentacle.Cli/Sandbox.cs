using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Tentacle.Cli;

/// <summary>
/// Confines the jobs a tentacle runs with Landlock, so whoever controls the broker
/// gets ffmpeg on the shared roots and not the worker's user. The broker decides
/// the command line; the agent alone decides TENTACLE_ROOTS. A job may read and
/// write under those roots, read the system (/usr, /etc, /sys, /proc...), use
/// /dev and /tmp, and nothing else: not /config, the token file or another
/// process. On Linux 6.7+ it may not listen on TCP, on 6.12+ not signal the agent
/// (see <see cref="Protections"/>).
///
/// The agent starts each job as `tentacle sandbox ... -- ffmpeg args`: that
/// process restricts itself and then execs ffmpeg, keeping its pid.
/// </summary>
internal sealed partial class Sandbox
{
    /// <summary>The command the agent starts jobs through.</summary>
    public const string Command = "sandbox";

    // Landlock (linux/landlock.h): the same syscall numbers on x86-64 and arm64.
    private const nint SysCreateRuleset = 444;
    private const nint SysAddRule = 445;
    private const nint SysRestrictSelf = 446;
    private const uint CreateRulesetVersion = 1;
    private const int RulePathBeneath = 1;
    private const int PrSetNoNewPrivs = 38;
    private const int OPath = 0x200000;
    private const int OCloExec = 0x80000;
    private const int ENoEnt = 2;

    private const ulong Execute = 1 << 0;
    private const ulong WriteFile = 1 << 1;
    private const ulong ReadFile = 1 << 2;
    private const ulong ReadDir = 1 << 3;
    private const ulong RemoveDir = 1 << 4;
    private const ulong RemoveFile = 1 << 5;
    private const ulong MakeDir = 1 << 7;
    private const ulong MakeReg = 1 << 8;
    private const ulong Refer = 1 << 13;
    private const ulong Truncate = 1 << 14;
    private const ulong NetBindTcp = 1 << 0;
    private const ulong ScopeAbstractUnixSocket = 1 << 0;
    private const ulong ScopeSignal = 1 << 1;

    private const ulong SystemAccess = Execute | ReadFile | ReadDir;
    private const ulong WritableAccess = ReadFile | ReadDir | WriteFile | Truncate | RemoveDir | RemoveFile | MakeDir | MakeReg | Refer;
    private const ulong DeviceAccess = ReadFile | ReadDir | WriteFile | Truncate;

    /// <summary>What a job may read (and run ffmpeg from), besides the roots.</summary>
    private static readonly string[] SystemPaths = ["/usr", "/lib", "/lib64", "/lib32", "/bin", "/sbin", "/etc", "/opt", "/sys", "/proc"];

    /// <summary>Scratch space a job may write besides the roots.</summary>
    private static readonly string[] ScratchPaths = ["/tmp", "/dev/shm"];

    private Sandbox(IReadOnlyList<string> roots, int abi)
    {
        Roots = roots;
        Abi = abi;
    }

    /// <summary>Gets the directories jobs may read and write.</summary>
    public IReadOnlyList<string> Roots { get; }

    /// <summary>Gets the kernel's Landlock ABI version.</summary>
    public int Abi { get; }

    /// <summary>
    /// The sandbox from TENTACLE_ROOTS (absolute directories, ':'-separated), or
    /// null when it is not set. Set, it is required: a kernel without Landlock
    /// is an error, not a quiet fallback.
    /// </summary>
    /// <param name="value">TENTACLE_ROOTS.</param>
    /// <returns>The sandbox, or null.</returns>
    /// <exception cref="ArgumentException">A root is not absolute, or Landlock is unavailable.</exception>
    public static Sandbox? Create(string? value)
    {
        var roots = ParseRoots(value);
        if (roots.Count == 0)
        {
            return null;
        }

        var abi = AbiVersion();
        return abi < 1
            ? throw new ArgumentException("TENTACLE_ROOTS is set but this kernel has no Landlock (5.13+, enabled in the LSM list) or a seccomp profile blocks it")
            : new Sandbox(roots, abi);
    }

    /// <summary>
    /// Parses TENTACLE_ROOTS: absolute paths, no "..", trailing slashes dropped.
    /// </summary>
    /// <param name="value">The variable.</param>
    /// <returns>The roots, in order, without duplicates.</returns>
    public static IReadOnlyList<string> ParseRoots(string? value)
    {
        var roots = new List<string>();
        foreach (var part in (value ?? string.Empty).Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!part.StartsWith('/') || part.Split('/').Any(s => s is ".." or "."))
            {
                throw new ArgumentException($"TENTACLE_ROOTS: '{part}' is not an absolute path");
            }

            var root = part.Length > 1 ? part.TrimEnd('/') : part;
            if (root == "/")
            {
                throw new ArgumentException("TENTACLE_ROOTS: '/' would allow everything");
            }

            if (!roots.Contains(root, StringComparer.Ordinal))
            {
                roots.Add(root);
            }
        }

        return roots;
    }

    /// <summary>
    /// `tentacle sandbox [--nice N] [--root DIR]... -- BINARY [ARGS]...`: restricts
    /// this process, then execs the binary. Only returns on failure.
    /// </summary>
    /// <param name="args">The arguments after "sandbox".</param>
    /// <returns>126 when it could not confine or exec.</returns>
    public static int Run(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var roots = new List<string>();
        var nice = 0;
        var i = 0;
        for (; i < args.Count && args[i] != "--"; i++)
        {
            switch (args[i])
            {
                case "--root" when i + 1 < args.Count:
                    roots.Add(args[++i]);
                    break;
                case "--nice" when i + 1 < args.Count && int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var n):
                    nice = n;
                    i++;
                    break;
                default:
                    return Refuse($"unexpected argument '{args[i]}'");
            }
        }

        if (i + 1 >= args.Count)
        {
            return Refuse("usage: tentacle sandbox [--nice N] [--root DIR]... -- BINARY [ARGS]...");
        }

        // Everything exec needs is ready before Landlock applies: after it, not even
        // the runtime may load another file.
        var binary = args[i + 1];
        var argv = args.Skip(i + 1).ToArray();
        _ = Console.Error;
        try
        {
            if (nice > 0)
            {
                Native.SetNice(0, nice);
            }

            Confine(roots, Path.GetDirectoryName(binary));
        }
        catch (IOException e)
        {
            return Refuse(e.Message);
        }

        var errno = Native.Exec(binary, argv);
        return Refuse($"exec {binary}: {Marshal.GetPInvokeErrorMessage(errno)}");
    }

    /// <summary>
    /// The command line that runs <paramref name="binary"/> confined.
    /// </summary>
    /// <param name="self">This executable.</param>
    /// <param name="binary">The real ffmpeg or ffprobe.</param>
    /// <param name="nice">The nice value, 0 for none.</param>
    /// <returns>argv after the executable, ending with the binary; the job's arguments follow.</returns>
    public IReadOnlyList<string> Wrap(string self, string binary, int nice)
    {
        ArgumentNullException.ThrowIfNull(self);
        var argv = new List<string> { Command };
        if (nice > 0)
        {
            argv.Add("--nice");
            argv.Add(nice.ToString(CultureInfo.InvariantCulture));
        }

        foreach (var root in Roots)
        {
            argv.Add("--root");
            argv.Add(root);
        }

        argv.Add("--");
        argv.Add(binary);
        return argv;
    }

    /// <summary>
    /// Whether a path is under one of the roots.
    /// </summary>
    /// <param name="path">An absolute path.</param>
    /// <returns>True when a job could reach it.</returns>
    /// <remarks>
    /// Landlock confines by the directories the roots resolve to, so each root is
    /// compared by its real path too; <paramref name="path"/> should already be resolved.
    /// </remarks>
    public bool Allows(string path) => Roots.Any(r => Tentacle.Protocol.ArgvAnalysis.IsUnder(path, r)
        || (Native.RealPath(r) is { } real && Tentacle.Protocol.ArgvAnalysis.IsUnder(path, real)));

    /// <summary>
    /// For the dashboard and the log.
    /// </summary>
    /// <returns>A one-line description.</returns>
    public string Describe() => $"Landlock ABI {Abi}: {Protections(Abi)}, roots {string.Join(", ", Roots)}";

    /// <summary>
    /// What a Landlock ABI enforces: files from ABI 1 (Linux 5.13), TCP listen from
    /// ABI 4 (6.7), signals and abstract unix sockets from ABI 6 (6.12). Older kernels
    /// confine files only, and the dashboard and log say so.
    /// </summary>
    /// <param name="abi">The ABI version.</param>
    /// <returns>A short description.</returns>
    internal static string Protections(int abi) => abi switch
    {
        >= 6 => "files, TCP listen, signals",
        >= 4 => "files, TCP listen; not signals (Linux 6.12+)",
        _ => "files only; not TCP listen (Linux 6.7+) or signals (Linux 6.12+)",
    };

    /// <summary>
    /// The kernel's Landlock ABI version, or 0 when there is none.
    /// </summary>
    /// <returns>The version.</returns>
    private static int AbiVersion()
    {
        if (!OperatingSystem.IsLinux())
        {
            return 0;
        }

        var version = Syscall(SysCreateRuleset, 0, 0, (nint)CreateRulesetVersion);
        return version > 0 ? (int)version : 0;
    }

    private static unsafe void Confine(IReadOnlyList<string> roots, string? binaryDir)
    {
        var abi = AbiVersion();
        if (abi < 1)
        {
            throw new IOException("this kernel has no Landlock");
        }

        // Every right this ABI knows is handled, so anything not granted below is denied.
        var handled = 0x1FFFUL | (abi >= 2 ? Refer : 0) | (abi >= 3 ? Truncate : 0);

        // struct landlock_ruleset_attr { u64 handled_access_fs, handled_access_net, scoped; }
        var attr = stackalloc ulong[3];
        attr[0] = handled;
        attr[1] = abi >= 4 ? NetBindTcp : 0;
        attr[2] = abi >= 6 ? ScopeSignal | ScopeAbstractUnixSocket : 0;
        var size = abi >= 6 ? 24 : abi >= 4 ? 16 : 8;
        var ruleset = (int)Syscall(SysCreateRuleset, (nint)attr, size, 0);
        if (ruleset < 0)
        {
            throw new IOException($"landlock_create_ruleset: {Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError())}");
        }

        try
        {
            foreach (var path in SystemPaths.Append(binaryDir ?? "/usr"))
            {
                Allow(ruleset, path, SystemAccess & handled);
            }

            Allow(ruleset, "/dev", DeviceAccess & handled);
            foreach (var path in ScratchPaths.Concat(roots))
            {
                Allow(ruleset, path, WritableAccess & handled);
            }

            if (SetNoNewPrivs(PrSetNoNewPrivs, 1, 0, 0, 0) != 0)
            {
                throw new IOException($"prctl(NO_NEW_PRIVS): {Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError())}");
            }

            if (Syscall(SysRestrictSelf, ruleset, 0, 0) != 0)
            {
                throw new IOException($"landlock_restrict_self: {Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError())}");
            }
        }
        finally
        {
            _ = Close(ruleset);
        }
    }

    /// <summary>
    /// Grants <paramref name="access"/> beneath a directory; one that does not
    /// exist here is skipped.
    /// </summary>
    private static unsafe void Allow(int ruleset, string path, ulong access)
    {
        var fd = OpenPath(path, OPath | OCloExec);
        if (fd < 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            if (errno == ENoEnt)
            {
                return;
            }

            throw new IOException($"{path}: {Marshal.GetPInvokeErrorMessage(errno)}");
        }

        try
        {
            // struct landlock_path_beneath_attr { u64 allowed_access; s32 parent_fd; } __packed
            var rule = stackalloc byte[12];
            *(ulong*)rule = access;
            *(int*)(rule + 8) = fd;
            if (Syscall(SysAddRule, ruleset, RulePathBeneath, (nint)rule) != 0)
            {
                throw new IOException($"landlock_add_rule {path}: {Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError())}");
            }
        }
        finally
        {
            _ = Close(fd);
        }
    }

    private static int Refuse(string message)
    {
        Console.Error.WriteLine($"tentacle sandbox: {message}");
        return 126;
    }

    // syscall(2) is variadic; on x86-64 and arm64 Linux integer arguments travel
    // in the same registers either way.
    [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial nint Syscall(nint number, nint a1, nint a2, nint a3, nint a4 = 0);

    [LibraryImport("libc", EntryPoint = "prctl", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int SetNoNewPrivs(int option, nuint a2, nuint a3, nuint a4, nuint a5);

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int OpenPath(string path, int flags);

    [LibraryImport("libc", EntryPoint = "close")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int Close(int fd);
}
