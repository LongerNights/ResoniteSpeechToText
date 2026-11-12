namespace SpeechToText;

/// <summary>Everything the Publisher mirrors into the world. Immutable; swapped atomically.</summary>
internal sealed record Snapshot(
    string Text, string Partial, int Sequence, bool Listening, bool Speaking, string Language)
{
    public static readonly Snapshot Empty = new("", "", 0, false, false, "");
}
