# Installing SpeechToText

SpeechToText is a [ResoniteModLoader](https://github.com/resonite-modding-group/ResoniteModLoader) (RML) mod for Windows x64.
Recognition runs on your own CPU; nothing is sent to a cloud service.

## Requirements

- Resonite with ResoniteModLoader working (standalone RML 5.0.x; the game's launch option must include
  `-LoadAssembly Libraries/ResoniteModLoader.dll`). Tested on Resonite 2026.11.10.1262 with RML 5.0.3.
- Windows x64 and a microphone.
- About 150 MB of free disk space for the speech model, and an internet connection the first time (see step 4).
- Optional: the ResoniteModSettings mod, which shows this mod's settings in the dash.

## Install

1. **Close Resonite.**
2. Download `SpeechToText-<version>.zip` from the [Releases page](../../releases) and unzip it.
3. Copy the `rml_mods` and `rml_libs` folders from the zip into your Resonite folder (for Steam usually
   `C:\Program Files (x86)\Steam\steamapps\common\Resonite`), merging with the existing folders. You should end up with:
   ```
   Resonite\rml_mods\SpeechToText.dll
   Resonite\rml_libs\SpeechToText.Core.dll        <- must be directly in rml_libs
   Resonite\rml_libs\SpeechToText\...             <- Whisper.net, its dependency and the native CPU library
   ```
   Copying only `SpeechToText.dll` is **not** enough: the mod needs the libraries too.
4. Start Resonite. The mod does nothing and downloads nothing while its **Mode** is **Off** (the default).
5. Open the mod's settings (ResoniteModSettings, or the RML config file) and set **Mode** to **Continuous** or **Triggered**.
   The first time, the mod downloads the speech model (`ggml-base.en.bin`, about 150 MB) and a 1 MB voice detector from
   Hugging Face into `rml_libs\SpeechToText\models`, with notifications. After that it works offline.

## Check that it works

- Set Mode to **Continuous**, wait for the "ready" notification, and say a sentence.
- On your user root there is now a child slot `SpeechToText` holding the dynamic variables `User/STT.Text`, `User/STT.Partial`
  and so on. After each finished sentence the mod also sends the dynamic impulse `STT.Text` to your user root.
- Reading the text, the trigger switch and all settings: see [USAGE.md](USAGE.md). A package of ready-made ProtoFlux trigger
  graphs (`SpeechToText-Recipes.resonitepackage`) is in the zip.

## Uninstall

Close Resonite and delete `rml_mods\SpeechToText.dll`, `rml_libs\SpeechToText.Core.dll` and the `rml_libs\SpeechToText` folder.

## Troubleshooting

- **Nothing happens / no settings:** check the RML log for "SpeechToText" lines and that `SpeechToText.Core.dll` is directly in
  `rml_libs`. Make sure Mode is not Off.
- **Model download fails:** check your connection; the mod retries after 30 s. You can also place the two model files in
  `rml_libs\SpeechToText\models` by hand.
- **No text while you speak:** the mod stays silent when you are muted, silenced, in Mute or Whisper voice mode, or when no world is
  focused.
More in [USAGE.md](USAGE.md#troubleshooting).
