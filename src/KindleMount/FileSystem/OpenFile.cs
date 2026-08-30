using KindleMount.Logging;
using KindleMount.Mtp;

namespace KindleMount.FileSystem;

/// <summary>
/// 開かれているファイル 1 本の実体。デバイス上のファイルはローカルへ取り出してから
/// 読み書きし、最後のハンドルが閉じられた時点でまとめて書き戻す。
///
/// 同じパスに対する複数ハンドルはこのインスタンスを共有するので、
/// 「読み用と書き用を同時に開く」ような Explorer の挙動でも内容が食い違わない。
/// </summary>
public sealed class OpenFile
{
    private readonly object _gate = new();
    private readonly MtpFileStore _store;
    private readonly ContentCache _cache;
    private readonly Action<string, Exception>? _onWriteBackFailed;

    private FileStream? _stream;
    private string? _localPath;
    private bool _dirty;
    private bool _existsOnDevice;
    private bool _truncateOnOpen;
    private long _knownLength;
    private DateTime? _knownWriteTime;

    internal int RefCount;

    public OpenFile(MtpFileStore store, ContentCache cache, string dokanPath, MtpEntry? entry, bool truncate,
        Action<string, Exception>? onWriteBackFailed = null)
    {
        _store = store;
        _cache = cache;
        _onWriteBackFailed = onWriteBackFailed;
        DokanPath = dokanPath;
        _existsOnDevice = entry is not null;
        _knownLength = entry?.Length ?? 0;
        _knownWriteTime = entry?.LastWriteTime;
        _truncateOnOpen = truncate;

        if (truncate)
        {
            _knownLength = 0;
            _dirty = true;
        }
        else if (entry is null)
        {
            _dirty = true; // 新規作成: 空でも書き戻す。
        }
    }

    public string DokanPath { get; private set; }

    public bool DeleteOnClose { get; set; }

    public long Length
    {
        get
        {
            lock (_gate)
            {
                return _stream?.Length ?? _knownLength;
            }
        }
    }

    public DateTime? LastWriteTime
    {
        get
        {
            lock (_gate)
            {
                return _dirty ? DateTime.Now : _knownWriteTime;
            }
        }
    }

    public bool ExistsOnDevice
    {
        get
        {
            lock (_gate)
            {
                return _existsOnDevice;
            }
        }
    }

    public int Read(byte[] buffer, long offset, int count)
    {
        lock (_gate)
        {
            Materialize();
            var stream = _stream!;
            if (offset >= stream.Length)
            {
                return 0;
            }

            stream.Position = offset;
            var toRead = (int)Math.Min(count, stream.Length - offset);
            var read = 0;
            while (read < toRead)
            {
                var n = stream.Read(buffer, read, toRead - read);
                if (n <= 0)
                {
                    break;
                }

                read += n;
            }

            return read;
        }
    }

    public int Write(byte[] buffer, long offset, int count, bool appendToEnd)
    {
        lock (_gate)
        {
            Materialize();
            var stream = _stream!;
            stream.Position = appendToEnd ? stream.Length : offset;
            stream.Write(buffer, 0, count);
            _dirty = true;
            return count;
        }
    }

    public void SetLength(long length)
    {
        lock (_gate)
        {
            Materialize();
            if (_stream!.Length != length)
            {
                _stream.SetLength(length);
                _dirty = true;
            }
        }
    }

    /// <summary>ローカル側の変更をデバイスへ書き戻す。変更がなければ何もしない。</summary>
    public void FlushToDevice()
    {
        lock (_gate)
        {
            if (!_dirty)
            {
                return;
            }

            if (_stream is null)
            {
                // 一度も読み書きせずに閉じられた新規ファイル: 空ファイルとして作る。
                Materialize();
            }

            _stream!.Flush(flushToDisk: true);

            Log.Debug($"デバイスへ書き戻し: {DokanPath} ({_stream.Length:N0} バイト)");

            // ローカル実体は排他で開いたままなので、パスではなくストリームを渡す。
            // (パス渡しで開き直すと自分自身と共有違反を起こす)
            _stream.Position = 0;
            _store.Upload(_stream, DokanPath);

            _knownLength = _stream.Length;
            _knownWriteTime = DateTime.Now;
            _existsOnDevice = true;
            _dirty = false;
        }
    }

    /// <summary>ファイル名が変わったときに追従する(MoveFile 用)。</summary>
    public void Retarget(string newDokanPath)
    {
        lock (_gate)
        {
            DokanPath = newDokanPath;
        }
    }

    /// <summary>最後のハンドルが閉じられたときに呼ばれる。</summary>
    internal void CloseCore()
    {
        lock (_gate)
        {
            try
            {
                if (!DeleteOnClose)
                {
                    FlushToDevice();
                }
            }
            catch (Exception ex)
            {
                // Dokan の Cleanup は失敗を呼び出し元へ返せない。黙って失われるとデータ損失に
                // 気づけないため、上位へ通知してユーザーに知らせる。
                Log.Error($"書き戻しに失敗しました: {DokanPath}", ex);
                _onWriteBackFailed?.Invoke(DokanPath, ex);
            }

            _stream?.Dispose();
            _stream = null;

            if (_localPath is not null)
            {
                // _dirty が残っている = 書き戻しに失敗している。この状態のローカルファイルは
                // デバイス上の内容と食い違うため、キャッシュとして残してはいけない。
                var discard = ContentCache.IsScratch(_localPath) || DeleteOnClose || _dirty;
                if (discard)
                {
                    _cache.Remove(_localPath);
                }
                else
                {
                    _cache.Touch(_localPath);
                }
            }

            _localPath = null;
        }
    }

    /// <summary>ローカル実体を用意する。必要ならデバイスからダウンロードする。</summary>
    private void Materialize()
    {
        if (_stream is not null)
        {
            return;
        }

        if (_existsOnDevice && !_truncateOnOpen)
        {
            var cachePath = _cache.GetCachePath(DokanPath, _knownLength, _knownWriteTime);
            if (!_cache.IsUsable(cachePath, _knownLength))
            {
                Log.Debug($"デバイスから取得: {DokanPath} ({_knownLength:N0} バイト)");
                try
                {
                    _store.Download(DokanPath, cachePath);
                }
                catch
                {
                    _cache.Remove(cachePath);
                    throw;
                }
            }

            _localPath = cachePath;
            _stream = new FileStream(cachePath, FileMode.Open, System.IO.FileAccess.ReadWrite, FileShare.None);
        }
        else
        {
            _localPath = _cache.CreateScratchPath();
            _stream = new FileStream(_localPath, FileMode.Create, System.IO.FileAccess.ReadWrite, FileShare.None);
            _truncateOnOpen = false;
        }
    }
}
