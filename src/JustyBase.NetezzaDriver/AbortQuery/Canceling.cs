using JustyBase.NetezzaDriver.Utility;

namespace JustyBase.NetezzaDriver.AbortQuery;

/// <summary>
/// Provides methods for sending cancel requests to the server.
/// </summary>
internal static class Canceling
{
    const int CancelRequestCode = 1234 << 16 | 5678;

    /// <summary>
    /// Writes a cancel request message to the specified stream.
    /// </summary>
    /// <param name="stream">The stream to write the cancel request to.</param>
    /// <param name="backendProcessId">The backend process ID.</param>
    /// <param name="backendSecretKey">The backend secret key.</param>
    public static void WriteCancelRequest(Stream stream, int backendProcessId, int backendSecretKey)
    {
        const int len = sizeof(int) +  // Length
                        sizeof(int) +  // Cancel request code
                        sizeof(int) +  // Backend process id
                        sizeof(int);   // Backend secret key

        PGUtil.WriteInt32(stream, len);
        PGUtil.WriteInt32(stream, CancelRequestCode);
        PGUtil.WriteInt32(stream, backendProcessId);
        PGUtil.WriteInt32(stream, backendSecretKey);
    }
}
