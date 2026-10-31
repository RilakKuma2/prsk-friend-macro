using System.IO.Pipes;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;

// Workers and the panel are separate processes. A private pipe carries the original PNG
// directly to the panel; a single pending frame replaces older unsent frames.
sealed class FrameRelay(string pipeName):IAsyncDisposable
{
    internal const int MaxFrameBytes=8*1024*1024;
    readonly Channel<(int Instance,byte[] Png)> pending=Channel.CreateBounded<(int,byte[])>(new BoundedChannelOptions(1)
        { FullMode=BoundedChannelFullMode.DropOldest,SingleReader=true });
    readonly CancellationTokenSource stop=new();
    Task? sending;
    internal static string PipeName(string workspace)=>"sekai-frame-"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(workspace))))[..24];
    internal void Publish(int instance,byte[] png)
    {
        if(png.Length>MaxFrameBytes) return;
        pending.Writer.TryWrite((instance,png));
        sending??=Send();
    }
    async Task Send()
    {
        try
        {
            await foreach(var frame in pending.Reader.ReadAllAsync(stop.Token))
            {
                try
                {
                    using var pipe=new NamedPipeClientStream(".",pipeName,PipeDirection.Out,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
                    await pipe.ConnectAsync(200,stop.Token);
                    using var timeout=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(2));
                    var header=new byte[8];
                    BinaryPrimitives.WriteInt32LittleEndian(header,frame.Instance);
                    BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4),frame.Png.Length);
                    await pipe.WriteAsync(header,timeout.Token);
                    await pipe.WriteAsync(frame.Png,timeout.Token);
                    await pipe.FlushAsync(timeout.Token);
                }
                // Closing the GUI or running from the CLI must never stop the macro.
                catch(Exception ex) when(ex is IOException or TimeoutException or OperationCanceledException) { }
            }
        }
        catch(OperationCanceledException) { }
    }
    public async ValueTask DisposeAsync()
    {
        pending.Writer.TryComplete();
        if(sending!=null)
        {
            try { await sending.WaitAsync(TimeSpan.FromSeconds(1)); }
            catch(TimeoutException) { stop.Cancel();await sending; }
        }
        stop.Dispose();
    }
}

sealed class FrameReceiver(string pipeName,Action<int,byte[]> publish):IAsyncDisposable
{
    readonly CancellationTokenSource stop=new();
    Task? receiving;
    internal void Start()=>receiving??=Receive();
    async Task Receive()
    {
        while(!stop.IsCancellationRequested)
        {
            try
            {
                using var pipe=new NamedPipeServerStream(pipeName,PipeDirection.In,1,PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stop.Token);
                using var timeout=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                var header=new byte[8];await pipe.ReadExactlyAsync(header,timeout.Token);
                var instance=BinaryPrimitives.ReadInt32LittleEndian(header);
                var size=BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
                if(instance<0 || size is <24 or >FrameRelay.MaxFrameBytes) continue;
                var bytes=new byte[size];await pipe.ReadExactlyAsync(bytes,timeout.Token);
                publish(instance,bytes);
            }
            catch(Exception ex) when(ex is IOException or OperationCanceledException or UnauthorizedAccessException)
            {
                if(!stop.IsCancellationRequested) await Task.Delay(100,stop.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        if(receiving!=null) await receiving;
        stop.Dispose();
    }
}

internal static partial class Program
{
    static FrameReceiver? PanelFrames;
    static void StartFrameReceiver(WebApplication app)
    {
        if(PanelFrames!=null) return;
        StartEvidenceMaintenance();
        PanelFrames=new(FrameRelay.PipeName(Workspace),(instance,bytes)=>
        {
            try { ValidateFrame(bytes);CurrentFrames.Publish(instance,bytes); }
            catch(InvalidDataException) { }
        });
        PanelFrames.Start();
        app.Lifetime.ApplicationStopped.Register(()=>
        {
            PanelFrames.DisposeAsync().AsTask().GetAwaiter().GetResult();
            PanelFrames=null;CurrentFrames.Clear();
        });
    }
}
