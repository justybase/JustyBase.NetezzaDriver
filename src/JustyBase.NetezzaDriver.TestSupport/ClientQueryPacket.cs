using System.Runtime.InteropServices;
using System.Text;

namespace JustyBase.NetezzaDriver.TestSupport;

/// <summary>
/// Detects a frontend query packet in the client-to-server byte stream.
/// Netezza sends a query as: 'P' + 4-byte command number + SQL text + NUL.
/// The SQL must begin with a known statement keyword, which distinguishes real
/// queries from the binary handshake bytes the client sends beforehand.
/// </summary>
internal static class ClientQueryPacket
{
    private static readonly string[] Keywords =
    [
        "select", "set", "insert", "update", "delete", "create", "drop", "alter",
        "show", "declare", "call", "with", "begin", "commit", "rollback", "explain",
        "grant", "revoke", "truncate", "values", "fetch", "prepare", "execute",
        "using", "copy", "reset",
    ];

    /// <summary>
    /// Returns the index of a complete query packet in <paramref name="pending"/>,
    /// or -1 when none is complete yet. <paramref name="packetEnd"/> is set to the
    /// byte offset just past the terminating NUL.
    /// </summary>
    public static int Find(List<byte> pending, out int packetEnd)
    {
        packetEnd = -1;
        int count = pending.Count;
        ReadOnlySpan<byte> span = CollectionsMarshal.AsSpan(pending);

        for (int i = 0; i + 5 <= count; i++)
        {
            if (span[i] != (byte)'P')
                continue;

            int sqlStart = i + 5;
            int j = sqlStart;
            while (j < count && span[j] != 0)
            {
                byte c = span[j];
                // Printable ASCII plus SQL whitespace (tab/newline/CR).
                if (c != (byte)'\t' && c != (byte)'\n' && c != (byte)'\r' && (c < 0x20 || c > 0x7E))
                    break;
                j++;
            }

            if (j < count && span[j] == 0 && j - sqlStart >= 8
                && StartsWithKeyword(span.Slice(sqlStart, j - sqlStart)))
            {
                packetEnd = j + 1;
                return i;
            }
        }

        return -1;
    }

    private static bool StartsWithKeyword(ReadOnlySpan<byte> sql)
    {
        int wordEnd = 0;
        while (wordEnd < sql.Length && sql[wordEnd] != (byte)' ')
            wordEnd++;

        if (wordEnd == 0 || wordEnd >= sql.Length)
            return false;

        Span<char> word = stackalloc char[wordEnd];
        for (int i = 0; i < wordEnd; i++)
            word[i] = char.ToLowerInvariant((char)sql[i]);

        foreach (string keyword in Keywords)
        {
            if (word.SequenceEqual(keyword))
                return true;
        }

        return false;
    }
}
