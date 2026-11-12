namespace SpeechToText.Tests;

/// <summary>
/// Frame-exact tests of the SPEC §6.1 state machine. Every frame is filled with (index + 1) so a test can see exactly
/// which frames ended up in a segment (pre-roll, trailing silence, post-roll) without any real audio.
/// Defaults: pre-roll 15 frames (300 ms), post-roll 10 (200 ms), silence 35 (700 ms), min speech 15 (300 ms).
/// </summary>
public class SegmenterTests
{
    private const int F = Segmenter.FrameSamples;

    private static (Segmenter Seg, List<SegmentEvent> Events) Make(SegmenterOptions o, Func<int, bool> speech)
    {
        var events = new List<SegmentEvent>();
        return (new Segmenter(o, new ScriptedVad(speech), events.Add), events);
    }

    private static void Run(Segmenter seg, int frames, Func<int, bool>? gate = null, Func<int, bool>? suppressed = null, int from = 0)
    {
        var frame = new float[F];
        for (int i = from; i < from + frames; i++)
        {
            Array.Fill(frame, i + 1f);
            seg.Push(frame, gate?.Invoke(i) ?? true, suppressed?.Invoke(i) ?? false);
        }
    }

    private static int FirstFrame(SegmentEvent e) => (int)e.Samples[0] - 1;
    private static int LastFrame(SegmentEvent e) => (int)e.Samples[^F] - 1;
    private static int FrameCount(SegmentEvent e) => e.Samples.Length / F;
    private static bool In(int i, int start, int count) => i >= start && i < start + count;

    // ---- Continuous ------------------------------------------------------------------------------------------------

    [Fact]
    public void Continuous_OneUtterance_IncludesPreRollAndTrimsTrailingSilenceToPostRoll()
    {
        var (seg, ev) = Make(new SegmenterOptions { PublishPartials = false }, i => In(i, 50, 50)); // 1 s of speech from frame 50
        Run(seg, 200);

        var final = Assert.Single(ev);
        Assert.Equal(SegmentEventKind.Final, final.Kind);
        Assert.Equal(35, FirstFrame(final));          // 15 frames of pre-roll before the VAD fired at 50
        Assert.Equal(109, LastFrame(final));          // speech ends at frame 99, plus 10 frames of post-roll (not the full 700 ms of silence)
        Assert.Equal(75, FrameCount(final));          // 300 + 1000 + 200 ms
        Assert.Equal(1000, final.VoicedMs);
        Assert.False(seg.InSegment);
    }

    [Fact]
    public void Continuous_ABlipTooShortToBeSpeechIsDiscarded()
    {
        var (seg, ev) = Make(new SegmenterOptions { PublishPartials = false }, i => In(i, 50, 5)); // 100 ms of speech
        Run(seg, 200);
        var e = Assert.Single(ev);
        Assert.Equal(SegmentEventKind.Discarded, e.Kind);
        Assert.Empty(e.Samples);
    }

    [Fact]
    public void Continuous_TwoUtterancesFarApartAreTwoSegments()
    {
        var (seg, ev) = Make(new SegmenterOptions { PublishPartials = false }, i => In(i, 50, 50) || In(i, 250, 50));
        Run(seg, 400);
        var finals = ev.Where(e => e.Kind == SegmentEventKind.Final).ToList();
        Assert.Equal(2, finals.Count);
        Assert.Equal(35, FirstFrame(finals[0]));
        Assert.Equal(235, FirstFrame(finals[1])); // second utterance gets its own pre-roll, no leftover audio from the first
    }

    [Fact]
    public void Continuous_AShortPauseDoesNotSplitAnUtterance()
    {
        // 300 ms gap is well inside the 700 ms silence window.
        var (seg, ev) = Make(new SegmenterOptions { PublishPartials = false }, i => In(i, 50, 50) || In(i, 115, 50));
        Run(seg, 300);
        var final = Assert.Single(ev);
        Assert.Equal(SegmentEventKind.Final, final.Kind);
        Assert.Equal(100 * Segmenter.FrameMs, final.VoicedMs);
        Assert.Equal(35, FirstFrame(final));
        Assert.Equal(174, LastFrame(final)); // second burst ends at 164, +10 post-roll
    }

    [Fact]
    public void Continuous_AGapJustOverSilenceMsSplits()
    {
        // 36 silent frames (720 ms) > 35 (700 ms).
        var (seg, ev) = Make(new SegmenterOptions { PublishPartials = false }, i => In(i, 50, 50) || In(i, 136, 50));
        Run(seg, 300);
        Assert.Equal(2, ev.Count(e => e.Kind == SegmentEventKind.Final));
    }

    [Fact]
    public void Continuous_StartingNearTheBeginningUsesWhateverPreRollExists()
    {
        var (seg, ev) = Make(new SegmenterOptions { PublishPartials = false }, i => In(i, 3, 50));
        Run(seg, 150);
        var final = Assert.Single(ev);
        Assert.Equal(0, FirstFrame(final));
    }

    [Fact]
    public void Continuous_LongSpeechIsSplitAtMaxSegmentLengthWithoutLosingOrRepeatingAudio()
    {
        var o = new SegmenterOptions { MaxSegmentSec = 5, PublishPartials = false };
        var (seg, ev) = Make(o, i => i >= 50 && i < 750); // 14 s of continuous speech
        Run(seg, 900);
        var finals = ev.Where(e => e.Kind == SegmentEventKind.Final).ToList();
        Assert.True(finals.Count >= 3, $"expected at least 3 pieces, got {finals.Count}");
        Assert.All(finals, f => Assert.True(FrameCount(f) * Segmenter.FrameMs <= 5000 + Segmenter.FrameMs));
        for (int i = 1; i < finals.Count; i++)
            Assert.Equal(LastFrame(finals[i - 1]) + 1, FirstFrame(finals[i])); // seamless: next piece starts right after the last
    }

    [Fact]
    public void Continuous_PartialsArriveAboutOncePerSecondAndGrow()
    {
        var (seg, ev) = Make(new SegmenterOptions(), i => In(i, 50, 300)); // 6 s of speech
        Run(seg, 500);
        var partials = ev.Where(e => e.Kind == SegmentEventKind.Partial).ToList();
        Assert.InRange(partials.Count, 5, 6);
        for (int i = 1; i < partials.Count; i++)
        {
            Assert.True(partials[i].Samples.Length > partials[i - 1].Samples.Length);
            Assert.Equal(50, FrameCount(partials[i]) - FrameCount(partials[i - 1])); // 1000 ms apart
        }
        Assert.Equal(35, FirstFrame(partials[0])); // every partial covers the utterance from its start
        Assert.Single(ev, e => e.Kind == SegmentEventKind.Final);
    }

    [Fact]
    public void Continuous_PartialsCanBeSwitchedOff()
    {
        var (seg, ev) = Make(new SegmenterOptions { PublishPartials = false }, i => In(i, 50, 300));
        Run(seg, 500);
        Assert.DoesNotContain(ev, e => e.Kind == SegmentEventKind.Partial);
    }

    [Fact]
    public void Continuous_NoPartialUntilEnoughSpeechWasHeard()
    {
        // Interval hits at 1 s but only 200 ms had been voiced: below MinSpeechMs, so no partial yet.
        var (seg, ev) = Make(new SegmenterOptions { PartialIntervalMs = 1000 }, i => In(i, 50, 10) || In(i, 90, 40));
        Run(seg, 120);
        Assert.DoesNotContain(ev, e => e.Kind == SegmentEventKind.Partial && e.VoicedMs < 300);
    }

    [Fact]
    public void Continuous_ClosingTheGateEndsTheUtteranceImmediately()
    {
        var (seg, ev) = Make(new SegmenterOptions { PublishPartials = false }, i => In(i, 50, 100));
        Run(seg, 200, gate: i => i < 100);
        var final = Assert.Single(ev);
        Assert.Equal(SegmentEventKind.Final, final.Kind);
        Assert.Equal(99, LastFrame(final)); // nothing after the gate closed, no post-roll invented
    }

    [Fact]
    public void Continuous_ClosedGateNeverStartsAnUtterance()
    {
        var (seg, ev) = Make(new SegmenterOptions(), i => true);
        Run(seg, 100, gate: _ => false);
        Assert.Empty(ev);
        Assert.False(seg.Speaking);
    }

    // ---- Suppression (muted / silenced / unfocused) ------------------------------------------------------------------

    [Fact]
    public void Suppression_MidUtteranceDiscardsAndForgetsEverything()
    {
        var (seg, ev) = Make(new SegmenterOptions { PublishPartials = false }, i => In(i, 50, 200));
        Run(seg, 150, suppressed: i => i >= 100 && i < 110);
        var discarded = Assert.Single(ev, e => e.Kind == SegmentEventKind.Discarded);
        Assert.Empty(discarded.Samples);
        Assert.DoesNotContain(ev, e => e.Kind == SegmentEventKind.Final);
        Assert.True(seg.InSegment); // speech is still going on after unmuting, so a fresh utterance already started
    }

    [Fact]
    public void Suppression_FreshUtteranceAfterUnmuteContainsNoAudioFromBefore()
    {
        var (seg, ev) = Make(new SegmenterOptions { PublishPartials = false }, i => In(i, 50, 200));
        Run(seg, 400, suppressed: i => i >= 100 && i < 110);
        var final = Assert.Single(ev, e => e.Kind == SegmentEventKind.Final);
        Assert.True(FirstFrame(final) >= 110, $"first frame {FirstFrame(final)} predates the mute");
    }

    [Fact]
    public void Suppression_WhileIdleDropsPreRoll()
    {
        // Speech starts right after a mute ends; audio from during the mute must not appear as pre-roll.
        var (seg, ev) = Make(new SegmenterOptions { PublishPartials = false }, i => In(i, 60, 50));
        Run(seg, 200, suppressed: i => i < 58);
        var final = Assert.Single(ev, e => e.Kind == SegmentEventKind.Final);
        Assert.True(FirstFrame(final) >= 58);
    }

    [Fact]
    public void Speaking_FollowsTheVadWhileTheGateIsOpenAndNotSuppressed()
    {
        var (seg, _) = Make(new SegmenterOptions(), i => In(i, 10, 10));
        var frame = new float[F];
        seg.Push(frame, true, false); Assert.False(seg.Speaking);
        Run(seg, 10, from: 1);
        seg.Push(frame, true, false); // frame 11, speech
        Assert.True(seg.Speaking);
        seg.Push(frame, true, true);  // suppressed
        Assert.False(seg.Speaking);
    }

    // ---- Triggered ---------------------------------------------------------------------------------------------------

    private static SegmenterOptions Triggered(bool partials = false) => new() { Mode = SttMode.Triggered, PublishPartials = partials };

    [Fact]
    public void Triggered_RecordsFromGateOpenWithPreRollUntilPostRollAfterRelease()
    {
        var (seg, ev) = Make(Triggered(), i => In(i, 60, 80)); // VAD hears 1.6 s of speech inside a 2 s hold
        Run(seg, 250, gate: i => In(i, 50, 100));            // gate held frames 50..149

        var final = Assert.Single(ev);
        Assert.Equal(SegmentEventKind.Final, final.Kind);
        Assert.Equal(35, FirstFrame(final));                 // pre-roll before the gate opened
        Assert.InRange(LastFrame(final), 159, 160);          // release at 150 + ~200 ms post-roll
        Assert.Equal(80 * Segmenter.FrameMs, final.VoicedMs);
    }

    [Fact]
    public void Triggered_VadSilenceDoesNotEndTheRecordingWhileTheGateIsHeld()
    {
        var (seg, ev) = Make(Triggered(), i => In(i, 50, 25)); // speaks for 0.5 s then goes quiet, but holds for 6 s
        Run(seg, 400, gate: i => In(i, 50, 300));
        var final = Assert.Single(ev);
        Assert.Equal(SegmentEventKind.Final, final.Kind);
        Assert.True(FrameCount(final) >= 15 + 300, $"only {FrameCount(final)} frames");
    }

    [Fact]
    public void Triggered_HoldingWithoutSayingAnythingIsDiscarded()
    {
        var (seg, ev) = Make(Triggered(), _ => false);
        Run(seg, 200, gate: i => In(i, 50, 100));
        var e = Assert.Single(ev);
        Assert.Equal(SegmentEventKind.Discarded, e.Kind);
    }

    [Fact]
    public void Triggered_ReopeningDuringPostRollContinuesTheSameUtterance()
    {
        var (seg, ev) = Make(Triggered(), i => In(i, 50, 100));
        // Held 50..99, released for 5 frames (< 10 post-roll), held again 105..154.
        Run(seg, 300, gate: i => In(i, 50, 50) || In(i, 105, 50));
        var final = Assert.Single(ev);
        Assert.Equal(SegmentEventKind.Final, final.Kind);
        Assert.Equal(35, FirstFrame(final));
        Assert.True(LastFrame(final) >= 164);
    }

    [Fact]
    public void Triggered_ReleasingForLongerThanPostRollStartsANewUtteranceNextTime()
    {
        var (seg, ev) = Make(Triggered(), i => In(i, 50, 40) || In(i, 200, 40));
        Run(seg, 400, gate: i => In(i, 50, 40) || In(i, 200, 40));
        Assert.Equal(2, ev.Count(e => e.Kind == SegmentEventKind.Final));
    }

    [Fact]
    public void Triggered_LongHoldIsSplitAtMaxSegmentLengthSeamlessly()
    {
        var o = Triggered() with { MaxSegmentSec = 5 };
        var (seg, ev) = Make(o, i => i >= 50 && i < 650);
        Run(seg, 800, gate: i => In(i, 50, 600)); // 12 s hold
        var finals = ev.Where(e => e.Kind == SegmentEventKind.Final).ToList();
        Assert.True(finals.Count >= 3, $"got {finals.Count} pieces");
        for (int i = 1; i < finals.Count; i++)
            Assert.Equal(LastFrame(finals[i - 1]) + 1, FirstFrame(finals[i]));
    }

    [Fact]
    public void Triggered_PartialsWhileHeld()
    {
        var (seg, ev) = Make(Triggered(partials: true), i => In(i, 50, 200));
        Run(seg, 300, gate: i => In(i, 50, 200));
        Assert.True(ev.Count(e => e.Kind == SegmentEventKind.Partial) >= 3);
    }

    // ---- Misc -----------------------------------------------------------------------------------------------------

    [Fact]
    public void WrongFrameSizeIsRejected()
    {
        var (seg, _) = Make(new SegmenterOptions(), _ => false);
        Assert.Throws<ArgumentException>(() => seg.Push(new float[100], true, false));
    }

    [Fact]
    public void ResetDropsAnInProgressUtteranceWithoutEmittingAnything()
    {
        var (seg, ev) = Make(new SegmenterOptions { PublishPartials = false }, i => In(i, 50, 400));
        Run(seg, 100);
        Assert.True(seg.InSegment);
        seg.Reset();
        Assert.False(seg.InSegment);
        Assert.Empty(ev);
    }

    [Fact]
    public void ZeroPreRollIsAllowed()
    {
        var (seg, ev) = Make(new SegmenterOptions { PreRollMs = 0, PublishPartials = false }, i => In(i, 50, 50));
        Run(seg, 200);
        var final = Assert.Single(ev);
        Assert.Equal(50, FirstFrame(final));
    }
}
