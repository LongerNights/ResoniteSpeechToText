# SpeechToText — Mod Spec

Status: draft v0.3 (2026-11-10). **v0.3 changes the activation design:** the mod no longer detects gestures, buttons or keys
itself. In *Triggered* mode the speech-to-text is switched by one boolean dynamic variable, `User/STT.Trigger`, that anything
inside Resonite (a ProtoFlux gesture detector, a button, an avatar, a world, another user's logic) can write. This replaces
the built-in HandToMouth / ControllerChord / DesktopKey detectors of v0.2 and the "Gesture" mode name (now "Triggered").
§3.1–3.6 were re-verified in M1 against a fresh decompile of the installed build **`2026.11.10.1262`** (the audio, mute,
user-space, input, notification and world-focus members all exist as described). Items marked *(unverified)* are still
assumptions. Project guide: [CLAUDE.md](CLAUDE.md). This spec replaces the earlier PLAN.md.

## 1. Summary

A Resonite mod (RML) that transcribes the **local user's microphone** on their own machine and publishes the text as
**dynamic variables on their user root slot**. Because the variables are synced world state, every user in the session
(with or without the mod) and any ProtoFlux, avatar or world item can read them. Two activation modes:
**Continuous** (always listening, voice-activity segmented) and **Triggered** (listens only while the boolean variable
`User/STT.Trigger` is true; what flips it is entirely up to the user, so gestures, buttons, keys, voice-independent logic
or other people's worlds are all built in Resonite, not in the mod).

## 2. Goals / non-goals

**Goals**
- G1. Accurate local transcription with no cloud service and no account.
- G2. A small, stable variable contract (§5) that creators can build on (captions, voice commands, logs, translators).
- G3. Two activation modes, switchable at runtime from the mod settings. In Triggered mode the user has maximum control:
  one boolean variable is the whole interface, so any gesture, button or logic they can build in Resonite can drive it
  (or something else entirely), with no mod update needed.
- G4. Never hitch or crash the game: no work on the audio callback or the update thread beyond copying and writing values.
- G5. Privacy by default: off until the user turns it on; never transcribes while muted; obvious "listening" state.

**Non-goals (v1)**
- Transcribing *other* users' voices (only the local mic).
- Cloud engines, speaker diarization, punctuation/command grammars beyond what Whisper produces.
- Any in-world UI of our own beyond a dash notification; creators build UIs from the variables. (A sample caption
  item is a README deliverable, not part of the mod.)
- **Detecting gestures, controller buttons or keyboard keys in the mod.** The trigger variable is the integration point;
  recipes for common triggers (hold a key, hand to mouth, toggle button) are README examples built from ProtoFlux, not mod code.
- Linux / headless support (headless clients have no mic). Linux desktop may work but is untested.

## 3. Verified engine facts

### 3.1 Microphone audio — `FrooxEngine.AudioInput`, `AudioSystem`
- `Engine.AudioSystem.AudioInputs` (`List<AudioInput>`), `DefaultAudioInputIndex`, `DefaultAudioInput`, and event
  `DefaultAudioInputChanged(AudioInput)` (fires when the user picks a different mic).
- `AudioInput` events `NewRawSamples` and `NewFilteredSamples`, type
  `delegate void AudioSamplesHandler(ReadOnlySpan<StereoSample> buffer)`.
  - Filtered = after the engine's `BufferDenoiser` (the noise suppression voice chat uses). We use **filtered**.
  - Samples arrive already resampled to the **engine rate** `AudioSystem.SampleRate` (`AudioInput.WriteSamples`
    resamples device rate → engine rate). Typically 48 kHz *(unverified on this machine; read it at runtime)*.
  - Raised on the audio/device thread. The span is only valid during the call.
- This is exactly the path voice chat uses: `UserAudioStream<S>.OnChanges` subscribes to
  `AudioInputs[DefaultAudioInputIndex].NewFilteredSamples`.

### 3.2 Mute and privacy signals
- `AudioInput.WriteSamples` returns early when `AudioSystem.IsMuted && this == DefaultAudioInput`, so **a muted default
  mic produces no samples at all**. We still check explicitly (defence in depth, and for non-default inputs).
- `User.isMuted` (synced copy of `AudioSystem.IsMuted`), `User.ActiveVoiceMode` (returns `Mute` when muted or
  `LocalVoiceModeOverride == Mute`), `User.IsSilenced` (host silenced the user).
- Voice chat writes audio only when `world.Focus == WorldFocus.Focused` and `!LocalUser.IsSilenced` (and not while
  recording a contacts voice message, `ContactsDialog.RecordingVoiceMessage`). We mirror all three.
- `VoiceMode` is `Mute, Whisper, Normal, Shout, Broadcast`. Whisper is heard only by people nearby, but the text we publish
  goes to **every** user in the session, so **Whisper mode also suppresses us** (v0.3 rule; the user's whisper stays private).
- Who changed a synced value: `SyncField<T>.OnValueChange` (event, raised on the world thread for local and remote changes) and
  `ConflictingSyncElement.LastModifyingUser` (the local user for local writes, the sender for remote ones, `null` before any
  write). `User.IsLocalUser` tells whether that user is us. This is how the trigger policy (§6.2) tells our own changes from
  someone else's, and how the gate reacts within the same frame instead of polling.

### 3.3 User root variable space
- `CommonAvatarBuilder.BuildAvatar(UserRoot)` attaches to every user root slot:
  `DynamicVariableSpace { SpaceName = "User", OnlyDirectBinding = true }`.
- `OnlyDirectBinding` means a variable binds only when its name is explicitly prefixed: **`User/STT.Text`**, never bare
  `STT.Text`.
- Worlds using a custom avatar builder may not have it *(unverified how common)*: if no `User` space exists on the root,
  we attach our own with the same settings on our container slot's parent (the root).

### 3.4 User root, head and hands — `FrooxEngine.UserRoot`
- `world.LocalUser.Root` → `UserRoot`; `UserRoot.Slot`, `HeadSlot`, `GlobalScale`, `HeadFacingRotation`,
  `LeftHandPosition` / `RightHandPosition`, `LeftControllerPosition` / `RightControllerPosition`.
- Head position: `HeadSlot.GlobalPosition`, rotation `HeadSlot.GlobalRotation`.

### 3.5 Input — `FrooxEngine.InputInterface`
- `engine.InputInterface.GetControllerNode(Chirality)` → `IStandardController` with `Digital ActionPrimary`,
  `ActionSecondary`, `ActionMenu`, `ActionGrab`, `TouchingStrength`, `TouchingAxis`; `Analog Strength`; `Analog2D Axis`.
- `InputInterface.GetKey(Key)` / `GetKeyDown` / `GetKeyUp` for desktop keys.
- `InputInterface.VR_Active` *(unverified name)* to tell VR from desktop.
- Finger pose: `FingerPoseStreamManager` (a `UserRootComponent`) implements `IFingerPoseSource`:
  `AreFingersTracking(Chirality)`, `GetFingerData(BodyNode, out float3, out floatQ)`.

### 3.6 Worlds and threading
- `engine.WorldManager.FocusedWorld`, events `WorldFocused(World)`, `WorldAdded`, `WorldRemoved`.
- `world.RunSynchronously(Action)` queues work onto that world's update thread. All slot/component writes go through it.
- `NotificationPanel.ShowNotification(userId, message, thumbnail, color, NotificationType)` shows a dash toast.

### 3.7 Mod platform
- MonkeyLoader with RML compatibility; mods in `rml_mods\`, shared libraries in `rml_libs\`. The project references
  `ResoniteModLoader.dll` and `0Harmony.dll` like the sibling mods.
- **ResoniteModSettings** is installed, so RML `ModConfigurationKey`s are editable from the dash. Enum keys render as
  dropdowns (EnumDropdown is installed too).
- Already installed and relevant: **ResonitePTTKeybinds** (push-to-talk keys for voice chat). Our desktop key must not
  default to the same key; read its config in M6 to avoid clashes.

### 3.8 Speech engine — Whisper.net
- `Whisper.net` 1.9.1 (latest seen, 2026) wraps whisper.cpp. Runtime packages: `Whisper.net.Runtime` (CPU),
  `.Runtime.NoAvx`, `.Runtime.Vulkan`, `.Runtime.Cuda`.
- 1.9.1 adds **voice activity detection**: `WhisperVadFactory` (`FromPath`), `WhisperVadProcessor`, builder/options
  APIs, and Silero VAD models in `WhisperGgmlDownloader` (`SileroVadType`). Exact member names are verified in M1.
- Models (ggml): `base.en` ≈ 142 MB, `small` ≈ 466 MB, `large-v3-turbo` ≈ 1.6 GB *(sizes approximate)*.
- **Native loading (verified standalone in M1, in-game pending):** `RuntimeOptions.LibraryPath` is treated as a *file*
  path. The loader takes its directory and probes `<dir>\runtimes\win-x64` (also `vulkan\`, `cuda\`, `noavx\`
  subfolders under `runtimes`). Setting it to a directory path does **not** work. We set it to
  `rml_libs\SpeechToText\SpeechToText.native` (a non-existent file in the right folder). Tested from a copy with no
  `runtimes` folder next to the app: loads `Cpu` runtime and transcribes. **Also confirmed in-game (M1).**
- **Managed deps:** `Whisper.net.dll` and `Microsoft.Extensions.AI.Abstractions.dll` live in `rml_libs\SpeechToText\`
  and are resolved by our `Resolver` (an `AssemblyLoadContext.Resolving` handler) installed first in `OnEngineInit`.
  Types that touch Whisper.net sit in separate classes so they are not JIT-compiled before the resolver exists.
- **Measured (standalone, base.en, CPU, 4 threads):** model load 235 ms; 4.9 s of speech transcribed in ~1.4 s
  (first run, includes warm-up); Silero VAD (`ggml-silero-v5.1.2.bin`, 885 KB, from `ggml-org/whisper-vad`) correctly
  split the two phrases. The §6.1 target of < 1 s for 5 s needs re-measuring in M5 with warmed-up state.

## 4. Architecture

```
 audio thread                     STT worker thread (1, BelowNormal)          recognizer thread            world update thread
┌──────────────┐  copy   ┌──────────────┐  frames  ┌────────────┐ segments ┌─────────────┐ results ┌──────────────┐
│ AudioInput   ├────────►│  AudioTap    ├─────────►│ Segmenter  ├─────────►│ Transcriber ├────────►│ Publisher    │
│ NewFiltered  │         │ ring buffer  │          │ VAD + gate │          │ Whisper.net │ RunSync │ dyn vars on  │
│ Samples      │         │ 16 kHz mono  │          └─────▲──────┘          └─────────────┘         │ user root    │
└──────────────┘         └──────────────┘                │ gate (volatile bool)                    └──────┬───▲───┘
                                                         │                                                │   │
                                                         └──── SttController ◄── OnValueChange ───────────┘   │
                                                               (Triggered mode only)   User/STT.Trigger  ◄────┘
                                                                                       written by anything in Resonite
```

### 4.1 Components (source files)

| File | Responsibility | Engine refs? |
|---|---|---|
| `src/Mod.cs` | `ResoniteMod` entry; config keys; creates and owns the pipeline; hooks engine update and world events | yes |
| `src/AudioTap.cs` | Subscribes to the current default input's `NewFilteredSamples`; follows `DefaultAudioInputChanged`; downmix + resample; writes to ring buffer; mute/focus checks | yes |
| `src/Publisher.cs` | Per-world variable container on the local user root; marshals writes; **watches `User/STT.Trigger`** (`OnValueChange`), applies the trigger policy and reverts rejected writes | yes |
| `src/Notify.cs` | Dash toasts (privacy notice, downloads, failures); safe from any thread; de-duplicates within 20 s | yes |
| `src/Controller.cs` | Starts/stops/rebuilds the pipeline from the settings; turns the accepted trigger into the pipeline gate; auto-off timer | yes |
| `core/Trigger.cs` | `TriggerPolicy` (whose writes are accepted) and `TriggerAutoOff` (switch off after N seconds), pure and unit-tested | no |
| `core/RingBuffer.cs` | Lock-free SPSC float ring buffer; overwrites the oldest data, the consumer detects being lapped | no |
| `core/Resampler.cs` | Streaming windowed-sinc polyphase resampler for any rational ratio (48 kHz → 16 kHz is 3:1, 96 taps) | no |
| `core/Vad.cs`, `core/SileroVad.cs` | `IVad`: `EnergyVad` (fallback) and `SileroVad` (Whisper.net VAD over a sliding 1 s window) | no (Silero uses Whisper.net) |
| `core/Segmenter.cs` | State machine (§6.1) turning 20 ms frames + gate into `Partial`/`Final`/`Discarded` events; time counted in frames, no clocks | no |
| `core/WhisperEngine.cs` | `ISpeechEngine` + Whisper.net implementation; serialized, processor reused, short clips padded to 1.1 s | no (Whisper.net) |
| `core/ModelManager.cs` | `ModelCatalog` + download to `.part`, redirect-following, size and SHA-256 check, atomic rename | no |
| `core/TextFilter.cs` | Marker stripping, hallucination filter, profanity mask, length cap | no |
| `core/Enums.cs` | `SttMode` (Off/Continuous/Triggered), `SttModel`, `SttBackend`, `VadKind`, `TriggerSource` (shared with the mod) | no |
| `tests/Core.Tests/` | xUnit tests for all of `core/` (unit tests + integration tests with the real models) | — |

`core/` (assembly `SpeechToText.Core`) must not reference FrooxEngine or RML, so it can be unit-tested without the game.
**Deployment gotcha:** the mod's static config initialisers use `SttMode` etc., which run before our `Resolver` is installed, so
`SpeechToText.Core.dll` is installed directly into `rml_libs\` (RML loads that folder early); only Whisper.net, its dependency and
the natives live in `rml_libs\SpeechToText\`.

### 4.2 Threading rules
1. **Audio callback:** downmix + resample into a stack/pooled buffer, push to the ring buffer, return. No allocation in
   steady state, no locks, no logging per call.
2. **STT worker:** one dedicated `Thread` (BelowNormal priority) loops: drain ring buffer in 20 ms frames → VAD →
   Segmenter → Whisper when a segment request is ready. Whisper threads capped by config `Threads`.
3. **Trigger changes** arrive on the world's update thread through `SyncField<bool>.OnValueChange` on `User/STT.Trigger`; the
   handler applies the policy and writes the accepted state into a `volatile` field the pipeline reads as its gate. No
   per-frame hook and no polling are needed, so a toggle takes effect in the same frame it is written.
4. **World writes:** only via `world.RunSynchronously`. The worker never touches engine objects.
5. Shutdown (`Engine.OnShutdown` / mod disable): unsubscribe audio, stop the worker, dispose Whisper, destroy containers.

### 4.3 Audio format
- Input: `StereoSample` (L/R float) at `AudioSystem.SampleRate`.
- Downmix: `(L + R) * 0.5`.
- Output: 16 000 Hz mono float32 in [-1, 1] (Whisper's required input).
- Ring buffer capacity: 60 s (≈3.8 MB). On overflow (worker stalled), drop oldest and log once per minute.

## 5. Variable contract (public API — changes are breaking)

All variables live in the `User` space on the **local user's root**.

| Name | Type | Written when | Meaning |
|---|---|---|---|
| `User/STT.Text` | `string` | each final result | Last finalized utterance (trimmed, filtered, capped to `MaxTextLength`). Not cleared afterwards. |
| `User/STT.Partial` | `string` | while speaking, ≤ `PartialRateHz` | Current hypothesis for the in-progress utterance; set to `""` when the utterance is finalized or discarded. Only written if `PublishPartials`. |
| `User/STT.Sequence` | `int` | each final result | Increments by 1 per final result (starts at 0 per world join). Creators drive "new line" logic off changes to this, so repeated identical text still fires. |
| `User/STT.Listening` | `bool` | on gate change | **Output.** The mic is actually being transcribed right now: the gate is open (Continuous mode, or Triggered mode with `Trigger` true) **and** nothing suppresses us (muted, silenced, Whisper voice mode, unfocused). The truthful "recording" indicator; `Trigger` is only the request. |
| `User/STT.Speaking` | `bool` | on VAD change | True while VAD detects speech inside an open gate. |
| `User/STT.Mode` | `string` | on config change | `"Off"`, `"Continuous"` or `"Triggered"`. Read it to know whether `Trigger` currently does anything. |
| `User/STT.Language` | `string` | each final result | Detected/selected language code (e.g. `"en"`). |
| `User/STT.Trigger` | `bool` | **by you** (any logic in Resonite) | **Input, the on/off switch.** In Triggered mode: `true` = listen, `false` = stop (the utterance in progress is finished after a short post-roll). Ignored in Continuous mode. The mod creates it as `false` and writes it only to revert a rejected write (§6.2) or for the optional auto-off. |

**Dynamic impulse `STT.Text`:** after the variables of a new final result are written, the publisher calls `ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulse(userRoot.Slot, "STT.Text", excludeDisabled: true)`, so a `DynamicImpulseReceiver` under the user root can read `User/STT.Text` in its flow. It is sent one update (frame) after the variables are written, from the 4 Hz publish tick, once per Sequence change (two results in one tick give one impulse), and not for text that existed before the container was (re)created. Not yet verified in-game.

All variables except `Trigger` are written only by the local user's client; `Trigger` is the one variable meant to be written by
other things (§6.2 says whose writes count).

Implementation:
- Components: one `DynamicValueVariable<T>` per row, on a child slot named `SpeechToText` under the user root slot.
  The slot is `PersistentSelf = false` and tagged `SpeechToText`. Creating the slot is the only structural change.
- Created lazily when a world becomes focused (or for all worlds with `PublishToAllWorlds`), and re-created if the user
  root respawns (detected by `world.LocalUser.Root` identity changing, checked once per second on the update thread).
- If a `SpeechToText` slot already exists under the root (e.g. left from a previous session state), reuse it.
- Mode `Off` destroys the containers in all worlds, so idle users leave no trace.
- When a world loses focus (and `PublishToAllWorlds` is off): write `Listening=false`, `Speaking=false`, `Partial=""`
  there, keep `Text`/`Sequence`.
- Value writes skip if unchanged (avoid sync traffic).

## 6. Behaviour

### 6.1 Segmenter state machine
States: `Idle` → `Speech` → `Trailing` → (finalize) → `Idle`.

| Event | Continuous mode | Triggered mode |
|---|---|---|
| Gate opens (`Trigger` becomes true) | — (gate is always open while enabled) | Start segment immediately with the last `PreRollMs` (300) of audio. VAD ignored for start. |
| VAD speech start (gate open) | `Idle`→`Speech`; segment starts `PreRollMs` before | sets `Speaking=true` only |
| VAD silence ≥ `SilenceMs` (700) | `Trailing`→finalize | ignored (user controls the end) |
| Gate closes (`Trigger` becomes false) | finalize current segment, go `Idle` | keep recording `PostRollMs` (200) more, then finalize; if the gate reopens within that time it is the same utterance |
| Segment length ≥ `MaxSegmentSec` (15) | finalize and continue in a new segment | same |
| Every `PartialIntervalMs` (1000) in `Speech` | request partial over the segment so far (if `PublishPartials` and worker idle) | same |
| Mute / silence / unfocus | discard current segment, `Idle`, `Partial=""` | same |

- Segments shorter than `MinSpeechMs` (300) of voiced audio are discarded.
- Partial requests are dropped (not queued) while a transcription is running; finals are never dropped. Queue depth
  for finals: 4; beyond that the oldest is transcribed with a lower-cost setting (greedy, no timestamps) and a warning is
  logged.
- With Whisper on CPU a final for a 5 s segment should take < 1 s on `base.en` (target, measured in M5).

### 6.2 Triggered mode: `User/STT.Trigger` is the switch

The mod contains **no** gesture, button or key detection. In Triggered mode the gate is simply the boolean variable
`User/STT.Trigger` on the local user root: `true` = listen, `false` = don't. Everything about *when* to flip it belongs to the
user: hold-to-talk, press-to-toggle, hand-to-mouth, a voice-independent hotword, a wrist menu, a world's own logic. Because it
is an ordinary dynamic variable, any of these can be built in Resonite (ProtoFlux, components, an avatar) or by something else
entirely, with the same one-line contract. Hold versus toggle is therefore the creator's choice, not a setting of ours.

- **Reaction time:** the mod subscribes to `OnValueChange` of the variable's value field, so the gate moves in the same frame the
  value is written (no polling). Opening starts the utterance with 300 ms of pre-roll, so the first word is not clipped.
- **Per world:** the variable exists in every world we publish to; the gate follows the one in the **focused** world (or any of
  them with `PublishToAllWorlds`). Switching worlds closes the gate for the world you left.
- **Suppression always wins:** muted, silenced by the host, voice mode Mute or Whisper, recording a voice message, no focused
  world: nothing is transcribed even if `Trigger` is true. `Listening` shows the real state; `Trigger` is only the request.
- **Who may flip it (`TriggerFrom`, default `OnlyMe`):** `Sync` fields remember who last changed them. With `OnlyMe` a change
  counts only if it was made by the local user (the user's own avatar logic, a manual edit, the user's own tools). A value set by
  another user (a world's ProtoFlux running on the host, a malicious avatar) is **rejected and reverted to false** with one log
  line, so nobody else can start transcribing your mic. `Anyone` accepts every writer, for people who deliberately let a world
  or another user drive their switch. A `null` last-modifier (never written) counts as local.
- **Optional safety net (`TriggerAutoOffSec`, default 0 = never):** if `Trigger` stays true longer than this, the mod writes it back
  to false. For forgetful toggles; off by default because the user asked for full control.
- **Switching modes:** leaving Triggered mode ignores the variable; entering Continuous mode listens regardless of it. In
  Triggered mode it starts closed (the variable is created `false`), so nothing is heard until the user flips it.
- **Examples (README, M7), all plain ProtoFlux:** hold a keyboard key (`Keyboard Key` node → `Write Dynamic Variable<bool>`);
  hand-to-mouth (distance between a hand slot and the head → write true/false with hysteresis); a toggle button
  (`Write` the negation of `Read`); an avatar gesture ring; a "talk" button on a wrist menu.

### 6.3 Continuous mode
Gate open whenever mode = Continuous and not muted. VAD decides segments. `Listening=true` the whole time.

### 6.4 VAD
- Default `SileroVad` through Whisper.net 1.9.1 (model ≈ 2 MB, downloaded with the Whisper model).
- Fallback `EnergyVad`: 20 ms frames, RMS in dB; noise floor = 10th percentile of the last 5 s; speech when RMS >
  floor + `VadSensitivity` dB (default 12) for 3 consecutive frames; hangover 300 ms.
- Config `Vad` = `Silero | Energy`.

### 6.5 Text post-processing (`TextFilter`)
- Trim; collapse whitespace; strip Whisper markers (`[BLANK_AUDIO]`, `(music)`, `[inaudible]` …).
- Drop results matching the hallucination list (`"Thank you."`, `"Thanks for watching!"`, `"you"`, `"."` …) when the
  segment's voiced duration is < 1 s.
- Optional `ProfanityFilter` (off): mask words from a bundled list with `*`.
- Cap at `MaxTextLength` (512) on a word boundary with `…`.

### 6.6 Models
- Stored in `rml_libs\SpeechToText\models\`. Download from Hugging Face (`ggerganov/whisper.cpp`) via
  `WhisperGgmlDownloader` or plain HTTPS, to a `.part` file, then rename. Verify size/hash.
- Download only when the user first enables a mode (not at engine start). Progress and errors as dash notifications.
- Model load happens on the worker thread; while loading, `Listening` stays false and a notification says so.
- Changing `Model` at runtime unloads and reloads.

## 7. Settings (`ModConfigurationKey`, via ResoniteModSettings)

| Key | Type | Default | Notes |
|---|---|---|---|
| `Mode` | enum `Off/Continuous/Triggered` | `Off` | **Decided (2026-11-10):** off until the user picks a mode (G5). No model download or mic hook happens while Off. `Triggered` = listen only while `User/STT.Trigger` is true (§6.2). |
| `TriggerFrom` | enum `OnlyMe/Anyone` | `OnlyMe` | Whose writes to `User/STT.Trigger` are accepted (§6.2). `OnlyMe` also reverts writes by others. |
| `TriggerAutoOffSec` | int | 0 | Switch `Trigger` back to false after this many seconds open; 0 = never. |

*(v0.2 had `GestureSources`, `GestureBehavior`, `GestureHand`, `HandToMouthDistance`, `HandToMouthRequireFist`, `GestureDwellMs`,
`ControllerChord`, `DesktopKey`, `ToggleTimeoutSec` and `AllowWorldTrigger`. They are removed: those behaviours now live in the
user's own Resonite logic. Saved values for them are simply ignored.)*
| `Model` | enum `TinyEn/BaseEn/SmallEn/Base/Small/LargeV3Turbo` | `BaseEn` | multilingual for non-English |
| `Language` | string | `auto` | ISO code or `auto` (multilingual models) |
| `TranslateToEnglish` | bool | false | Whisper translate task |
| `Backend` | enum `Cpu/Vulkan/Cuda` | `Cpu` | GPU competes with rendering |
| `Threads` | int | 4 | |
| `Vad` | enum `Silero/Energy` | `Silero` | |
| `VadSensitivity` | float (dB) | 12 | Energy VAD only |
| `SilenceMs` | int | 700 | |
| `MaxSegmentSec` | int | 15 | |
| `PublishPartials` | bool | true | |
| `PartialRateHz` | float | 4 | |
| `PublishToAllWorlds` | bool | false | |
| `MaxTextLength` | int | 512 | |
| `ProfanityFilter` | bool | false | |
| `DebugLogging` | bool | false | per-segment timings to the log |

Config changes apply live (`ModConfiguration.OnThisConfigurationChanged`), except `Backend` (needs restart, notify).

## 8. Robustness

- Every engine callback and Harmony patch body is wrapped in try/catch → log, never throw into the engine.
- If the Whisper native library fails to load: log the full exception, notify once, set mode effectively Off; the
  rest of the game is unaffected.
- No mic / input disconnected (`AudioInput.ConnectedChanged`): `Listening=false`, resume automatically on reconnect.
- Never write to userspace world variables; only to normal worlds' local user roots.
- If attaching to the user root is rejected by world permissions *(unverified whether hosts can block this)*: log once
  per world, skip that world.
- Memory: segment buffers pooled (`ArrayPool<float>`); Whisper context created once and reused.

## 9. Risks

| Risk | Impact | Mitigation |
|---|---|---|
| Native whisper DLLs not found when loaded from `rml_libs` | blocker | **Solved and confirmed in-game** (§3.8: `LibraryPath` is a file path) |
| CPU cost hurts frame rate | high | thread cap, BelowNormal priority, `base.en` default, GPU backends, measure with McpLink `perf` |
| Whisper hallucinations on silence | medium | Silero VAD gating + `TextFilter` |
| Host permission systems strip our components | medium | detect and skip; document |
| Privacy: speech text is visible to the whole session | high | default Off, mute respected, `Listening` indicator, notification on first enable explaining this |
| Someone else flipping `User/STT.Trigger` to start transcribing you | low | `TriggerFrom=OnlyMe` by default: writes by other users are rejected and reverted; suppression (mute, Whisper mode) still applies; `Listening` always shows the truth |
| A world's host-side ProtoFlux can't drive your trigger under `OnlyMe` (its writes arrive from the host) | low | by design; people who want world-driven control choose `Anyone` (documented in the README) *(unverified in a multi-user session)* |
| Engine update changes the audio APIs | medium | small surface (§3.1); re-verify each game update |

## 10. Milestones

1. **M1 Scaffold + spike.** *(Status 2026-11-10: **done.** Scaffold, all §7 config keys, redeploy script, fresh decompile,
   standalone spike and install done. In-game load test passed on build 2026.11.10.1262 with RML 5.0.3: model loaded in 276 ms,
   native runtime `Cpu` loaded from `rml_libs\SpeechToText`, 4.9 s of audio transcribed in 2617 ms with 2 threads while the
   engine was idle in the dash (the standalone run took ~1.4 s with 4 threads). Whisper.net and its natives load fine inside
   the game through our `Resolver` + `LibraryPath` trick. Note: RML 5.0.1.2 had failed on this build (`IPlatformConnector`
   gained `NotifyOfVideo`); the fix was RML 5.0.3 run standalone.)* csproj (net10.0, referencing the game's DLLs, plus Whisper.net), `Mod.cs` with config keys,
   `tools/redeploy.ps1`. Fresh decompile of build `2026.11.10.1262` into `ref/`; re-verify §3. **Spike:** load Whisper.net
   from `rml_libs` and transcribe a bundled WAV at engine start, logging the text and timing. Exit: text in the log.
2. **M2 Publisher.** *(Status 2026-11-10: implemented in `src/Publisher.cs` (+ `Snapshot.cs`, `DummyFeed`). Verified in-game: all 8
   variables appear in the stock `User` space on the local user root and carry live values; created in the focused world, and
   recreated in the new focused world on a world switch (World 0 → World 1); Mode Off removes them everywhere and returning
   to a mode recreates them only in the focused world; a `DynamicValueVariableDriver<string>` bound to `User/STT.Text` tracked
   the value live; no errors logged. **Respawn verified:** destroying the user root made the engine respawn the user (new root), and the publisher
   recreated all 8 variables on it within ~0.2 s with live values, leaving exactly one container. (To respawn from McpLink,
   `destroy` the user root slot: setting `User.respawnRequest` only acts when `Root == null`.) **M2 exit criteria met.**
   Notes: `DynamicVariableSpace.TryReadValue` takes the bare name (`STT.Text`); ProtoFlux reads strings with
   `ReadDynamicObjectVariable<string>` and ints/bools with `ReadDynamicValueVariable<T>`. Test hook for McpLink:
   `call_method type:SpeechToText.SpeechToTextMod DebugSet ["Continuous", true]` sets Mode and the fake-utterance feed
   (config `PublishDummyText`, default off).)* Variables on the local user root in the focused world, fed by a timer with dummy text. Exit:
   McpLink `dynvar_space` shows all §5 variables; a ProtoFlux `DynamicVariableInput<string>` on `User/STT.Text` reads them;
   they survive respawn and world switching; Mode Off removes them.
3. **M3 STT core.** *(Status 2026-11-10: **done**, 101 tests green + 1 opt-in network test, no game involved. Everything
   in §4.1 `core/` is implemented. Results: the full pipeline (audio → VAD → segmenter → Whisper `base.en` → TextFilter) turns
   the 17 s fixture monologue into exactly 3 utterances in order with the right words, with both Silero and the energy VAD, also
   at 10 dB SNR white noise; pure silence/noise yields no segments; triggered (gate-controlled) mode records while the gate is held and transcribes on release;
   48 kHz → 16 kHz resampling doesn't hurt recognition. Warmed-up speed on this machine (CPU, 4 threads): 17.3 s of audio in
   1.9 s, RTF 0.11, so the §6.1 target (< 1 s for a 5 s utterance) is met with room to spare. The real Hugging Face download
   was verified (server announces SHA-256 via `X-Linked-ETag` on the redirect; checked, file identical to the curl copy).
   Findings that changed the design: whisper.cpp **refuses clips under 1 s**, so `WhisperEngine` pads to 1.1 s; the Silero
   binding is buffer-oriented, so `SileroVad` re-runs a sliding 1 s window every 100 ms (decision lags ~0.1–0.3 s, covered by
   the 300 ms pre-roll); the ring buffer drops oldest on the consumer side (lock-free) rather than the producer side.
   Left for M4/M5 on purpose: dropping partials while a transcription runs (worker policy), the final-queue depth rule, and
   moving the spike out of the mod. **Not yet verified in-game:** the mod loading with the new `SpeechToText.Core.dll`
   layout (needs a restart; done at the start of M4).)* Ring buffer, resampler, VADs, segmenter, Whisper engine, model manager, text filter, with unit tests
   on WAV fixtures (silence, short word, long monologue, noisy). Exit: tests green, no game needed.
4. **M4 Audio tap.** *(Status 2026-11-10: **done**, except two paths not exercised in-game (below). Implemented: `core/AudioPipeline.cs`
   (ring → 20 ms frames → VAD → segmenter on a BelowNormal worker thread; suppression and "starved" both discard the utterance
   in progress; 12 new unit tests, 113 total green), `src/AudioTap.cs` (subscribes to the default mic's `NewFilteredSamples`,
   follows `DefaultAudioInputChanged`, downmix + resample in the callback, `Suppression` rules of §3.2), `src/Controller.cs`
   (starts/stops/rebuilds the pipeline from the settings, loads Silero via `ModelManager`, falls back to the energy VAD,
   publishes `STT.Listening`/`STT.Speaking`). Verified in-game on the Blue Snowball, Silero detector: the mod loads with
   `SpeechToText.Core.dll` flat in `rml_libs`; the engine delivers exactly 48,000 samples/s in 100 callbacks/s and the worker
   keeps pace (~50 frames/s, nothing dropped); muting through the engine sets `Listening=false` within ~0.25 s and unmuting
   restores it; real speech produced the expected stream of `speaking` flips, ~1 s partials and `FINAL` events ~0.7 s after
   speech stopped (1.0–2.8 s utterances, 0.6–1.8 s voiced); variables show `Listening=true`/`Mode=Continuous`; zero errors.
   Findings: (1) the **filtered** stream is exact digital silence (−180 dBFS) between utterances, because the engine's noise
   suppression gates it, so the energy VAD's floor sits at its −100 dB clamp (fine), and the stream is hot (peaks at 0 dBFS, RMS ≈
   −17 dB when speaking); (2) the engine does **not** call us at all while the default mic is muted, so mute is detected by the
   controller's 4 Hz re-check plus the pipeline's 500 ms starvation rule, not by the callback; (3) the callback runs under the
   input's own lock, so it must never block; (4) the first seconds after launch deliver slightly fewer samples (world loading).
   Not exercised in-game: **default-device switching** (code path exists, no second input tried) and **focus changes** (the
   tap only suppresses when no world is focused). A known cosmetic quirk: a partial can fire during the trailing-silence tail
   after speech has ended; harmless, M5 may skip such partials.)* Hook `NewFilteredSamples`, device switching, mute/focus handling. Exit: `DebugLogging` shows segment
   timings in-game; muting stops capture.
5. **M5 Continuous mode end to end** *(Status 2026-11-10: **done** (two small follow-ups still need an in-game look, see the end).
   **Re-test after the robustness fix, live (Blue Snowball, user hummed/sang ~15 s then spoke ~1 minute of normal sentences, 75 s
   capture):** world p90 2.87 ms vs 2.23 ms baseline for the same session, ticks 1680 vs 1760 per 10 s, worst frame 10.6 ms (baseline
   worst 24 ms): **no freeze** (before the fix: 327/647 ticks per 10 s). Humming came out as `(vocalizing)` / "Doo-boo-boo" and was
   handled. Spoken text was accurate over utterances of up to 11 s ("2014. It's kind of old and falling apart anyways, so we kind
   of need to sell it before it gets too too far gone to sell..."). Time from the segment ending to the text being published:
   0.2–0.4 s for utterances up to 4 s, 0.75 s at 7 s, 1.3 s at 9.7 s, 2.2 s at 11 s (inference 1.4 s, plus ~0.8 s waiting for a
   running partial). With the 0.7 s silence wait this meets "within ~1.5 s of finishing a sentence" for normal sentences (about
   1.0–1.5 s) but not for 10 s monologues (about 2–2.9 s). Empty finals (breaths, 0.4 s blips) publish nothing and clear the partial.
   **Follow-ups added after that capture, covered by unit tests but NOT yet re-checked in-game:** a running partial is cancelled the
   moment its utterance ends, so the final starts immediately (should take the 11 s case from 2.2 s to about 1.4 s); any short
   parenthetical such as `(vocalizing)` or `(humming)` is stripped as a sound annotation.
   Earlier status text follows.) Built: `core/Transcriber.cs` (finals ordered and never dropped,
   only the newest pending partial kept while busy, stale partials of ended utterances discarded; 13 tests), utterance ids on
   segments, partials only while speaking, Whisper model load + warm-up and result publishing in `src/Controller.cs`, text
   filter wired to settings. Live results so far, Blue Snowball, base.en, CPU, 4 threads: model + Silero load and warm-up
   0.9 s; normal utterances published 0.3–0.75 s after the segment ended (1.3–7 s of speech), i.e. about 1.0–1.5 s after the
   speaker stops once the 0.7 s silence wait is added. Frame-time cost (FrameBudget, world p90 / ticks per 10 s): mode Off
   3.1 ms / 1525; Continuous idle 3.4 ms / 1390 (noise); simulated talking at 35% duty 4.1 ms; **recognition running non-stop
   (about 8× denser than real speech) 5.2 ms / 1190 with 4 threads, 4.2 ms / 1290 with 2 threads**, worst frame no worse than
   baseline (17 ms). Idle cost is Silero ≈ 5% of one core.
   **Two serious problems found by the live test and fixed in code (not yet re-verified in-game):**
   (1) Whisper encodes a fixed 30 s window per call, so every utterance cost ~1.8 s regardless of length. Fixed by an adaptive
   audio context (250/500/1000/1500, `WhisperEngine.AudioContextFor`): 3 s of speech 1793 ms → 305 ms, identical text.
   (2) **Singing/humming made the decoder run away**: the game's frame rate fell to ~20% for ~20 s (327 and 647 ticks per
   10 s, `I I I I` output, 7.6 s inference for a 1.6 s clip). Reproduced with synthetic hum (10.3 s), "oh oh oh" (12.8 s) and a
   220 Hz tone (9.5 s). Fixed by a token cap per clip size (`MaxTokensFor`), no temperature retries, a per-call deadline
   (`DeadlineFor`, cancellation takes effect within ~1 ms) and a repetition collapse in `TextFilter`; those clips now take
   1.0–2.1 s and yield short text. Also fixed: `Engine.Dispose` now uses `DisposeAsync` ("Cannot dispose while processing").
   Findings: log lines written from the worker threads are labelled `[ResoniteModLoader]` instead of the mod's name, so filter
   McpLink logs with `\[stt\]` / `\[segment\]`, not the mod prefix.)* Continuous mode end to end with partials and rate limits. Exit: speech appears in `User/STT.Text` within ~1.5 s
   of finishing a sentence; frame-time delta measured and recorded here.
6. **M6 Triggered mode.** *(Status 2026-11-10: **done**, except foreign-user writes. Implemented: `Gesture` renamed `Triggered` in core,
   tests and mod; the ten gesture settings and `Settings.cs` deleted; `TriggerFrom` and `TriggerAutoOffSec` added;
   `core/Trigger.cs` (`TriggerPolicy`, `TriggerAutoOff`, 10 tests, suite 159 green); `Publisher` subscribes to the trigger's
   `OnValueChange` and evaluates `LastModifyingUser`; `SttController.SetTrigger/ApplyGate`; Whisper voice mode suppresses us.
   **Verified in-game (Blue Snowball):** Triggered mode starts with `Trigger=false`, `Listening=false`; writing `true` opens the gate
   **within 2–4 ms** of the write (same frame; `listening=True` logged at .098 for a write returning at .095) and writing `false`
   closes it just as fast; with the trigger closed, 2+ minutes of loud talking (mic ≈ −11 dB) produced no segment and no text;
   a 10 s open window while reading aloud produced one utterance (`FINAL` 0.2 s after the close = the post-roll) whose text was
   published 1.83 s later (10.5 s clip, inference 1.83 s, **no wait behind its stale partial: the M5 partial-cancel follow-up works**),
   a 5.5 s window published 0.64 s after the close; mute wins over an open trigger and listening resumes on unmute; auto-off at 4 s
   fired at 4.6 s with one log line; Continuous mode ignores the trigger; **Whisper voice mode switches `Listening` off and Normal
   restores it**; zero errors or warnings. **Not verified in-game: a foreign user's write being rejected and reverted** (needs a
   second user; the policy table is unit-tested and the revert path is the same `RunSynchronously` write as the auto-off, which
   was verified). **The M5 `(vocalizing)` filter follow-up is covered by unit tests only.** Lessons: McpLink `set_member` on a
   variable needs the **component** id plus `member:"Value"`; the 10 s+ window cost 1.8 s because clips over 9.5 s fall into the
   1000 audio-context bucket (a 750 bucket would cut that to ~1.3 s, not done).)* Originally: rename Gesture to Triggered; delete the ten obsolete settings and add `TriggerFrom` / `TriggerAutoOffSec`;
   `Publisher` subscribes to the variable's `OnValueChange`, applies `TriggerPolicy`, reverts rejected writes; `SttController`
   turns the accepted trigger into the pipeline gate; Whisper voice mode joins the suppression rules; auto-off. Exit: with Mode
   Triggered, nothing is transcribed while `Trigger` is false; setting it true (from a tool, from ProtoFlux) starts listening
   within the same frame and `Listening` follows; setting it false ends the utterance and its text appears; mute and Whisper
   mode still win; unit tests cover the policy and the auto-off; a rejected foreign write is reverted (verified as far as one
   machine allows).
7. **M7 Polish + release.** *(Status 2026-11-10: **done**, version 1.0.0. `src/Notify.cs` shows dash toasts titled "Speech to text" (uses the
   private `NotificationPanel.AddNotification` overload so the title isn't "N/A", with the public call as fallback; identical
   messages are not repeated within 20 s): one-time privacy notice (hidden config key `PrivacyNoticeShown`), "ready" line per start,
   download start, failure, auto-off, rejected foreign trigger write. **Verified in-game:** the privacy and ready toasts render
   in the dash with the right title; the **first-run download path works end to end** (voice-detector model removed, the mod
   showed the "Downloading ... (1 MB)" toast, logged 0–100%, re-downloaded 885,098 bytes byte-identical to the original and started).
   `README.md` (install, variables, settings, privacy, performance, troubleshooting, tested/untested, licences);
   `tools/package.ps1` builds `dist/SpeechToText-1.0.0.zip` (1.0 MB, all 8 library files byte-identical to the installed ones;
   no models bundled); `dist/SpeechToText-Recipes.resonitepackage` (5 KB, 50 slots, 43 components, no assets).
   **Every recipe was built and run in the game:** hold F8 and tap F9 with real key presses (four trigger windows, text published
   about 0.27 s after release), hand to mouth with hysteresis (moving the thresholds around the real 0.24 m head-to-hand distance:
   on, stays on inside the band, off, stays off), voice command (fires only on the matching utterance, tested with the fake feed,
   including the packaged `ReadDynamic…Variable` variant), caption (a `TextRenderer.Text` driven by `User/STT.Text` follows it
   live). The package was re-imported and its toggle graph worked. Findings: `KeyPressed`/`KeyReleased`/`KeyHeld` are *value* nodes
   (turn them into actions with `FireOnLocal…`), the write node's slot input is `Target`, use the **Local** FireOn nodes for input
   devices; a caption's text renderer drags in a 19 MB font so the caption is documented, not packaged; a hand-to-mouth graph left
   running woke the trigger during an unrelated test, a reminder to remove test graphs. **Not verified:** `(vocalizing)`-style label
   stripping (unit tests only), other models, non-English, a second user, VR hardware, microphone switching, the failure and
   rejected-write toasts. Open: choose a licence; the project is not a git repository.)* Notifications, first-enable privacy notice, README with ProtoFlux recipes for triggers (hold a key,
   toggle button, hand-to-mouth with hysteresis, avatar gesture ring), the caption-bubble example reading `Partial`/`Text`, a
   command matcher on `Sequence`, a note on `TriggerFrom=Anyone`; version 1.0.0.

## 11. Verification plan

- Unit tests (M3) for resampler accuracy, VAD state transitions, segmenter timing tables in §6.1, text filter cases.
- In-game (McpLink): `dynvar_space` / `dynvar_users` on the local user root; `watch` on `User/STT.Sequence`;
  `logs pattern:"SpeechToText"`; `perf` with mode Off vs Continuous for the frame-time budget.
- Manual: second account or alt client in the same session confirms remote users see the text.

## 12. Open questions

1. Should `Text` also keep a rolling transcript variable (e.g. `User/STT.History`, last N lines)? Cheap to add, but more
   sync traffic.
2. Ship the model download URL hard-coded, or let users point `Model` at any local `.bin`?
3. Add a cloud engine later behind `ISpeechEngine`, or keep the mod strictly local?
4. Should `User/STT.Mode` also be an input (write `"Off"`, `"Continuous"` or `"Triggered"` from Resonite)? It would extend the
   "control everything from inside Resonite" idea to the mode itself, but switching modes loads models and starts or stops the
   microphone, so it would need the same `TriggerFrom` policy. Not in v1 unless asked for.
