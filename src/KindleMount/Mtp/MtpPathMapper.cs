using KindleMount.Logging;

namespace KindleMount.Mtp;

/// <summary>
/// Dokan 側のパス("\documents\book.azw3")と MTP 側のパス("\内部ストレージ\documents\book.azw3")
/// を相互変換する。
///
/// ストレージが 1 つだけのデバイス(ほとんどの Kindle)はそのストレージを直接ドライブ直下に
/// マップする。複数ある場合はストレージ名を仮想的なトップレベルフォルダとして見せる。
/// </summary>
public sealed class MtpPathMapper
{
    private readonly List<StorageInfo> _storages = new();
    private string? _singleStorageRoot;

    public IReadOnlyList<StorageInfo> Storages => _storages;

    /// <summary>複数ストレージを仮想ルート配下に並べているか。</summary>
    public bool HasVirtualRoot => _singleStorageRoot is null;

    public void Refresh(MtpConnection connection)
    {
        var storages = connection.Exec("ストレージ列挙", device =>
        {
            var list = new List<StorageInfo>();
            foreach (var directory in device.GetRootDirectory().EnumerateDirectories())
            {
                var name = directory.Name;
                var fullName = directory.FullName;
                if (string.IsNullOrEmpty(fullName))
                {
                    continue;
                }

                list.Add(new StorageInfo(SanitizeName(name), fullName));
            }

            return list;
        });

        _storages.Clear();
        _storages.AddRange(storages);

        // 同名ストレージがあり得るので一意化する。
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < _storages.Count; i++)
        {
            var name = _storages[i].Name;
            if (seen.TryGetValue(name, out var count))
            {
                seen[name] = count + 1;
                _storages[i] = _storages[i] with { Name = $"{name} ({count + 1})" };
            }
            else
            {
                seen[name] = 1;
            }
        }

        _singleStorageRoot = _storages.Count == 1 ? _storages[0].MtpPath : null;

        Log.Info(_storages.Count switch
        {
            0 => "ストレージが見つかりませんでした(デバイスがロックされている可能性があります)。",
            1 => $"ストレージ: {_storages[0].Name} をドライブ直下にマップします。",
            _ => $"ストレージ {_storages.Count} 件を仮想ルート配下に並べます: {string.Join(", ", _storages.Select(s => s.Name))}",
        });
    }

    /// <summary>Dokan パスを MTP パスへ。仮想ルート自身を指す場合は null を返す。</summary>
    public string? ToMtpPath(string dokanPath)
    {
        var normalized = NormalizeDokanPath(dokanPath);

        if (_singleStorageRoot is not null)
        {
            return normalized == "\\" ? _singleStorageRoot : _singleStorageRoot + normalized;
        }

        if (normalized == "\\")
        {
            return null; // 仮想ルート
        }

        var rest = normalized[1..];
        var slash = rest.IndexOf('\\');
        var storageName = slash < 0 ? rest : rest[..slash];
        var tail = slash < 0 ? string.Empty : rest[slash..];

        var storage = _storages.FirstOrDefault(s => string.Equals(s.Name, storageName, StringComparison.OrdinalIgnoreCase));
        if (storage is null)
        {
            return null;
        }

        return storage.MtpPath + tail;
    }

    /// <summary>仮想ルート(ストレージ一覧を返すべきパス)かどうか。</summary>
    public bool IsVirtualRoot(string dokanPath) => HasVirtualRoot && NormalizeDokanPath(dokanPath) == "\\";

    /// <summary>ストレージ自体を指すパスかどうか(削除・リネーム禁止の判定に使う)。</summary>
    public bool IsStorageRoot(string dokanPath)
    {
        var normalized = NormalizeDokanPath(dokanPath);
        if (normalized == "\\")
        {
            return true;
        }

        return HasVirtualRoot && !normalized[1..].Contains('\\');
    }

    public static string NormalizeDokanPath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "\\";
        }

        var normalized = path.Replace('/', '\\');
        if (!normalized.StartsWith('\\'))
        {
            normalized = "\\" + normalized;
        }

        if (normalized.Length > 1)
        {
            normalized = normalized.TrimEnd('\\');
            if (normalized.Length == 0)
            {
                normalized = "\\";
            }
        }

        return normalized;
    }

    public static string GetParent(string dokanPath)
    {
        var normalized = NormalizeDokanPath(dokanPath);
        if (normalized == "\\")
        {
            return "\\";
        }

        var index = normalized.LastIndexOf('\\');
        return index <= 0 ? "\\" : normalized[..index];
    }

    public static string GetName(string dokanPath)
    {
        var normalized = NormalizeDokanPath(dokanPath);
        if (normalized == "\\")
        {
            return string.Empty;
        }

        return normalized[(normalized.LastIndexOf('\\') + 1)..];
    }

    public static string Combine(string parent, string name)
    {
        var normalized = NormalizeDokanPath(parent);
        return normalized == "\\" ? "\\" + name : normalized + "\\" + name;
    }

    /// <summary>Windows のファイル名として使えない文字を除去する(ストレージ名向け)。</summary>
    private static string SanitizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Storage";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return cleaned.Length == 0 ? "Storage" : cleaned;
    }

    public sealed record StorageInfo(string Name, string MtpPath)
    {
        public string Name { get; init; } = Name;
    }
}
