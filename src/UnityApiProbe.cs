using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
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

    /// <summary>
    /// Logs the methods a type actually exposes at runtime, so a negative probe
    /// can be told apart from a type that failed to resolve at all. The total
    /// count proves the class was found; the filtered list shows what survived
    /// stripping. Diagnostic only — call it once, behind a one-shot guard.
    /// </summary>
    internal static void DescribeType(
        string assemblyName,
        string namespaceName,
        string typeName,
        string[] nameFilters)
    {
        try
        {
            var declaringClass = IL2CPP.GetIl2CppClass(
                assemblyName,
                namespaceName,
                typeName);
            if (declaringClass == IntPtr.Zero)
            {
                Plugin.Logger.LogWarning(
                    $"[PROBE] TYPE_UNRESOLVED {namespaceName}.{typeName} in " +
                    $"{assemblyName}; every method probe against it will report " +
                    "stripped whether or not it is.");
                return;
            }

            var iterator = IntPtr.Zero;
            var total = 0;
            var matched = new List<string>();
            while (true)
            {
                var method = IL2CPP.il2cpp_class_get_methods(
                    declaringClass,
                    ref iterator);
                if (method == IntPtr.Zero)
                    break;

                total++;
                var namePointer = IL2CPP.il2cpp_method_get_name(method);
                if (namePointer == IntPtr.Zero)
                    continue;
                var name = Marshal.PtrToStringAnsi(namePointer);
                if (string.IsNullOrEmpty(name) || !MatchesAny(name, nameFilters))
                    continue;
                var parameters = IL2CPP.il2cpp_method_get_param_count(method);
                matched.Add(name + "/" + parameters.ToString());
            }

            Plugin.Logger.LogInfo(
                $"[PROBE] TYPE {namespaceName}.{typeName} methods={total}, " +
                $"matching=[{string.Join(", ", matched)}]");
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning(
                $"[PROBE] DESCRIBE_FAILED {namespaceName}.{typeName}: " +
                exception.Message);
        }
    }

    private static bool MatchesAny(string name, string[] filters)
    {
        if (filters == null || filters.Length == 0)
            return true;
        for (var index = 0; index < filters.Length; index++)
        {
            if (name.IndexOf(filters[index], StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }

        return false;
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
