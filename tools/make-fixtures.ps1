# Regenerates the speech fixtures in tests\fixtures with the Windows TTS voice (16 kHz mono 16-bit PCM).
# hello.wav / short.wav / monologue.wav. Noise and silence fixtures are synthesised inside the tests.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Speech
$out = Join-Path (Split-Path -Parent $PSScriptRoot) 'tests\fixtures'
New-Item -ItemType Directory -Force $out | Out-Null

function New-Wav([string]$name, [string]$ssml) {
    $s = New-Object System.Speech.Synthesis.SpeechSynthesizer
    $fmt = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo(16000, [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen, [System.Speech.AudioFormat.AudioChannel]::Mono)
    $s.SetOutputToWaveFile((Join-Path $out $name), $fmt)
    $s.SpeakSsml($ssml)
    $s.Dispose()
}
function Ssml([string]$body) { "<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='en-US'>$body</speak>" }

New-Wav 'hello.wav' (Ssml 'Hello Resonite. This is a test of local speech to text.')
New-Wav 'short.wav' (Ssml 'Hello there.')
New-Wav 'monologue.wav' (Ssml ('The quick brown fox jumps over the lazy dog.<break time="1500ms"/>' +
    'Resonite lets everyone build together in virtual reality.<break time="1500ms"/>' +
    'This is a longer sentence that should be transcribed correctly by the local speech recognition model without any trouble.'))
# Awkward inputs for the engine-hardening tests: repetition and hum are what make Whisper's decoder loop.
New-Wav 'stutter.wav' (Ssml 'I I I I I I I I I I I I.')
New-Wav 'hum.wav' (Ssml 'Mmm mmm mmm mmm mmm mmm mmm mmm mmm.')
New-Wav 'la.wav' (Ssml 'La la la la la la la la la la la la la la la.')
New-Wav 'oh.wav' (Ssml 'Oh oh oh oh oh oh oh oh oh oh oh oh.')
Get-ChildItem $out -Filter *.wav | Select-Object Name, Length
