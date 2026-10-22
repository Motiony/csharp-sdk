# MCP server in Unity (Editor, desktop players, Android)

This fork runs the MCP C# SDK inside Unity without ASP.NET Core or Kestrel:

- `ModelContextProtocol.Core` targets **netstandard2.0 only** and is pinned to **System.Text.Json 8.0.6**
  (the newest 8.x package; there is no 8.0.18 on nuget.org) and **Microsoft.Extensions.AI.Abstractions 9.10.1**
  (10.x requires System.Text.Json 10).
- `ModelContextProtocol.HttpListener` serves MCP over stateless Streamable HTTP with `System.Net.HttpListener`.
- Tools, prompts and resources use the normal attributes (`[McpServerToolType]`, `[McpServerTool]`, …).
  Register them with `McpServerOptions` extension methods; no DI or hosting is needed.

## 1. Build the DLLs

```sh
dotnet publish samples/UnityMcpServer/UnityPlugins -c Release -o <UnityProject>/Assets/Plugins/Mcp
```

This copies everything Unity needs (`ModelContextProtocol.*`, `System.Text.Json` 8.0.6,
`Microsoft.Extensions.AI.Abstractions`, logging abstractions and their dependencies) plus `link.xml`.
If your project already contains one of these DLLs (for example `System.Text.Json` from another package),
keep only one copy.

## 2. Unity settings

- **Api Compatibility Level**: .NET Standard 2.1 (Player Settings → Other Settings).
- **IL2CPP stripping**: `link.xml` (copied with the DLLs) preserves the reflection-bound types. It also preserves
  `Assembly-CSharp`; add an entry for any other assembly (asmdef) that contains your tool classes.
- **Android**: Player Settings → Internet Access = **Require**. To reach the phone from your PC, bind
  `http://*:3001/` and connect to `http://<phone-ip>:3001/mcp` on the same network.

## 3. Add the scripts

Copy `Assets/Mcp/*.cs` into your project and add `McpServerBehaviour` to a GameObject.

```csharp
var options = new McpServerOptions
{
    ServerInfo = new Implementation { Name = "my-game", Version = "1.0" },
};
options
    .WithAllFromAssembly(typeof(McpServerBehaviour).Assembly) // static or instance attribute types
    .WithTools(new SceneTools(this));                          // tools bound to an existing object

var server = new McpHttpListenerServer(options);              // http://localhost:3001/mcp
server.Start();
// ...
server.Dispose();
```

MCP handlers run on thread-pool threads. Use `UnityMainThread.RunAsync` (in `SampleTools.cs`) for anything that
touches Unity APIs.

## 4. Connect

```sh
npx @modelcontextprotocol/inspector
```

Choose transport **Streamable HTTP** and URL `http://localhost:3001/mcp`.

## Limits

- Stateless only: each POST gets a fresh server, so there are no sessions, no GET stream, and the server cannot
  send requests to the client (sampling, roots, elicitation) or unsolicited notifications.
  Progress and log notifications that belong to a running request still work.
- Browser clients: requests with an `Origin` header are accepted only from localhost unless you set
  `McpHttpListenerServerOptions.AllowedOrigins`.
- No TLS or authentication. Keep the server on trusted networks, or add a check in front of it.
