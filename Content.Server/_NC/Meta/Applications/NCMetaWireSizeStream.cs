// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.IO;

namespace Content.Server._NC.Meta.Applications;

/// <summary>Counts the actual network serializer output without retaining another copy of the UI graph.</summary>
internal sealed class NCMetaWireSizeStream : Stream
{
    private long _length;
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _length;
    public override long Position { get => _length; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override void Write(byte[] buffer, int offset, int count) => _length = checked(_length + count);
    public override void Write(ReadOnlySpan<byte> buffer) => _length = checked(_length + buffer.Length);
    public override void WriteByte(byte value) => _length = checked(_length + 1);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
