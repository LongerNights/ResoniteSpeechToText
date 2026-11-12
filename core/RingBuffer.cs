using System;
using System.Threading;

namespace SpeechToText;

/// <summary>
/// Lock-free single-producer / single-consumer float ring buffer that overwrites the oldest data when the consumer
/// falls behind (SPEC §4.3). The producer only ever advances the write counter; the consumer detects that it has been
/// lapped, skips forward to the oldest surviving sample, and discards any copy that raced with an overwrite.
/// </summary>
public sealed class RingBuffer
{
    private readonly float[] _data;
    private readonly int _mask;
    private long _write; // total samples ever written (producer-owned)
    private long _read;  // total samples ever consumed or skipped (consumer-owned)
    private long _dropped;

    /// <param name="minCapacity">Rounded up to a power of two.</param>
    public RingBuffer(int minCapacity)
    {
        int cap = 1;
        while (cap < minCapacity) cap <<= 1;
        _data = new float[cap];
        _mask = cap - 1;
    }

    public int Capacity => _data.Length;

    /// <summary>Samples currently readable (never more than <see cref="Capacity"/>).</summary>
    public int Available => (int)Math.Min(Volatile.Read(ref _write) - Volatile.Read(ref _read), _data.Length);

    /// <summary>Total samples lost because the consumer was lapped.</summary>
    public long DroppedSamples => Volatile.Read(ref _dropped);

    /// <summary>Producer side. Never blocks, never allocates.</summary>
    public void Write(ReadOnlySpan<float> samples)
    {
        if (samples.Length > _data.Length)
        {
            // Bigger than the whole buffer: only the newest Capacity samples can be kept.
            Interlocked.Add(ref _dropped, samples.Length - _data.Length);
            samples = samples.Slice(samples.Length - _data.Length);
        }
        long w = _write;
        int start = (int)(w & _mask);
        int first = Math.Min(samples.Length, _data.Length - start);
        samples.Slice(0, first).CopyTo(_data.AsSpan(start, first));
        if (first < samples.Length) samples.Slice(first).CopyTo(_data.AsSpan(0, samples.Length - first));
        Volatile.Write(ref _write, w + samples.Length);
    }

    /// <summary>Consumer side. Copies up to <paramref name="destination"/>.Length samples; returns how many.</summary>
    public int Read(Span<float> destination)
    {
        while (true)
        {
            long r = _read;
            long w = Volatile.Read(ref _write);
            if (w - r > _data.Length)
            {
                // Lapped: everything older than (w - capacity) is gone.
                long newRead = w - _data.Length;
                Interlocked.Add(ref _dropped, newRead - r);
                Volatile.Write(ref _read, newRead);
                r = newRead;
            }
            int n = (int)Math.Min(w - r, destination.Length);
            if (n <= 0) return 0;

            int start = (int)(r & _mask);
            int first = Math.Min(n, _data.Length - start);
            _data.AsSpan(start, first).CopyTo(destination);
            if (first < n) _data.AsSpan(0, n - first).CopyTo(destination.Slice(first));

            // If the producer wrote past our region while we copied, the copy may be torn: resync and retry.
            if (Volatile.Read(ref _write) - r > _data.Length) continue;

            Volatile.Write(ref _read, r + n);
            return n;
        }
    }

    /// <summary>Consumer side: forget everything currently buffered.</summary>
    public void Clear() => Volatile.Write(ref _read, Volatile.Read(ref _write));
}
