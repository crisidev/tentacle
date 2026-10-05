using System;
using Tentacle.Protocol;

namespace Tentacle.Cli;

/// <summary>
/// Entry point of the multi-call `tentacle` binary.
/// </summary>
internal static class Program
{
    private const string Usage = """
        usage: tentacle <command>

          version        print the version
          agent          run the worker agent
          agent-status   exit 0 if the agent is up (k8s readiness probe)
          sandbox        run a job confined by Landlock (the agent's launcher)

        Invoked as ffmpeg or ffprobe (symlinks), tentacle is the shim Jellyfin runs.
        """;

    private static int Main(string[] args)
    {
        var mode = Invocation.Resolve(Invocation.ReadArgv0());
        if (mode != ToolMode.Tentacle)
        {
            return Shim.Run(mode, args);
        }

        var command = args.Length > 0 ? args[0] : "help";
        switch (command)
        {
            case "version":
            case "--version":
                Console.Out.WriteLine($"tentacle {ProtocolInfo.ProductVersion} (protocol {ProtocolInfo.Version})");
                return 0;
            case "agent":
                return Agent.Run();
            case "agent-status":
                return Agent.Status();
            case Sandbox.Command:
                return Sandbox.Run(args[1..]);
            case "help":
            case "--help":
            case "-h":
                Console.Out.WriteLine(Usage);
                return 0;
            default:
                Console.Error.WriteLine($"tentacle: unknown command '{command}'");
                Console.Error.WriteLine(Usage);
                return 2;
        }
    }
}
