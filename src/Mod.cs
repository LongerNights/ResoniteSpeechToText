using System;
using System.Threading.Tasks;
using Elements.Core;
using ResoniteModLoader;

namespace SpeechToText;

public class SpeechToTextMod : ResoniteMod
{
    public override string Name => "SpeechToText";
    public override string Author => "Winter";
    public override string Version => "1.0.0";
    public override string Link => "";

    private static ModConfiguration? config;

    private static ModConfigurationKey<dummy> Header(string name, string title, string color = "#4fd9bd") =>
        new(name, $"<align=center><color={color}><b>[ {title} ]</b></color></align>");
    private static ModConfigurationKey<dummy> Note(string name, string text) =>
        new(name, $"<size=70%><color=#86888b>{text}</color></size>");

    // ---- General ---------------------------------------------------------------------------------------------------
    [AutoRegisterConfigKey] private static readonly ModConfigurationKey<dummy> hdrGeneral = Header("_general", "Speech to text");
    [AutoRegisterConfigKey] private static readonly ModConfigurationKey<dummy> noteGeneral = Note("_generalNote",
        "Text is published as User/STT.* variables that everyone in the session can read. Off until you choose a mode.");
    [AutoRegisterConfigKey] internal static readonly ModConfigurationKey<SttMode> modeKey = new("Mode", "Mode", () => SttMode.Off);
    [AutoRegisterConfigKey] internal static readonly ModConfigurationKey<bool> publishAllKey = new("PublishToAllWorlds", "Publish in every world (not just the focused one)", () => false);
    [AutoRegisterConfigKey] internal static readonly ModConfigurationKey<bool> partialsKey = new("PublishPartials", "Publish partial text while speaking", () => true);
    [AutoRegisterConfigKey] internal static readonly ModConfigurationKey<float> partialRateKey = new("PartialRateHz", "Partial updates per second (1-10)", () => 4f, false, v => v >= 1f && v <= 10f);
    [AutoRegisterConfigKey] internal static readonly ModConfigurationKey<int> maxTextKey = new("MaxTextLength", "Max characters per result (32-4096)", () => 512, false, v => v >= 32 && v <= 4096);
    [AutoRegisterConfigKey] internal static readonly ModConfigurationKey<bool> profanityKey = new("ProfanityFilter", "Mask profanity", () => false);

    // ---- Triggered mode --------------------------------------------------------------------------------------------
    [AutoRegisterConfigKey] private static readonly ModConfigurationKey<dummy> hdrTrigger = Header("_trigger", "Triggered mode");
    [AutoRegisterConfigKey] private static readonly ModConfigurationKey<dummy> noteTrigger = Note("_triggerNote",
        "In Triggered mode the mod listens only while the boolean variable User/STT.Trigger is true. Flip it with any gesture, button or logic you build in Resonite.");
    [AutoRegisterConfigKey] internal static readonly ModConfigurationKey<TriggerSource> triggerFromKey = new("TriggerFrom", "Who may flip User/STT.Trigger (Only me ignores and reverts other users)", () => TriggerSource.OnlyMe);
    [AutoRegisterConfigKey] internal static readonly ModConfigurationKey<int> triggerAutoOffKey = new("TriggerAutoOffSec", "Switch the trigger off after N seconds (0 = never)", () => 0, false, v => v >= 0);

    // ---- Recognition -----------------------------------------------------------------------------------------------
    [AutoRegisterConfigKey] private static readonly ModConfigurationKey<dummy> hdrRecog = Header("_recog", "Recognition");
    [AutoRegisterConfigKey] internal static readonly ModConfigurationKey<SttModel> modelKey = new("Model", "Model (downloaded on first use)", () => SttModel.BaseEn);
    [AutoRegisterConfigKey] internal static readonly ModConfigurationKey<string> languageKey = new("Language", "Language code, or auto (multilingual models)", () => "auto");
    [AutoRegisterConfigKey] internal static readonly ModConfigurationKey<bool> translateKey = new("TranslateToEnglish", "Translate to English", () => false);
    [AutoRegisterConfigKey] internal static readonly ModConfigurationKey<SttBackend> backendKey = new("Backend", "Backend (restart needed)", () => SttBackend.Cpu);
    [AutoRegisterConfigKey] internal static readonly ModConfigurationKey<int> threadsKey = new("Threads", "CPU threads (1-16)", () => 4, false, v => v >= 1 && v <= 16);
    [AutoRegisterConfigKey] internal static readonly ModConfigurationKey<VadKind> vadKey = new("Vad", "Voice activity detector", () => VadKind.Silero);
    [AutoRegisterConfigKey] internal static readonly ModConfigurationKey<float> vadSensKey = new("VadSensitivity", "Energy detector threshold in dB (3-30)", () => 12f, false, v => v >= 3f && v <= 30f);
    [AutoRegisterConfigKey] internal static readonly ModConfigurationKey<int> silenceKey = new("SilenceMs", "Silence that ends an utterance, ms (200-3000)", () => 700, false, v => v >= 200 && v <= 3000);
    [AutoRegisterConfigKey] internal static readonly ModConfigurationKey<int> maxSegKey = new("MaxSegmentSec", "Longest utterance in seconds (5-30)", () => 15, false, v => v >= 5 && v <= 30);

    // ---- Diagnostics -----------------------------------------------------------------------------------------------
    [AutoRegisterConfigKey] private static readonly ModConfigurationKey<dummy> hdrDiag = Header("_diag", "Diagnostics");
    [AutoRegisterConfigKey] internal static readonly ModConfigurationKey<bool> debugKey = new("DebugLogging", "Log per-utterance timings", () => false);
    // Hidden (internalAccessOnly): remembers that the one-time privacy notice has been shown.
    [AutoRegisterConfigKey] internal static readonly ModConfigurationKey<bool> privacyShownKey = new("PrivacyNoticeShown", "Privacy notice shown", () => false, true);
    [AutoRegisterConfigKey] internal static readonly ModConfigurationKey<bool> dummyKey = new("PublishDummyText", "Publish fake test utterances (needs a mode other than Off)", () => false);

    public override void OnEngineInit()
    {
        // Must happen before anything referencing Whisper.net is JIT-compiled.
        Resolver.Install();
        config = GetConfiguration();
        modeKey.OnChanged += _ => { Publisher.Poke(); SttController.Poke(); };
        Publisher.Start();
        DummyFeed.Start();
        SttController.Start();
    }

    internal static void Set<T>(ModConfigurationKey<T> key, T value) => config?.Set(key, value);

    internal static T Get<T>(ModConfigurationKey<T> key) => config != null ? config.GetValue(key)! : default!;

    /// <summary>Test hook for McpLink (call_method): set the mode and dummy-feed toggle without the settings UI.</summary>
    internal static void DebugSet(string mode, bool dummy)
    {
        config?.Set(modeKey, Enum.Parse<SttMode>(mode));
        config?.Set(dummyKey, dummy);
    }

    /// <summary>Test hook for McpLink: also switch per-utterance / per-5-seconds debug logging.</summary>
    internal static void DebugSet(string mode, bool dummy, bool debugLogging)
    {
        DebugSet(mode, dummy);
        config?.Set(debugKey, debugLogging);
    }

    /// <summary>Test hook for McpLink: mute or unmute the local microphone exactly as the in-game mute button does.</summary>
    internal static void DebugMute(bool muted)
    {
        var audio = FrooxEngine.Engine.Current?.AudioSystem;
        if (audio != null) audio.IsMuted = muted;
    }

    /// <summary>Test hook for McpLink: set any setting by its key name (e.g. "Threads", "5"; "Model", "SmallEn"). Returns the stored value.</summary>
    internal static string DebugConfig(string name, string value)
    {
        if (config == null) return "no configuration";
        foreach (var key in config.ConfigurationItemDefinitions)
        {
            if (key.Name != name) continue;
            Type t = key.ValueType();
            object converted = t.IsEnum ? Enum.Parse(t, value)
                : t == typeof(string) ? value
                : Convert.ChangeType(value, t, System.Globalization.CultureInfo.InvariantCulture);
            config.Set(key, converted);
            return $"{name} = {converted}";
        }
        return "unknown setting " + name;
    }

    /// <summary>Test hook for McpLink: simulate continuous talking (see <see cref="SttController.DebugStress"/>).</summary>
    internal static string DebugStress(int seconds, int everyMs) => SttController.DebugStress(seconds, everyMs);
}
