namespace SpeechToText.Tests;

public class EnergyVadTests
{
    /// <summary>Runs the VAD over audio and returns the speech flag after each 20 ms frame.</summary>
    private static bool[] Flags(IVad vad, float[] audio)
    {
        var flags = new List<bool>();
        for (int i = 0; i + Segmenter.FrameSamples <= audio.Length; i += Segmenter.FrameSamples)
        {
            vad.Push(audio.AsSpan(i, Segmenter.FrameSamples));
            flags.Add(vad.IsSpeech);
        }
        return flags.ToArray();
    }

    [Fact]
    public void DigitalSilenceIsNeverSpeech() =>
        Assert.DoesNotContain(true, Flags(new EnergyVad(), Audio.Silence(10)));

    [Fact]
    public void FaintHissIsNeverSpeech() =>
        Assert.DoesNotContain(true, Flags(new EnergyVad(), Audio.Noise(10, -70)));

    [Fact]
    public void LoudToneBecomesSpeechAfterThreeFramesAndEndsAfterTheHangover()
    {
        var audio = Audio.Concat(Audio.Silence(1), Audio.Sine(300, 1, 0.3), Audio.Silence(1));
        var flags = Flags(new EnergyVad(), audio);

        int firstSpeech = Array.IndexOf(flags, true);
        Assert.Equal(50 + 2, firstSpeech); // tone starts at frame 50; the third loud frame trips it
        int lastSpeech = Array.LastIndexOf(flags, true);
        // Tone ends at frame 100; 15 hangover frames keep it on.
        Assert.InRange(lastSpeech, 100 + 13, 100 + 16);
        Assert.False(flags[^1]);
    }

    [Fact]
    public void ABriefClickShorterThanThreeFramesIsIgnored()
    {
        var audio = Audio.Concat(Audio.Silence(1), Audio.Sine(300, 0.04, 0.5), Audio.Silence(1)); // 2 frames
        Assert.DoesNotContain(true, Flags(new EnergyVad(), audio));
    }

    [Fact]
    public void AdaptsToASteadyNoiseFloorButStillHearsSpeechAboveIt()
    {
        // 8 s of steady -40 dBFS hiss, then a -20 dBFS tone burst on top (20 dB above the floor).
        var hiss = Audio.Noise(8, -40, 11);
        var burst = Audio.Sine(250, 1.0, 0.1414); // -23 dBFS RMS sine
        var tail = Audio.Noise(1, -40, 12);
        var burstOnHiss = new float[burst.Length];
        var hiss2 = Audio.Noise(1, -40, 13);
        for (int i = 0; i < burst.Length; i++) burstOnHiss[i] = burst[i] + hiss2[i];

        var vad = new EnergyVad();
        var flags = Flags(vad, Audio.Concat(hiss, burstOnHiss, tail));
        int hissFrames = 8 * 50;
        // After the 5 s window has seen only hiss, the hiss alone must read as silence...
        Assert.DoesNotContain(true, flags[(hissFrames - 100)..hissFrames]);
        Assert.InRange(vad.NoiseFloorDb, -43, -37);
        // ...and the burst must be detected.
        Assert.Contains(true, flags[hissFrames..(hissFrames + 50)]);
    }

    [Fact]
    public void ResetClearsState()
    {
        var vad = new EnergyVad();
        Flags(vad, Audio.Sine(300, 1, 0.3));
        Assert.True(vad.IsSpeech);
        vad.Reset();
        Assert.False(vad.IsSpeech);
    }

    [Fact]
    public void FindsTheSpeechInARealRecordingAndNotTheSilenceAroundIt()
    {
        var speech = Audio.ReadWav(Repo.Fixture("hello.wav"));
        var audio = Audio.Concat(Audio.Silence(1), speech, Audio.Silence(1.5));
        var flags = Flags(new EnergyVad(), audio);

        int speechStart = 50, speechEnd = 50 + speech.Length / Segmenter.FrameSamples;
        int hitsInside = flags[speechStart..speechEnd].Count(f => f);
        Assert.True(hitsInside > (speechEnd - speechStart) * 0.6, $"only {hitsInside}/{speechEnd - speechStart} speech frames detected");
        Assert.DoesNotContain(true, flags[..(speechStart - 3)]);
        Assert.DoesNotContain(true, flags[(speechEnd + 20)..]); // well after the 300 ms hangover
    }
}
