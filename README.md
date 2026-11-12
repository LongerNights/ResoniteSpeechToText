# SpeechToText for Resonite

A [ResoniteModLoader](https://github.com/resonite-modding-group/ResoniteModLoader) mod that turns what you say into text, **on
your own PC** (no cloud service, no account), and publishes it as **dynamic variables on your user root** so any ProtoFlux, avatar
or world can read it: live captions, voice commands, chat logs, translators.

- **Local.** Recognition runs on your CPU with [Whisper](https://github.com/ggml-org/whisper.cpp). Your audio stays in memory on
  your machine; only the resulting *text* is published, in your own variables.
- **Two modes.** *Continuous* listens all the time and cuts your speech into sentences. *Triggered* listens only while a boolean
  variable, `User/STT.Trigger`, is `true`. The mod has **no built-in gesture, button or key detection on purpose**: you build
  whatever flips that switch (a key, a button, a hand gesture, a world's logic) inside Resonite.
- **Private by default.** Off until you pick a mode. It stops while you are muted, silenced or whispering.

```
you speak ─► microphone ─► [ mod: voice detection ─► Whisper ─► text ] ─► User/STT.Text  ─► your ProtoFlux, avatar, captions, ...
                                                                         User/STT.Partial
          User/STT.Trigger (Triggered mode) ─► opens / closes the microphone gate
```

**Status:** version 1.0.0, developed and tested on Resonite build 2026.11.10.1262 with ResoniteModLoader 5.0.3 on Windows x64.
It has been tested with one user and one microphone, not with VR hardware or a second user: see
[docs/USAGE.md](docs/USAGE.md#what-has-and-hasnt-been-tested).

## Quick start

1. Make sure ResoniteModLoader works in your Resonite.
2. Close Resonite and copy the contents of the release zip into your Resonite folder, keeping its structure:
   ```
   rml_mods\SpeechToText.dll
   rml_libs\SpeechToText.Core.dll              <- must be directly in rml_libs
   rml_libs\SpeechToText\...                   <- Whisper.net, its runtime, the CPU speech library
   ```
3. Start Resonite and set **Mode** in the mod's settings (ResoniteModSettings shows them in the dash).
4. The first time, the mod downloads the speech model once (about 150 MB) plus a 1 MB voice detector, with notifications.
   Nothing is downloaded and the microphone is not touched while the mode is **Off**.

Step-by-step instructions: **[docs/INSTALL.md](docs/INSTALL.md)**. To uninstall, delete those files. How to read the text in-game, the ProtoFlux recipes for the trigger switch and every setting
are in **[docs/USAGE.md](docs/USAGE.md)**. The design, with every measurement and verification, is in [SPEC.md](SPEC.md).

## How it works

### The pipeline

```
 engine audio thread          audio worker thread (BelowNormal)             recognizer thread           Resonite world thread
┌────────────────────┐       ┌──────────────────────────────────┐        ┌────────────────────┐       ┌─────────────────────┐
│ AudioTap           │ ring  │ 20 ms frames ─► VoiceDetector    │ events │ Transcriber        │ text  │ Publisher           │
│ microphone samples ├──────►│              ─► Segmenter        ├───────►│ Whisper, one clip  ├──────►│ User/STT.* variables│
│ mono, 16 kHz       │buffer │ (pre-roll, silence, post-roll)   │        │ at a time          │       │ on your user root   │
└────────────────────┘       └────────────────▲─────────────────┘        └────────────────────┘       └──────────┬──────────┘
                                              │ gate open / closed                                                │
                                              └───────────────── Controller ◄───── User/STT.Trigger ◄─────────────┘
```

1. **Hooking the microphone (`AudioTap`).** The mod subscribes to the default input's `NewFilteredSamples` event: the same
   noise-suppressed stream Resonite's own voice chat uses. The callback runs on the engine's audio thread, so it only downmixes
   to mono, resamples from the engine rate (normally 48 kHz) to the 16 kHz Whisper needs, and writes into a lock-free ring buffer.
   It follows the default device if you change it.
2. **Voice detection and segmentation (`AudioPipeline`, `Segmenter`).** A worker thread cuts the audio into 20 ms frames and asks
   a voice detector whether anyone is speaking: [Silero VAD](https://github.com/snakers4/silero-vad) (run through Whisper.net) by
   default, or a simple energy detector as a fallback. A state machine turns that into utterances: it starts with 0.3 s of
   pre-roll so the first word isn't clipped, ends after 0.7 s of silence (Continuous mode) or shortly after the trigger closes
   (Triggered mode), splits anything longer than 15 s, and asks for a *partial* result every second so captions can update live.
3. **Recognition (`Transcriber`, `WhisperEngine`).** One thread runs Whisper on one clip at a time. Finished utterances are never
   dropped and come out in order; a partial that is still running when its utterance ends is cancelled. Two things make it
   fast and safe enough for a game: the audio window is sized to the clip (Whisper normally encodes a fixed 30 s window, which
   costs about 1.8 s even for a short sentence; sizing it brought a 3 s sentence from 1.8 s to 0.3 s), and the decoder has a token
   cap and a deadline, because humming, singing and tones can otherwise make it loop for 10+ s and freeze the game.
4. **Cleaning the text (`TextFilter`).** Whisper's sound labels (`[music]`, `(humming)`), the phrases it invents on near-silence
   ("Thank you."), endless repetition and (optionally) profanity are filtered, and the length is capped.
5. **Publishing (`Publisher`).** In the world you are in, a child slot `SpeechToText` under your user root holds one dynamic
   variable per value (`User/STT.Text`, `Partial`, `Sequence`, ...). Writes are made on the world's own thread, the slot is
   recreated if your avatar respawns, and it is removed again when the mode is Off.
6. **The trigger.** In Triggered mode the gate in step 2 is `User/STT.Trigger`. The mod subscribes to that variable's change event,
   so the gate follows a write in the same frame. By default it accepts only changes made by you: Resonite records who last
   changed a synced value, and a write by anyone else is ignored and reverted.

### What starts and stops it

`SttController` watches the **Mode** setting. Off does nothing. Picking Continuous or Triggered loads the models (downloading
them the first time, verified by size and SHA-256), builds the pipeline above and shows a notification; changing a setting that
affects recognition rebuilds it, and Off tears everything down. Muting, being silenced by a host, the Mute and Whisper voice modes,
and having no focused world all pause transcription, because the text goes to everyone in the session.

### How it is loaded

ResoniteModLoader loads `SpeechToText.dll` from `rml_mods` and everything in `rml_libs` early. The mod's settings use types from
`SpeechToText.Core.dll`, which is why that file must sit directly in `rml_libs`. The libraries that are only needed once speech
starts (Whisper.net and friends) live in `rml_libs\SpeechToText` and are found by a small resolver; the mod points Whisper.net at
the native `whisper.dll` in `runtimes\win-x64` there.

## Dependencies

**To run the mod**

| What | Version | Needed for | Licence |
|---|---|---|---|
| Resonite | tested on build 2026.11.10.1262 | the game; the mod uses its audio, user and dynamic-variable APIs | proprietary |
| [ResoniteModLoader](https://github.com/resonite-modding-group/ResoniteModLoader) | tested on 5.0.3 | loads the mod | see its repository |
| Windows x64, a microphone | | the speech library shipped is the Windows CPU build | |
| ResoniteModSettings (a separate mod) | any | *optional*: shows the mod's settings in the dash (otherwise edit the RML config file) | |

**Bundled in the release zip** (nothing else is needed)

| What | Version | Role | Licence |
|---|---|---|---|
| [Whisper.net](https://github.com/sandrohanea/whisper.net) | 1.9.1 | .NET bindings for whisper.cpp; also runs the voice detector | MIT |
| Whisper.net.Runtime | 1.9.1 | the native `whisper.dll` / `ggml-*.dll` CPU libraries (whisper.cpp) | MIT |
| Microsoft.Extensions.AI.Abstractions | 10.2.0 | a dependency of Whisper.net | MIT |

**Downloaded on first use** (not in the zip, not in this repository), from Hugging Face into `rml_libs\SpeechToText\models`

| File | Size | Source | Role |
|---|---|---|---|
| `ggml-base.en.bin` | 148 MB | `ggerganov/whisper.cpp` | the default speech model (other models are selectable) |
| `ggml-silero-v5.1.2.bin` | 0.9 MB | `ggml-org/whisper-vad` | the voice detector |

whisper.cpp, the OpenAI Whisper model files and Silero VAD are described by their projects as MIT; I confirmed that only through
secondary sources, so check upstream before redistributing the models.

**To build and test**

- The .NET 10 SDK. The speech pipeline in `core/` and its tests need nothing else.
- Building the **mod** (`src/`) also needs a Resonite install: it references `FrooxEngine.dll`, `Elements.Core.dll`,
  `Elements.Assets.dll` (in the game folder) and `Libraries\ResoniteModLoader.dll`. Those are not in this repository and must not
  be redistributed.
- Tests: xunit 2.9.2, Microsoft.NET.Test.Sdk 17.11.1, xunit.runner.visualstudio 2.8.2. The test speech was generated with the
  Windows text-to-speech voice (`tools/make-fixtures.ps1`; the generated files are committed).

## Building

```
dotnet test tests/Core.Tests            # about 160 tests, about 1 minute; no game and no models required
dotnet build src -c Release             # the mod; needs Resonite (see below)
powershell tools/package.ps1            # builds dist\SpeechToText-<version>.zip
```

Tell the build where Resonite is with `-p:ResonitePath="..."`, a git-ignored `Local.props` (copy `Local.props.example`), or the
`RESONITE_PATH` environment variable; otherwise it looks in the default Steam location. `dotnet build src -p:InstallMod=true`
installs into that Resonite folder (close the game first) and `tools/redeploy.ps1` also restarts the game and relaunches it.

The GitHub workflow in `.github/workflows` runs the `core/` tests only (the mod needs Resonite's DLLs, which cannot be on a
build server). Tests that use the real models skip themselves unless `models\ggml-base.en.bin` and `models\ggml-silero-v5.1.2.bin` exist;
`STT_NETWORK_TESTS=1` also runs the test that downloads from Hugging Face.

## Repository layout

| Path | What is there |
|---|---|
| `core/` | the speech pipeline (`SpeechToText.Core`): ring buffer, resampler, voice detectors, segmenter, transcriber, Whisper engine, model download, text filter, trigger policy. No Resonite dependency. |
| `src/` | the mod: settings, controller, microphone hook, variable publisher, notifications |
| `tests/` | unit and integration tests for `core/`, and the speech fixtures |
| `recipes/` | `SpeechToText-Recipes.resonitepackage`: four ready-made ProtoFlux trigger graphs |
| `docs/USAGE.md` | variables, recipes, settings, privacy, troubleshooting |
| `SPEC.md` | the design and its history, with measurements |
| `tools/` | packaging, redeploy and fixture-generation scripts |
| `CLAUDE.md` | notes for development with Claude Code |

## License

No licence has been chosen for this repository yet, which means that by default nobody else may reuse the code. Add a `LICENSE`
file before publishing if you want others to be able to.
