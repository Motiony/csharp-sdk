using System.Threading;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using UnityEngine;

/// <summary>
/// Runs an MCP server inside a Unity player while this component is enabled.
/// Add it to a GameObject in your first scene.
/// </summary>
public sealed class McpServerBehaviour : MonoBehaviour
{
    [Tooltip("Use http://*:3001/ to reach the server from other devices (e.g. an Android phone on Wi-Fi).")]
    [SerializeField] private string _prefix = "http://localhost:3001/";

    private McpHttpListenerServer _server;

    private void OnEnable()
    {
        // Tools run on thread-pool threads; capture Unity's main-thread context so tools can call Unity APIs.
        UnityMainThread.Context = SynchronizationContext.Current;

        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = Application.productName, Version = Application.version },
        };

        // Attribute-defined tools, prompts and resources from this assembly (Assembly-CSharp),
        // plus tools bound to this component instance.
        options
            .WithAllFromAssembly(typeof(McpServerBehaviour).Assembly)
            .WithTools(new SceneTools(this));

        var httpOptions = new McpHttpListenerServerOptions();
        httpOptions.Prefixes.Clear();
        httpOptions.Prefixes.Add(_prefix);

        _server = new McpHttpListenerServer(options, httpOptions);
        _server.Start();
        Debug.Log($"MCP server listening on {_prefix.TrimEnd('/')}{httpOptions.Path}");
    }

    private void OnDisable()
    {
        _server?.Dispose();
        _server = null;
    }
}
