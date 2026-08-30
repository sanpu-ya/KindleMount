using KindleMount.Logging;
using System.Security.Cryptography;
using System.Text;

namespace KindleMount.FileSystem;

/// <summary>
/// デバイスから取り出したファイル本体のローカルキャッシュ。
///
/// MTP はランダムアクセスに向かないため、読み書きは必ずローカルの実体ファイルを介して行う。
/// キャッシュキーにサイズと更新日時を含めるので、デバイス側で内容が変わると自動的に別ファイルになる。
/// </summary>
public sealed class ContentCache
{
    private readonly string _directory;
    private readonly long _limitBytes;
    private readonly object _gate = new();

    public ContentCache(string directory, long limitBytes)
    {
        _directory = directory;
        _limitBytes = limitBytes;
        Directory.CreateDirectory(_directory);
    }

    /// <summary>デバイス上のファイルに対応するキャッシュファイルのパス(存在するとは限らない)。</summary>
    public string GetCachePath(string dokanPath, long length, DateTime? lastWriteTime)
    {
        var stamp = lastWriteTime?.Ticks ?? 0;
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(dokanPath.ToLowerInvariant())), 0, 10);
        return Path.Combine(_directory, $"{hash}_{length}_{stamp}.bin");
    }

    /// <summary>新規作成ファイル用の一時パス(キャッシュ対象外)。</summary>
    public string CreateScratchPath()
    {
        return Path.Combine(_directory, $"scratch_{Guid.NewGuid():N}.tmp");
    }

    public static bool IsScratch(string path) =>
        Path.GetFileName(path).StartsWith("scratch_", StringComparison.Ordinal);

    /// <summary>キャッシュが有効(サイズ一致)かどうか。</summary>
    public bool IsUsable(string cachePath, long expectedLength)
    {
        try
        {
            var info = new FileInfo(cachePath);
            return info.Exists && info.Length == expectedLength;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public void Touch(string cachePath)
    {
        try
        {
            if (File.Exists(cachePath))
            {
                File.SetLastAccessTimeUtc(cachePath, DateTime.UtcNow);
            }
        }
        catch (IOException)
        {
        }
    }

    public void Remove(string cachePath)
    {
        try
        {
            if (File.Exists(cachePath))
            {
                File.Delete(cachePath);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>上限を超えた分を最終アクセスの古い順に削除する。</summary>
    public void Trim()
    {
        if (_limitBytes <= 0)
        {
            return;
        }

        lock (_gate)
        {
            try
            {
                var files = new DirectoryInfo(_directory)
                    .GetFiles("*", SearchOption.TopDirectoryOnly)
                    .OrderBy(f => f.LastAccessTimeUtc)
                    .ToList();

                var total = files.Sum(f => f.Length);
                foreach (var file in files)
                {
                    if (total <= _limitBytes)
                    {
                        break;
                    }

                    try
                    {
                        var size = file.Length;
                        file.Delete();
                        total -= size;
                    }
                    catch (IOException)
                    {
                        // 使用中のファイルは飛ばす。
                    }
                }
            }
            catch (DirectoryNotFoundException)
            {
            }
            catch (Exception ex)
            {
                Log.Debug($"キャッシュの整理に失敗しました: {ex.Message}");
            }
        }
    }

    /// <summary>アンマウント時に呼ぶ。書きかけの一時ファイルだけ消す。</summary>
    public void CleanupScratch()
    {
        try
        {
            foreach (var file in Directory.GetFiles(_directory, "scratch_*.tmp"))
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}
