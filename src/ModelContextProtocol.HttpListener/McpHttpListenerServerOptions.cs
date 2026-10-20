namespace ModelContextProtocol.Server;

/// <summary>Configures <see cref="McpHttpListenerServer"/>.</summary>
public sealed class McpHttpListenerServerOptions
{
    /// <summary>
    /// Gets the <see cref="System.Net.HttpListener"/> URI prefixes to listen on. Each must end with <c>/</c>.
    /// </summary>
    /// <remarks>
    /// Defaults to <c>http://localhost:3001/</c>. To accept connections from other devices (for example to
    /// reach an Android phone over Wi-Fi), use <c>http://*:3001/</c>; on Android the app also needs the
    /// <c>android.permission.INTERNET</c> permission. Binding non-localhost prefixes on Windows requires
    /// a URL ACL (<c>netsh http add urlacl</c>) or administrator rights.
    /// </remarks>
    public IList<string> Prefixes { get; } = new List<string> { "http://localhost:3001/" };

    /// <summary>Gets or sets the request path that serves MCP. Defaults to <c>/mcp</c>.</summary>
    public string Path { get; set; } = "/mcp";

    /// <summary>Gets or sets the largest accepted request body in bytes. Defaults to 4 MiB.</summary>
    public long MaxRequestBodyBytes { get; set; } = 4 * 1024 * 1024;

    /// <summary>
    /// Gets or sets the browser origins allowed to call the server. When <see langword="null"/> (the default),
    /// requests carrying an <c>Origin</c> header are accepted only from <c>localhost</c>, <c>127.0.0.1</c> and <c>[::1]</c>.
    /// Requests without an <c>Origin</c> header (non-browser clients) are always accepted.
    /// </summary>
    /// <remarks>
    /// The MCP specification asks servers to validate <c>Origin</c> to block DNS-rebinding attacks from web pages.
    /// Add <c>"*"</c> to allow every origin.
    /// </remarks>
    public ISet<string>? AllowedOrigins { get; set; }
}
