using System.Collections.ObjectModel;
using System.Text;

namespace JustyBase.NetezzaDriver;

internal sealed record BackendDiagnosticResponse(
    string Message,
    string RawResponse,
    string? Severity,
    string? SqlState,
    string? Detail,
    string? Hint,
    IReadOnlyDictionary<char, string> Diagnostics);

/// <summary>
/// Parses PostgreSQL-style ErrorResponse and NoticeResponse payloads while
/// retaining a plain-text fallback for older Netezza servers.
/// </summary>
internal static class BackendDiagnosticResponseParser
{
    private const string EmptyMessage = "Netezza backend returned an empty error response";

    public static BackendDiagnosticResponse Parse(ReadOnlySpan<byte> payload)
    {
        string rawResponse = Encoding.UTF8.GetString(payload);
        string fallbackMessage = rawResponse.Replace("\0", string.Empty).Trim();
        if (fallbackMessage.Length == 0)
        {
            fallbackMessage = EmptyMessage;
        }

        int nullCount = 0;
        foreach (byte value in payload)
        {
            if (value == 0)
            {
                nullCount++;
            }
        }

        var fields = new Dictionary<char, string>();
        if (nullCount >= 2)
        {
            int offset = 0;
            while (offset < payload.Length)
            {
                byte fieldCode = payload[offset++];
                if (fieldCode == 0)
                {
                    break;
                }

                int valueStart = offset;
                while (offset < payload.Length && payload[offset] != 0)
                {
                    offset++;
                }

                string value = Encoding.UTF8.GetString(payload[valueStart..offset]);
                fields[(char)fieldCode] = value;

                if (offset < payload.Length)
                {
                    offset++;
                }
            }
        }

        fields.TryGetValue('M', out string? message);
        fields.TryGetValue('V', out string? nonLocalizedSeverity);
        fields.TryGetValue('S', out string? localizedSeverity);
        fields.TryGetValue('C', out string? sqlState);
        fields.TryGetValue('D', out string? detail);
        fields.TryGetValue('H', out string? hint);

        var diagnostics = new ReadOnlyDictionary<char, string>(fields);
        return new BackendDiagnosticResponse(
            string.IsNullOrEmpty(message) ? fallbackMessage : message,
            rawResponse,
            nonLocalizedSeverity ?? localizedSeverity,
            sqlState,
            detail,
            hint,
            diagnostics);
    }
}
