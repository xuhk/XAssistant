using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace XAssistant.Services;

/// <summary>
/// 本机私有模块的挂载点。
/// 私有代码放在被 .gitignore 的 Local/ 目录里（通常是指向仓库外目录的联结），
/// 只要实现本接口就会被自动发现并调用；开源版没有 Local/ 时集合为空，整个机制完全无感。
/// </summary>
public interface ILocalModule
{
    /// <summary>容器构建前调用：注册本模块需要的服务。</summary>
    void Register(IServiceCollection services);

    /// <summary>启动完成后调用：绑定事件、往托盘插菜单项等。</summary>
    void Attach(IServiceProvider provider);
}

/// <summary>私有模块的发现与执行。找不到任何实现时静默跳过。</summary>
public static class LocalModules
{
    private static List<ILocalModule>? _instances;

    /// <summary>已发现的私有模块数量。0 表示这是开源版。</summary>
    public static int Count => Instances.Count;

    private static List<ILocalModule> Instances => _instances ??= Discover().ToList();

    private static IEnumerable<ILocalModule> Discover()
    {
        Type[] types;
        try
        {
            types = Assembly.GetExecutingAssembly().GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // 个别类型加载失败不应拖垮整个应用
            types = ex.Types.Where(t => t is not null).Cast<Type>().ToArray();
        }

        foreach (
            var type in types.Where(t =>
                t is { IsClass: true, IsAbstract: false }
                && typeof(ILocalModule).IsAssignableFrom(t)
            )
        )
        {
            // 实例化失败的模块跳过，不拖垮启动
            if (TryCreate(type) is { } instance)
                yield return instance;
        }
    }

    private static ILocalModule? TryCreate(Type type)
    {
        try
        {
            return Activator.CreateInstance(type, nonPublic: true) as ILocalModule;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LocalModules] {type.Name} 实例化失败：{ex}");
            return null;
        }
    }

    /// <summary>逐个注册。此时 Serilog 还没配置，失败只能写调试输出。</summary>
    public static void RegisterAll(IServiceCollection services)
    {
        foreach (var module in Instances)
        {
            try
            {
                module.Register(services);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[LocalModules] {module.GetType().Name}.Register 失败：{ex}"
                );
            }
        }
    }

    /// <summary>逐个挂载。单个模块失败只记警告，不拦启动。</summary>
    public static void AttachAll(IServiceProvider provider)
    {
        if (Instances.Count == 0)
            return;

        var logger = provider.GetService<ILoggerFactory>()?.CreateLogger(typeof(LocalModules));
        foreach (var module in Instances)
        {
            try
            {
                module.Attach(provider);
                logger?.LogInformation("已加载本机私有模块 {Module}", module.GetType().Name);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(
                    ex,
                    "本机私有模块 {Module} 挂载失败，已跳过",
                    module.GetType().Name
                );
            }
        }
    }
}
