using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace SpeechToText;

/// <summary>
/// Cleans Whisper output (SPEC §6.5): strips non-speech markers, drops the stock hallucinations Whisper produces on
/// near-silence, optionally masks profanity, and caps the length on a word boundary.
/// </summary>
public sealed class TextFilter
{
    private static readonly Regex Brackets = new(@"\[[^\]]*\]", RegexOptions.Compiled);
    // Whisper writes sound annotations in parentheses: "(music)", "(vocalizing)", "(humming)", "(laughs)", ... Spoken words never
    // come out that way, so any short parenthetical is dropped.
    private static readonly Regex Parens = new(@"\([^()\r\n]{1,40}\)", RegexOptions.Compiled);
    private static readonly Regex Actions = new(@"\*[^*\r\n]{1,30}\*", RegexOptions.Compiled);
    private static readonly Regex Notes = new(@"[♪♫]+", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);
    // 8+ of the same letter/digit/bracket in a row ("MMMMMMMM"), the way a humming or looping decode shows up.
    private static readonly Regex CharRuns = new(@"([\p{L}\p{N}()\[\]])\1{7,}", RegexOptions.Compiled);
    private static readonly Regex Word = new(@"[\p{L}']+", RegexOptions.Compiled);

    // What Whisper tends to "hear" in silence or breath. Only dropped when the segment had little voiced audio.
    private static readonly HashSet<string> Hallucinations = new(StringComparer.OrdinalIgnoreCase)
    {
        "thank you", "thanks", "thank you very much", "thanks for watching", "thank you for watching",
        "thanks for watching!", "please subscribe", "subscribe", "you", "bye", "bye bye", "goodbye",
        "so", "okay", "oh", "uh", "um", "hmm", "mm-hmm", "the end",
    };

    private static readonly HashSet<string> Profanity = new(StringComparer.OrdinalIgnoreCase)
    {
        "fuck", "fucking", "fucked", "shit", "shitty", "bitch", "asshole", "bastard", "dick", "cunt", "damn", "crap",
    };

    private readonly int _maxLength;
    private readonly bool _maskProfanity;

    public TextFilter(int maxLength = 512, bool maskProfanity = false)
    {
        _maxLength = Math.Max(2, maxLength);
        _maskProfanity = maskProfanity;
    }

    /// <summary>Returns the cleaned text, or "" if it should be dropped.</summary>
    /// <param name="voicedMs">How much of the segment the VAD called speech (short segments get the strict hallucination check).</param>
    public string Apply(string? raw, int voicedMs)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        string t = Brackets.Replace(raw, " ");
        t = Parens.Replace(t, " ");
        t = Actions.Replace(t, " ");
        t = Notes.Replace(t, " ");
        t = Spaces.Replace(t, " ").Trim();
        if (t.Length == 0) return "";

        if (!Word.IsMatch(t)) return ""; // punctuation only

        t = CollapseRepeats(t);

        if (voicedMs < 1000)
        {
            string bare = t.Trim(' ', '.', ',', '!', '?', '…');
            if (Hallucinations.Contains(bare)) return "";
        }

        if (_maskProfanity) t = Word.Replace(t, m => Profanity.Contains(m.Value) ? Mask(m.Value) : m.Value);

        return Truncate(t);
    }

    /// <summary>
    /// Whisper loops on humming, singing and tones ("oh, oh, oh, ..." x35, "MMMMMM..."). Keep the gist, drop the flood:
    /// a word repeated more than 6 times in a row, or a character repeated 8+ times, is cut to 3 plus an ellipsis.
    /// </summary>
    private static string CollapseRepeats(string t)
    {
        t = CharRuns.Replace(t, m => new string(m.Value[0], 3) + "…");

        var tokens = t.Split(' ');
        var sb = new StringBuilder(t.Length);
        int i = 0;
        while (i < tokens.Length)
        {
            string key = Normalize(tokens[i]);
            int j = i + 1;
            while (j < tokens.Length && key.Length > 0 && Normalize(tokens[j]) == key) j++;
            int run = j - i;
            if (run > 6)
            {
                for (int k = 0; k < 3; k++) { if (sb.Length > 0) sb.Append(' '); sb.Append(k == 2 ? tokens[i + k].TrimEnd(',', '.', '!', '?', ';', ':') + "…" : tokens[i + k]); }
            }
            else
                for (int k = i; k < j; k++) { if (sb.Length > 0) sb.Append(' '); sb.Append(tokens[k]); }
            i = j;
        }
        return sb.ToString();
    }

    private static string Normalize(string token) => token.Trim(' ', '.', ',', '!', '?', ';', ':', '"', '\'', '…').ToLowerInvariant();

    private static string Mask(string w)
    {
        var sb = new StringBuilder(w.Length);
        sb.Append(w[0]);
        sb.Append('*', w.Length - 1);
        return sb.ToString();
    }

    private string Truncate(string t)
    {
        if (t.Length <= _maxLength) return t;
        int limit = _maxLength - 1; // room for the ellipsis
        int cut = t.LastIndexOf(' ', limit);
        if (cut < limit / 2) cut = limit; // no usable word boundary: hard cut
        return t.Substring(0, cut).TrimEnd(' ', ',', ';', ':', '-') + "…";
    }
}
