using System.Buffers.Binary;
using System.Text;

namespace JustyBase.NetezzaDriver.TestSupport;

/// <summary>
/// Builds byte-exact backend messages for replay fault injection. Every
/// message uses the Netezza framing observed on the wire: one message code,
/// a four-byte frame header (<c>-1</c>) and a four-byte payload length.
/// </summary>
public static class BackendTestMessages
{
    private const int FrameHeader = -1;

    public static byte[] CommandComplete(string tag = "SELECT 0")
        => Frame((byte)'C', Encoding.UTF8.GetBytes(tag + "\0"));

    public static byte[] ReadyForQuery()
        => Frame((byte)'Z', []);

    public static byte[] Success(string tag = "SELECT 0")
        => Concat(CommandComplete(tag), ReadyForQuery());

    public static byte[] Error(string message, string sqlState = "42S02", string severity = "ERROR", string? detail = null, string? hint = null)
        => Frame((byte)'E', Diagnostic(severity, sqlState, message, detail, hint));

    public static byte[] Notice(string message, string sqlState = "01000", string severity = "NOTICE", string? detail = null, string? hint = null)
        => Frame((byte)'N', Diagnostic(severity, sqlState, message, detail, hint));

    public static byte[] ErrorThenReady(string message, string sqlState = "42S02", string? detail = null, string? hint = null)
        => Concat(Error(message, sqlState, detail: detail, hint: hint), ReadyForQuery());

    public static byte[] NoticeThenSuccess(string message, string tag = "SELECT 0")
        => Concat(Notice(message), Success(tag));

    public static byte[] Frame(byte code, ReadOnlySpan<byte> payload)
    {
        byte[] message = new byte[9 + payload.Length];
        message[0] = code;
        BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(1), FrameHeader);
        BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(5), payload.Length);
        payload.CopyTo(message.AsSpan(9));
        return message;
    }

    public static byte[] Concat(params byte[][] parts)
    {
        int length = 0;
        foreach (byte[] part in parts)
        {
            length += part.Length;
        }

        byte[] result = new byte[length];
        int offset = 0;
        foreach (byte[] part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }
        return result;
    }

    private static byte[] Diagnostic(string severity, string sqlState, string message, string? detail, string? hint)
    {
        var payload = new List<byte>(64);
        Append(payload, 'S', severity);
        Append(payload, 'C', sqlState);
        Append(payload, 'M', message);
        if (detail is not null)
        {
            Append(payload, 'D', detail);
        }
        if (hint is not null)
        {
            Append(payload, 'H', hint);
        }
        payload.Add(0);
        return payload.ToArray();
    }

    private static void Append(List<byte> payload, char code, string value)
    {
        payload.Add((byte)code);
        payload.AddRange(Encoding.UTF8.GetBytes(value));
        payload.Add(0);
    }
}
