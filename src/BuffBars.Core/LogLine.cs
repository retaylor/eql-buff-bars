namespace BuffBars.Core;

/// <summary>
/// Splits a raw EQ log line `[Www Mmm DD HH:MM:SS YYYY] action` into timestamp + action.
/// Timestamp re-parse is skipped while the date substring is unchanged (EQ emits bursts of
/// lines within the same second) - the same optimization EQLogParser uses.
/// </summary>
public sealed class LogLineReader
{
    public const int PrefixLength = 27;      // "[Www Mmm DD HH:MM:SS YYYY] "

    private string _lastStamp = "";
    private DateTime _lastTime;

    public bool TryParse(string line, out DateTime ts, out string action)
    {
        ts = default;
        action = "";
        if (line.Length <= PrefixLength + 1 || line[0] != '[' || line[25] != ']')
            return false;

        var stamp = line.Substring(1, 24);
        if (stamp == _lastStamp)
        {
            ts = _lastTime;
        }
        else
        {
            if (!TryParseStamp(stamp, out ts)) return false;
            _lastStamp = stamp;
            _lastTime = ts;
        }
        action = line[PrefixLength..];
        return true;
    }

    // "Sat Aug 01 00:00:00 2026" - fixed-width, zero-padded asctime
    private static bool TryParseStamp(string s, out DateTime ts)
    {
        ts = default;
        if (s.Length != 24) return false;
        var month = (s[4], s[5], s[6]) switch
        {
            ('J', 'a', 'n') => 1, ('F', 'e', 'b') => 2, ('M', 'a', 'r') => 3,
            ('A', 'p', 'r') => 4, ('M', 'a', 'y') => 5, ('J', 'u', 'n') => 6,
            ('J', 'u', 'l') => 7, ('A', 'u', 'g') => 8, ('S', 'e', 'p') => 9,
            ('O', 'c', 't') => 10, ('N', 'o', 'v') => 11, ('D', 'e', 'c') => 12,
            _ => 0,
        };
        if (month == 0) return false;
        int D2(int i) => (s[i] - '0') * 10 + (s[i + 1] - '0');
        try
        {
            var day = D2(8);
            var hour = D2(11);
            var min = D2(14);
            var sec = D2(17);
            var year = D2(20) * 100 + D2(22);
            ts = new DateTime(year, month, day, hour, min, sec, DateTimeKind.Local);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
