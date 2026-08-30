using DokanNet;
using KindleMount.Logging;
using KindleMount.Mtp;
using System.Security.AccessControl;
using FileAccess = DokanNet.FileAccess;

namespace KindleMount.FileSystem;

/// <summary>
/// MTP デバイスを Windows のドライブとして見せる Dokan ファイルシステム実装。
///
/// MTP はランダムアクセス・部分書き込みができないため、読み書きは必ずローカルの
/// キャッシュファイル経由で行い、ハンドルが閉じられた時点でデバイスへ書き戻す。
/// </summary>
public sealed class MtpDokanOperations : IDokanOperations
{
    /// <summary>実データの読み書きを伴うアクセス要求。これ以外は属性照会だけで済ませる。</summary>
    private const FileAccess DataAccess = FileAccess.ReadData | FileAccess.WriteData | FileAccess.AppendData |
                                          FileAccess.Execute | FileAccess.GenericExecute | FileAccess.GenericWrite |
                                          FileAccess.GenericRead;

    private const FileAccess DataWriteAccess = FileAccess.WriteData | FileAccess.AppendData |
                                               FileAccess.Delete | FileAccess.GenericWrite;

    private readonly MtpFileStore _store;
    private readonly ContentCache _cache;
    private readonly OpenFileTable _openFiles = new();
    private readonly string _volumeLabel;
    private readonly bool _readOnly;

    public MtpDokanOperations(MtpFileStore store, ContentCache cache, string volumeLabel, bool readOnly)
    {
        _store = store;
        _cache = cache;
        _volumeLabel = Truncate(volumeLabel, 32);
        _readOnly = readOnly;
    }

    /// <summary>デバイス喪失を検知したときに 1 度だけ発火する(上位がアンマウントする)。</summary>
    public event Action? DeviceLost;

    /// <summary>
    /// デバイスへの書き戻しに失敗したときに発火する。
    /// Dokan の Cleanup は失敗をアプリへ返せないため、この経路でユーザーに知らせる。
    /// </summary>
    public event Action<string, Exception>? WriteBackFailed;

    /// <summary>
    /// Dokan 側でボリュームが切り離されたときに発火する。
    /// エクスプローラーの「取り出し」やドライバ経由のアンマウントもここを通る。
    /// </summary>
    public event Action? VolumeUnmounted;

    public void CloseAllHandles() => _openFiles.CloseAll();

    #region ファイル/ディレクトリのオープン

    public NtStatus CreateFile(string fileName, FileAccess access, FileShare share, FileMode mode,
        FileOptions options, FileAttributes attributes, IDokanFileInfo info)
    {
        var path = MtpPathMapper.NormalizeDokanPath(fileName);

        return Guard($"CreateFile {path}", () =>
        {
            var entry = _store.GetEntry(path);

            if (info.IsDirectory)
            {
                return CreateDirectoryHandle(path, mode, entry, info);
            }

            if (entry is { IsDirectory: true })
            {
                // 実体はディレクトリ。Dokan の作法に従いディレクトリとして開き直させる。
                if (mode is FileMode.CreateNew)
                {
                    return DokanResult.FileExists;
                }

                info.IsDirectory = true;
                return DokanResult.Success;
            }

            var wantsWrite = (access & DataWriteAccess) != 0 ||
                             mode is FileMode.Create or FileMode.CreateNew or FileMode.Truncate or FileMode.Append;

            if (_readOnly && wantsWrite)
            {
                return DokanResult.AccessDenied;
            }

            switch (mode)
            {
                case FileMode.CreateNew when entry is not null:
                    return DokanResult.FileExists;
                case FileMode.Open or FileMode.Truncate when entry is null:
                    return DokanResult.FileNotFound;
                case FileMode.Append when entry is null:
                    // 追記対象が無い場合は新規作成として扱う。
                    break;
            }

            if (_store.Mapper.IsStorageRoot(path))
            {
                return DokanResult.AccessDenied;
            }

            if ((access & DataAccess) == 0 && mode == FileMode.Open)
            {
                // 属性照会のみ。実体を開かずに済ませる(サムネイル生成などで大量に来る)。
                return entry is null ? DokanResult.FileNotFound : DokanResult.Success;
            }

            var truncate = mode is FileMode.Create or FileMode.Truncate;
            var existed = entry is not null;

            var file = _openFiles.Acquire(path, () => new OpenFile(_store, _cache, path, entry, truncate, OnWriteBackFailed));

            if (truncate)
            {
                file.SetLength(0);
            }

            info.Context = file;

            if ((options & FileOptions.DeleteOnClose) != 0)
            {
                file.DeleteOnClose = true;
            }

            if (existed && mode is FileMode.Create or FileMode.OpenOrCreate)
            {
                return DokanResult.AlreadyExists;
            }

            return DokanResult.Success;
        });
    }

    private NtStatus CreateDirectoryHandle(string path, FileMode mode, MtpEntry? entry, IDokanFileInfo info)
    {
        switch (mode)
        {
            case FileMode.CreateNew when entry is not null:
                return DokanResult.FileExists;

            case FileMode.CreateNew or FileMode.Create or FileMode.OpenOrCreate when entry is null:
                if (_readOnly)
                {
                    return DokanResult.AccessDenied;
                }

                if (_store.Mapper.ToMtpPath(path) is null)
                {
                    return DokanResult.AccessDenied;
                }

                _store.CreateDirectory(path);
                return DokanResult.Success;

            default:
                if (entry is null)
                {
                    return path == "\\" ? DokanResult.Success : DokanResult.PathNotFound;
                }

                if (!entry.IsDirectory)
                {
                    return DokanResult.NotADirectory;
                }

                return DokanResult.Success;
        }
    }

    public void Cleanup(string fileName, IDokanFileInfo info)
    {
        var path = MtpPathMapper.NormalizeDokanPath(fileName);
        var file = info.Context as OpenFile;
        info.Context = null;

        try
        {
            if (info.DeletePending)
            {
                if (file is not null)
                {
                    file.DeleteOnClose = true;
                }

                DeleteFromDevice(path, info.IsDirectory);
            }
        }
        catch (MtpDeviceGoneException)
        {
            OnDeviceLost();
        }
        catch (Exception ex)
        {
            Log.Error($"削除に失敗しました: {path}", ex);
        }
        finally
        {
            if (file is not null)
            {
                // 書き戻しに時間がかかるのでタイムアウトを延長しておく。
                using var keepAlive = new DokanKeepAlive(info);
                _openFiles.Release(file);
            }
        }
    }

    public void CloseFile(string fileName, IDokanFileInfo info)
    {
        if (info.Context is OpenFile file)
        {
            info.Context = null;
            using var keepAlive = new DokanKeepAlive(info);
            _openFiles.Release(file);
        }
    }

    #endregion

    #region 読み書き

    public NtStatus ReadFile(string fileName, byte[] buffer, out int bytesRead, long offset, IDokanFileInfo info)
    {
        var path = MtpPathMapper.NormalizeDokanPath(fileName);
        var read = 0;

        var status = Guard($"ReadFile {path}", () =>
        {
            if (info.Context is OpenFile open)
            {
                using var keepAlive = new DokanKeepAlive(info);
                read = open.Read(buffer, offset, buffer.Length);
                return DokanResult.Success;
            }

            // ハンドル無しで読まれるケース(メモリマップ経由など)。
            var entry = _store.GetEntry(path);
            if (entry is null || entry.IsDirectory)
            {
                return DokanResult.FileNotFound;
            }

            using var keepAlive2 = new DokanKeepAlive(info);
            var temp = _openFiles.Acquire(path, () => new OpenFile(_store, _cache, path, entry, truncate: false, OnWriteBackFailed));
            try
            {
                read = temp.Read(buffer, offset, buffer.Length);
            }
            finally
            {
                _openFiles.Release(temp);
            }

            return DokanResult.Success;
        });

        bytesRead = read;
        return status;
    }

    public NtStatus WriteFile(string fileName, byte[] buffer, out int bytesWritten, long offset, IDokanFileInfo info)
    {
        var path = MtpPathMapper.NormalizeDokanPath(fileName);
        var written = 0;

        if (_readOnly)
        {
            bytesWritten = 0;
            return DokanResult.AccessDenied;
        }

        var status = Guard($"WriteFile {path}", () =>
        {
            using var keepAlive = new DokanKeepAlive(info);

            if (info.Context is OpenFile open)
            {
                written = open.Write(buffer, offset, buffer.Length, info.WriteToEndOfFile);
                return DokanResult.Success;
            }

            var entry = _store.GetEntry(path);
            var temp = _openFiles.Acquire(path, () => new OpenFile(_store, _cache, path, entry, truncate: false, OnWriteBackFailed));
            try
            {
                written = temp.Write(buffer, offset, buffer.Length, info.WriteToEndOfFile);
            }
            finally
            {
                _openFiles.Release(temp);
            }

            return DokanResult.Success;
        });

        bytesWritten = written;
        return status;
    }

    public NtStatus FlushFileBuffers(string fileName, IDokanFileInfo info)
    {
        // ここで毎回デバイスへ書き戻すとコピーが極端に遅くなる。
        // 実際の転送は最後のハンドルが閉じられる Cleanup で行う。
        return DokanResult.Success;
    }

    public NtStatus SetEndOfFile(string fileName, long length, IDokanFileInfo info)
    {
        if (_readOnly)
        {
            return DokanResult.AccessDenied;
        }

        var path = MtpPathMapper.NormalizeDokanPath(fileName);
        return Guard($"SetEndOfFile {path}", () =>
        {
            if (info.Context is OpenFile open)
            {
                // 既存ファイルのリサイズは実体の取り出しを伴うことがあるため、
                // 他の低速パスと同様にタイムアウトを延長しておく。
                using var keepAlive = new DokanKeepAlive(info);
                open.SetLength(length);
                return DokanResult.Success;
            }

            return DokanResult.InvalidHandle;
        });
    }

    public NtStatus SetAllocationSize(string fileName, long length, IDokanFileInfo info) =>
        SetEndOfFile(fileName, length, info);

    #endregion

    #region メタデータ

    public NtStatus GetFileInformation(string fileName, out FileInformation fileInfo, IDokanFileInfo info)
    {
        var path = MtpPathMapper.NormalizeDokanPath(fileName);
        fileInfo = default;

        FileInformation result = default;
        var status = Guard($"GetFileInformation {path}", () =>
        {
            if (path == "\\")
            {
                result = new FileInformation
                {
                    FileName = path,
                    Attributes = FileAttributes.Directory,
                    Length = 0,
                };
                return DokanResult.Success;
            }

            var entry = _store.GetEntry(path);
            if (entry is null)
            {
                // 書き込み中でまだデバイス上に無いファイルはハンドルから答える。
                var open = _openFiles.TryGet(path);
                if (open is null)
                {
                    return DokanResult.FileNotFound;
                }

                result = new FileInformation
                {
                    FileName = MtpPathMapper.GetName(path),
                    Attributes = FileAttributes.Normal,
                    Length = open.Length,
                    LastWriteTime = open.LastWriteTime,
                };
                return DokanResult.Success;
            }

            result = ToFileInformation(entry);

            // 開いているハンドルがあれば、そちらの最新サイズを優先する。
            var handle = _openFiles.TryGet(path);
            if (handle is not null && !entry.IsDirectory)
            {
                result = result with { Length = handle.Length };
            }

            return DokanResult.Success;
        });

        fileInfo = result;
        return status;
    }

    public NtStatus FindFiles(string fileName, out IList<FileInformation> files, IDokanFileInfo info)
    {
        var path = MtpPathMapper.NormalizeDokanPath(fileName);
        IList<FileInformation> list = new List<FileInformation>();

        var status = Guard($"FindFiles {path}", () =>
        {
            using var keepAlive = new DokanKeepAlive(info);
            var entries = _store.ListDirectory(path);
            if (entries is null)
            {
                return DokanResult.PathNotFound;
            }

            list = entries.Select(ToFileInformation).ToList();
            return DokanResult.Success;
        });

        files = list;
        return status;
    }

    public NtStatus FindFilesWithPattern(string fileName, string searchPattern, out IList<FileInformation> files,
        IDokanFileInfo info)
    {
        var status = FindFiles(fileName, out var all, info);
        if (status != DokanResult.Success)
        {
            files = new List<FileInformation>();
            return status;
        }

        if (string.IsNullOrEmpty(searchPattern) || searchPattern == "*")
        {
            files = all;
            return DokanResult.Success;
        }

        files = all
            .Where(f => DokanHelper.DokanIsNameInExpression(searchPattern, f.FileName, ignoreCase: true))
            .ToList();
        return DokanResult.Success;
    }

    public NtStatus SetFileAttributes(string fileName, FileAttributes attributes, IDokanFileInfo info)
    {
        // MTP に属性の概念はほぼ無い。失敗を返すとコピーが中断するため成功扱いにする。
        return _readOnly ? DokanResult.AccessDenied : DokanResult.Success;
    }

    public NtStatus SetFileTime(string fileName, DateTime? creationTime, DateTime? lastAccessTime,
        DateTime? lastWriteTime, IDokanFileInfo info)
    {
        // 同上。タイムスタンプはデバイス側が決める。
        return _readOnly ? DokanResult.AccessDenied : DokanResult.Success;
    }

    public NtStatus GetDiskFreeSpace(out long freeBytesAvailable, out long totalNumberOfBytes,
        out long totalNumberOfFreeBytes, IDokanFileInfo info)
    {
        long total = 0;
        long free = 0;

        var status = Guard("GetDiskFreeSpace", () =>
        {
            (total, free) = _store.GetSpace();
            return DokanResult.Success;
        });

        freeBytesAvailable = free;
        totalNumberOfBytes = total;
        totalNumberOfFreeBytes = free;
        return status;
    }

    public NtStatus GetVolumeInformation(out string volumeLabel, out FileSystemFeatures features,
        out string fileSystemName, out uint maximumComponentLength, IDokanFileInfo info)
    {
        volumeLabel = _volumeLabel;
        fileSystemName = "MTP";
        maximumComponentLength = 255;
        features = FileSystemFeatures.CasePreservedNames | FileSystemFeatures.UnicodeOnDisk;

        if (_readOnly)
        {
            features |= FileSystemFeatures.ReadOnlyVolume;
        }

        return DokanResult.Success;
    }

    public NtStatus GetFileSecurity(string fileName, out FileSystemSecurity? security, AccessControlSections sections,
        IDokanFileInfo info)
    {
        security = null;
        return DokanResult.NotImplemented;
    }

    public NtStatus SetFileSecurity(string fileName, FileSystemSecurity security, AccessControlSections sections,
        IDokanFileInfo info) => DokanResult.NotImplemented;

    public NtStatus FindStreams(string fileName, out IList<FileInformation> streams, IDokanFileInfo info)
    {
        streams = new List<FileInformation>();
        return DokanResult.NotImplemented;
    }

    public NtStatus LockFile(string fileName, long offset, long length, IDokanFileInfo info) => DokanResult.Success;

    public NtStatus UnlockFile(string fileName, long offset, long length, IDokanFileInfo info) => DokanResult.Success;

    #endregion

    #region 削除・移動

    public NtStatus DeleteFile(string fileName, IDokanFileInfo info)
    {
        if (_readOnly)
        {
            return DokanResult.AccessDenied;
        }

        var path = MtpPathMapper.NormalizeDokanPath(fileName);
        return Guard($"DeleteFile {path}", () =>
        {
            if (_store.Mapper.IsStorageRoot(path))
            {
                return DokanResult.AccessDenied;
            }

            var entry = _store.GetEntry(path);
            if (entry is null)
            {
                // ハンドルだけ存在する(作成直後に消される)場合も許可する。
                return _openFiles.TryGet(path) is not null ? DokanResult.Success : DokanResult.FileNotFound;
            }

            return entry.IsDirectory ? DokanResult.AccessDenied : DokanResult.Success;
        });
    }

    public NtStatus DeleteDirectory(string fileName, IDokanFileInfo info)
    {
        if (_readOnly)
        {
            return DokanResult.AccessDenied;
        }

        var path = MtpPathMapper.NormalizeDokanPath(fileName);
        return Guard($"DeleteDirectory {path}", () =>
        {
            if (_store.Mapper.IsStorageRoot(path))
            {
                return DokanResult.AccessDenied;
            }

            var entries = _store.ListDirectory(path);
            if (entries is null)
            {
                return DokanResult.PathNotFound;
            }

            return entries.Count > 0 ? DokanResult.DirectoryNotEmpty : DokanResult.Success;
        });
    }

    public NtStatus MoveFile(string oldName, string newName, bool replace, IDokanFileInfo info)
    {
        if (_readOnly)
        {
            return DokanResult.AccessDenied;
        }

        var source = MtpPathMapper.NormalizeDokanPath(oldName);
        var destination = MtpPathMapper.NormalizeDokanPath(newName);

        return Guard($"MoveFile {source} -> {destination}", () =>
        {
            if (_store.Mapper.IsStorageRoot(source) || _store.Mapper.IsStorageRoot(destination))
            {
                return DokanResult.AccessDenied;
            }

            var sourceEntry = _store.GetEntry(source);

            // 作成直後でまだデバイスへ書き出されていないファイルは、開いているハンドルにしか存在しない。
            // (一時ファイルへ書いてから目的名へ改名する、という保存方法で通る経路)
            var pendingHandle = _openFiles.TryGet(source);

            if (sourceEntry is null && pendingHandle is null)
            {
                return DokanResult.FileNotFound;
            }

            var destinationEntry = _store.GetEntry(destination);
            if (destinationEntry is not null)
            {
                if (!replace)
                {
                    return DokanResult.FileExists;
                }

                if (destinationEntry.IsDirectory)
                {
                    return DokanResult.AccessDenied;
                }

                _store.DeleteFile(destination);
            }

            using var keepAlive = new DokanKeepAlive(info);

            if (sourceEntry is null)
            {
                // デバイス上に実体がないので、書き出し先の名前を差し替えるだけでよい。
                // 実際の転送はハンドルが閉じられるときに新しいパスへ行われる。
                _openFiles.Rename(source, destination);
                return DokanResult.Success;
            }

            var sameParent = string.Equals(
                MtpPathMapper.GetParent(source),
                MtpPathMapper.GetParent(destination),
                StringComparison.OrdinalIgnoreCase);

            if (sameParent)
            {
                _store.Rename(source, MtpPathMapper.GetName(destination));
                _openFiles.Rename(source, destination);
                return DokanResult.Success;
            }

            if (sourceEntry.IsDirectory)
            {
                // MTP にフォルダ移動の一括操作は無い。エクスプローラーは NotImplemented を受けると
                // 個別コピー + 削除にフォールバックする。
                return DokanResult.NotImplemented;
            }

            // 別フォルダへの移動は取り出し→書き込み→削除で代替する。
            var temp = _cache.CreateScratchPath();
            try
            {
                _store.Download(source, temp);
                _store.Upload(temp, destination);
                _store.DeleteFile(source);
                _openFiles.Rename(source, destination);
                return DokanResult.Success;
            }
            finally
            {
                _cache.Remove(temp);
            }
        });
    }

    private void DeleteFromDevice(string path, bool isDirectory)
    {
        if (_readOnly || _store.Mapper.IsStorageRoot(path))
        {
            return;
        }

        var entry = _store.GetEntry(path);
        if (entry is null)
        {
            return;
        }

        if (isDirectory || entry.IsDirectory)
        {
            _store.DeleteDirectory(path, recursive: false);
        }
        else
        {
            _store.DeleteFile(path);
        }

        Log.Debug($"デバイス上から削除しました: {path}");
    }

    #endregion

    #region マウント状態

    public NtStatus Mounted(string mountPoint, IDokanFileInfo info)
    {
        Log.Info($"マウントしました: {mountPoint}");
        return DokanResult.Success;
    }

    public NtStatus Unmounted(IDokanFileInfo info)
    {
        Log.Info("アンマウントしました。");

        // アプリ自身の停止だけでなく、エクスプローラーからの「取り出し」でもここに来る。
        // 上位が資源(MTP 接続・ドライブレター)を解放できるよう必ず知らせる。
        VolumeUnmounted?.Invoke();
        return DokanResult.Success;
    }

    #endregion

    /// <summary>すべてのコールバックを包む共通の例外処理。Dokan へ例外を投げ返さない。</summary>
    private NtStatus Guard(string operation, Func<NtStatus> action)
    {
        try
        {
            return action();
        }
        catch (MtpDeviceGoneException ex)
        {
            Log.Warn($"{operation}: デバイスが切断されました ({ex.Message})");
            OnDeviceLost();
            return DokanResult.NotReady;
        }
        catch (UnauthorizedAccessException)
        {
            return DokanResult.AccessDenied;
        }
        catch (FileNotFoundException)
        {
            return DokanResult.FileNotFound;
        }
        catch (DirectoryNotFoundException)
        {
            return DokanResult.PathNotFound;
        }
        catch (IOException ex)
        {
            Log.Debug($"{operation}: {ex.Message}");
            return DokanResult.Error;
        }
        catch (Exception ex)
        {
            Log.Error($"{operation} で予期しないエラー", ex);
            return DokanResult.InternalError;
        }
    }

    private void OnWriteBackFailed(string path, Exception ex) => WriteBackFailed?.Invoke(path, ex);

    private int _deviceLostRaised;

    private void OnDeviceLost()
    {
        if (Interlocked.Exchange(ref _deviceLostRaised, 1) == 0)
        {
            DeviceLost?.Invoke();
        }
    }

    private static FileInformation ToFileInformation(MtpEntry entry)
    {
        var attributes = entry.IsDirectory ? FileAttributes.Directory : FileAttributes.Normal;

        return new FileInformation
        {
            FileName = entry.Name,
            Attributes = attributes,
            Length = entry.Length,
            CreationTime = entry.CreationTime,
            LastWriteTime = entry.LastWriteTime,
            LastAccessTime = entry.LastWriteTime,
        };
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
