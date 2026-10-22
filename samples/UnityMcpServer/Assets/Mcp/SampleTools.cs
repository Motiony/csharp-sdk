using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using UnityEngine;

/// <summary>Runs work on Unity's main thread from MCP handlers (which run on thread-pool threads).</summary>
public static class UnityMainThread
{
    public static SynchronizationContext Context { get; set; }

    public static Task<T> RunAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Context.Post(_ =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        }, null);
        return tcs.Task;
    }
}

/// <summary>Static tools discovered by <c>WithAllFromAssembly</c>.</summary>
[McpServerToolType]
public static class EchoTools
{
    [McpServerTool, Description("Echoes the message back.")]
    public static string Echo([Description("Text to echo.")] string message) => "echo: " + message;
}

/// <summary>Tools bound to a live component instance with <c>WithTools(instance)</c>.</summary>
public sealed class SceneTools
{
    private readonly MonoBehaviour _owner;

    public SceneTools(MonoBehaviour owner) => _owner = owner;

    [McpServerTool, Description("Lists the root GameObjects of the active scene.")]
    public Task<string[]> ListRootObjects() =>
        UnityMainThread.RunAsync(() =>
        {
            var roots = _owner.gameObject.scene.GetRootGameObjects();
            return Array.ConvertAll(roots, go => go.name);
        });

    [McpServerTool, Description("Moves a GameObject to a world position.")]
    public Task<string> MoveObject(string name, float x, float y, float z) =>
        UnityMainThread.RunAsync(() =>
        {
            var go = GameObject.Find(name);
            if (go == null)
            {
                return $"No GameObject named '{name}'.";
            }

            go.transform.position = new Vector3(x, y, z);
            return $"Moved '{name}' to ({x}, {y}, {z}).";
        });
}

[McpServerPromptType]
public static class SamplePrompts
{
    [McpServerPrompt, Description("Asks the model to describe the scene.")]
    public static string DescribeScene() => "Call list_root_objects and describe the scene.";
}

[McpServerResourceType]
public static class SampleResources
{
    [McpServerResource(UriTemplate = "unity://app/info", Name = "app_info", MimeType = "text/plain")]
    public static Task<string> AppInfo() =>
        UnityMainThread.RunAsync(() => $"{Application.productName} {Application.version} on {Application.platform}");
}
