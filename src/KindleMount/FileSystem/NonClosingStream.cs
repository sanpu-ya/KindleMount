namespace KindleMount.FileSystem;

/// <summary>
/// 転送ライブラリへ渡すための薄いラッパー。相手が Dispose しても実体のストリームは閉じない。
/// (開いたままのローカル実体をそのままアップロードに使うため)
/// </summary>
internal sealed class NonClosingStream : Stream
{
    private readonly Stream _inner;

    public NonClosingStream(Stream inner) => _inner = inner;

    public override bool CanRead => _inner.CanRead;

    public override bool CanSeek => _inner.CanSeek;

    public override bool CanWrite => _inner.CanWrite;

    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override void Flush() => _inner.Flush();

    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

    public override void SetLength(long value) => _inner.SetLength(value);

    public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

    protected override void Dispose(bool disposing)
    {
        // 実体は所有者(OpenFile)が閉じる。
    }

    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
