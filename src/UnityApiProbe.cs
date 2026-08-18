using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;

namespace Ramblers;

/// <summary>
/// Asks the IL2CPP runtime whether a Unity method actually exists in this build.
///
/// Big Walk ships a managed-stripped IL2CPP build, but BepInEx generates its
/// interop assemblies from the full Unity API surface. A method the game itself
/// never calls therefore still compiles here and is simply absent at runtime,
/// where Il2CppInterop resolves a null method pointer and the failure path
/// corrupts memory: the process dies on an access violation before any catch
/// block runs. Compiling proves nothing, and neither does a try/catch.
///
/// Any capability that depends on a Unity API the game may not use itself must
/// probe for it and disable itself with a reportable error instead.
/// </summary>
internal static class UnityApiProbe
{
    internal const string CoreModule = "UnityEngine.CoreModule.dll";

    private static readonly Dictionary<string, bool> Results =
        new Dictionary<string, bool>(StringComparer.Ordinal);

    /// <summary>
    /// Whether the named method exists in this build. Results are cached: the
    /// answer cannot change while the process lives. Main thread only.
    /// </summary>
    internal static bool IsMethodPresent(
        string assemblyName,
        string namespaceName,
        string typeName,
        string methodName,
        int argumentCount)
    {
        var key = namespaceName + "." + typeName + "." + methodName +
                  "/" + argumentCount.ToString();
        bool present;
        if (Results.TryGetValue(key, out present))
            return present;

        present = Resolve(
            assemblyName,
            namespaceName,
            typeName,
            methodName,
            argumentCount);
        Results[key] = present;
        Plugin.Logger.LogInfo(
            $"[PROBE] {(present ? "PRESENT" : "STRIPPED")} api={key}, " +
            $"assembly={assemblyName}.");
        return present;
    }

    private static bool Resolve(
        string assemblyName,
        string namespaceName,
        string typeName,
        string methodName,
        int argumentCount)
    {
        try
        {
            var declaringClass = IL2CPP.GetIl2CppClass(
                assemblyName,
                namespaceName,
                typeName);
            if (declaringClass == IntPtr.Zero)
                return false;

            var method = IL2CPP.il2cpp_class_get_method_from_name(
                declaringClass,
                methodName,
                argumentCount);
            return method != IntPtr.Zero;
        }
        catch (Exception exception)
        {
            // A missing type raises rather than returning null on some
            // Il2CppInterop versions. Either way the API is unusable.
            Plugin.Logger.LogWarning(
                $"[PROBE] LOOKUP_FAILED api={namespaceName}.{typeName}." +
                $"{methodName}: {exception.Message}");
            return false;
        }
    }
}
