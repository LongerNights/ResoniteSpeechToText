using System;
using System.IO;

namespace SpeechToText;

/// <summary>Locations of our private dependencies (Whisper.net, native runtimes, models, test audio).</summary>
internal static class ModPaths
{
    private static string? _root;

    /// <summary>"…\Resonite\rml_libs\SpeechToText". First existing candidate, else the first candidate.</summary>
    public static string Root => _root ??= Find();

    public static string Models => Path.Combine(Root, "models");
    public static string TestAudio => Path.Combine(Root, "spike", "hello.wav");

    /// <summary>A path inside <see cref="Root"/> that Whisper.net treats as "the app location": it looks for runtimes\win-x64 next to it.</summary>
    public static string NativeAnchor => Path.Combine(Root, "SpeechToText.native");

    private static string Find()
    {
        string asmDir = Path.GetDirectoryName(typeof(ModPaths).Assembly.Location) ?? AppContext.BaseDirectory;
        string[] candidates =
        {
            Path.Combine(AppContext.BaseDirectory, "rml_libs", "SpeechToText"),
            Path.GetFullPath(Path.Combine(asmDir, "..", "rml_libs", "SpeechToText")),
        };
        foreach (string c in candidates)
            if (Directory.Exists(c)) return c;
        return candidates[0];
    }
}
