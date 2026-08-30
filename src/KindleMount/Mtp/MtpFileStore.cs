using KindleMount.FileSystem;
using KindleMount.Logging;
using MediaDevices;
using System.Collections.Concurrent;

namespace KindleMount.Mtp;

/// <summary>
/// Dokan から見た「デバイス上のファイルシステム」。パス変換・列挙キャッシュ・
/// 実データの転送をまとめて受け持つ。
///
/// MTP の列挙は 1 件あたり数十 ms かかることもあるため、ディレクトリ単位で
/// 短時間キャッシュし、書き込み系操作のたびに該当ディレクトリを破棄する。
/// </summary>
public sealed class MtpFileStore
{
    private readonly MtpConnection _connection;
    private readonly MtpPathMapper _mapper;
    private readonly TimeSpan _cacheTtl;
    private readonly ConcurrentDictionary<string, CachedListing> _listings = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _spaceGate = new();
    private (long Total, long Free) _space;
    private DateTime _spaceFetchedAt = DateTime.MinValue;

    public MtpFileStore(MtpConnection connection, MtpPathMapper mapper, TimeSpan cacheTtl)
    {
        _connection = connection;
        _mapper = mapper;
        _cacheTtl = cacheTtl;
    }

    public MtpPathMapper Mapper => _mapper;

    public void InvalidateAll() => _listings.Clear();

    public void Invalidate(string dokanPath)
    {
        _listings.TryRemove(MtpPathMapper.NormalizeDokanPath(dokanPath), out _);
    }

    /// <summary>指定パスとその親の列挙キャッシュを破棄する。</summary>
    public void InvalidateWithParent(string dokanPath)
    {
        Invalidate(dokanPath);
        Invalidate(MtpPathMapper.GetParent(dokanPath));
    }

    /// <summary>ディレクトリの中身を返す。存在しなければ null。</summary>
    public IReadOnlyList<MtpEntry>? ListDirectory(string dokanPath)
    {
        var key = MtpPathMapper.NormalizeDokanPath(dokanPath);

        if (_listings.TryGetValue(key, out var cached) && !cached.IsExpired(_cacheTtl))
        {
            return cached.Entries;
        }

        if (_mapper.IsVirtualRoot(key))
        {
            var storages = _mapper.Storages
                .Select(s => new MtpEntry(s.Name, IsDirectory: true, 0, null, null, CanDelete: false))
                .ToList();
            _listings[key] = new CachedListing(storages);
            return storages;
        }

        var mtpPath = _mapper.ToMtpPath(key);
        if (mtpPath is null)
        {
            return null;
        }

        try
        {
            var entries = _connection.Exec($"列挙 {key}", device =>
            {
                var directory = device.GetDirectoryInfo(mtpPath);
                var list = new List<MtpEntry>();
                foreach (var item in directory.EnumerateFileSystemInfos())
                {
                    list.Add(ToEntry(item));
                }

                return list;
            });

            _listings[key] = new CachedListing(entries);
            return entries;
        }
        catch (MtpDeviceGoneException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Debug($"列挙に失敗しました ({key}): {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 単一エントリのメタデータ。個別に GetFileInfo を呼ぶより親ディレクトリの
    /// 列挙キャッシュから引く方が速いため、まずキャッシュを見る。
    /// </summary>
    public MtpEntry? GetEntry(string dokanPath)
    {
        var key = MtpPathMapper.NormalizeDokanPath(dokanPath);
        if (key == "\\")
        {
            return new MtpEntry(string.Empty, IsDirectory: true, 0, null, null, CanDelete: false);
        }

        var parent = MtpPathMapper.GetParent(key);
        var name = MtpPathMapper.GetName(key);

        var siblings = ListDirectory(parent);
        if (siblings is null)
        {
            return null;
        }

        return siblings.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public bool Exists(string dokanPath) => GetEntry(dokanPath) is not null;

    public void CreateDirectory(string dokanPath)
    {
        var mtpPath = RequireMtpPath(dokanPath);
        _connection.Exec($"作成 {dokanPath}", device => device.CreateDirectory(mtpPath));
        InvalidateWithParent(dokanPath);
    }

    public void DeleteFile(string dokanPath)
    {
        var mtpPath = RequireMtpPath(dokanPath);
        _connection.Exec($"削除 {dokanPath}", device => device.DeleteFile(mtpPath));
        InvalidateWithParent(dokanPath);
    }

    public void DeleteDirectory(string dokanPath, bool recursive)
    {
        var mtpPath = RequireMtpPath(dokanPath);
        _connection.Exec($"ディレクトリ削除 {dokanPath}", device => device.DeleteDirectory(mtpPath, recursive));
        InvalidateWithParent(dokanPath);
    }

    public void Rename(string dokanPath, string newName)
    {
        var mtpPath = RequireMtpPath(dokanPath);
        _connection.Exec($"改名 {dokanPath} -> {newName}", device => device.Rename(mtpPath, newName));
        InvalidateWithParent(dokanPath);
    }

    /// <summary>デバイス上のファイルをローカルファイルへ取り出す。</summary>
    public void Download(string dokanPath, string localPath)
    {
        var mtpPath = RequireMtpPath(dokanPath);
        _connection.Exec($"取得 {dokanPath}", device =>
        {
            using var target = new FileStream(localPath, FileMode.Create, System.IO.FileAccess.Write, FileShare.None);
            device.DownloadFile(mtpPath, target);
        });
    }

    /// <summary>ローカルファイルをデバイスへ書き戻す。既存があれば置き換える。</summary>
    public void Upload(string localPath, string dokanPath)
    {
        using var source = new FileStream(localPath, FileMode.Open, System.IO.FileAccess.Read, FileShare.Read);
        Upload(source, dokanPath);
    }

    /// <summary>
    /// 開いているストリームの内容をデバイスへ書き戻す。
    /// 呼び出し側がストリームを所有したまま渡せるよう、ここでは閉じない。
    /// </summary>
    public void Upload(Stream source, string dokanPath)
    {
        var mtpPath = RequireMtpPath(dokanPath);

        _connection.Exec($"転送 {dokanPath}", device =>
        {
            // MTP には上書きの概念がなく、同名オブジェクトが 2 つ並んで存在し得る。
            // 削除に失敗したまま送ると古い方を読み続ける危険があるので、ここでは例外を握り潰さない。
            if (device.FileExists(mtpPath))
            {
                device.DeleteFile(mtpPath);
            }

            device.UploadFile(new NonClosingStream(source), mtpPath);
        });

        InvalidateWithParent(dokanPath);
    }

    /// <summary>容量情報(30 秒キャッシュ)。取得できない場合は 0 を返す。</summary>
    public (long Total, long Free) GetSpace()
    {
        lock (_spaceGate)
        {
            if (DateTime.UtcNow - _spaceFetchedAt < TimeSpan.FromSeconds(30))
            {
                return _space;
            }
        }

        long total = 0;
        long free = 0;
        try
        {
            (total, free) = _connection.Exec("容量取得", device =>
            {
                long t = 0;
                long f = 0;
                foreach (var drive in device.GetDrives())
                {
                    try
                    {
                        t += drive.TotalSize;
                        f += drive.AvailableFreeSpace;
                    }
                    catch (Exception)
                    {
                        // 一部ストレージが応答しなくても続行する。
                    }
                }

                return (t, f);
            });
        }
        catch (MtpDeviceGoneException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Debug($"容量の取得に失敗しました: {ex.Message}");
        }

        lock (_spaceGate)
        {
            _space = (total, free);
            _spaceFetchedAt = DateTime.UtcNow;
            return _space;
        }
    }

    private string RequireMtpPath(string dokanPath)
    {
        var mtpPath = _mapper.ToMtpPath(dokanPath);
        if (mtpPath is null)
        {
            throw new IOException($"デバイス上のパスに変換できません: {dokanPath}");
        }

        return mtpPath;
    }

    private static MtpEntry ToEntry(MediaFileSystemInfo item)
    {
        var attributes = item.Attributes;
        var isDirectory = attributes.HasFlag(MediaFileAttributes.Directory);
        var length = isDirectory ? 0L : unchecked((long)item.Length);
        if (length < 0)
        {
            length = 0;
        }

        return new MtpEntry(
            item.Name,
            isDirectory,
            length,
            item.CreationTime,
            item.LastWriteTime,
            attributes.HasFlag(MediaFileAttributes.CanDelete));
    }

    private sealed class CachedListing
    {
        public CachedListing(IReadOnlyList<MtpEntry> entries)
        {
            Entries = entries;
            FetchedAt = DateTime.UtcNow;
        }

        public IReadOnlyList<MtpEntry> Entries { get; }

        public DateTime FetchedAt { get; }

        public bool IsExpired(TimeSpan ttl) => ttl <= TimeSpan.Zero || DateTime.UtcNow - FetchedAt > ttl;
    }
}
