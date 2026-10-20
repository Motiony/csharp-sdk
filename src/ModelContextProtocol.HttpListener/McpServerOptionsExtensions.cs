using System.Reflection;
using System.Text.Json;

namespace ModelContextProtocol.Server;

/// <summary>
/// Registers attribute-defined tools, prompts and resources on <see cref="McpServerOptions"/> without
/// dependency injection or Microsoft.Extensions.Hosting.
/// </summary>
/// <remarks>
/// These mirror <c>WithToolsFromAssembly</c>, <c>WithPromptsFromAssembly</c> and <c>WithResourcesFromAssembly</c>
/// from the <c>ModelContextProtocol</c> package, but write straight into
/// <see cref="McpServerOptions.ToolCollection"/>, <see cref="McpServerOptions.PromptCollection"/> and
/// <see cref="McpServerOptions.ResourceCollection"/>. They avoid Microsoft.Extensions.DependencyInjection,
/// which can rely on Reflection.Emit that IL2CPP does not support.
/// For instance methods, a new instance of the declaring type is created for each call, using
/// the <c>services</c> argument when it can supply the type and its public parameterless constructor otherwise.
/// Use the <c>instance</c> overloads to bind methods to an object you already own (for example a MonoBehaviour).
/// </remarks>
public static class McpServerOptionsExtensions
{
    private const BindingFlags MethodFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

    /// <summary>Adds tools from every type in <paramref name="assembly"/> marked with <see cref="McpServerToolTypeAttribute"/>.</summary>
    public static McpServerOptions WithToolsFromAssembly(this McpServerOptions options, Assembly assembly, IServiceProvider? services = null, JsonSerializerOptions? serializerOptions = null)
    {
        Throw.IfNull(assembly);
        return options.WithTools(TypesWith<McpServerToolTypeAttribute>(assembly), services, serializerOptions);
    }

    /// <summary>Adds tools for methods marked with <see cref="McpServerToolAttribute"/> on <typeparamref name="T"/>.</summary>
    public static McpServerOptions WithTools<T>(this McpServerOptions options, IServiceProvider? services = null, JsonSerializerOptions? serializerOptions = null)
        => options.WithTools([typeof(T)], services, serializerOptions);

    /// <summary>Adds tools for methods marked with <see cref="McpServerToolAttribute"/> on each of <paramref name="toolTypes"/>.</summary>
    public static McpServerOptions WithTools(this McpServerOptions options, IEnumerable<Type> toolTypes, IServiceProvider? services = null, JsonSerializerOptions? serializerOptions = null)
    {
        Throw.IfNull(options);
        Throw.IfNull(toolTypes);

        var collection = options.ToolCollection ??= new();
        foreach (var type in toolTypes)
        {
            foreach (var method in MethodsWith<McpServerToolAttribute>(type))
            {
                var createOptions = new McpServerToolCreateOptions { Services = services, SerializerOptions = serializerOptions };
                collection.Add(method.IsStatic
                    ? McpServerTool.Create(method, target: null, createOptions)
                    : McpServerTool.Create(method, _ => CreateTarget(services, type), createOptions));
            }
        }

        return options;
    }

    /// <summary>Adds tools for methods marked with <see cref="McpServerToolAttribute"/> on <paramref name="instance"/>, bound to that instance.</summary>
    public static McpServerOptions WithTools(this McpServerOptions options, object instance, IServiceProvider? services = null, JsonSerializerOptions? serializerOptions = null)
    {
        Throw.IfNull(options);
        Throw.IfNull(instance);

        var collection = options.ToolCollection ??= new();
        foreach (var method in MethodsWith<McpServerToolAttribute>(instance.GetType()))
        {
            collection.Add(McpServerTool.Create(method, method.IsStatic ? null : instance,
                new McpServerToolCreateOptions { Services = services, SerializerOptions = serializerOptions }));
        }

        return options;
    }

    /// <summary>Adds prompts from every type in <paramref name="assembly"/> marked with <see cref="McpServerPromptTypeAttribute"/>.</summary>
    public static McpServerOptions WithPromptsFromAssembly(this McpServerOptions options, Assembly assembly, IServiceProvider? services = null, JsonSerializerOptions? serializerOptions = null)
    {
        Throw.IfNull(assembly);
        return options.WithPrompts(TypesWith<McpServerPromptTypeAttribute>(assembly), services, serializerOptions);
    }

    /// <summary>Adds prompts for methods marked with <see cref="McpServerPromptAttribute"/> on each of <paramref name="promptTypes"/>.</summary>
    public static McpServerOptions WithPrompts(this McpServerOptions options, IEnumerable<Type> promptTypes, IServiceProvider? services = null, JsonSerializerOptions? serializerOptions = null)
    {
        Throw.IfNull(options);
        Throw.IfNull(promptTypes);

        var collection = options.PromptCollection ??= new();
        foreach (var type in promptTypes)
        {
            foreach (var method in MethodsWith<McpServerPromptAttribute>(type))
            {
                var createOptions = new McpServerPromptCreateOptions { Services = services, SerializerOptions = serializerOptions };
                collection.Add(method.IsStatic
                    ? McpServerPrompt.Create(method, target: null, createOptions)
                    : McpServerPrompt.Create(method, _ => CreateTarget(services, type), createOptions));
            }
        }

        return options;
    }

    /// <summary>Adds prompts for methods marked with <see cref="McpServerPromptAttribute"/> on <paramref name="instance"/>, bound to that instance.</summary>
    public static McpServerOptions WithPrompts(this McpServerOptions options, object instance, IServiceProvider? services = null, JsonSerializerOptions? serializerOptions = null)
    {
        Throw.IfNull(options);
        Throw.IfNull(instance);

        var collection = options.PromptCollection ??= new();
        foreach (var method in MethodsWith<McpServerPromptAttribute>(instance.GetType()))
        {
            collection.Add(McpServerPrompt.Create(method, method.IsStatic ? null : instance,
                new McpServerPromptCreateOptions { Services = services, SerializerOptions = serializerOptions }));
        }

        return options;
    }

    /// <summary>Adds resources from every type in <paramref name="assembly"/> marked with <see cref="McpServerResourceTypeAttribute"/>.</summary>
    public static McpServerOptions WithResourcesFromAssembly(this McpServerOptions options, Assembly assembly, IServiceProvider? services = null, JsonSerializerOptions? serializerOptions = null)
    {
        Throw.IfNull(assembly);
        return options.WithResources(TypesWith<McpServerResourceTypeAttribute>(assembly), services, serializerOptions);
    }

    /// <summary>Adds resources for methods marked with <see cref="McpServerResourceAttribute"/> on each of <paramref name="resourceTypes"/>.</summary>
    public static McpServerOptions WithResources(this McpServerOptions options, IEnumerable<Type> resourceTypes, IServiceProvider? services = null, JsonSerializerOptions? serializerOptions = null)
    {
        Throw.IfNull(options);
        Throw.IfNull(resourceTypes);

        var collection = options.ResourceCollection ??= new();
        foreach (var type in resourceTypes)
        {
            foreach (var method in MethodsWith<McpServerResourceAttribute>(type))
            {
                var createOptions = new McpServerResourceCreateOptions { Services = services, SerializerOptions = serializerOptions };
                collection.Add(method.IsStatic
                    ? McpServerResource.Create(method, target: null, createOptions)
                    : McpServerResource.Create(method, _ => CreateTarget(services, type), createOptions));
            }
        }

        return options;
    }

    /// <summary>Adds resources for methods marked with <see cref="McpServerResourceAttribute"/> on <paramref name="instance"/>, bound to that instance.</summary>
    public static McpServerOptions WithResources(this McpServerOptions options, object instance, IServiceProvider? services = null, JsonSerializerOptions? serializerOptions = null)
    {
        Throw.IfNull(options);
        Throw.IfNull(instance);

        var collection = options.ResourceCollection ??= new();
        foreach (var method in MethodsWith<McpServerResourceAttribute>(instance.GetType()))
        {
            collection.Add(McpServerResource.Create(method, method.IsStatic ? null : instance,
                new McpServerResourceCreateOptions { Services = services, SerializerOptions = serializerOptions }));
        }

        return options;
    }

    /// <summary>Adds every tool, prompt and resource type found in <paramref name="assembly"/>.</summary>
    public static McpServerOptions WithAllFromAssembly(this McpServerOptions options, Assembly assembly, IServiceProvider? services = null, JsonSerializerOptions? serializerOptions = null)
        => options
            .WithToolsFromAssembly(assembly, services, serializerOptions)
            .WithPromptsFromAssembly(assembly, services, serializerOptions)
            .WithResourcesFromAssembly(assembly, services, serializerOptions);

    private static IEnumerable<Type> TypesWith<TAttribute>(Assembly assembly) where TAttribute : Attribute =>
        assembly.GetTypes().Where(t => t.GetCustomAttribute<TAttribute>() is not null);

    private static IEnumerable<MethodInfo> MethodsWith<TAttribute>(Type type) where TAttribute : Attribute =>
        type.GetMethods(MethodFlags).Where(m => m.GetCustomAttribute<TAttribute>() is not null);

    private static object CreateTarget(IServiceProvider? services, Type type) =>
        services?.GetService(type) ?? Activator.CreateInstance(type)!;
}
