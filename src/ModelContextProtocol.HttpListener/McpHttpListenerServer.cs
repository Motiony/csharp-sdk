using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;

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
    private readonly CancellationTokenSource _stopping = new();
    private Task? _acceptLoop;
    private int _state; // 0 = created, 1 = started, 2 = stopped

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

            _ = Task.Run(() => HandleAsync(context, token));
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken token)
    {
        try
        {
            await _handler.HandleAsync(context, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (token.IsCancellationRequested || ex is HttpListenerException or ObjectDisposedException or IOException)
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
}
