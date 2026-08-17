using System;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WhisperNote.Services;

public sealed class RemoteExecutionServer : IDisposable
{
    const long MaxRequestBytes = 64 * 1024 * 1024;
    readonly Func<byte[], int, CancellationToken, Task<string?>> _transcribe;
    readonly Func<bool> _isAvailable;
    readonly SemaphoreSlim _requestLock = new(1, 1);
    HttpListener? _listener;
    CancellationTokenSource? _cts;
    Task? _acceptTask;
    string _basePath = "";
    string _listenEndpoint = "";

    public string Status { get; private set; } = "Server role off";
    public bool IsListening => _listener?.IsListening == true;
    public event EventHandler? StatusChanged;

    public RemoteExecutionServer(
        Func<byte[], int, CancellationToken, Task<string?>> transcribe,
        Func<bool>? isAvailable = null)
    {
        _transcribe = transcribe;
        _isAvailable = isAvailable ?? (() => true);
    }

    public Task StartAsync(string listenEndpoint)
    {
        Stop();
        var prefix = listenEndpoint.TrimEnd('/') + "/";
        _basePath = new Uri(prefix, UriKind.Absolute).AbsolutePath.TrimEnd('/');
        var listener = new HttpListener();
        listener.Prefixes.Add(CreateHttpListenerPrefix(prefix));
        try
        {
            listener.Start();
        }
        catch
        {
            listener.Close();
            SetStatus("Server bind failed");
            throw;
        }

        _listener = listener;
        _cts = new CancellationTokenSource();
        _listenEndpoint = listenEndpoint;
        SetStatus($"Serving {listenEndpoint}");
        _acceptTask = AcceptLoopAsync(listener, _cts.Token);
        return Task.CompletedTask;
    }

    static string CreateHttpListenerPrefix(string prefix)
    {
        if (!Uri.TryCreate(prefix, UriKind.Absolute, out var uri) || uri.Host != "0.0.0.0")
            return prefix;

        // HttpListener uses '+' for an all-interface binding; 0.0.0.0 is the
        // user-facing endpoint format but is rejected as a listener prefix.
        return new UriBuilder(uri) { Host = "+" }.Uri.AbsoluteUri;
    }

    async Task AcceptLoopAsync(HttpListener listener, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var context = await listener.GetContextAsync().WaitAsync(ct);
                _ = HandleAsync(context, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (HttpListenerException) when (ct.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Logger.Error($"Remote execution listener: {ex.Message}");
            SetStatus("Server listener failed");
        }
    }

    async Task HandleAsync(HttpListenerContext context, CancellationToken serverCt)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath.TrimEnd('/');
            if (context.Request.HttpMethod == "GET" && path == _basePath + "/health")
            {
                var available = _isAvailable();
                await WriteJsonAsync(
                    context.Response,
                    available ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable,
                    new { status = available ? "ready" : "local-mode-required" },
                    serverCt);
                return;
            }

            if (context.Request.HttpMethod != "POST" || path != _basePath + "/api/transcriptions")
            {
                await WriteJsonAsync(context.Response, HttpStatusCode.NotFound, new { error = "Not found" }, serverCt);
                return;
            }

            if (!_isAvailable())
            {
                await WriteJsonAsync(context.Response, HttpStatusCode.ServiceUnavailable, new { error = "Server is not in Local LLM mode" }, serverCt);
                return;
            }

            if (context.Request.ContentLength64 > MaxRequestBytes)
            {
                await WriteJsonAsync(context.Response, HttpStatusCode.RequestEntityTooLarge, new { error = "Audio request is too large" }, serverCt);
                return;
            }

            if (!await _requestLock.WaitAsync(0, serverCt))
            {
                await WriteJsonAsync(context.Response, HttpStatusCode.Conflict, new { error = "Server is busy" }, serverCt);
                return;
            }

            try
            {
                SetStatus("Client request active");
                var request = await JsonSerializer.DeserializeAsync<RemoteTranscriptionRequest>(
                    context.Request.InputStream,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                    cancellationToken: serverCt);
                if (request?.Pcm == null || request.Pcm.Length == 0 || request.Channels < 1)
                {
                    await WriteJsonAsync(context.Response, HttpStatusCode.BadRequest, new { error = "PCM audio and a positive channel count are required" }, serverCt);
                    return;
                }
                if (request.Pcm.LongLength > MaxRequestBytes)
                {
                    await WriteJsonAsync(context.Response, HttpStatusCode.RequestEntityTooLarge, new { error = "Audio request is too large" }, serverCt);
                    return;
                }

                var text = await _transcribe(request.Pcm, request.Channels, serverCt);
                await WriteJsonAsync(context.Response, HttpStatusCode.OK, new RemoteTranscriptionResponse(text), serverCt);
            }
            finally
            {
                _requestLock.Release();
                if (IsListening)
                    SetStatus($"Serving {_listenEndpoint}");
            }
        }
        catch (JsonException)
        {
            await TryWriteErrorAsync(context.Response, HttpStatusCode.BadRequest, "Invalid request", serverCt);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Logger.Error($"Remote execution request: {ex.Message}");
            await TryWriteErrorAsync(context.Response, HttpStatusCode.ServiceUnavailable, "Local transcription unavailable", serverCt);
        }
        finally
        {
            try { context.Response.Close(); } catch { }
        }
    }

    static async Task TryWriteErrorAsync(HttpListenerResponse response, HttpStatusCode status, string message, CancellationToken ct)
    {
        try
        {
            if (response.OutputStream.CanWrite)
                await WriteJsonAsync(response, status, new { error = message }, ct);
        }
        catch { }
    }

    static async Task WriteJsonAsync(HttpListenerResponse response, HttpStatusCode status, object value, CancellationToken ct)
    {
        response.StatusCode = (int)status;
        response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(response.OutputStream, value, value.GetType(), cancellationToken: ct);
    }

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
            _listener.Close();
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
}

public sealed record RemoteTranscriptionRequest(byte[] Pcm, int Channels);
public sealed record RemoteTranscriptionResponse(string? Text);
