using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WhisperNote.Services;

public sealed class RemoteExecutionServer : IDisposable
{
    const long MaxRequestBytes = 64 * 1024 * 1024;
    const int MaxHeaderBytes = 64 * 1024;
    readonly Func<byte[], int, CancellationToken, Task<string?>> _transcribe;
    readonly Func<bool> _isAvailable;
    readonly Func<bool> _allowRemoteSettings;
    readonly Func<RemoteExecutionSettings, CancellationToken, Task<RemoteExecutionSettings>>? _updateRemoteSettings;
    readonly SemaphoreSlim _requestLock = new(1, 1);
    TcpListener? _listener;
    CancellationTokenSource? _cts;
    Task? _acceptTask;
    string _basePath = "";
    string _listenEndpoint = "";

    public string Status { get; private set; } = "Server role off";
    public bool IsListening => _listener?.Server.IsBound == true;
    public event EventHandler? StatusChanged;

    public RemoteExecutionServer(
        Func<byte[], int, CancellationToken, Task<string?>> transcribe,
        Func<bool>? isAvailable = null,
        Func<bool>? allowRemoteSettings = null,
        Func<RemoteExecutionSettings, CancellationToken, Task<RemoteExecutionSettings>>? updateRemoteSettings = null)
    {
        _transcribe = transcribe;
        _isAvailable = isAvailable ?? (() => true);
        _allowRemoteSettings = allowRemoteSettings ?? (() => false);
        _updateRemoteSettings = updateRemoteSettings;
    }

    public async Task StartAsync(string listenEndpoint)
    {
        Stop();

        TcpListener? listener = null;
        try
        {
            var endpoint = new Uri(listenEndpoint.TrimEnd('/') + "/", UriKind.Absolute);
            if (endpoint.Scheme != Uri.UriSchemeHttp)
                throw new InvalidOperationException("Remote execution only supports HTTP listen endpoints.");

            var address = await ResolveListenAddressAsync(endpoint.Host);
            listener = new TcpListener(address, endpoint.Port);
            listener.Start();
        }
        catch
        {
            listener?.Stop();
            SetStatus("Server bind failed");
            throw;
        }

        var normalizedEndpoint = listenEndpoint.Trim().TrimEnd('/');
        var parsedEndpoint = new Uri(normalizedEndpoint + "/", UriKind.Absolute);
        _basePath = parsedEndpoint.AbsolutePath.TrimEnd('/');
        _listener = listener;
        _cts = new CancellationTokenSource();
        _listenEndpoint = normalizedEndpoint;
        SetStatus($"Serving {_listenEndpoint}");
        _acceptTask = AcceptLoopAsync(listener, _cts.Token);
    }

    static async Task<IPAddress> ResolveListenAddressAsync(string host)
    {
        if (host == "0.0.0.0" || host == "+" || host == "*")
            return IPAddress.Any;

        if (IPAddress.TryParse(host, out var address))
            return address;

        var addresses = await Dns.GetHostAddressesAsync(host);
        foreach (var candidate in addresses)
        {
            if (candidate.AddressFamily == AddressFamily.InterNetwork)
                return candidate;
        }

        if (addresses.Length > 0)
            return addresses[0];

        throw new InvalidOperationException($"Could not resolve listen host '{host}'.");
    }

    async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                _ = HandleAsync(client, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) when (ct.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Logger.Error($"Remote execution listener: {ex.Message}");
            SetStatus("Server listener failed");
        }
    }

    async Task HandleAsync(TcpClient client, CancellationToken serverCt)
    {
        using (client)
        using (var stream = client.GetStream())
        {
            try
            {
                var request = await ReadRequestAsync(stream, serverCt);
                if (request == null)
                    return;

                var path = GetPath(request.Target);
                var healthPath = EndpointPath("/health");
                var settingsPath = EndpointPath("/api/settings");
                var transcriptionPath = EndpointPath("/api/transcriptions");

                if (request.Method == "GET" && path == healthPath)
                {
                    var available = _isAvailable();
                    await WriteJsonAsync(
                        stream,
                        available ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable,
                        new { status = available ? "ready" : "local-mode-required" },
                        serverCt);
                    return;
                }

                if (request.Method == "POST" && path == settingsPath)
                {
                    await HandleRemoteSettingsAsync(stream, request.Body, serverCt);
                    return;
                }

                if (request.Method != "POST" || path != transcriptionPath)
                {
                    await WriteJsonAsync(stream, HttpStatusCode.NotFound, new { error = "Not found" }, serverCt);
                    return;
                }

                if (!_isAvailable())
                {
                    await WriteJsonAsync(
                        stream,
                        HttpStatusCode.ServiceUnavailable,
                        new { error = "Server is not in Local LLM mode" },
                        serverCt);
                    return;
                }

                if (!await _requestLock.WaitAsync(0, serverCt))
                {
                    await WriteJsonAsync(stream, HttpStatusCode.Conflict, new { error = "Server is busy" }, serverCt);
                    return;
                }

                try
                {
                    SetStatus("Client request active");
                    var remoteRequest = JsonSerializer.Deserialize<RemoteTranscriptionRequest>(
                        request.Body,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (remoteRequest?.Pcm == null || remoteRequest.Pcm.Length == 0 || remoteRequest.Channels < 1)
                    {
                        await WriteJsonAsync(
                            stream,
                            HttpStatusCode.BadRequest,
                            new { error = "PCM audio and a positive channel count are required" },
                            serverCt);
                        return;
                    }
                    if (remoteRequest.Pcm.LongLength > MaxRequestBytes)
                    {
                        await WriteJsonAsync(
                            stream,
                            HttpStatusCode.RequestEntityTooLarge,
                            new { error = "Audio request is too large" },
                            serverCt);
                        return;
                    }

                    var text = await _transcribe(remoteRequest.Pcm, remoteRequest.Channels, serverCt);
                    await WriteJsonAsync(stream, HttpStatusCode.OK, new RemoteTranscriptionResponse(text), serverCt);
                }
                finally
                {
                    _requestLock.Release();
                    if (IsListening)
                        SetStatus($"Serving {_listenEndpoint}");
                }
            }
            catch (RequestFormatException ex)
            {
                await TryWriteErrorAsync(stream, ex.Status, ex.Message, serverCt);
            }
            catch (JsonException)
            {
                await TryWriteErrorAsync(stream, HttpStatusCode.BadRequest, "Invalid request", serverCt);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Logger.Error($"Remote execution request: {ex.Message}");
                await TryWriteErrorAsync(stream, HttpStatusCode.ServiceUnavailable, "Local transcription unavailable", serverCt);
            }
        }
    }

    async Task HandleRemoteSettingsAsync(NetworkStream stream, byte[] body, CancellationToken serverCt)
    {
        if (!_isAvailable())
        {
            await WriteJsonAsync(
                stream,
                HttpStatusCode.ServiceUnavailable,
                new { error = "Server is not in Local LLM mode" },
                serverCt);
            return;
        }

        if (!_allowRemoteSettings())
        {
            await WriteJsonAsync(
                stream,
                HttpStatusCode.Forbidden,
                new { error = "Remote settings control is disabled on the server" },
                serverCt);
            return;
        }

        if (_updateRemoteSettings == null)
        {
            await WriteJsonAsync(
                stream,
                HttpStatusCode.NotImplemented,
                new { error = "Remote settings control is unavailable" },
                serverCt);
            return;
        }

        if (!await _requestLock.WaitAsync(0, serverCt))
        {
            await WriteJsonAsync(stream, HttpStatusCode.Conflict, new { error = "Server is busy" }, serverCt);
            return;
        }

        try
        {
            var settings = JsonSerializer.Deserialize<RemoteExecutionSettings>(
                body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (settings == null)
            {
                await WriteJsonAsync(stream, HttpStatusCode.BadRequest, new { error = "Settings are required" }, serverCt);
                return;
            }

            SetStatus("Applying remote settings");
            var applied = await _updateRemoteSettings(settings, serverCt);
            await WriteJsonAsync(
                stream,
                HttpStatusCode.OK,
                new RemoteSettingsResponse(true, applied.AutoOffloadVram, applied.ThinkingEnabled),
                serverCt);
        }
        finally
        {
            _requestLock.Release();
            if (IsListening)
                SetStatus($"Serving {_listenEndpoint}");
        }
    }

    async Task<HttpRequestData?> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var headerBytes = new MemoryStream();
        var oneByte = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(oneByte.AsMemory(), ct);
            if (read == 0)
                return null;

            headerBytes.WriteByte(oneByte[0]);
            if (headerBytes.Length > MaxHeaderBytes)
                throw new RequestFormatException(HttpStatusCode.RequestEntityTooLarge, "Request headers are too large");

            if (headerBytes.Length >= 4)
            {
                var buffer = headerBytes.ToArray();
                var length = (int)headerBytes.Length;
                if (buffer[length - 4] == '\r' && buffer[length - 3] == '\n' &&
                    buffer[length - 2] == '\r' && buffer[length - 1] == '\n')
                    break;
            }
        }

        var headerText = Encoding.ASCII.GetString(headerBytes.ToArray());
        var lines = headerText.Split("\r\n", StringSplitOptions.None);
        var requestLine = lines.Length > 0 ? lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>();
        if (requestLine.Length != 3)
            throw new RequestFormatException(HttpStatusCode.BadRequest, "Invalid HTTP request line");

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 1; index < lines.Length; index++)
        {
            var line = lines[index];
            if (line.Length == 0)
                break;
            var separator = line.IndexOf(':');
            if (separator <= 0)
                throw new RequestFormatException(HttpStatusCode.BadRequest, "Invalid HTTP header");
            headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }

        long contentLength = 0;
        var isChunked = headers.TryGetValue("Transfer-Encoding", out var transferEncoding) &&
            transferEncoding.Contains("chunked", StringComparison.OrdinalIgnoreCase);
        if (string.Equals(requestLine[0], "POST", StringComparison.OrdinalIgnoreCase))
        {
            if (!isChunked && (!headers.TryGetValue("Content-Length", out var contentLengthText) ||
                !long.TryParse(contentLengthText, out contentLength) || contentLength < 0))
                throw new RequestFormatException(HttpStatusCode.BadRequest, "Content-Length is required");
            if (!isChunked && contentLength > MaxRequestBytes)
                throw new RequestFormatException(HttpStatusCode.RequestEntityTooLarge, "Audio request is too large");
        }

        var body = isChunked
            ? await ReadChunkedBodyAsync(stream, ct)
            : contentLength == 0 ? Array.Empty<byte>() : await ReadBodyAsync(stream, contentLength, ct);
        return new HttpRequestData(requestLine[0], requestLine[1], body);
    }

    static async Task<byte[]> ReadChunkedBodyAsync(NetworkStream stream, CancellationToken ct)
    {
        using var body = new MemoryStream();
        while (true)
        {
            var sizeLine = await ReadLineAsync(stream, ct);
            var extensionIndex = sizeLine.IndexOf(';');
            var sizeText = extensionIndex >= 0 ? sizeLine[..extensionIndex] : sizeLine;
            if (!long.TryParse(sizeText.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var chunkSize) || chunkSize < 0)
                throw new RequestFormatException(HttpStatusCode.BadRequest, "Invalid chunked request");

            if (chunkSize == 0)
            {
                while (!string.IsNullOrEmpty(await ReadLineAsync(stream, ct))) { }
                return body.ToArray();
            }

            if (body.Length + chunkSize > MaxRequestBytes)
                throw new RequestFormatException(HttpStatusCode.RequestEntityTooLarge, "Audio request is too large");

            var chunk = await ReadBodyAsync(stream, chunkSize, ct);
            body.Write(chunk, 0, chunk.Length);
            var lineEnding = await ReadBodyAsync(stream, 2, ct);
            if (lineEnding[0] != '\r' || lineEnding[1] != '\n')
                throw new RequestFormatException(HttpStatusCode.BadRequest, "Invalid chunked request");
        }
    }

    static async Task<string> ReadLineAsync(NetworkStream stream, CancellationToken ct)
    {
        using var line = new MemoryStream();
        var oneByte = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(oneByte.AsMemory(), ct);
            if (read == 0)
                throw new RequestFormatException(HttpStatusCode.BadRequest, "Incomplete request");
            line.WriteByte(oneByte[0]);
            if (line.Length > MaxHeaderBytes)
                throw new RequestFormatException(HttpStatusCode.RequestEntityTooLarge, "Request line is too large");
            if (line.Length >= 2)
            {
                var bytes = line.ToArray();
                var length = bytes.Length;
                if (bytes[length - 2] == '\r' && bytes[length - 1] == '\n')
                    return Encoding.ASCII.GetString(bytes, 0, length - 2);
            }
        }
    }

    static async Task<byte[]> ReadBodyAsync(NetworkStream stream, long contentLength, CancellationToken ct)
    {
        if (contentLength > int.MaxValue)
            throw new RequestFormatException(HttpStatusCode.RequestEntityTooLarge, "Request is too large");

        var body = new byte[(int)contentLength];
        var offset = 0;
        while (offset < body.Length)
        {
            var read = await stream.ReadAsync(body.AsMemory(offset, body.Length - offset), ct);
            if (read == 0)
                throw new RequestFormatException(HttpStatusCode.BadRequest, "Incomplete request body");
            offset += read;
        }
        return body;
    }

    string EndpointPath(string suffix) => string.IsNullOrEmpty(_basePath) ? suffix : _basePath + suffix;

    static string GetPath(string target)
    {
        if (Uri.TryCreate(target, UriKind.Absolute, out var absolute))
            return absolute.AbsolutePath.TrimEnd('/');

        var queryIndex = target.IndexOf('?');
        var path = queryIndex >= 0 ? target[..queryIndex] : target;
        return path.TrimEnd('/');
    }

    static async Task TryWriteErrorAsync(NetworkStream stream, HttpStatusCode status, string message, CancellationToken ct)
    {
        try
        {
            await WriteJsonAsync(stream, status, new { error = message }, ct);
        }
        catch { }
    }

    static async Task WriteJsonAsync(NetworkStream stream, HttpStatusCode status, object value, CancellationToken ct)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(value, value.GetType());
        var header = $"HTTP/1.1 {(int)status} {ReasonPhrase(status)}\r\n" +
                     "Content-Type: application/json; charset=utf-8\r\n" +
                     $"Content-Length: {body.Length}\r\n" +
                     "Connection: close\r\n\r\n";
        var headerBytes = Encoding.ASCII.GetBytes(header);
        await stream.WriteAsync(headerBytes.AsMemory(), ct);
        await stream.WriteAsync(body.AsMemory(), ct);
    }

    static string ReasonPhrase(HttpStatusCode status) => status switch
    {
        HttpStatusCode.OK => "OK",
        HttpStatusCode.BadRequest => "Bad Request",
        HttpStatusCode.NotFound => "Not Found",
        HttpStatusCode.Forbidden => "Forbidden",
        HttpStatusCode.Conflict => "Conflict",
        HttpStatusCode.NotImplemented => "Not Implemented",
        HttpStatusCode.RequestEntityTooLarge => "Payload Too Large",
        HttpStatusCode.ServiceUnavailable => "Service Unavailable",
        _ => status.ToString()
    };

    void SetStatus(string status)
    {
        Status = status;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Stop()
    {
        _cts?.Cancel();
        if (_listener != null)
        {
            try { _listener.Stop(); } catch { }
        }
        _listener = null;
        _cts?.Dispose();
        _cts = null;
        _acceptTask = null;
        SetStatus("Server role off");
    }

    public void Dispose()
    {
        Stop();
    }

    sealed record HttpRequestData(string Method, string Target, byte[] Body);

    sealed class RequestFormatException : Exception
    {
        public HttpStatusCode Status { get; }

        public RequestFormatException(HttpStatusCode status, string message)
            : base(message)
        {
            Status = status;
        }
    }
}

public sealed record RemoteTranscriptionRequest(byte[] Pcm, int Channels);
public sealed record RemoteTranscriptionResponse(string? Text);
public sealed record RemoteExecutionSettings(bool AutoOffloadVram, bool ThinkingEnabled);
public sealed record RemoteSettingsResponse(bool Applied, bool AutoOffloadVram, bool ThinkingEnabled);
