# SpeechToText

*Development guide for Claude Code sessions (and a quick orientation for humans). Keep it short and current.*

A Resonite mod (ResoniteModLoader) that transcribes the **local user's microphone** with Whisper, on their own PC, and exposes
the result in-world as **dynamic variables on the user root slot**. Two modes: **Continuous** (always listening, voice-activity
segmented) and **Triggered** (listens only while the boolean variable `User/STT.Trigger` is true). **By design the mod has no
gesture, button or key detection:** users build whatever flips the trigger inside Resonite.

- User docs: [README.md](README.md) (how it works, dependencies) and [docs/USAGE.md](docs/USAGE.md) (variables, recipes, settings).
- Design and history, with every measured result and verification: [SPEC.md](SPEC.md). Keep it current when behaviour changes.
- Machine-specific notes (install path, in-game test procedures) live in `CLAUDE.local.md`, which is git-ignored.

## Layout

- `core/` (assembly `SpeechToText.Core`): the engine-free speech pipeline, no Resonite dependency, fully unit-tested.
  `RingBuffer`, `Resampler`, `Vad`/`SileroVad`, `Segmenter`, `AudioPipeline`, `Transcriber`, `WhisperEngine`, `ModelManager`,
  `TextFilter`, `Trigger` (policy and auto-off), `Enums`.
- `src/` (the mod, net10.0): `Mod.cs` (config keys, test hooks), `Controller.cs` (`SttController`: starts, stops and rebuilds the
  pipeline from the settings), `AudioTap.cs` (microphone hook and the mute/privacy `Suppression` rules), `Publisher.cs` +
  `Snapshot.cs` (the `User/STT.*` variables, trigger handling), `Notify.cs` (dash toasts), `ModPaths.cs`, `Resolver.cs`.
- `tests/Core.Tests/` (xUnit, about 160 tests) and `tests/fixtures/` (speech made with the Windows TTS voice, regenerate with
  `tools/make-fixtures.ps1`).
- `recipes/` the ProtoFlux recipe package. `tools/` build helpers. `ref/`, `models/`, `dist/` are git-ignored.

## Build and test

- Prerequisites: .NET 10 SDK. Building the **mod** also needs a Resonite install (it references the game's DLLs, which are not in
  this repo). Point at it with `-p:ResonitePath=...`, the `RESONITE_PATH` environment variable, or a git-ignored `Local.props`
  (see `Local.props.example`). `core/` and the tests need none of that.
- `dotnet test tests/Core.Tests` (about 1 minute). Tests that need the real models (`models/ggml-base.en.bin`,
  `models/ggml-silero-v5.1.2.bin`) skip themselves when absent; `STT_NETWORK_TESTS=1` also runs the real-download test.
- `dotnet build src -c Release -p:InstallMod=true` copies the mod into the game's `rml_mods` and its libraries into `rml_libs`
  (game must be closed). `tools/redeploy.ps1` closes Resonite gracefully, builds, installs and relaunches.
  **Never restart the user's game without asking.** `tools/package.ps1` builds `dist/SpeechToText-<version>.zip`.
- Resonite must run RML standalone with the launch option `-LoadAssembly Libraries/ResoniteModLoader.dll` (or via MonkeyLoader).

## Rules that are easy to break

- **Library placement:** `SpeechToText.Core.dll` goes directly in `rml_libs\` (the mod's static config initialisers need its enums
  before `Resolver` exists); Whisper.net, its dependency and the natives stay in `rml_libs\SpeechToText\` and are resolved by `Resolver`.
- **Whisper.net:** `RuntimeOptions.LibraryPath` must be a *file* path; the loader uses its directory and looks in `runtimes\win-x64`.
  Types touching Whisper.net must not be JIT-compiled before `Resolver.Install()`. whisper.cpp rejects clips under 1 s (the engine
  pads them) and always encodes a 30 s window unless the audio context is sized to the clip (`WhisperEngine.AudioContextFor`).
- **Whisper robustness:** never loosen the token cap, the "no temperature retries" setting or the per-call deadline in
  `WhisperEngine`: humming, singing and tones make the decoder spin for 10+ s otherwise and freeze the game's frame rate.
  `TextFilter` collapses the resulting repetition. `hum.wav`, `oh.wav` etc. are the regression fixtures.
- **Threads:** the audio callback runs on the engine's audio thread while holding the input's lock: it must not block, allocate in
  steady state or log. All world/slot/component work goes through `world.RunSynchronously`. Changing a synced field inside its own
  `OnValueChange` is blocked, so reverts are posted to the next update.
- **Privacy:** audio never leaves memory and is never written to disk. Text reaches everyone in the session, so the mod must stay
  silent when muted, silenced, in Mute or Whisper voice mode, or without a focused world (`Suppression.Reason`). The recognised text is
  only logged with `DebugLogging`, which must default to off.
- **Trigger:** `User/STT.Trigger` is written by users. `TriggerFrom=OnlyMe` (default) ignores and reverts writes whose
  `LastModifyingUser` isn't the local user. Don't make the mod *drive* the variable (a driven field silently ignores writes).

## Engine facts the design relies on (verified against a decompile, see SPEC section 3)

- Microphone: `Engine.AudioSystem.DefaultAudioInput.NewFilteredSamples` (`ReadOnlySpan<StereoSample>`, engine sample rate, usually
  48 kHz); follow `AudioSystem.DefaultAudioInputChanged`. A muted default mic delivers no samples at all, so mute is also detected by
  a periodic re-check and a starvation rule in `AudioPipeline`.
- Every user root has a `DynamicVariableSpace` named `User` with `OnlyDirectBinding`, so names need the `User/` prefix.
- `SyncField<T>.OnValueChange` and `ConflictingSyncElement.LastModifyingUser` tell who changed a value and when.
- Notifications: `NotificationPanel.AddNotification` (private overload with a title) is used so toasts aren't titled "N/A".

## Test hooks

Static methods on `SpeechToText.SpeechToTextMod`, meant to be called through McpLink's `call_method` (or reflection) during development:
`DebugSet(mode, dummy[, debugLogging])`, `DebugMute(bool)`, `DebugConfig(name, value)`, `DebugStress(seconds, everyMs)`. The fake-utterance
feed (`PublishDummyText`) publishes `Test utterance N`. Always leave the mod set to `Off` and unmuted after a test.
