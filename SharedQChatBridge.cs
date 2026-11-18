using System;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Alife.Framework;

namespace Suran.DailyReport;

/// <summary>
/// 反射探测宿主里已加载的 Suran.QChat，共用它的连接发动作；没装/没启用就返回 null，调用方退回自己的独立连接。
/// 不声明对 Suran.QChat 的依赖：官方 QQ聊天 环境下不会把 fork 强行拉起来。
/// </summary>
static class SharedQChatBridge
{
    const string QChatTypeName = "Suran.QChat.QChatService";

    static Type? cachedQChatType;
    static PropertyInfo? primaryClientProp;
    static MethodInfo? callRawMethod;
    static bool probed;

    /// <summary>已探测到的共享客户端；null 表示没有可用的 Suran.QChat 连接。</summary>
    public static object? FindClient(ChatActivity activity)
    {
        if (activity == null)
            return null;

        try
        {
            if (!probed)
            {
                cachedQChatType = Type.GetType("Suran.QChat.QChatService, Suran.QChat", throwOnError: false);
                if (cachedQChatType == null)
                {
                    // 程序集还没加载时按已加载实例的名字找一次，拿到它的真实 Type
                    foreach (object instance in activity.Container.Instances)
                    {
                        Type t = instance.GetType();
                        if (t.FullName == QChatTypeName)
                        {
                            cachedQChatType = t;
                            break;
                        }
                    }
                }
                if (cachedQChatType != null)
                {
                    primaryClientProp = cachedQChatType.GetProperty("PrimaryClient",
                        BindingFlags.Public | BindingFlags.Instance);
                    callRawMethod = cachedQChatType.Assembly
                        .GetType("Suran.QChat.OneBotClient")?
                        .GetMethod("CallActionRawAsync", BindingFlags.Public | BindingFlags.Instance);
                }
                probed = true;
            }

            if (cachedQChatType == null || primaryClientProp == null || callRawMethod == null)
                return null;

            // 直接在当前角色的实例集合里找，拿到的就是本角色那一个 QChatService（多角色不会串号）
            foreach (object instance in activity.Container.Instances)
            {
                if (cachedQChatType.IsInstanceOfType(instance))
                    return primaryClientProp.GetValue(instance);
            }
        }
        catch (Exception)
        {
            // 反射失败一律当作没有共享连接，调用方照旧用独立连接
        }
        return null;
    }

    /// <summary>用共享连接发动作，返回原始响应 JSON；失败抛异常由调用方处理或退回独立连接。</summary>
    public static async Task<string> CallRawAsync(object client, string action, JsonObject parameters)
    {
        if (callRawMethod == null)
            throw new InvalidOperationException("Suran.QChat 的 CallActionRawAsync 不可用");
        object? task = callRawMethod.Invoke(client, new object?[] { action, parameters.DeepClone() });
        if (task is not Task<string> stringTask)
            throw new InvalidOperationException("Suran.QChat 的动作调用返回类型不匹配");
        return await stringTask;
    }
}
