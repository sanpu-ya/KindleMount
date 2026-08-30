using KindleMount.Logging;

namespace KindleMount.Mounting;

/// <summary>空きドライブレターを選ぶ。マウント処理中の重複割り当ても防ぐ。</summary>
public sealed class DriveLetterAllocator
{
    private readonly HashSet<char> _reserved = new();
    private readonly object _gate = new();

    /// <summary>希望レターの中から空きを 1 つ確保する。無ければ null。</summary>
    public char? Allocate(string preferredLetters)
    {
        lock (_gate)
        {
            var inUse = GetLettersInUse();

            foreach (var letter in preferredLetters)
            {
                var upper = char.ToUpperInvariant(letter);
                if (!inUse.Contains(upper) && !_reserved.Contains(upper))
                {
                    _reserved.Add(upper);
                    return upper;
                }
            }

            // 希望が全部埋まっていたら残りから探す。
            for (var c = 'Z'; c >= 'D'; c--)
            {
                if (!inUse.Contains(c) && !_reserved.Contains(c))
                {
                    _reserved.Add(c);
                    return c;
                }
            }

            Log.Warn("空きドライブレターがありません。");
            return null;
        }
    }

    public void Release(char letter)
    {
        lock (_gate)
        {
            _reserved.Remove(char.ToUpperInvariant(letter));
        }
    }

    private static HashSet<char> GetLettersInUse()
    {
        var set = new HashSet<char>();
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.Name.Length > 0)
                {
                    set.Add(char.ToUpperInvariant(drive.Name[0]));
                }
            }
        }
        catch (IOException ex)
        {
            Log.Warn($"ドライブ一覧の取得に失敗しました: {ex.Message}");
        }

        return set;
    }
}
