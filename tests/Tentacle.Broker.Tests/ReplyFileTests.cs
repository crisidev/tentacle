using System;
using System.Diagnostics;
using System.IO;
using Tentacle.Broker;
using Xunit;

namespace Tentacle.Broker.Tests;

public class ReplyFileTests
{
    [Fact]
    public void ReadsAShortReply()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "abc123\n");
            Assert.Equal("abc123\n", ReplyFile.Read(path));
            Assert.Throws<IOException>(() => ReplyFile.Read(path + ".missing"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ATentacleCannotMakeTheServerReadMuchOrWait()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var dir = Directory.CreateTempSubdirectory("tentacle-reply-").FullName;
        try
        {
            // A link to anything is refused, /dev/zero included.
            var link = Path.Combine(dir, "link");
            File.CreateSymbolicLink(link, "/dev/zero");
            Assert.Contains("symbolic link", Assert.Throws<IOException>(() => ReplyFile.Read(link)).Message, StringComparison.Ordinal);

            // A large file is read up to MaxBytes.
            var big = Path.Combine(dir, "big");
            File.WriteAllText(big, new string('x', 1 << 20));
            Assert.Equal(ReplyFile.MaxBytes, ReplyFile.Read(big).Length);

            // A FIFO with no writer would block a plain open forever.
            var fifo = Path.Combine(dir, "fifo");
            using (var mkfifo = Process.Start("mkfifo", fifo))
            {
                mkfifo.WaitForExit();
            }

            var clock = Stopwatch.StartNew();
            Assert.Equal(string.Empty, ReplyFile.Read(fifo));
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
