using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace ModelContextProtocol.Server;

/// <summary>
/// Handles one Streamable HTTP request statelessly. Ported from the stateless POST path of
/// <c>ModelContextProtocol.AspNetCore.StreamableHttpHandler</c>, without session support.
/// </summary>
internal sealed class StatelessMcpRequestHandler(
    McpServerOptions serverOptions,
    McpHttpListenerServerOptions httpOptions,
    ILoggerFactory? loggerFactory,
    IServiceProvider? services)
{
    private const string ProtocolVersionHeader = McpHttpHeaders.ProtocolVersion;

    private static readonly JsonTypeInfo<JsonRpcMessage> s_messageTypeInfo = GetTypeInfo<JsonRpcMessage>();
    private static readonly JsonTypeInfo<JsonRpcError> s_errorTypeInfo = GetTypeInfo<JsonRpcError>();

    private readonly string _path = NormalizePath(httpOptions.Path);
    private readonly string[] _supportedProtocolVersions = GetConfiguredSupportedProtocolVersions(serverOptions.ProtocolVersion);

    public async Task HandleAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var request = context.Request;
        var response = context.Response;

        if (!string.Equals(NormalizePath(request.Url?.AbsolutePath), _path, StringComparison.OrdinalIgnoreCase))
        {
            response.StatusCode = (int)HttpStatusCode.NotFound;
            return;
        }

        if (!IsOriginAllowed(request.Headers["Origin"]))
        {
            await WriteJsonRpcErrorAsync(response, "Forbidden: Origin is not allowed.", (int)HttpStatusCode.Forbidden, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!string.Equals(request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
        {
            // Stateless servers have no standalone GET stream and no sessions to DELETE.
            response.AddHeader("Allow", "POST");
            await WriteJsonRpcErrorAsync(response, "Method Not Allowed: This stateless server only accepts POST.", (int)HttpStatusCode.MethodNotAllowed, cancellationToken).ConfigureAwait(false);
            return;
        }

        // The Streamable HTTP spec requires clients to accept both application/json and text/event-stream.
        var accept = request.Headers["Accept"];
        if (!AcceptsMediaType(accept, "application/json") || !AcceptsMediaType(accept, "text/event-stream"))
        {
            await WriteJsonRpcErrorAsync(response,
                "Not Acceptable: Client must accept both application/json and text/event-stream",
                (int)HttpStatusCode.NotAcceptable, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.ContentLength64 > httpOptions.MaxRequestBodyBytes)
        {
            await WriteJsonRpcErrorAsync(response, "Payload Too Large", 413, cancellationToken).ConfigureAwait(false);
            return;
        }

        var protocolVersion = request.Headers[ProtocolVersionHeader];

        JsonRpcMessage? message;
        try
        {
            message = await ReadJsonRpcMessageAsync(request, protocolVersion, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            await WriteJsonRpcErrorAsync(response,
                $"Bad Request: The POST body did not contain a valid JSON-RPC message: {ex.Message}",
                (int)HttpStatusCode.BadRequest, cancellationToken, (int)McpErrorCode.InvalidRequest).ConfigureAwait(false);
            return;
        }
        catch (InvalidDataException)
        {
            await WriteJsonRpcErrorAsync(response, "Payload Too Large", 413, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (message is null)
        {
            await WriteJsonRpcErrorAsync(response,
                "Bad Request: The POST body did not contain a valid JSON-RPC message.",
                (int)HttpStatusCode.BadRequest, cancellationToken, (int)McpErrorCode.InvalidRequest).ConfigureAwait(false);
            return;
        }

        // Every error for a parseable request must echo its id.
        var requestId = message is JsonRpcRequest jsonRpcRequest ? jsonRpcRequest.Id : default;

        if (!ValidateProtocolVersionHeader(protocolVersion, out var error) ||
            !ValidateProtocolVersionEnvelope(protocolVersion, message, out error) ||
            !ValidateRequiredPerRequestMeta(protocolVersion, message, out error))
        {
            await WriteJsonRpcErrorDetailAsync(response, error, (int)HttpStatusCode.BadRequest, cancellationToken, requestId).ConfigureAwait(false);
            return;
        }

        var requiresPerRequestMetadata = McpProtocolVersions.RequiresPerRequestMetadata(protocolVersion);

        // SEP-2575 removed these RPCs from the per-request-metadata HTTP surface.
        if (requiresPerRequestMetadata &&
            message is JsonRpcRequest
            {
                Method: RequestMethods.Initialize or RequestMethods.Ping or RequestMethods.LoggingSetLevel
                    or RequestMethods.ResourcesSubscribe or RequestMethods.ResourcesUnsubscribe,
            } removedMethodRequest)
        {
            await WriteJsonRpcErrorAsync(response,
                $"Method '{removedMethodRequest.Method}' is not available on protocol version '{protocolVersion}'.",
                (int)HttpStatusCode.NotFound, cancellationToken, (int)McpErrorCode.MethodNotFound, requestId).ConfigureAwait(false);
            return;
        }

        await ServeAsync(response, message, requiresPerRequestMetadata, cancellationToken).ConfigureAwait(false);
    }

    private async Task ServeAsync(HttpListenerResponse response, JsonRpcMessage message, bool requiresPerRequestMetadata, CancellationToken cancellationToken)
    {
        using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var transport = new StreamableHttpServerTransport(loggerFactory) { Stateless = true };
        var server = McpServer.Create(transport, serverOptions, loggerFactory, services);
        var runTask = server.RunAsync(requestCts.Token);

        try
        {
            Func<JsonRpcMessage?, ValueTask>? onResponseStarting = null;
            if (requiresPerRequestMetadata)
            {
                // SEP-2575 maps some JSON-RPC error codes to HTTP statuses. The status line can only be
                // chosen before the first response byte, so set it from the first message.
                onResponseStarting = firstMessage =>
                {
                    if (firstMessage is JsonRpcError { Error: { } detail })
                    {
                        response.StatusCode = (McpErrorCode)detail.Code switch
                        {
                            McpErrorCode.MethodNotFound => (int)HttpStatusCode.NotFound,
                            McpErrorCode.MissingRequiredClientCapability => (int)HttpStatusCode.BadRequest,
                            McpErrorCode.UnsupportedProtocolVersion => (int)HttpStatusCode.BadRequest,
                            McpErrorCode.HeaderMismatch => (int)HttpStatusCode.BadRequest,
                            _ => response.StatusCode,
                        };
                    }

                    return default;
                };
            }

            response.StatusCode = (int)HttpStatusCode.OK;
            response.ContentType = "text/event-stream";
            response.Headers["Cache-Control"] = "no-cache,no-store";
            response.Headers["X-Accel-Buffering"] = "no";
            response.SendChunked = true;

            var wroteResponse = await transport.HandlePostRequestAsync(message, response.OutputStream, onResponseStarting, requestCts.Token).ConfigureAwait(false);
            if (!wroteResponse)
            {
                // Nothing to answer (a notification or response): empty 202 Accepted.
                response.ContentType = null;
                response.SendChunked = false;
                response.StatusCode = (int)HttpStatusCode.Accepted;
                response.ContentLength64 = 0;
            }
        }
        finally
        {
            // Dispose the transport first so the server's read loop completes without an exception.
            await transport.DisposeAsync().ConfigureAwait(false);
            requestCts.Cancel();
            try
            {
                await runTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                await server.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<JsonRpcMessage?> ReadJsonRpcMessageAsync(HttpListenerRequest request, string? protocolVersion, CancellationToken cancellationToken)
    {
        using var body = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await request.InputStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (body.Length + read > httpOptions.MaxRequestBodyBytes)
            {
                throw new InvalidDataException("Request body too large.");
            }

            body.Write(buffer, 0, read);
        }

        body.Position = 0;
        var message = await JsonSerializer.DeserializeAsync(body, s_messageTypeInfo, cancellationToken).ConfigureAwait(false);

        if (message is not null && !string.IsNullOrEmpty(protocolVersion))
        {
            message.Context ??= new();
            message.Context.ProtocolVersion = protocolVersion;
        }

        return message;
    }

    private bool IsOriginAllowed(string? origin)
    {
        if (string.IsNullOrEmpty(origin))
        {
            return true;
        }

        if (httpOptions.AllowedOrigins is { } allowed)
        {
            return allowed.Contains("*") || allowed.Contains(origin!);
        }

        return Uri.TryCreate(origin, UriKind.Absolute, out var uri) &&
            (uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase));
    }

    private bool ValidateProtocolVersionHeader(string? protocolVersion, [NotNullWhen(false)] out JsonRpcErrorDetail? errorDetail)
    {
        if (!string.IsNullOrEmpty(protocolVersion) && !_supportedProtocolVersions.Contains(protocolVersion))
        {
            // Advertise the per-request-metadata revisions: that is what server/discover advertises on a stateless server.
            var advertised = _supportedProtocolVersions.Where(McpProtocolVersions.RequiresPerRequestMetadata).ToArray();
            if (advertised.Length == 0)
            {
                advertised = _supportedProtocolVersions;
            }

            errorDetail = new JsonRpcErrorDetail
            {
                Code = (int)McpErrorCode.UnsupportedProtocolVersion,
                Message = $"Bad Request: The MCP-Protocol-Version header value '{protocolVersion}' is not supported.",
                Data = JsonSerializer.SerializeToNode(
                    new UnsupportedProtocolVersionErrorData { Supported = [.. advertised], Requested = protocolVersion! },
                    GetTypeInfo<UnsupportedProtocolVersionErrorData>()),
            };
            return false;
        }

        errorDetail = null;
        return true;
    }

    private static bool ValidateProtocolVersionEnvelope(string? protocolVersion, JsonRpcMessage message, [NotNullWhen(false)] out JsonRpcErrorDetail? errorDetail)
    {
        errorDetail = null;
        if (message is not (JsonRpcRequest or JsonRpcNotification))
        {
            return true;
        }

        if (message is JsonRpcRequest { Method: RequestMethods.Initialize, Params: JsonObject initializeParams } &&
            initializeParams["protocolVersion"] is JsonValue initializeVersionValue &&
            initializeVersionValue.TryGetValue(out string? initializeVersion) &&
            !string.IsNullOrEmpty(protocolVersion) &&
            !string.Equals(protocolVersion, initializeVersion, StringComparison.Ordinal))
        {
            errorDetail = HeaderMismatch($"Bad Request: The {ProtocolVersionHeader} header value '{protocolVersion}' does not match body params.protocolVersion value '{initializeVersion}'.");
            return false;
        }

        if (string.IsNullOrEmpty(protocolVersion) || McpProtocolVersions.SupportsInitializeHandshake(protocolVersion))
        {
            return true;
        }

        if (!TryGetProtocolVersionMeta(message, out var metaVersion))
        {
            // Notifications are exempt: an error response would have no recipient.
            if (message is not JsonRpcRequest)
            {
                return true;
            }

            errorDetail = new JsonRpcErrorDetail
            {
                Code = (int)McpErrorCode.InvalidParams,
                Message = $"Requests using protocol version '{protocolVersion}' must include '_meta/{MetaKeys.ProtocolVersion}'.",
            };
            return false;
        }

        if (!string.Equals(protocolVersion, metaVersion, StringComparison.Ordinal))
        {
            errorDetail = HeaderMismatch($"Bad Request: The {ProtocolVersionHeader} header value '{protocolVersion}' does not match body _meta/{MetaKeys.ProtocolVersion} value '{metaVersion}'.");
            return false;
        }

        return true;
    }

    private static bool ValidateRequiredPerRequestMeta(string? protocolVersion, JsonRpcMessage message, [NotNullWhen(false)] out JsonRpcErrorDetail? errorDetail)
    {
        if (message is JsonRpcRequest { Params: var requestParams } &&
            McpProtocolVersions.RequiresPerRequestMetadata(protocolVersion) &&
            (requestParams is not JsonObject paramsObj ||
             paramsObj["_meta"] is not JsonObject metaObj ||
             metaObj[MetaKeys.ClientCapabilities] is not JsonObject))
        {
            errorDetail = new JsonRpcErrorDetail
            {
                Code = (int)McpErrorCode.InvalidParams,
                Message = $"Requests using protocol version '{protocolVersion}' must include '_meta/{MetaKeys.ClientCapabilities}' as a JSON object.",
            };
            return false;
        }

        errorDetail = null;
        return true;
    }

    private static bool TryGetProtocolVersionMeta(JsonRpcMessage message, [NotNullWhen(true)] out string? protocolVersion)
    {
        var parameters = message switch
        {
            JsonRpcRequest r => r.Params,
            JsonRpcNotification n => n.Params,
            _ => null,
        };

        if (parameters is JsonObject paramsObj &&
            paramsObj["_meta"] is JsonObject metaObj &&
            metaObj[MetaKeys.ProtocolVersion] is JsonValue value &&
            value.TryGetValue(out string? text) &&
            !string.IsNullOrEmpty(text))
        {
            protocolVersion = text!;
            return true;
        }

        protocolVersion = null;
        return false;
    }

    private static JsonRpcErrorDetail HeaderMismatch(string message) => new()
    {
        Code = (int)McpErrorCode.HeaderMismatch,
        Message = message,
    };

    private static string[] GetConfiguredSupportedProtocolVersions(string? protocolVersion)
    {
        if (protocolVersion is null)
        {
            return McpProtocolVersions.SupportedProtocolVersions;
        }

        if (!McpProtocolVersions.IsSupportedProtocolVersion(protocolVersion))
        {
            throw new McpException(
                $"Unsupported server protocol version '{protocolVersion}'. Supported protocol versions: " +
                string.Join(", ", McpProtocolVersions.SupportedProtocolVersions) + ".");
        }

        return [protocolVersion];
    }

    /// <summary>
    /// Returns whether an Accept header value admits <paramref name="mediaType"/>, honoring <c>*/*</c> and <c>type/*</c>.
    /// </summary>
    internal static bool AcceptsMediaType(string? acceptHeader, string mediaType)
    {
        if (string.IsNullOrEmpty(acceptHeader))
        {
            return false;
        }

        var slash = mediaType.IndexOf('/');
        var type = mediaType.Substring(0, slash);

        foreach (var part in acceptHeader!.Split(','))
        {
            var semicolon = part.IndexOf(';');
            var value = (semicolon >= 0 ? part.Substring(0, semicolon) : part).Trim();

            if (value == "*/*" ||
                string.Equals(value, mediaType, StringComparison.OrdinalIgnoreCase) ||
                (value.EndsWith("/*", StringComparison.Ordinal) && string.Equals(value.Substring(0, value.Length - 2), type, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "/";
        }

        path = path!.TrimEnd('/');
        return path.Length == 0 ? "/" : path[0] == '/' ? path : "/" + path;
    }

    private static Task WriteJsonRpcErrorAsync(HttpListenerResponse response, string message, int statusCode, CancellationToken cancellationToken, int errorCode = -32000, RequestId requestId = default) =>
        WriteJsonRpcErrorDetailAsync(response, new JsonRpcErrorDetail { Code = errorCode, Message = message }, statusCode, cancellationToken, requestId);

    private static async Task WriteJsonRpcErrorDetailAsync(HttpListenerResponse response, JsonRpcErrorDetail detail, int statusCode, CancellationToken cancellationToken, RequestId requestId = default)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new JsonRpcError { Id = requestId, Error = detail }, s_errorTypeInfo);
        response.StatusCode = statusCode;
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
    }

    private static JsonTypeInfo<T> GetTypeInfo<T>() => (JsonTypeInfo<T>)McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(T));
}
