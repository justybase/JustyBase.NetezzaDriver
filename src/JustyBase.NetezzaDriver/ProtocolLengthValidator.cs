namespace JustyBase.NetezzaDriver;

internal static class ProtocolLengthValidator
{
    internal const int MaxPayloadLength = 10_000_000;

    internal static int Validate(
        int length,
        string field,
        bool allowZero = true,
        string? context = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(field);

        if (length < 0)
        {
            throw new NetezzaException(CreateMessage(
                field,
                length,
                context,
                "negative lengths are not valid"));
        }

        if (!allowZero && length == 0)
        {
            throw new NetezzaException(CreateMessage(
                field,
                length,
                context,
                "zero is not valid for this field"));
        }

        if (length > MaxPayloadLength)
        {
            throw new NetezzaException(CreateMessage(
                field,
                length,
                context,
                $"length exceeds the maximum supported protocol payload of {MaxPayloadLength} bytes"));
        }

        return length;
    }

    internal static int ValidateAfterOverhead(
        int frameLength,
        int overhead,
        string frameField,
        string payloadField,
        bool payloadAllowZero = true,
        string? context = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(frameField);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadField);

        if (overhead < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(overhead), overhead, "Protocol overhead cannot be negative.");
        }

        // Validate the raw frame length before doing arithmetic on it. This is
        // important for protocol fields which contain a trailing terminator or
        // another fixed-size part of the frame.
        Validate(frameLength, frameField, allowZero: false, context);

        if (frameLength < overhead)
        {
            throw new NetezzaException(
                CreateMessage(
                    payloadField,
                    frameLength - overhead,
                    context,
                    $"frame length is smaller than the required overhead of {overhead} bytes"));
        }

        return Validate(frameLength - overhead, payloadField, payloadAllowZero, context);
    }

    private static string CreateMessage(
        string field,
        int length,
        string? context,
        string reason)
    {
        var suffix = string.IsNullOrWhiteSpace(context) ? string.Empty : $" ({context})";
        return $"Invalid backend protocol length for '{field}': {length}{suffix}; {reason}. " +
               "The connection is no longer safe to reuse; reconnect is required.";
    }
}
