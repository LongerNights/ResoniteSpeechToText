# Using SpeechToText

How to use the mod once it is installed: the variables it publishes, the two modes, ProtoFlux recipes for the
trigger switch, every setting, privacy behaviour, performance and troubleshooting. For what the mod is and how it works inside, see
the [README](../README.md).

## The variables

Everything lives on your **user root**, in the stock `User` variable space, in a child slot called `SpeechToText`.
Variable names must include the `User/` prefix.

| Variable | Type | What it is |
|---|---|---|
| `User/STT.Text` | string | The last finished sentence. Not cleared afterwards. |
| `User/STT.Partial` | string | Live guess while you are still speaking; empty when idle. |
| `User/STT.Sequence` | int | Goes up by one per finished sentence. React to changes of this, so repeating the same words still counts. |
| `User/STT.Listening` | bool | The truthful "I am transcribing right now" light (gate open **and** not muted/silenced/whispering/unfocused). |
| `User/STT.Speaking` | bool | The voice detector hears speech right now. |
| `User/STT.Mode` | string | `Off`, `Continuous` or `Triggered`. |
| `User/STT.Language` | string | Language code of the last sentence (for example `en`). |
| `User/STT.Trigger` | bool | **Your switch.** In Triggered mode: `true` = listen, `false` = stop. Ignored in Continuous mode. |

**Dynamic impulse:** after each finished sentence is written, the mod sends the dynamic impulse tag **`STT.Text`** to your user
root (and everything under it), one update after all the variables above are written. Put a `DynamicImpulseReceiver` with that tag
anywhere under your user root and read `User/STT.Text` in its flow. It fires at most once per publish tick (about 4 times a
second), so two sentences finished within the same tick give one impulse; `STT.Sequence` still counts both.

Everyone in the session, with or without the mod, can read these variables. In ProtoFlux, read text with
`ReadDynamicObjectVariable<string>` (or `DynamicVariableObjectInput<string>`), numbers and booleans with the `Value` versions.
The variables only exist in the world you are focused on (unless you turn on "Publish in every world"), and they disappear
when the mode is Off.

## Triggered mode: you build the switch

`User/STT.Trigger` is just a boolean, so whatever can write a dynamic variable can control it. A write takes effect in the
same frame, and the first words are not clipped (the mod keeps the last 0.3 s before the switch). Rules to know:

- Use the **Local** `FireOn…` nodes (`FireOnLocalTrue`, `FireOnLocalValueChange`): input devices belong to *you*, and a write by
  you is what the mod accepts by default (see "Who may flip the switch").
- Don't *drive* the variable (a drive makes it read-only for the mod, so its own safety writes silently fail). *Write* it.
- Hold-to-talk or toggle-to-talk is your choice: it is how you build the graph.
- Muting, whispering and the other safety rules always win over the switch. `User/STT.Listening` shows the real state.

A ready-made package with the four graphs below is in the repository and the release zip:
**`SpeechToText-Recipes.resonitepackage`** (5 KB). Drag it into Resonite or use the import dialog. It adds one slot,
*Speech to Text Recipes*, with one child per recipe. Use only the ones you want and delete the rest.

All node names below are the real ProtoFlux node names. `LocalUserSlot` is a node that gives *your user root slot*, which is
what the Write/Read nodes take as their slot input.

**1. Hold a key to talk** (`Hold F8 to talk`)

`KeyHeld` (Key = F8) feeds `FireOnLocalValueChange<bool>` (Value). Its `OnChange` impulse goes into
`WriteDynamicValueVariable<bool>` with `Target` = `LocalUserSlot`, `Path` = `User/STT.Trigger`, and `Value` = the same
`KeyHeld` output. Press F8 and you listen, release and you stop.

**2. Tap a key to switch on and off** (`Tap F9 to toggle`)

`KeyPressed` (Key = F9) feeds `FireOnLocalTrue` (Condition). Its `OnChange` goes into `WriteDynamicValueVariable<bool>`
(`Target` = `LocalUserSlot`, `Path` = `User/STT.Trigger`) whose `Value` is `NOT_Bool` of
`ReadDynamicValueVariable<bool>` (`Source` = `LocalUserSlot`, `Path` = `User/STT.Trigger`).

**3. Hand to mouth** (`Hand to mouth`)

`LocalUserRoot` feeds `HeadPosition` and `RightHandPosition`; `Distance_Float3` measures between them. Two comparisons make
a hysteresis band so it doesn't flicker: `ValueLessThan<float>` (B = 0.18) goes to a `FireOnLocalTrue` that writes `true`,
and `ValueGreaterThan<float>` (B = 0.26) goes to another `FireOnLocalTrue` that writes `false`. Between the two numbers
nothing changes. The distances are in world units, so if you scale your avatar, scale the numbers (a `UserRootGlobalScale`
node exists). 0.18 and 0.26 are starting values: watch the `Distance_Float3` output in VR and tune them. For both hands,
add `LeftHandPosition` and take the smaller distance.

**4. Voice command** (`Voice command (connect If > OnTrue)`)

`ReadDynamicValueVariable<int>` (`Source` = `LocalUserSlot`, `Path` = `User/STT.Sequence`) feeds
`FireOnLocalValueChange<int>`. Its `OnChange` goes into an `If` whose `Condition` is `Contains` (`Str` =
`ReadDynamicObjectVariable<string>` of `User/STT.Text`, `Substring` = your phrase). **Connect the `If`'s `OnTrue` to whatever
you want to happen**; the shipped graph leaves it unconnected so it can't do anything by surprise. Whisper capitalises the
start of sentences and I did not test whether `Contains` is case-sensitive, so route the text through a `ToLower` node first
and use a lowercase phrase.

**5. Caption** (not in the package: it needs a text renderer, which would bundle a 19 MB font)

Add a slot under your avatar (for example under your head slot, so `User/` can be found and the caption follows you) with a
`TextRenderer`, then a `DynamicValueVariableDriver<string>` with `VariableName` = `User/STT.Text` and `Target` = the
renderer's `Text`. A second driver on a second renderer with `User/STT.Partial` gives live captions. Make it look as you like.

Reading *someone else's* captions works the same way with that user's root slot as the `Source`, but only reading your own
has been tested.

### Who may flip the switch

Resonite remembers who last changed a synced value. With **Who may flip User/STT.Trigger** = *Only me* (the default), a
change made by another user is ignored and set back to `false`, with a notification, so nobody else can switch your
transcription on. A world's own ProtoFlux often runs on the host, so under *Only me* it cannot drive your switch: choose
*Anyone* if you deliberately want a world or another user to control it.

## Settings

Settings are RML configuration keys (ResoniteModSettings shows them in the dash).

| Setting | Default | Meaning |
|---|---|---|
| Mode | Off | Off, Continuous, or Triggered. |
| Publish in every world | off | By default only the world you are in. |
| Publish partial text while speaking | on | The live `Partial` variable. |
| Partial updates per second | 4 | Limit for partial updates. |
| Max characters per result | 512 | Longer text is cut at a word boundary with `…`. |
| Mask profanity | off | Replaces letters after the first with `*`. |
| Who may flip User/STT.Trigger | Only me | See above. |
| Switch the trigger off after N seconds | 0 | A safety net for forgetful toggles; 0 = never. |
| Model | Base English | `base.en` is the tested one. Others: tiny, small, multilingual base/small, large-v3-turbo. |
| Language | auto | A code like `en`, or `auto` with a multilingual model. English-only models always use English. |
| Translate to English | off | Whisper's translate task (multilingual models). |
| Backend | Cpu | This release ships only the CPU speech library. |
| CPU threads | 4 | Fewer threads = less impact on frame rate, slower recognition. |
| Voice activity detector | Silero | Or a simpler energy-based one. |
| Silence that ends an utterance | 700 ms | Continuous mode. |
| Longest utterance | 15 s | Longer speech is split. |
| Log per-utterance timings | off | Writes timings and **the recognised text** to the log. Keep off normally. |

Settings apply immediately. Changing one that affects recognition (mode, model, language, threads, detector, silence, utterance
length, partial text) restarts the speech engine by itself, which takes about a second.

## Privacy and safety

- Audio is processed in memory on your PC and is never sent anywhere or written to disk. The only network use is the one-time
  model download.
- The **text** is published in your user-root variables, so **everyone in the session can read it**. A notification explains
  this the first time you turn the mod on.
- Nothing is transcribed while you are **muted**, **silenced by the host**, in the **Mute** or **Whisper** voice mode (a whisper
  is only heard nearby, but text would go to everyone), recording a voice message, or when no world is focused.
- `User/STT.Listening` is the honest indicator of whether you are being transcribed right now.

## Performance

Measured on one desktop (CPU only, `base.en`, 4 threads) with the FrameBudget mod:

- Idle (Continuous mode, not speaking): no measurable change in frame time (the voice detector is about 5% of one core).
- Recognition running non-stop (about 8 times denser than real speech): frame-time 90th percentile 5.2 ms against 3.1 ms
  without the mod, about 20% fewer frames per second; with 2 threads, 4.2 ms.
- Sentences up to about 4 s are published 0.2–0.4 s after you stop (plus the 0.7 s silence wait in Continuous mode); a 10 s
  utterance takes about 1.3–2.2 s to appear.
- Singing, humming and steady tones can make speech recognition spin; the mod caps and times out such runs, but those sounds
  produce little useful text.

## Troubleshooting

- *Nothing happens:* is **Mode** Off? Are you muted, or in Whisper/Mute voice mode? In Triggered mode is `User/STT.Trigger`
  true? Check `User/STT.Listening` and `User/STT.Mode`.
- *"Could not start":* the notification and the log say why (for example the model download failed). It retries after 30 s or
  when you change a setting.
- *Mods don't load at all:* that is ResoniteModLoader, not this mod; make sure RML matches your Resonite build.
- *Reading the log:* lines are tagged `[pipeline]`, `[audio]`, `[segment]`, `[stt]`, `[trigger]`, `[publisher]`; the mod's own
  name is not always shown, so search by those tags.

## What has and hasn't been tested

Tested in the game: Continuous and Triggered mode with real speech, the notifications, mute and Whisper-mode suppression,
auto-off, the four package recipes (the key recipes with real key presses), frame-time cost, worlds switching and respawning,
and the first-run model download.

**Not tested:** other models than `base.en`, non-English speech, the GPU backends (not shipped), a second user in the session
(so: another user's write being rejected, and reading someone else's captions), VR hardware, and switching the default
microphone while running. Everything was developed and tested on Resonite build 2026.11.10.1262 with ResoniteModLoader 5.0.3.
