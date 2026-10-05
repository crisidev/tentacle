using System;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Tentacle.Broker;

/// <summary>
/// Who is on the other end of a unix socket (SO_PEERCRED), and who we are.
/// </summary>
/// <param name="Pid">The peer's pid.</param>
/// <param name="Uid">The peer's uid.</param>
public sealed partial record PeerCredentials(int Pid, int Uid)
{
    private const int SolSocket = 1;
    private const int SoPeerCred = 17;

    /// <summary>
    /// Gets the uid this process runs as, or -1 off Linux.
    /// </summary>
    public static int CurrentUid { get; } = OperatingSystem.IsLinux() ? (int)GetUid() : -1;

    /// <summary>
    /// Reads the peer credentials of a connected unix socket.
    /// </summary>
    /// <param name="socket">The socket.</param>
    /// <returns>The credentials, or null when unavailable.</returns>
    public static PeerCredentials? Of(Socket? socket)
    {
        if (socket is null || !OperatingSystem.IsLinux())
        {
            return null;
        }

        try
        {
            // struct ucred { pid_t pid; uid_t uid; gid_t gid; }
            Span<byte> ucred = stackalloc byte[12];
            var n = socket.GetRawSocketOption(SolSocket, SoPeerCred, ucred);
            return n < 8 ? null : new PeerCredentials(BinaryPrimitives.ReadInt32LittleEndian(ucred), BinaryPrimitives.ReadInt32LittleEndian(ucred[4..]));
        }
        catch (SocketException)
        {
            return null;
        }
    }

    [LibraryImport("libc", EntryPoint = "getuid")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial uint GetUid();
}
