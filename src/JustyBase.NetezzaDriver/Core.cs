using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace JustyBase.NetezzaDriver;

public enum ClientTypeId : short
{
    Invalid = -1,
    None = 0,
    Sql = 1,
    SqlOdbc = 2,
    SqlJdbc = 3,
    Load = 4,
    Client = 5,
    Bnr = 6,
    Reclaim = 7,
    //Unknown = 8,
    SqlOledb = 9,
    Internal = 10,
    SqlDotnet = 11,
    SqlGolang = 12,
    SqlPython = 13
    //Unknown2 = 14
}

enum BackendMessageCode : byte
{
    AuthenticationRequest = (byte)'R',
    BackendKeyData = (byte)'K',
    BindComplete = (byte)'2',
    CommandComplete = (byte)'C',
    CopyData = (byte)'d',
    CopyDone = (byte)'c',
    CopyInResponse = (byte)'G',
    CopyOutResponse = (byte)'H',
    DataRow = (byte)'D',
    RowStandard = (byte)'Y',
    RowDescriptionStandard = (byte)'X',
    EmptyQueryResponse = (byte)'I',
    ErrorResponse = (byte)'E', // 69
    NoData = (byte)'n',
    NoticeResponse = (byte)'N',
    ParameterStatus = (byte)'S',
    ParseComplete = (byte)'1',
    PortalSuspended = (byte)'s',
    ReadyForQuery = (byte)'Z',
    RowDescription = (byte)'T',
    ParameterDescription = (byte)'t',
    NotificationResponse = (byte)'A'
}

public enum SecurityLevelCode : int
{
    PreferredUnsecured = 0,
    OnlyUnsecuredSession = 1,
    PreferredSecuredSession = 2,
    OnlySecuredSession = 3
}


//static class FrontendMessageCode
//{
//internal const byte Describe =   (byte)'D';
//internal const byte Sync =       (byte)'S';
//internal const byte Execute =    (byte)'E';
//internal const byte Parse =      (byte)'P';
//internal const byte Bind =       (byte)'B';
//internal const byte Close =      (byte)'C';
//internal const byte Query =    (byte)'Q';
//internal const byte CopyData = (byte)'d';
//internal const byte CopyDone = (byte)'c';
//internal const byte CopyFail = (byte)'f';
//internal const byte Terminate =  (byte)'X';
//internal const byte Password =   (byte)'p';
//internal const byte Flush =      (byte)'H';
//}

//enum StatementOrPortal : byte
//{
//    Statement = (byte)'S',
//    Portal = (byte)'P'
//}

internal static class Core
{
    //public const int CONN_NOT_CONNECTED = 0;
    //public const int CONN_CONNECTED = 1;
    //public const int CONN_FETCHING = 3;
    //public const int CONN_CANCELLED = 4;

    public const int EXTAB_SOCK_DATA = 1;//  # block of records
    public const int EXTAB_SOCK_ERROR = 2;//  # error message
    public const int EXTAB_SOCK_DONE = 3;//  # normal wrap-up
    //public const int EXTAB_SOCK_FLUSH = 4;//  # Flush the current buffer/data
    
    //public static byte[] FLUSH_MSG;
    //public static byte[] SYNC_MSG;
    //public static byte[] TERMINATE_MSG;
    //public static byte[] COPY_DONE_MSG;
    //public static byte[] EXECUTE_MSG;

    //static Core()
    //{
        //FLUSH_MSG  = CreateMessage(FrontendMessageCode.Flush);
        //SYNC_MSG = CreateMessage(FrontendMessageCode.Sync);
        //TERMINATE_MSG = CreateMessage(FrontendMessageCode.Terminate);
        //COPY_DONE_MSG = CreateMessage((byte)BackendMessageCode.CopyDone);
        //EXECUTE_MSG = CreateMessage(FrontendMessageCode.Execute, [0,0,0,0,0]);
    //}
    //private static byte[] CreateMessage(byte code, byte[]? data = null)
    //{
    //    return CreateMessage([code],data);
    //}
    //private static byte[] CreateMessage(byte[] code, byte[]? data = null)
    //{
    //    data ??= [];
    //    return code.Concat(IPack(data.Length + 4)).Concat(data).ToArray();
    //}

    public static void IPack(int value, Span<byte> destination)
    {
        value = BitConverter.IsLittleEndian ? BinaryPrimitives.ReverseEndianness(value) : value;
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        Unsafe.As<byte, int>(ref bytes[0]) = value;
        bytes.CopyTo(destination);
    }
}
