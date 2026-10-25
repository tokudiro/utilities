using System.Text;

namespace NfsServe;

public sealed class XdrException : Exception
{
    public XdrException(string message) : base(message) { }
}

/// <summary>XDR (RFC 4506) の読み取り。値はすべてビッグエンディアンの4バイト境界。</summary>
public sealed class XdrReader
{
    readonly byte[] _b;
    int _p;
    readonly int _end;

    public XdrReader(byte[] buffer, int offset, int length)
    {
        _b = buffer; _p = offset; _end = offset + length;
    }

    void Need(int n)
    {
        if (n < 0 || _end - _p < n) throw new XdrException("データが足りません");
    }

    public uint U32()
    {
        Need(4);
        var v = (uint)_b[_p] << 24 | (uint)_b[_p + 1] << 16 | (uint)_b[_p + 2] << 8 | _b[_p + 3];
        _p += 4;
        return v;
    }

    public ulong U64() => (ulong)U32() << 32 | U32();
    public bool Bool() => U32() != 0;

    public byte[] Fixed(int n)
    {
        var padded = (n + 3) & ~3;
        Need(padded);
        var r = new byte[n];
        Buffer.BlockCopy(_b, _p, r, 0, n);
        _p += padded;
        return r;
    }

    public byte[] Opaque(int max)
    {
        var n = U32();
        if (n > (uint)max) throw new XdrException("opaque が長すぎます");
        return Fixed((int)n);
    }

    public string String(int max = 1024) => Encoding.UTF8.GetString(Opaque(max));
}

/// <summary>XDR の書き出し。</summary>
public sealed class XdrWriter
{
    readonly MemoryStream _ms = new();

    public int Length => (int)_ms.Length;

    public void U32(uint v)
    {
        Span<byte> s = stackalloc byte[4];
        s[0] = (byte)(v >> 24); s[1] = (byte)(v >> 16); s[2] = (byte)(v >> 8); s[3] = (byte)v;
        _ms.Write(s);
    }

    public void U64(ulong v) { U32((uint)(v >> 32)); U32((uint)v); }
    public void Bool(bool v) => U32(v ? 1u : 0u);

    public void Fixed(ReadOnlySpan<byte> data)
    {
        _ms.Write(data);
        var pad = (4 - (data.Length & 3)) & 3;
        for (var i = 0; i < pad; i++) _ms.WriteByte(0);
    }

    public void Opaque(ReadOnlySpan<byte> data) { U32((uint)data.Length); Fixed(data); }
    public void String(string s) => Opaque(Encoding.UTF8.GetBytes(s));

    public void Append(XdrWriter other) => other._ms.WriteTo(_ms);
    public byte[] ToArray() => _ms.ToArray();
}
