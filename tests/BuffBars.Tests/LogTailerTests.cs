using System.Threading.Channels;
using BuffBars.Core;
using Xunit;

namespace BuffBars.Tests;

public class LogTailerTests
{
    [Fact]
    public void Character_name_parses_from_filename()
    {
        Assert.Equal("Doofus", LogTailer.CharacterFromFileName(@"C:\x\Logs\eqlog_Doofus_oggok.txt"));
        Assert.Equal("Sira", LogTailer.CharacterFromFileName("eqlog_Sira_oggok.txt"));
        Assert.Null(LogTailer.CharacterFromFileName("dbg.txt"));
        Assert.Null(LogTailer.CharacterFromFileName("eqlog_.txt"));
    }

    [Fact]
    public async Task Tails_appended_lines_from_a_growing_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"eqlog_Testchar_test_{Guid.NewGuid():N}.txt");
        var channel = Channel.CreateUnbounded<TailedLine>();
        try
        {
            // simulate EQ: file held open with sharing, appended over time
            await using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            void Append(string line)
            {
                var bytes = System.Text.Encoding.ASCII.GetBytes(line + "\r\n");
                writer.Write(bytes, 0, bytes.Length);
                writer.Flush();
            }
            Append("[Sat Aug 15 12:00:00 2026] You begin casting Minor Healing.");

            await using var tailer = new LogTailer(path, "Testchar", channel.Writer, backfillBytes: 4096);
            tailer.Start();

            var first = await ReadWithTimeout(channel.Reader);
            Assert.Equal("Testchar", first.Character);
            Assert.Contains("Minor Healing", first.Line);

            Append("[Sat Aug 15 12:00:05 2026] You feel a little better.");
            var second = await ReadWithTimeout(channel.Reader);
            Assert.Contains("a little better", second.Line);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    private static async Task<TailedLine> ReadWithTimeout(ChannelReader<TailedLine> reader)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return await reader.ReadAsync(cts.Token);
    }
}
