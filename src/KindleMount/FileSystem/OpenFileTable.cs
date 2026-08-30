namespace KindleMount.FileSystem;

/// <summary>
/// パス → <see cref="OpenFile"/> の参照カウント付きテーブル。
/// 同じファイルに対する複数ハンドルを 1 つの実体に集約する。
/// </summary>
public sealed class OpenFileTable
{
    private readonly Dictionary<string, OpenFile> _map = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public OpenFile Acquire(string dokanPath, Func<OpenFile> factory)
    {
        lock (_gate)
        {
            if (!_map.TryGetValue(dokanPath, out var file))
            {
                file = factory();
                _map[dokanPath] = file;
            }

            file.RefCount++;
            return file;
        }
    }

    public OpenFile? TryGet(string dokanPath)
    {
        lock (_gate)
        {
            return _map.TryGetValue(dokanPath, out var file) ? file : null;
        }
    }

    /// <summary>参照を 1 つ返す。最後の 1 本だった場合のみ実体を閉じる(書き戻しはロック外)。</summary>
    public void Release(OpenFile file)
    {
        var shouldClose = false;
        lock (_gate)
        {
            file.RefCount--;
            if (file.RefCount <= 0)
            {
                // Retarget 済みの場合に備え、値一致で消す。
                foreach (var pair in _map.Where(p => ReferenceEquals(p.Value, file)).ToList())
                {
                    _map.Remove(pair.Key);
                }

                shouldClose = true;
            }
        }

        if (shouldClose)
        {
            file.CloseCore();
        }
    }

    /// <summary>MoveFile に合わせてキーを張り替える。</summary>
    public void Rename(string oldPath, string newPath)
    {
        lock (_gate)
        {
            if (_map.Remove(oldPath, out var file))
            {
                file.Retarget(newPath);
                _map[newPath] = file;
            }
        }
    }

    public IReadOnlyList<OpenFile> Snapshot()
    {
        lock (_gate)
        {
            return _map.Values.Distinct().ToList();
        }
    }

    /// <summary>アンマウント時に残っているハンドルを強制的に閉じる。</summary>
    public void CloseAll()
    {
        List<OpenFile> files;
        lock (_gate)
        {
            files = _map.Values.Distinct().ToList();
            _map.Clear();
        }

        foreach (var file in files)
        {
            file.CloseCore();
        }
    }
}
