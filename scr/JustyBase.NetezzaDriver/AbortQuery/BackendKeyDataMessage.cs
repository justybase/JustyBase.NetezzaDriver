using JustyBase.NetezzaDriver.Utility;

namespace JustyBase.NetezzaDriver.AbortQuery;

/// <summary>
/// Represents a message containing backend key data from the server.
/// </summary>
internal sealed class BackendKeyDataMessage
{
    /// <summary>
    /// Gets the backend process ID.
    /// </summary>
    internal int BackendProcessId { get; }

    /// <summary>
    /// Gets the backend secret key.
    /// </summary>
    internal int BackendSecretKey { get; }

    private BackendKeyDataMessage(int backendProcessId, int backendSecretKey)
    {
        BackendProcessId = backendProcessId;
        BackendSecretKey = backendSecretKey;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="BackendKeyDataMessage"/> class.
    /// </summary>
    /// <param name="stream">The stream to read the backend key data from.</param>
    public BackendKeyDataMessage(Stream stream)
        : this(PGUtil.ReadInt32(stream), PGUtil.ReadInt32(stream))
    {
    }

    public static async Task<BackendKeyDataMessage> CreateAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        int backendProcessId = await PGUtil.ReadInt32Async(stream, cancellationToken).ConfigureAwait(false);
        int backendSecretKey = await PGUtil.ReadInt32Async(stream, cancellationToken).ConfigureAwait(false);
        return new BackendKeyDataMessage(backendProcessId, backendSecretKey);
    }
}
