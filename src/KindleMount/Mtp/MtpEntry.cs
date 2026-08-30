namespace KindleMount.Mtp;

/// <summary>ディレクトリ列挙 1 件分のメタデータ(MTP から取得した生の値)。</summary>
public sealed record MtpEntry(
    string Name,
    bool IsDirectory,
    long Length,
    DateTime? CreationTime,
    DateTime? LastWriteTime,
    bool CanDelete);
