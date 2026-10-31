using SkiaSharp;
using System.Diagnostics;

internal static partial class Program
{
    static async Task VerifyCaptureStorage()
    {
        var passed=0;
        void Assert(bool ok,string description) { if(!ok) throw new Exception(description);passed++;Console.WriteLine("PASS "+description); }
        var root=Path.Combine(Path.GetTempPath(),"sekai-capture-check-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var original=Directory.GetCurrentDirectory();
        try
        {
            using var bitmap=new SKBitmap(960,540);
            using(var canvas=new SKCanvas(bitmap))
            {
                canvas.Clear(SKColors.White);
                using var paint=new SKPaint { Color=SKColors.HotPink };
                canvas.DrawRect(120,90,540,210,paint);
            }
            using var image=SKImage.FromBitmap(bitmap);
            using var encoded=image.Encode(SKEncodedImageFormat.Png,100);
            var png=encoded.ToArray();
            Directory.SetCurrentDirectory(root);
            Directory.CreateDirectory("artifacts");
            await Snapshot(capture:()=>Task.FromResult(png));
            Assert(!Directory.EnumerateFiles(root,"*.png",SearchOption.AllDirectories).Any(),"runtime recognition captures never write a PNG");
            using(var screen=LoadCurrentScreen()) Assert(screen.Width==960 && screen.GetPixel(150,100).R>200,"recognition reads the original in-memory PNG");
            await Snapshot(Path.Combine(root,"export.png"),quiet:true,capture:()=>Task.FromResult(png));
            Assert(File.ReadAllBytes("export.png").SequenceEqual(png),"explicit CLI PNG export remains available");

            var archive=new EvidenceStore(root,report:message=>throw new Exception(message));
            var webp=archive.Save(png,Path.Combine(root,"artifacts","proof.png"));
            using(var decoded=SKBitmap.Decode(webp)) Assert(decoded.Width==960 && decoded.Height==540,"WebP evidence preserves the full capture resolution");
            Assert(Path.GetExtension(webp)==".webp" && File.ReadAllBytes(webp).AsSpan().StartsWith("RIFF"u8) && EvidenceStore.Quality==80,"evidence is actual WebP encoded at quality 80");
            var legacy=Path.Combine(root,"local","instances","2","artifacts");Directory.CreateDirectory(legacy);
            var timestamp=DateTime.UtcNow.AddDays(-1);
            File.WriteAllBytes(Path.Combine(legacy,"basic-data-verified.png"),png);
            File.SetLastWriteTimeUtc(Path.Combine(legacy,"basic-data-verified.png"),timestamp);
            File.WriteAllBytes(Path.Combine(legacy,"current.png"),png);
            File.WriteAllBytes(Path.Combine(legacy,"ocr-region.png"),png);
            archive.Migrate();
            Assert(File.Exists(Path.Combine(legacy,"basic-data-verified.webp")) && !File.Exists(Path.Combine(legacy,"basic-data-verified.png")),"old PNG evidence is migrated across instance archives");
            Assert(File.GetLastWriteTimeUtc(Path.Combine(legacy,"basic-data-verified.webp"))==timestamp,"migration preserves the original retention timestamp");
            Assert(File.Exists(Path.Combine(legacy,"basic-data-verified.sent")),"workflow progress is independent of evictable evidence images");
            Assert(File.Exists(Path.Combine(legacy,"current.png")) && File.Exists(Path.Combine(legacy,"ocr-region.png")),"migration leaves runtime and OCR temporary PNGs alone");
            var recent=archive.Save(png,Path.Combine(root,"artifacts","recent.webp"));
            var budget=new FileInfo(recent).Length;
            new EvidenceStore(root,budget).Migrate();
            var retained=archive.Roots().SelectMany(d=>Directory.Exists(d)?Directory.GetFiles(d,"*.webp",SearchOption.AllDirectories):[]).ToArray();
            Assert(retained.Sum(f=>new FileInfo(f).Length)<=budget && File.Exists(recent),"the shared budget prunes the oldest captures across all instances");
            Assert(File.Exists(Path.Combine(legacy,"basic-data-verified.sent")),"pruning evidence preserves the workflow checkpoint");

            var frames=new LiveFrameStore(2,10);frames.Publish(0,new byte[4]);
            var old=frames.Read(0)!;
            for(var i=0;i<1000;i++) frames.Publish(0,new byte[4]);
            Assert(frames.Count==1 && frames.Bytes==4 && frames.Read(0)!.Revision>old.Revision,"repeated previews replace the previous frame without accumulating memory");
            frames.Publish(1,new byte[4]);frames.Publish(2,new byte[8]);
            Assert(frames.Count==1 && frames.Bytes==8 && frames.Read(0)==null,"both instance count and byte limits evict old frames");
            frames.Keep([]);Assert(frames.Count==0 && frames.Bytes==0,"removed instances release their cached memory");
            archive.ResetBasicDataVerification(legacy);
            Assert(!File.Exists(Path.Combine(legacy,"basic-data-verified.sent")),"account reset removes the separate basic-data checkpoint too");

            // A real child process sends a PNG through the same pipe used by background workers.
            if(FrameSender!=null) { await FrameSender.DisposeAsync();FrameSender=null; }
            var received=new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var receiver=new FrameReceiver(FrameRelay.PipeName(original),(n,bytes)=>received.TrySetResult(bytes));
            receiver.Start();
            var info=new ProcessStartInfo(Environment.ProcessPath!) { WorkingDirectory=original,UseShellExecute=false };
            if(Path.GetFileNameWithoutExtension(Environment.ProcessPath)=="dotnet") info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory,"SekaiMacro.dll"));
            info.ArgumentList.Add("check-frame-publish");info.ArgumentList.Add(Path.Combine(root,"export.png"));
            using var child=Process.Start(info)!;
            await child.WaitForExitAsync();
            var transferred=await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(child.ExitCode==0 && transferred.SequenceEqual(png),"a separate worker delivers the original PNG to the panel entirely through memory IPC");
            Assert(!Directory.Exists(Path.Combine(root,"local","frames")),"frame relay creates no screenshot transport files");
            Console.WriteLine($"Capture storage: {passed} checks passed; no device input sent.");
        }
        finally { Directory.SetCurrentDirectory(original);Directory.Delete(root,true);CurrentFrames.Clear(); }
    }
}
