// One immutable PNG payload per instance. Replacing a slot releases the cache's reference to the old
// bytes; in-flight HTTP responses may finish using them. No bitmap or stream stays alive in the cache.
sealed class LiveFrameStore(int maxFrames=32,long maxBytes=64L*1024*1024)
{
    internal sealed record Frame(byte[] Png,long Revision);
    readonly object gate=new();
    readonly Dictionary<int,Frame> frames=[];
    // JSON numbers must remain exact in JavaScript so every frame changes the image URL.
    long revision=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    long bytes;
    internal long Bytes { get { lock(gate)return bytes; } }
    internal int Count { get { lock(gate)return frames.Count; } }
    internal void Publish(int index,byte[] png)
    {
        if (png.LongLength>maxBytes) throw new InvalidDataException("화면 캡처가 메모리 한도를 넘었습니다.");
        lock(gate)
        {
            if(frames.Remove(index,out var previous))bytes-=previous.Png.LongLength;
            frames[index]=new(png,++revision);bytes+=png.LongLength;
            while(frames.Count>maxFrames || bytes>maxBytes)
            {
                var oldest=frames.MinBy(p=>p.Value.Revision);
                frames.Remove(oldest.Key);bytes-=oldest.Value.Png.LongLength;
            }
        }
    }
    internal Frame? Read(int index) { lock(gate)return frames.GetValueOrDefault(index); }
    internal void Keep(IEnumerable<int> indexes)
    {
        var keep=indexes.ToHashSet();
        lock(gate)
            foreach(var index in frames.Keys.Where(i=>!keep.Contains(i)).ToArray())
            { bytes-=frames[index].Png.LongLength;frames.Remove(index); }
    }
    internal void Clear() { lock(gate) { frames.Clear();bytes=0; } }
}

internal static partial class Program
{
    static readonly LiveFrameStore CurrentFrames=new();
    static FrameRelay? FrameSender;
    static byte[] CurrentFrameBytes()=>CurrentFrames.Read(Instance)?.Png ?? throw new IOException("현재 화면의 메모리 캡처가 없습니다.");
    static bool HasCurrentFrame=>CurrentFrames.Read(Instance)!=null;
    static void PublishCurrentFrame(byte[] bytes)
    {
        ValidateFrame(bytes);
        CurrentFrames.Publish(Instance,bytes);
        (FrameSender??=new(FrameRelay.PipeName(Workspace))).Publish(Instance,bytes);
    }
    static void ValidateFrame(byte[] bytes)
    {
        if(bytes.Length<24 || !bytes.AsSpan(0,8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10}) ||
            System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16,4))!=960 ||
            System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20,4))!=540)
            throw new InvalidDataException("Invalid screenshot output or size; expected 960x540 PNG.");
    }
}
