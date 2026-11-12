namespace SpeechToText.Tests;

public class TextFilterTests
{
    private static readonly TextFilter F = new(512, false);

    [Theory]
    [InlineData("[BLANK_AUDIO]", "")]
    [InlineData("  [Music]  ", "")]
    [InlineData("(music)", "")]
    [InlineData("(vocalizing)", "")]                                  // what Whisper writes for humming
    [InlineData("(humming)", "")]
    [InlineData("(laughs) hello there", "hello there")]
    [InlineData("Doo (whistling) boo", "Doo boo")]
    [InlineData("( ( ( (", "")]
    [InlineData("(Applause)", "")]
    [InlineData("♪ la la ♪", "la la")]
    [InlineData("*sighs*", "")]
    [InlineData("Hello [inaudible] world", "Hello world")]
    [InlineData("Hello   there,\n  friend.", "Hello there, friend.")]
    [InlineData("...", "")]
    [InlineData("?!", "")]
    [InlineData("", "")]
    public void StripsMarkersAndTidiesWhitespace(string raw, string expected) => Assert.Equal(expected, F.Apply(raw, 3000));

    [Fact]
    public void NullIsEmpty() => Assert.Equal("", F.Apply(null, 3000));

    [Theory]
    [InlineData("Thank you.")]
    [InlineData("Thanks for watching!")]
    [InlineData("you")]
    [InlineData("Bye.")]
    public void HallucinationsAreDroppedWhenLittleWasSpoken(string text) => Assert.Equal("", F.Apply(text, 400));

    [Fact]
    public void SameWordsSurviveWhenTheSpeakerReallySaidThem()
    {
        // A full second or more of voiced audio: "thank you" is legitimate speech.
        Assert.Equal("Thank you.", F.Apply("Thank you.", 1500));
    }

    [Fact]
    public void RealSentencesAreNeverTreatedAsHallucinations() =>
        Assert.Equal("Thank you for the invite to your world.", F.Apply("Thank you for the invite to your world.", 300));

    [Fact]
    public void ProfanityMaskedOnlyWhenEnabled()
    {
        Assert.Equal("what the shit", F.Apply("what the shit", 3000));
        Assert.Equal("what the s***", new TextFilter(512, true).Apply("what the shit", 3000));
        Assert.Equal("Assume nothing.", new TextFilter(512, true).Apply("Assume nothing.", 3000)); // no false positives inside words
    }

    [Theory]
    [InlineData("MMMMMMMMMMMMMMMMMMMMMMMMMMMM", "MMM…")]                                  // hum
    [InlineData("Oh, oh, oh, oh, oh, oh, oh, oh, oh, oh, oh, oh.", "Oh, oh, oh…")]       // looping decode
    [InlineData("La La La La La La La La La La La", "La La La…")]
    [InlineData("( ( ( ( ( ( ( ( ( ( (", "")]                                              // no letters at all
    [InlineData("I said no no no no no, that is wrong", "I said no no no no no, that is wrong")] // 5 repeats can be real speech
    [InlineData("Hello Mississippi, wooooow!", "Hello Mississippi, wooooow!")]               // ordinary stretches are untouched
    [InlineData("Go go go go go go go now", "Go go go… now")]
    public void DegenerateRepetitionIsCollapsed(string raw, string expected) => Assert.Equal(expected, F.Apply(raw, 3000));

    [Fact]
    public void LongTextIsCutOnAWordBoundaryWithAnEllipsis()
    {
        var f = new TextFilter(32, false);
        string result = f.Apply("The quick brown fox jumps over the lazy dog again and again", 3000);
        Assert.True(result.Length <= 32, result);
        Assert.EndsWith("…", result);
        Assert.Equal("The quick brown fox jumps over…", result);
    }

    [Fact]
    public void LongTextWithoutSpacesIsHardCut()
    {
        var f = new TextFilter(16, false);
        string result = f.Apply(string.Concat(Enumerable.Repeat("abcdefgh", 12)), 3000); // long, no spaces, nothing repetitive enough to collapse
        Assert.Equal(16, result.Length);
        Assert.EndsWith("…", result);
    }

    [Fact]
    public void ShortTextIsUntouched() => Assert.Equal("Hello.", new TextFilter(6, false).Apply("Hello.", 3000));
}
