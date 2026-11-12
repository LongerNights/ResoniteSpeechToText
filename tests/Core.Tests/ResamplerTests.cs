namespace SpeechToText.Tests;

public class ResamplerTests
{
    private static float[] Run(Resampler r, float[] input, int chunk = int.MaxValue)
    {
        var output = new List<float>();
        for (int i = 0; i < input.Length; i += chunk)
        {
            int n = Math.Min(chunk, input.Length - i);
            var tmp = new float[r.MaxOutput(n)];
            int o = r.Process(input.AsSpan(i, n), tmp);
            output.AddRange(tmp.AsSpan(0, o).ToArray());
        }
        return output.ToArray();
    }

    [Fact]
    public void SameRateIsPassThrough()
    {
        var input = Audio.Sine(440, 0.1, 0.5);
        Assert.Equal(input, Run(new Resampler(16000, 16000), input));
    }

    [Theory]
    [InlineData(48000, 16000)]
    [InlineData(44100, 16000)]
    [InlineData(8000, 16000)]
    [InlineData(96000, 16000)]
    public void OutputLengthMatchesRatio(int inRate, int outRate)
    {
        var input = Audio.Sine(300, 2.0, 0.5, inRate);
        var output = Run(new Resampler(inRate, outRate), input);
        double expected = input.Length * (double)outRate / inRate;
        // The filter's look-ahead holds back a few samples at the end.
        Assert.InRange(output.Length, expected - 40, expected + 2);
    }

    [Theory]
    [InlineData(48000)]
    [InlineData(44100)]
    public void InBandToneKeepsItsAmplitude(int inRate)
    {
        var r = new Resampler(inRate, 16000);
        var output = Run(r, Audio.Sine(1000, 1.0, 0.5, inRate));
        var settled = output.AsSpan(1000, output.Length - 2000); // skip filter start/end transients
        Assert.InRange(Audio.RmsDb(settled), -9.2, -8.8); // 0.5 amplitude sine = -9.03 dBFS RMS
    }

    [Fact]
    public void ToneAbove8kHzIsFilteredNotAliased()
    {
        // 12 kHz would fold down to 4 kHz at 16 kHz output if the anti-alias filter were missing.
        var output = Run(new Resampler(48000, 16000), Audio.Sine(12000, 1.0, 0.5, 48000));
        double db = Audio.RmsDb(output.AsSpan(1000, output.Length - 2000));
        Assert.True(db < -9.03 - 40, $"12 kHz tone only attenuated to {db:F1} dBFS");
    }

    [Fact]
    public void DcPassesAtUnityGain()
    {
        var input = Enumerable.Repeat(0.25f, 48000).ToArray();
        var output = Run(new Resampler(48000, 16000), input);
        Assert.All(output.Skip(500).Take(output.Length - 1000), v => Assert.InRange(v, 0.2495f, 0.2505f));
    }

    [Theory]
    [InlineData(48000)]
    [InlineData(44100)]
    public void ChunkingDoesNotChangeTheOutput(int inRate)
    {
        var input = Audio.Concat(Audio.Sine(500, 0.5, 0.4, inRate), Audio.Sine(3000, 0.5, 0.3, inRate));
        var whole = Run(new Resampler(inRate, 16000), input);

        var rnd = new Random(3);
        var r = new Resampler(inRate, 16000);
        var pieces = new List<float>();
        for (int i = 0; i < input.Length;)
        {
            int n = Math.Min(rnd.Next(1, 1500), input.Length - i); // includes tiny chunks smaller than the filter
            var tmp = new float[r.MaxOutput(n)];
            int o = r.Process(input.AsSpan(i, n), tmp);
            pieces.AddRange(tmp.AsSpan(0, o).ToArray());
            i += n;
        }
        Assert.Equal(whole.Length, pieces.Count);
        for (int i = 0; i < whole.Length; i++) Assert.True(Math.Abs(whole[i] - pieces[i]) < 1e-5f, $"sample {i} differs");
    }

    [Fact]
    public void TooSmallOutputBufferIsRejected()
    {
        var r = new Resampler(48000, 16000);
        Assert.Throws<ArgumentException>(() => r.Process(new float[4800], new float[10]));
    }
}
