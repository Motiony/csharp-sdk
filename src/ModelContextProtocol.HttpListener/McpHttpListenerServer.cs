using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text.Json;

namespace ModelContextProtocol.Server;

/// <summary>
/// Serves MCP over stateless Streamable HTTP using <see cref="HttpListener"/>, with no ASP.NET Core or Kestrel.
/// </summary>
/// <remarks>
/// <para>
/// Each POST is handled by a fresh, short-lived <see cref="McpServer"/>, so the server keeps no sessions.
/// Requests from the server to the client (sampling, roots, elicitation) and unsolicited notifications are not available.
/// </para>
/// <para>
/// Tool, prompt and resource handlers run on thread-pool threads. In Unity, marshal calls into Unity APIs onto the
/// main thread (for example through a <see cref="SynchronizationContext"/> captured on the main thread).
/// </para>
/// </remarks>
public sealed class McpHttpListenerServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly StatelessMcpRequestHandler _handler;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, Func<object?>> _routes = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _stopping = new();
    private Task? _acceptLoop;
    private int _state; // 0 = created, 1 = started, 2 = stopped
    private readonly JsonSerializerOptions _jsonSerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Creates a server. Call <see cref="Start"/> to begin listening.</summary>
    /// <param name="serverOptions">MCP options: server info, tools, prompts, resources and handlers.</param>
    /// <param name="httpOptions">HTTP options. Defaults to <c>http://localhost:3001/mcp</c>.</param>
    /// <param name="loggerFactory">Optional logger factory.</param>
    /// <param name="services">Optional services passed to tools, prompts and resources.</param>
    public McpHttpListenerServer(
        McpServerOptions serverOptions,
        McpHttpListenerServerOptions? httpOptions = null,
        ILoggerFactory? loggerFactory = null,
        IServiceProvider? services = null)
    {
        Throw.IfNull(serverOptions);

        httpOptions ??= new();
        if (httpOptions.Prefixes.Count == 0)
        {
            throw new ArgumentException("At least one prefix is required.", nameof(httpOptions));
        }

        // Each request gets its own McpServer, which must not create DI scopes.
        serverOptions.ScopeRequests = false;

        foreach (var prefix in httpOptions.Prefixes)
        {
            _listener.Prefixes.Add(prefix);
        }

        _logger = loggerFactory?.CreateLogger<McpHttpListenerServer>() ?? NullLogger<McpHttpListenerServer>.Instance;
        _handler = new StatelessMcpRequestHandler(serverOptions, httpOptions, loggerFactory, services);
    }

    /// <summary>
    /// </summary>
    /// <param name="path">The request path, for example <c>/id</c>.</param>
    /// <param name="handler">Produces the response body</param>
    /// <returns>This server, for chaining.</returns>
    public McpHttpListenerServer MapGet<T>(string path, Func<T> handler)
    {
        Throw.IfNull(path);
        Throw.IfNull(handler);
        _routes[path] = () => handler();
        return this;
    }

    /// <summary>Gets a value indicating whether the server is listening.</summary>
    public bool IsListening => _listener.IsListening;

    /// <summary>Starts listening and handling requests in the background.</summary>
    public void Start()
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
        {
            throw new InvalidOperationException("The server can only be started once.");
        }

        _listener.Start();
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    /// <summary>Stops listening, cancels in-flight requests and waits for the accept loop to finish.</summary>
    public void Stop()
    {
        if (Interlocked.Exchange(ref _state, 2) != 1)
        {
            return;
        }

        _stopping.Cancel();
        try
        {
            _listener.Stop();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            _acceptLoop?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Stop();
        _listener.Close();
        _stopping.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        var token = _stopping.Token;
        while (!token.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (token.IsCancellationRequested || !_listener.IsListening)
            {
                break;
            }
            catch (HttpListenerException ex)
            {
                _logger.LogWarning(ex, "HttpListener failed to accept a request.");
                continue;
            }

            if (context.Request.HttpMethod == "GET" && _routes.ContainsKey(context.Request.Url?.AbsolutePath ?? ""))
            {
                _ = Task.Run(() => HandleAsync(context, HandleRoute, token), CancellationToken.None);
            }
            else
            {
                _ = Task.Run(() => HandleAsync(context, _handler.HandleAsync, token), CancellationToken.None);
            }
        }
    }

    private async Task HandleAsync(HttpListenerContext context,
        Func<HttpListenerContext, CancellationToken, Task> handler,
        CancellationToken token)
    {
        try
        {
            await handler(context, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (token.IsCancellationRequested ||
                                   ex is HttpListenerException or ObjectDisposedException or IOException)
        {
            // Client went away or the server is stopping.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error while processing an MCP request.");
            try
            {
                context.Response.StatusCode = 500;
            }
            catch
            {
            }
        }
        finally
        {
            try
            {
                context.Response.Close();
            }
            catch
            {
            }
        }
    }

    private async Task HandleRoute(HttpListenerContext context, CancellationToken token)
    {
        if (_routes.TryGetValue(context.Request.Url?.AbsolutePath ?? "", out var func) && func != null)
        {
            await WriteResult(func(), context, token).ConfigureAwait(false);
        }
    }

    private async Task WriteResult<T>(T response, HttpListenerContext context, CancellationToken cancellationToken)
    {
        using var resp = context.Response;
        resp.ContentType = "application/json";
        var buffer = JsonSerializer.SerializeToUtf8Bytes(response, _jsonSerializerOptions);
        resp.ContentLength64 = buffer.Length;
        await resp.OutputStream.WriteAsync(buffer, cancellationToken);
    }
}