using System.Data.Common;

namespace JustyBase.NetezzaDriver;

public sealed class NetezzaException : DbException
{
    private static readonly IReadOnlyDictionary<char, string> EmptyDiagnostics =
        new System.Collections.ObjectModel.ReadOnlyDictionary<char, string>(new Dictionary<char, string>());

    public NetezzaException() : this(string.Empty) { }

    public NetezzaException(string msg) : base(msg)
    {
        RawResponse = msg;
        Diagnostics = EmptyDiagnostics;
    }

    public NetezzaException(string msg, Exception exception) : base(msg, exception)
    {
        RawResponse = msg;
        Diagnostics = EmptyDiagnostics;
    }

    public NetezzaException(Exception exception) : this(string.Empty, exception) { }

    internal NetezzaException(BackendDiagnosticResponse response) : base(response.Message)
    {
        RawResponse = response.RawResponse;
        Severity = response.Severity;
        SqlState = response.SqlState;
        Detail = response.Detail;
        Hint = response.Hint;
        Diagnostics = response.Diagnostics;
    }

    /// <summary>The severity reported by Netezza, when included in the response.</summary>
    public string? Severity { get; }

    /// <summary>The five-character SQLSTATE reported by Netezza, when included.</summary>
    public override string? SqlState { get; }

    /// <summary>Additional server detail, when included in the response.</summary>
    public string? Detail { get; }

    /// <summary>A server-provided hint, when included in the response.</summary>
    public string? Hint { get; }

    /// <summary>The complete decoded backend response, including its diagnostic fields.</summary>
    public string? RawResponse { get; }

    /// <summary>All diagnostic fields supplied by the backend, keyed by protocol field code.</summary>
    public IReadOnlyDictionary<char, string> Diagnostics { get; }
}
public sealed class InterfaceException : DbException
{
    public InterfaceException() : base() { }
    public InterfaceException(string msg) : base(msg) { }
    public InterfaceException(string msg, Exception exception) : base(msg, exception) { }
}


public sealed class AttributeException : DbException { }
//public class NotSupportedException : DbException { }
//public class ConnectionClosedException : DbException { }
//public class DatabaseException : DbException { }
//public class OperationalException : DbException { }
//public class IntegrityException : DbException { }
//public class InternalException : DbException { }
