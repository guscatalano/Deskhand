using System.Buffers.Binary;

namespace Deskhand.Core.Services;

/// <summary>Wire protocol shared by the main server (<see cref="SecureHelperClient"/>) and the SYSTEM
/// secure helper. A message is a length-prefixed JSON line optionally followed by a length-prefixed
/// binary blob (e.g. a JPEG frame): [4-byte LE jsonLen][json][4-byte LE blobLen][blob].</summary>
public static class SecureHelperProtocol
{
    public const string DefaultPipeName = "deskhand-secure";

    public static void WriteMessage(Stream s, byte[] json, byte[]? blob)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(len, json.Length);
        s.Write(len);
        s.Write(json);
        BinaryPrimitives.WriteInt32LittleEndian(len, blob?.Length ?? 0);
        s.Write(len);
        if (blob is { Length: > 0 }) s.Write(blob);
        s.Flush();
    }

    public static (byte[] json, byte[] blob) ReadMessage(Stream s)
    {
        byte[] json = ReadExactly(s, ReadLen(s));
        byte[] blob = ReadExactly(s, ReadLen(s));
        return (json, blob);
    }

    private static int ReadLen(Stream s)
    {
        Span<byte> b = stackalloc byte[4];
        ReadInto(s, b);
        int n = BinaryPrimitives.ReadInt32LittleEndian(b);
        if (n < 0 || n > 64 * 1024 * 1024) throw new IOException($"Bad frame length {n}.");
        return n;
    }

    private static byte[] ReadExactly(Stream s, int n)
    {
        var buf = new byte[n];
        ReadInto(s, buf);
        return buf;
    }

    private static void ReadInto(Stream s, Span<byte> buf)
    {
        int off = 0;
        while (off < buf.Length)
        {
            int r = s.Read(buf[off..]);
            if (r <= 0) throw new EndOfStreamException("Secure-helper pipe closed.");
            off += r;
        }
    }
}
