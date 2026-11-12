namespace SpeechToText.Tests;

public class RingBufferTests
{
    [Fact]
    public void RoundTripAndWraparound()
    {
        var rb = new RingBuffer(8);
        Assert.Equal(8, rb.Capacity);
        var buf = new float[8];

        rb.Write(new float[] { 1, 2, 3, 4, 5, 6 });
        Assert.Equal(6, rb.Available);
        Assert.Equal(4, rb.Read(buf.AsSpan(0, 4)));
        Assert.Equal(new float[] { 1, 2, 3, 4 }, buf[..4]);

        rb.Write(new float[] { 7, 8, 9, 10 }); // wraps past the end
        Assert.Equal(6, rb.Available);
        Assert.Equal(6, rb.Read(buf));
        Assert.Equal(new float[] { 5, 6, 7, 8, 9, 10 }, buf[..6]);
        Assert.Equal(0, rb.Available);
        Assert.Equal(0, rb.DroppedSamples);
    }

    [Fact]
    public void CapacityRoundsUpToPowerOfTwo() => Assert.Equal(1024, new RingBuffer(1000).Capacity);

    [Fact]
    public void OverflowDropsOldest()
    {
        var rb = new RingBuffer(8);
        rb.Write(Enumerable.Range(0, 20).Select(i => (float)i).ToArray()); // only the last 8 survive
        var buf = new float[16];
        int n = rb.Read(buf);
        Assert.Equal(8, n);
        Assert.Equal(Enumerable.Range(12, 8).Select(i => (float)i).ToArray(), buf[..8]);
        Assert.Equal(12, rb.DroppedSamples); // the 12 oldest of the oversized write were trimmed and counted
    }

    [Fact]
    public void LappedConsumerSkipsAheadAndCounts()
    {
        var rb = new RingBuffer(8);
        rb.Write(new float[] { 0, 1, 2, 3 });
        rb.Write(new float[] { 4, 5, 6, 7 });
        rb.Write(new float[] { 8, 9, 10, 11 }); // consumer never read: 0..3 are gone
        var buf = new float[8];
        Assert.Equal(8, rb.Read(buf));
        Assert.Equal(new float[] { 4, 5, 6, 7, 8, 9, 10, 11 }, buf);
        Assert.Equal(4, rb.DroppedSamples);
    }

    [Fact]
    public void ClearForgetsBufferedAudio()
    {
        var rb = new RingBuffer(16);
        rb.Write(new float[] { 1, 2, 3 });
        rb.Clear();
        Assert.Equal(0, rb.Available);
        Assert.Equal(0, rb.Read(new float[4]));
    }

    [Fact]
    public async Task ConcurrentProducerConsumerNeverSeesTornOrReorderedData()
    {
        // Producer writes an increasing counter in odd-sized chunks, consumer reads as fast as it can with a small
        // buffer so it is regularly lapped. Whatever it sees must be strictly increasing (gaps allowed), never garbage.
        var rb = new RingBuffer(4096);
        const int total = 3_000_000;
        var producer = Task.Run(() =>
        {
            var chunk = new float[333];
            int next = 0;
            while (next < total)
            {
                int n = Math.Min(chunk.Length, total - next);
                for (int i = 0; i < n; i++) chunk[i] = next + i;
                rb.Write(chunk.AsSpan(0, n));
                next += n;
            }
        });

        float last = -1;
        long seen = 0;
        var buf = new float[777];
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while ((!producer.IsCompleted || rb.Available > 0) && DateTime.UtcNow < deadline)
        {
            int n = rb.Read(buf);
            for (int i = 0; i < n; i++)
            {
                Assert.True(buf[i] > last, $"out of order: {buf[i]} after {last}");
                last = buf[i];
            }
            seen += n;
        }
        await producer;
        Assert.True(seen > 0);
        Assert.Equal(total - 1, last); // the very last sample is never lost
        Assert.Equal(total, seen + rb.DroppedSamples);
    }
}
