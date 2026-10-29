namespace Studio.Host;

/// <summary>One captured window image: tightly packed, premultiplied BGRA, top row first.</summary>
public sealed record CapturedFrame(byte[] Pixels, int Width, int Height, long Timestamp, long Sequence);
