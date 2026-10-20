using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Xunit;

namespace ModelContextProtocol.HttpListener.Tests;

[McpServerToolType]
public sealed class TestTools
{
    [McpServerTool, Description("Echoes the message back.")]
    public static string Echo([Description("Text to echo.")] string message) => "echo: " + message;

    [McpServerTool]
    public int Add(int a, int b) => a + b;
}

[McpServerPromptType]
public static class TestPrompts
{
    [McpServerPrompt, Description("Greets someone.")]
    public static string Greet(string name) => $"Say hello to {name}.";
}

[McpServerResourceType]
public static class TestResources
{
    [McpServerResource(UriTemplate = "test://status", Name = "status", MimeType = "text/plain")]
    public static string Status() => "ok";
}

public sealed class InstanceTools(string prefix)
{
    [McpServerTool]
    public string Tag(string value) => prefix + value;
}

public sealed class McpHttpListenerServerTests : IDisposable
{
    private readonly int _port = GetFreePort();
    private readonly McpHttpListenerServer _server;
    private readonly HttpClient _http = new();

    public McpHttpListenerServerTests()
    {
        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "test-server", Version = "1.0.0" },
        };
        options
            .WithAllFromAssembly(typeof(TestTools).Assembly)
            .WithTools(new InstanceTools("#"));

        var httpOptions = new McpHttpListenerServerOptions();
        httpOptions.Prefixes.Clear();
        httpOptions.Prefixes.Add($"http://localhost:{_port}/");

        _server = new McpHttpListenerServer(options, httpOptions);
        _server.Start();
    }

    private Uri Endpoint => new($"http://localhost:{_port}/mcp");

    public void Dispose()
    {
        _http.Dispose();
        _server.Dispose();
    }

    private Task<McpClient> ConnectAsync() =>
        McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions { Endpoint = Endpoint, TransportMode = HttpTransportMode.StreamableHttp }),
            cancellationToken: TestContext.Current.CancellationToken);

    [Fact]
    public async Task Client_ListsAndCallsAttributeTools()
    {
        await using var client = await ConnectAsync();
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal("test-server", client.ServerInfo?.Name);

        var tools = await client.ListToolsAsync(cancellationToken: ct);
        Assert.Equal(["add", "echo", "tag"], tools.Select(t => t.Name).OrderBy(n => n));

        var echo = await client.CallToolAsync("echo", new Dictionary<string, object?> { ["message"] = "hi" }, cancellationToken: ct);
        Assert.Equal("echo: hi", Assert.IsType<TextContentBlock>(Assert.Single(echo.Content)).Text);

        var add = await client.CallToolAsync("add", new Dictionary<string, object?> { ["a"] = 2, ["b"] = 3 }, cancellationToken: ct);
        Assert.Equal("5", Assert.IsType<TextContentBlock>(Assert.Single(add.Content)).Text);

        var tag = await client.CallToolAsync("tag", new Dictionary<string, object?> { ["value"] = "x" }, cancellationToken: ct);
        Assert.Equal("#x", Assert.IsType<TextContentBlock>(Assert.Single(tag.Content)).Text);
    }

    [Fact]
    public async Task Client_GetsPromptAndReadsResource()
    {
        await using var client = await ConnectAsync();
        var ct = TestContext.Current.CancellationToken;

        var prompts = await client.ListPromptsAsync(cancellationToken: ct);
        Assert.Equal("greet", Assert.Single(prompts).Name);

        var prompt = await client.GetPromptAsync("greet", new Dictionary<string, object?> { ["name"] = "Ada" }, cancellationToken: ct);
        var message = Assert.Single(prompt.Messages);
        Assert.Equal(Role.User, message.Role);
        Assert.Equal("Say hello to Ada.", Assert.IsType<TextContentBlock>(message.Content).Text);

        var resource = await client.ReadResourceAsync("test://status", cancellationToken: ct);
        Assert.Equal("ok", Assert.IsType<TextResourceContents>(Assert.Single(resource.Contents)).Text);
    }

    [Fact]
    public void Runtime_LoadsSystemTextJson8()
    {
        // Guards the fork's purpose: if a dependency drags in STJ 9/10, this host would load it instead.
        Assert.Equal(8, typeof(JsonSerializer).Assembly.GetName().Version!.Major);
    }

    [Fact]
    public void EnumMemberNames_UseLowercaseWireValues()
    {
        // System.Text.Json 8 has no built-in [JsonStringEnumMemberName]; the restored converter must honor it.
        Assert.Equal("\"user\"", JsonSerializer.Serialize(Role.User, McpJsonUtilities.DefaultOptions));
        Assert.Equal("\"warning\"", JsonSerializer.Serialize(LoggingLevel.Warning, McpJsonUtilities.DefaultOptions));
        Assert.Equal(Role.Assistant, JsonSerializer.Deserialize<Role>("\"assistant\"", McpJsonUtilities.DefaultOptions));
    }

    [Fact]
    public async Task Post_WithoutEventStreamAccept_Returns406()
    {
        using var response = await PostAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", accept: "application/json");
        Assert.Equal(HttpStatusCode.NotAcceptable, response.StatusCode);
    }

    [Fact]
    public async Task Post_MalformedJson_Returns400()
    {
        using var response = await PostAsync("{not json");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("\"code\":-32600", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Post_Notification_Returns202()
    {
        using var response = await PostAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task Get_Returns405()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
        request.Headers.Add("Accept", "text/event-stream");
        using var response = await _http.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task Post_FromForeignOrigin_Returns403()
    {
        using var response = await PostAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", origin: "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Post_WrongPath_Returns404()
    {
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(new Uri($"http://localhost:{_port}/other"), content, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private async Task<HttpResponseMessage> PostAsync(string body, string accept = "application/json, text/event-stream", string? origin = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Accept", accept);
        if (origin is not null)
        {
            request.Headers.TryAddWithoutValidation("Origin", origin);
        }

        return await _http.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
