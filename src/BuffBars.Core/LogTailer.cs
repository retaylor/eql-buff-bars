using System.Text;
using System.Threading.Channels;

namespace BuffBars.Core;

public readonly record struct TailedLine(string Character, string Line);

/// <summary>
/// Follows one live eqlog file. EQ holds the file open for append, so the stream MUST be
/// opened FileShare.ReadWrite | Delete. Pattern (from EQLogParser): drain to EOF, sleep
/// 200ms, repeat; truncation (length &lt; position) or delete/rename triggers a reopen loop.
/// </summary>
public sealed class LogTailer : IAsyncDisposable
{
    public const int PollMs = 200;

    public string FilePath { get; }
    public string Character { get; }

    private readonly ChannelWriter<TailedLine> _out;
    private readonly long _backfillBytes;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public LogTailer(string filePath, string character, ChannelWriter<TailedLine> output, long backfillBytes = 0)
    {
        FilePath = filePath;
        Character = character;
        _out = output;
        _backfillBytes = backfillBytes;
    }

    /// <summary>Parses "eqlog_Charname_server.txt" into the character name; null if not an eqlog.</summary>
    public static string? CharacterFromFileName(string path)
    {
        var name = Path.GetFileName(path);
        if (!name.StartsWith("eqlog_", StringComparison.OrdinalIgnoreCase)) return null;
        if (!name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) return null;
        var body = name[6..^4];
        var us = body.IndexOf('_');
        if (us <= 0) return null;
        return body[..us];
    }

    public void Start() => _loop ??= Task.Run(RunAsync);

    private async Task RunAsync()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            FileStream? fs = null;
            StreamReader? reader = null;
            try
            {
                fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 131072, FileOptions.SequentialScan);
                var start = Math.Max(0, fs.Length - _backfillBytes);
                fs.Seek(start, SeekOrigin.Begin);
                reader = new StreamReader(fs, Encoding.Latin1);
                if (start > 0) await reader.ReadLineAsync(ct);   // discard partial line

                var lastLength = fs.Length;
                while (!ct.IsCancellationRequested)
                {
                    string? line;
                    while ((line = await reader.ReadLineAsync(ct)) is not null)
                    {
                        if (line.Length > LogLineReader.PrefixLength + 1)
                            await _out.WriteAsync(new TailedLine(Character, line), ct);
                    }
                    await Task.Delay(PollMs, ct);
                    var len = new FileInfo(FilePath).Length;
                    if (len < lastLength) break;                 // truncated - reopen from start
                    lastLength = len;
                }
            }
            catch (OperationCanceledException) { return; }
            catch (FileNotFoundException) { }
            catch (IOException) { }
            finally
            {
                reader?.Dispose();
                fs?.Dispose();
            }
            try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { return; }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_loop is not null)
        {
            try { await _loop; } catch { /* shutdown */ }
        }
        _cts.Dispose();
    }
}

/// <summary>
/// Watches the Logs directory: one LogTailer per eqlog_*.txt, attaching to new files as they
/// appear (a character typing /log on mid-session gets picked up within the rescan interval).
/// </summary>
public sealed class LogWatcher : IAsyncDisposable
{
    public const int RescanMs = 5000;

    private readonly string _logsDir;
    private readonly ChannelWriter<TailedLine> _out;
    private readonly long _backfillBytes;
    private readonly Dictionary<string, LogTailer> _tailers = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public LogWatcher(string logsDir, ChannelWriter<TailedLine> output, long backfillBytes)
    {
        _logsDir = logsDir;
        _out = output;
        _backfillBytes = backfillBytes;
    }

    public IReadOnlyCollection<string> Characters
    {
        get { lock (_tailers) return _tailers.Values.Select(t => t.Character).ToList(); }
    }

    public void Start() => _loop ??= Task.Run(RunAsync);

    private async Task RunAsync()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                foreach (var path in Directory.EnumerateFiles(_logsDir, "eqlog_*.txt"))
                {
                    var character = LogTailer.CharacterFromFileName(path);
                    if (character is null) continue;
                    lock (_tailers)
                    {
                        if (_tailers.ContainsKey(path)) continue;
                        var tailer = new LogTailer(path, character, _out, _backfillBytes);
                        _tailers[path] = tailer;
                        tailer.Start();
                    }
                }
            }
            catch (DirectoryNotFoundException) { }
            try { await Task.Delay(RescanMs, ct); } catch (OperationCanceledException) { return; }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_loop is not null) { try { await _loop; } catch { } }
        List<LogTailer> tailers;
        lock (_tailers) tailers = _tailers.Values.ToList();
        foreach (var t in tailers) await t.DisposeAsync();
        _cts.Dispose();
    }
}
