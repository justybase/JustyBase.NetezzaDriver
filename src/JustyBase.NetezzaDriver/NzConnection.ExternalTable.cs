using JustyBase.NetezzaDriver.AbortQuery;
using JustyBase.NetezzaDriver.StringPool;
using JustyBase.NetezzaDriver.TypeConvertions;
using JustyBase.NetezzaDriver.Utility;
using Microsoft.Extensions.Logging;
using System.Buffers;
using System.Buffers.Binary;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;

// External-table export/import and unload file transfer.

namespace JustyBase.NetezzaDriver;

public sealed partial class NzConnection
{

    private void InitializeUnloadFileStream(string fileName)
    {
        try
        {
            _nextRelatedFileStream = new FileStream(fileName, FileMode.OpenOrCreate, FileAccess.Write);
            _logger?.LogDebug("Successfully opened file: {Filename}", fileName);
            byte[] buf = [0, 0, 0, 0];
            WriteSpan(buf);
            Flush();
        }
        catch (IOException ex)
        {
            _logger?.LogWarning(ex, "Error while opening file");
            throw new NetezzaException("Error while opening unload file", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger?.LogWarning(ex, "Error while opening file");
            throw new NetezzaException("Error while opening unload file", ex);
        }
        catch (ArgumentException ex)
        {
            _logger?.LogWarning(ex, "Error while opening file");
            throw new NetezzaException("Error while opening unload file", ex);
        }
        catch (NotSupportedException ex)
        {
            _logger?.LogWarning(ex, "Error while opening file");
            throw new NetezzaException("Error while opening unload file", ex);
        }
    }

    private async Task InitializeUnloadFileStreamAsync(string fileName, CancellationToken cancellationToken)
    {
        try
        {
            _nextRelatedFileStream = new FileStream(fileName, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 4096, useAsync: true);
            _logger?.LogDebug("Successfully opened file: {Filename}", fileName);
            byte[] buf = [0, 0, 0, 0];
            await WriteSpanAsync(buf, cancellationToken).ConfigureAwait(false);
            await FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            _logger?.LogWarning(ex, "Error while opening file");
            throw new NetezzaException("Error while opening unload file", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger?.LogWarning(ex, "Error while opening file");
            throw new NetezzaException("Error while opening unload file", ex);
        }
        catch (ArgumentException ex)
        {
            _logger?.LogWarning(ex, "Error while opening file");
            throw new NetezzaException("Error while opening unload file", ex);
        }
        catch (NotSupportedException ex)
        {
            _logger?.LogWarning(ex, "Error while opening file");
            throw new NetezzaException("Error while opening unload file", ex);
        }
    }

    private void HandleExternalTableProtocolMessage()
    {
        switch (_lastResponse)
        {
            case (byte)'u':
                _readBuffer!.Skip(10);
                _readBuffer.Skip(16);
                int length = ReadProtocolLength("externalTable.fileNameLength");
                var fileNameBytes = Read(length);
                string fileName = Encoding.UTF8.GetString(fileNameBytes, 0, length);
                InitializeUnloadFileStream(fileName);
                return;

            case (byte)'U':
                if (_nextRelatedFileStream is null)
                {
                    throw new NetezzaException("Unload file stream is not initialized.");
                }
                ReceiveAndWriteDataToExternal(_nextRelatedFileStream);
                return;

            case (byte)'l':
                XferTable();
                return;

            case (byte)'x':
                _readBuffer!.Skip(4);
                _logger?.LogWarning("Error operation cancel");
                return;
        }
    }

    private async Task HandleExternalTableProtocolMessageAsync(CancellationToken cancellationToken)
    {
        switch (_lastResponse)
        {
            case (byte)'u':
                await SkipBytesAsync(10, cancellationToken).ConfigureAwait(false);
                await SkipBytesAsync(16, cancellationToken).ConfigureAwait(false);
                int length = await ReadProtocolLengthAsync(
                    "externalTable.fileNameLength",
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                var fileNameBytes = await ReadAsync(length, cancellationToken: cancellationToken).ConfigureAwait(false);
                string fileName = Encoding.UTF8.GetString(fileNameBytes, 0, length);
                await InitializeUnloadFileStreamAsync(fileName, cancellationToken).ConfigureAwait(false);
                return;

            case (byte)'U':
                if (_nextRelatedFileStream is null)
                {
                    throw new NetezzaException("Unload file stream is not initialized.");
                }
                await ReceiveAndWriteDataToExternalAsync(_nextRelatedFileStream, cancellationToken).ConfigureAwait(false);
                return;

            case (byte)'l':
                await XferTableAsync(cancellationToken).ConfigureAwait(false);
                return;

            case (byte)'x':
                await SkipBytesAsync(4, cancellationToken).ConfigureAwait(false);
                _logger?.LogWarning("Error operation cancel");
                return;
        }
    }

    private bool IntepretReturnedByte(NzCommand nzCommand, bool discardRows = false)
    {
        _shouldReadByte = true;
        if (_logger?.IsEnabled(LogLevel.Debug) == true)
            _logger.LogDebug(
                "Backend response: ResponseType={ResponseType} ResponseCode=0x{ResponseCode:X2} Row={Row}",
                ProtocolResponseType,
                _lastResponse,
                _currentProtocolRowNumber);
        ReadProtocolInt32("frameHeaderValue");

        if (_lastResponse == (byte)BackendMessageCode.CommandComplete)
        {
            // portal query command, no tuples returned
            int length = ReadProtocolLength("commandCompletePayloadLength");
            var data = ReadMessagePayload(length);
            HandleCommandComplete(data, length, nzCommand);
            //returnet data informs about command type (SELECT/SET VARIABLE/...)
            if (_logger?.IsEnabled(LogLevel.Debug) == true)
                _logger.LogDebug("Response received from backend: {Data}", Encoding.UTF8.GetString(data, 0, length));
        }
        else if (_lastResponse == (byte)BackendMessageCode.ReadyForQuery)
        {
            return false;
        }
        else if (_lastResponse == (byte)'L')
        {
            return false;
        }
        else if (_lastResponse == (byte)'0')
        {
            //return true;
        }
        else if (_lastResponse == (byte)'A')
        {
            //return true;
        }
        else if (_lastResponse == (byte)'P')//80
        {
            int length = ReadProtocolLength("preparedPayloadLength");
            var data = ReadMessagePayload(length);
            if (_logger?.IsEnabled(LogLevel.Debug) == true)
                _logger.LogDebug("Response received from backend: {Data}", Encoding.UTF8.GetString(data, 0, length));
            //doContinue = true;
        }
        else if (_lastResponse == (byte)BackendMessageCode.ErrorResponse)
        {
            int length = ReadProtocolLength("errorPayloadLength");
            var data = ReadMessagePayload(length);
            _backendException = new NetezzaException(BackendDiagnosticResponseParser.Parse(data.AsSpan(0, length)));
            _error = _backendException.Message;
            if (_logger?.IsEnabled(LogLevel.Debug) == true)
                _logger.LogDebug("Response received from backend: {_error}", _error);
            //doContinue = true;
        }
        //this STARTS (after 'P') single rowset
        else if (_lastResponse == (byte)BackendMessageCode.RowDescription)
        {
            int length = ReadProtocolLength("rowDescriptionPayloadLength");
            nzCommand.NewPreparedStatement ??= new PreparedStatement();
            var data = ReadMessagePayload(length);
            HandleRowDescription(data, nzCommand);
            // We've got row_desc that allows us to identify what we're going to get back from this statement.
            //nzCommand.NewPreparedStatement.input_funcs = nzCommand.NewPreparedStatement!.Description!.GetFuncArray;
        }
        else if (_lastResponse == (byte)BackendMessageCode.DataRow)//read rows in schema/system queries - hot path
        {
            if (discardRows)
            {
                SkipDataRowPayload();
            }
            else
            {
                int length = ReadProtocolLength("dataRowPayloadLength");
                var data = ReadMessagePayload(length);
                HandleDataRow(data, nzCommand);
            }
        }
        else if (_lastResponse == (byte)BackendMessageCode.RowDescriptionStandard)// metadata for standard query, occurs after BackendMessageCode.RowDescription
        {
            int length = ReadProtocolLength("rowDescriptionStandardPayloadLength");
            _tupdesc = new DbosTupleDesc();
            var data = ReadMessagePayload(length);
            ResGetDbosColumnDescriptions(data.AsSpan(0, length));
            //doContinue = true;
        }
        else if (_lastResponse == (byte)BackendMessageCode.RowStandard)//!!!!!!, main hot path - read rows
        {
            if (discardRows)
                SkipRowStandardPayload();
            else
                ResReadDbosTuple(nzCommand);
            //Thread.Sleep(50);
            //doContinue = true;
        }
        else if (_lastResponse is (byte)'u' or (byte)'U' or (byte)'l' or (byte)'x')
        {
            HandleExternalTableProtocolMessage();
        }
        else if (_lastResponse == (byte)'e')
        {
            int length = ReadProtocolLength("fileTransfer.logDirectoryLength", allowZero: false);
            int logDirectoryPayloadLength = ValidateProtocolLengthAfterOverhead(
                length,
                1,
                "fileTransfer.logDirectoryLength",
                "fileTransfer.logDirectoryPayloadLength");
            string logDir = Encoding.UTF8.GetString(Read(logDirectoryPayloadLength));

            _readBuffer!.ReadByte();
            // ignore one byte as it is null character at the end of the string
            var filenameBuf = new List<byte> { _readBuffer.ReadByte() };
            while (true)
            {
                var charByte = _readBuffer.ReadByte();
                if (charByte == 0x00)
                {
                    break;
                }
                filenameBuf.Add(charByte);
            }

            string filename = Encoding.UTF8.GetString(filenameBuf.ToArray());
            int logType = _readBuffer.ReadInt32BigEndian();
            if (_pendingBulkData is not null)
            {
                DrainFileTransferPayload();
            }
            else if (!GetFileFromBE(logDir, filename, logType))
            {
                _logger?.LogDebug("Error in writing file received from BE");
            }
            //doContinue = true;
        }
        else if (_lastResponse == (byte)BackendMessageCode.NoticeResponse)
        {
            int length = ReadProtocolLength("noticePayloadLength");
            var data = ReadMessagePayload(length);
            string notice = BackendDiagnosticResponseParser.Parse(data.AsSpan(0, length)).Message;
            OnNoticeReceived(notice, nzCommand);
            if (_logger?.IsEnabled(LogLevel.Debug) == true)
                _logger.LogDebug("Response received from backend: {Notice}", notice);
        }
        else if (_lastResponse == (byte)'I')
        {
            int length = ReadProtocolLength("emptyQueryPayloadLength");
            var data = ReadMessagePayload(length);
            string notice = Encoding.UTF8.GetString(data[0..length]);
            OnNoticeReceived(notice, nzCommand);
            if (_logger?.IsEnabled(LogLevel.Debug) == true)
                _logger.LogDebug("Response received from backend: {Notice}", notice);
            nzCommand.AddRow([]);
        }

        return true;
    }

    private async ValueTask<bool> IntepretReturnedByteAsync(NzCommand nzCommand, CancellationToken cancellationToken = default, bool discardRows = false)
    {
        _shouldReadByte = true;
        if (_logger?.IsEnabled(LogLevel.Debug) == true)
            _logger.LogDebug(
                "Backend response: ResponseType={ResponseType} ResponseCode=0x{ResponseCode:X2} Row={Row}",
                ProtocolResponseType,
                _lastResponse,
                _currentProtocolRowNumber);
        await ReadProtocolInt32Async("frameHeaderValue", cancellationToken)
            .ConfigureAwait(false);

        if (_lastResponse == (byte)BackendMessageCode.CommandComplete)
        {
            int length = await ReadProtocolLengthAsync(
                "commandCompletePayloadLength",
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var data = await ReadMessagePayloadAsync(length, cancellationToken).ConfigureAwait(false);
            HandleCommandComplete(data, length, nzCommand);
            if (_logger?.IsEnabled(LogLevel.Debug) == true)
                _logger.LogDebug("Response received from backend: {Data}", Encoding.UTF8.GetString(data, 0, length));
        }
        else if (_lastResponse == (byte)BackendMessageCode.ReadyForQuery)
        {
            return false;
        }
        else if (_lastResponse == (byte)'L')
        {
            return false;
        }
        else if (_lastResponse == (byte)'0')
        {
        }
        else if (_lastResponse == (byte)'A')
        {
        }
        else if (_lastResponse == (byte)'P')
        {
            int length = await ReadProtocolLengthAsync(
                "preparedPayloadLength",
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var data = await ReadMessagePayloadAsync(length, cancellationToken).ConfigureAwait(false);
            if (_logger?.IsEnabled(LogLevel.Debug) == true)
                _logger.LogDebug("Response received from backend: {Data}", Encoding.UTF8.GetString(data, 0, length));
        }
        else if (_lastResponse == (byte)BackendMessageCode.ErrorResponse)
        {
            int length = await ReadProtocolLengthAsync(
                "errorPayloadLength",
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var data = await ReadMessagePayloadAsync(length, cancellationToken).ConfigureAwait(false);
            _backendException = new NetezzaException(BackendDiagnosticResponseParser.Parse(data.AsSpan(0, length)));
            _error = _backendException.Message;
            if (_logger?.IsEnabled(LogLevel.Debug) == true)
                _logger.LogDebug("Response received from backend: {_error}", _error);
        }
        else if (_lastResponse == (byte)BackendMessageCode.RowDescription)
        {
            int length = await ReadProtocolLengthAsync(
                "rowDescriptionPayloadLength",
                cancellationToken: cancellationToken).ConfigureAwait(false);
            nzCommand.NewPreparedStatement ??= new PreparedStatement();
            var data = await ReadMessagePayloadAsync(length, cancellationToken).ConfigureAwait(false);
            HandleRowDescription(data, nzCommand);
        }
        else if (_lastResponse == (byte)BackendMessageCode.DataRow)
        {
            if (discardRows)
            {
                await SkipDataRowPayloadAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                int length = await ReadProtocolLengthAsync(
                    "dataRowPayloadLength",
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                var data = await ReadMessagePayloadAsync(length, cancellationToken).ConfigureAwait(false);
                HandleDataRow(data, nzCommand);
            }
        }
        else if (_lastResponse == (byte)BackendMessageCode.RowDescriptionStandard)
        {
            int length = await ReadProtocolLengthAsync(
                "rowDescriptionStandardPayloadLength",
                cancellationToken: cancellationToken).ConfigureAwait(false);
            _tupdesc = new DbosTupleDesc();
            var data = await ReadMessagePayloadAsync(length, cancellationToken).ConfigureAwait(false);
            ResGetDbosColumnDescriptions(data.AsSpan(0, length));
        }
        else if (_lastResponse == (byte)BackendMessageCode.RowStandard)
        {
            if (discardRows)
                await SkipRowStandardPayloadAsync(cancellationToken).ConfigureAwait(false);
            else
                await ResReadDbosTupleAsync(nzCommand, cancellationToken).ConfigureAwait(false);
        }
        else if (_lastResponse is (byte)'u' or (byte)'U' or (byte)'l' or (byte)'x')
        {
            await HandleExternalTableProtocolMessageAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (_lastResponse == (byte)'e')
        {
            int length = await ReadProtocolLengthAsync(
                "fileTransfer.logDirectoryLength",
                allowZero: false,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            int logDirectoryPayloadLength = ValidateProtocolLengthAfterOverhead(
                length,
                1,
                "fileTransfer.logDirectoryLength",
                "fileTransfer.logDirectoryPayloadLength");
            var logDirBytes = await ReadAsync(
                logDirectoryPayloadLength,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            string logDir = Encoding.UTF8.GetString(logDirBytes);

            _ = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
            var filenameBuf = new List<byte> { (byte)await ReadByteAsync(cancellationToken).ConfigureAwait(false) };
            while (true)
            {
                var charByte = (byte)await ReadByteAsync(cancellationToken).ConfigureAwait(false);
                if (charByte == 0x00)
                {
                    break;
                }
                filenameBuf.Add(charByte);
            }

            string filename = Encoding.UTF8.GetString(filenameBuf.ToArray());
            int logType = await ReadInt32Async(cancellationToken).ConfigureAwait(false);
            if (_pendingBulkData is not null)
            {
                await DrainFileTransferPayloadAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (!await GetFileFromBEAsync(logDir, filename, logType, cancellationToken).ConfigureAwait(false))
            {
                _logger?.LogDebug("Error in writing file received from BE");
            }
        }
        else if (_lastResponse == (byte)BackendMessageCode.NoticeResponse)
        {
            int length = await ReadProtocolLengthAsync(
                "noticePayloadLength",
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var data = await ReadMessagePayloadAsync(length, cancellationToken).ConfigureAwait(false);
            string notice = BackendDiagnosticResponseParser.Parse(data.AsSpan(0, length)).Message;
            OnNoticeReceived(notice, nzCommand);
            if (_logger?.IsEnabled(LogLevel.Debug) == true)
                _logger.LogDebug("Response received from backend: {Notice}", notice);
        }
        else if (_lastResponse == (byte)'I')
        {
            int length = await ReadProtocolLengthAsync(
                "emptyQueryPayloadLength",
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var data = await ReadMessagePayloadAsync(length, cancellationToken).ConfigureAwait(false);
            string notice = Encoding.UTF8.GetString(data, 0, length);
            OnNoticeReceived(notice, nzCommand);
            if (_logger?.IsEnabled(LogLevel.Debug) == true)
                _logger.LogDebug("Response received from backend: {Notice}", notice);
            nzCommand.AddRow([]);
        }

        return true;
    }


    private void XferTable()
    {
        ReadProtocolInt32("externalTable.frameHeaderValue");
        int clientVersion = 1;

        byte charByte = _readBuffer!.ReadByte();

        var filenameBuf = new List<byte> { charByte };
        while (true)
        {
            charByte = _readBuffer.ReadByte();
            if (charByte == 0x00)
            {
                break;
            }
            filenameBuf.Add(charByte);
        }

        string filename = NzConnectionHelpers.ClientEncoding.GetString(filenameBuf.ToArray());

        int hostVersion = _readBuffer.ReadInt32BigEndian();
        PGUtil.WriteInt32(_stream, clientVersion);

        Flush();

        int format = _readBuffer.ReadInt32BigEndian();
        int blockSize = ReadProtocolLength("externalTable.blockSize", allowZero: false);
        _logger?.LogInformation("Format={Format} Block size={BlockSize} Host version={HostVersion}", format, blockSize, hostVersion);

        if (_pendingBulkData is { } bulkData)
        {
            bulkData.WriteRows(_stream, blockSize);
            _logger?.LogInformation("Streamed {RowCount} bulk-insert rows from memory", bulkData.RowCount);
            return;
        }

        int effectiveBlockSize = Math.Max(blockSize, 1);
        try
        {
            using var filehandle = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.Read, Math.Max(effectiveBlockSize, 4096), useAsync: false);

            if (_logger?.IsEnabled(LogLevel.Information) == true)
            {
                _logger?.LogInformation("Successfully opened External file to read: {Filename}", filename);
            }

            byte[] bytesBuffer = ArrayPool<byte>.Shared.Rent(effectiveBlockSize);
            try
            {
                while (true)
                {
                    int bytesReaded = filehandle.Read(bytesBuffer, 0, effectiveBlockSize);
                    if (bytesReaded == 0)
                    {
                        break;
                    }

                    Span<byte> dataBytes = bytesBuffer.AsSpan();
                    dataBytes = dataBytes[..bytesReaded];

                    if (blockSize < dataBytes.Length)
                    {
                        int diff = dataBytes.Length - blockSize;

                        PGUtil.WriteInt32(_stream, Core.EXTAB_SOCK_DATA);
                        PGUtil.WriteInt32(_stream, blockSize);
                        WriteSpan(dataBytes[..blockSize]);
                        Flush();

                        PGUtil.WriteInt32(_stream, Core.EXTAB_SOCK_DATA);
                        PGUtil.WriteInt32(_stream, diff);
                        WriteSpan(dataBytes[blockSize..]);
                        Flush();
                    }
                    else
                    {
                        PGUtil.WriteInt32(_stream, Core.EXTAB_SOCK_DATA);
                        PGUtil.WriteInt32(_stream, dataBytes.Length);
                        WriteSpan(dataBytes);
                        Flush();
                    }
                    if (_logger?.IsEnabled(LogLevel.Debug) == true)
                    {
                        _logger?.LogDebug("No. of bytes sent to BE: {BytesSent}", dataBytes.Length);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(bytesBuffer);
            }

            PGUtil.WriteInt32(_stream, Core.EXTAB_SOCK_DONE);
            Flush();
            _logger?.LogInformation("sent EXTAB_SOCK_DONE to reader");
        }
        catch (IOException ex)
        {
            _logger?.LogWarning(ex, "Error opening file");
            throw new NetezzaException("Error opening file", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger?.LogWarning(ex, "Error opening file");
            throw new NetezzaException("Error opening file", ex);
        }
        catch (ArgumentException ex)
        {
            _logger?.LogWarning(ex, "Error opening file");
            throw new NetezzaException("Error opening file", ex);
        }
        catch (NotSupportedException ex)
        {
            _logger?.LogWarning(ex, "Error opening file");
            throw new NetezzaException("Error opening file", ex);
        }
    }

    private async Task XferTableAsync(CancellationToken cancellationToken = default)
    {
        await ReadProtocolInt32Async(
            "externalTable.frameHeaderValue",
            cancellationToken).ConfigureAwait(false);
        int clientVersion = 1;

        byte charByte = (byte)await ReadByteAsync(cancellationToken).ConfigureAwait(false);
        var filenameBuf = new List<byte> { charByte };
        while (true)
        {
            charByte = (byte)await ReadByteAsync(cancellationToken).ConfigureAwait(false);
            if (charByte == 0x00)
            {
                break;
            }
            filenameBuf.Add(charByte);
        }

        string filename = NzConnectionHelpers.ClientEncoding.GetString(filenameBuf.ToArray());
        int hostVersion = await ReadInt32Async(cancellationToken).ConfigureAwait(false);
        await PGUtil.WriteInt32Async(_stream, clientVersion, cancellationToken).ConfigureAwait(false);
        await FlushAsync(cancellationToken).ConfigureAwait(false);

        int format = await ReadInt32Async(cancellationToken).ConfigureAwait(false);
        int blockSize = await ReadProtocolLengthAsync(
            "externalTable.blockSize",
            allowZero: false,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        _logger?.LogInformation("Format={Format} Block size={BlockSize} Host version={HostVersion}", format, blockSize, hostVersion);

        if (_pendingBulkData is { } bulkData)
        {
            await bulkData.WriteRowsAsync(_stream, blockSize, cancellationToken).ConfigureAwait(false);
            _logger?.LogInformation("Streamed {RowCount} bulk-insert rows from memory", bulkData.RowCount);
            return;
        }

        int effectiveBlockSize = Math.Max(blockSize, 1);
        try
        {
            await using var filehandle = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.Read, Math.Max(effectiveBlockSize, 4096), useAsync: true);

            if (_logger?.IsEnabled(LogLevel.Information) == true)
            {
                _logger?.LogInformation("Successfully opened External file to read: {Filename}", filename);
            }

            byte[] bytesBuffer = ArrayPool<byte>.Shared.Rent(effectiveBlockSize);
            try
            {
                while (true)
                {
                    int bytesReaded = await filehandle.ReadAsync(bytesBuffer.AsMemory(0, effectiveBlockSize), cancellationToken).ConfigureAwait(false);
                    if (bytesReaded == 0)
                    {
                        break;
                    }

                    ReadOnlyMemory<byte> dataBytes = bytesBuffer.AsMemory(0, bytesReaded);
                    await PGUtil.WriteInt32Async(_stream, Core.EXTAB_SOCK_DATA, cancellationToken).ConfigureAwait(false);
                    await PGUtil.WriteInt32Async(_stream, dataBytes.Length, cancellationToken).ConfigureAwait(false);
                    await WriteSpanAsync(dataBytes, cancellationToken).ConfigureAwait(false);
                    await FlushAsync(cancellationToken).ConfigureAwait(false);

                    if (_logger?.IsEnabled(LogLevel.Debug) == true)
                    {
                        _logger?.LogDebug("No. of bytes sent to BE: {BytesSent}", dataBytes.Length);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(bytesBuffer);
            }

            await PGUtil.WriteInt32Async(_stream, Core.EXTAB_SOCK_DONE, cancellationToken).ConfigureAwait(false);
            await FlushAsync(cancellationToken).ConfigureAwait(false);
            _logger?.LogInformation("sent EXTAB_SOCK_DONE to reader");
        }
        catch (IOException ex)
        {
            _logger?.LogWarning(ex, "Error opening file");
            throw new NetezzaException("Error opening file", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger?.LogWarning(ex, "Error opening file");
            throw new NetezzaException("Error opening file", ex);
        }
        catch (ArgumentException ex)
        {
            _logger?.LogWarning(ex, "Error opening file");
            throw new NetezzaException("Error opening file", ex);
        }
        catch (NotSupportedException ex)
        {
            _logger?.LogWarning(ex, "Error opening file");
            throw new NetezzaException("Error opening file", ex);
        }
    }

    private void DrainFileTransferPayload()
    {
        while (true)
        {
            int length = ReadProtocolLength("fileTransfer.chunkLength");
            if (length == 0)
                return;
            _ = ReadMessagePayload(length);
        }
    }

    private async Task DrainFileTransferPayloadAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            int length = await ReadProtocolLengthAsync(
                "fileTransfer.chunkLength",
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (length == 0)
                return;
            _ = await ReadMessagePayloadAsync(length, cancellationToken).ConfigureAwait(false);
        }
    }

    private bool GetFileFromBE(string logDir, string filename, int logType)
    {
        bool status = true;

        // If no explicit -logDir mentioned (defaulted by backend to /tmp)
        string fullpath = Path.Combine(logDir, filename);

        FileStream? fh = null;
        if (logType == 1)
        {
            fullpath += ".nzlog";
            fh = new FileStream(fullpath, FileMode.Create, FileAccess.Write);
        }
        else if (logType == 2)
        {
            fullpath += ".nzbad";
            fh = new FileStream(fullpath, FileMode.Create, FileAccess.Write);
        }
        else if (logType == 3)
        {
            fullpath += ".nzstats";
            fh = new FileStream(fullpath, FileMode.Create, FileAccess.Write);
        }
        if (fh is null)
        {
            throw new NullReferenceException(nameof(fh));
        }

        using (StreamWriter writer = new StreamWriter(fh, Encoding.UTF8))
        {
            while (true)
            {
                int numBytes = ReadProtocolLength("fileTransfer.chunkLength");

                if (numBytes == 0)  // zeros means EOF, no more data
                {
                    break;
                }
                var data = ReadMessagePayload(numBytes);
                if (status)
                {
                    int maxCharCount = NzConnectionHelpers.ClientEncoding.GetMaxCharCount(numBytes);
                    char[] tmpChars = ArrayPool<char>.Shared.Rent(maxCharCount);
                    try
                    {
                        int charsWritten = NzConnectionHelpers.ClientEncoding.GetChars(data, 0, numBytes, tmpChars, 0);
                        writer.Write(tmpChars, 0, charsWritten);
                        writer.Flush();
                        _logger?.LogInformation("Successfully written data into file: {FullPath}", fullpath);
                    }
                    catch (IOException ex)
                    {
                        _logger?.LogWarning(ex, "Error in writing data to file");
                        status = false;
                    }
                    catch (ArgumentException ex)
                    {
                        _logger?.LogWarning(ex, "Error in writing data to file");
                        status = false;
                    }
                    finally
                    {
                        ArrayPool<char>.Shared.Return(tmpChars);
                    }
                }
            }
        }

        return status;
    }

    private async Task<bool> GetFileFromBEAsync(string logDir, string filename, int logType, CancellationToken cancellationToken = default)
    {
        bool status = true;
        string fullpath = Path.Combine(logDir, filename);

        FileStream? fh = null;
        if (logType == 1)
        {
            fullpath += ".nzlog";
            fh = new FileStream(fullpath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
        }
        else if (logType == 2)
        {
            fullpath += ".nzbad";
            fh = new FileStream(fullpath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
        }
        else if (logType == 3)
        {
            fullpath += ".nzstats";
            fh = new FileStream(fullpath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
        }
        if (fh is null)
        {
            throw new NullReferenceException(nameof(fh));
        }

        await using (fh.ConfigureAwait(false))
        {
            using StreamWriter writer = new StreamWriter(fh, Encoding.UTF8);
            while (true)
            {
                int numBytes = await ReadProtocolLengthAsync(
                    "fileTransfer.chunkLength",
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                if (numBytes == 0)
                {
                    break;
                }
                var data = await ReadMessagePayloadAsync(numBytes, cancellationToken).ConfigureAwait(false);
                if (status)
                {
                    try
                    {
                        int maxCharCount = NzConnectionHelpers.ClientEncoding.GetMaxCharCount(numBytes);
                        char[] tmpChars = ArrayPool<char>.Shared.Rent(maxCharCount);
                        try
                        {
                            int charsWritten = NzConnectionHelpers.ClientEncoding.GetChars(data, 0, numBytes, tmpChars, 0);
                            await writer.WriteAsync(tmpChars, 0, charsWritten).ConfigureAwait(false);
                            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                            _logger?.LogInformation("Successfully written data into file: {FullPath}", fullpath);
                        }
                        finally
                        {
                            ArrayPool<char>.Shared.Return(tmpChars);
                        }
                    }
                    catch (IOException ex)
                    {
                        _logger?.LogWarning(ex, "Error in writing data to file");
                        status = false;
                    }
                    catch (ArgumentException ex)
                    {
                        _logger?.LogWarning(ex, "Error in writing data to file");
                        status = false;
                    }
                }
            }
        }

        return status;
    }


    private void ReceiveAndWriteDataToExternal(FileStream fh)
    {
        if (fh is null)
        {
            throw new NetezzaException("Unload file stream is not initialized.");
        }

        ReadProtocolInt32("externalTable.receiveFrameHeaderValue");

        while (true)
        {
            // Get EXTAB_SOCK Status
            int status;
            try
            {
                status = _readBuffer!.ReadInt32BigEndian();
            }
            catch (IOException ex)
            {
                _logger?.LogWarning(ex, "Error while retrieving status, closing unload file");
                fh.Close();
                throw new NetezzaException("Error while retrieving unload status", ex);
            }

            if (status == Core.EXTAB_SOCK_DATA)
            {
                // get number of bytes in block
                int numBytes = ReadProtocolLength("externalTable.chunkLength");
                byte[] bytes = ArrayPool<byte>.Shared.Rent(numBytes);
                try
                {
                    bytes = Read(numBytes, bytes);
                    fh.Write(bytes, 0, numBytes);
                    fh.Flush();
                    _logger?.LogInformation("Successfully written data into file");
                }
                catch (IOException ex)
                {
                    _logger?.LogWarning(ex, "Error in writing data to file");
                    throw new NetezzaException("Error in writing data to unload file", ex);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(bytes);
                }
                continue;
            }

            if (status == Core.EXTAB_SOCK_DONE)
            {
                fh.Close();
                _logger?.LogInformation("unload - done receiving data");
                break;
            }

            if (status == Core.EXTAB_SOCK_ERROR)
            {
                //int len = HUnpack(_read(2));
                short len = ReadProtocolInt16Length("externalTable.errorMessageLength");

                string errorMsg = NzConnectionHelpers.ClientEncoding.GetString(Read(len));
                //len = HUnpack(_read(2));
                len = ReadProtocolInt16Length("externalTable.errorObjectLength");
                string errorObject = NzConnectionHelpers.ClientEncoding.GetString(Read(len));

                _logger?.LogWarning("unload - ErrorMsg: {ErrorMsg}", errorMsg);
                _logger?.LogWarning("unload - ErrorObj: {ErrorObject}", errorObject);

                fh.Close();
                _logger?.LogDebug("unload - done receiving data");
                throw new NetezzaException($"Unload error: {errorMsg}. Object: {errorObject}");
            }
            else
            {
                fh.Close();
                throw new NetezzaException($"Unknown unload status code: {status}");
            }
        }
    }

    private async Task ReceiveAndWriteDataToExternalAsync(FileStream fh, CancellationToken cancellationToken = default)
    {
        if (fh is null)
        {
            throw new NetezzaException("Unload file stream is not initialized.");
        }

        await ReadProtocolInt32Async(
            "externalTable.receiveFrameHeaderValue",
            cancellationToken).ConfigureAwait(false);

        while (true)
        {
            int status;
            try
            {
                status = await ReadInt32Async(cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                _logger?.LogWarning(ex, "Error while retrieving status, closing unload file");
                await fh.DisposeAsync().ConfigureAwait(false);
                throw new NetezzaException("Error while retrieving unload status", ex);
            }

            if (status == Core.EXTAB_SOCK_DATA)
            {
                int numBytes = await ReadProtocolLengthAsync(
                    "externalTable.chunkLength",
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                try
                {
                    byte[] bytes = ArrayPool<byte>.Shared.Rent(numBytes);
                    try
                    {
                        await _readBuffer!.ReadExactlyAsync(bytes.AsMemory(0, numBytes), cancellationToken).ConfigureAwait(false);
                        await fh.WriteAsync(bytes.AsMemory(0, numBytes), cancellationToken).ConfigureAwait(false);
                        await fh.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(bytes);
                    }
                    _logger?.LogInformation("Successfully written data into file");
                }
                catch (IOException ex)
                {
                    _logger?.LogWarning(ex, "Error in writing data to file");
                    throw new NetezzaException("Error in writing data to unload file", ex);
                }
                continue;
            }

            if (status == Core.EXTAB_SOCK_DONE)
            {
                await fh.DisposeAsync().ConfigureAwait(false);
                _logger?.LogInformation("unload - done receiving data");
                break;
            }

            if (status == Core.EXTAB_SOCK_ERROR)
            {
                short len = await ReadProtocolInt16LengthAsync(
                    "externalTable.errorMessageLength",
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                string errorMsg = NzConnectionHelpers.ClientEncoding.GetString(await ReadAsync(len, cancellationToken: cancellationToken).ConfigureAwait(false));
                len = await ReadProtocolInt16LengthAsync(
                    "externalTable.errorObjectLength",
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                string errorObject = NzConnectionHelpers.ClientEncoding.GetString(await ReadAsync(len, cancellationToken: cancellationToken).ConfigureAwait(false));

                _logger?.LogWarning("unload - ErrorMsg: {ErrorMsg}", errorMsg);
                _logger?.LogWarning("unload - ErrorObj: {ErrorObject}", errorObject);

                await fh.DisposeAsync().ConfigureAwait(false);
                _logger?.LogDebug("unload - done receiving data");
                throw new NetezzaException($"Unload error: {errorMsg}. Object: {errorObject}");
            }
            else
            {
                await fh.DisposeAsync().ConfigureAwait(false);
                throw new NetezzaException($"Unknown unload status code: {status}");
            }
        }
    }

}
