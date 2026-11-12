using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace SpeechToText;

/// <summary>
/// Resolves managed dependencies (Whisper.net etc.) from rml_libs\SpeechToText so we don't litter the shared rml_libs folder.
/// Must be installed before any type that references those assemblies is JIT-compiled.
/// </summary>
internal static class Resolver
{
    private static bool _installed;

    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        var alc = AssemblyLoadContext.GetLoadContext(typeof(Resolver).Assembly) ?? AssemblyLoadContext.Default;
        alc.Resolving += OnResolving;
        if (alc != AssemblyLoadContext.Default) AssemblyLoadContext.Default.Resolving += OnResolving;
    }

    private static Assembly? OnResolving(AssemblyLoadContext ctx, AssemblyName name)
    {
        if (name.Name == null) return null;
        string path = Path.Combine(ModPaths.Root, name.Name + ".dll");
        return File.Exists(path) ? ctx.LoadFromAssemblyPath(path) : null;
    }
}
