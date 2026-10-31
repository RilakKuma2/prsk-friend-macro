using SkiaSharp;

// The cap covers all instance archives together. The mutex also serializes independent
// workers, so migration and pruning cannot race another worker's atomic image write.
sealed class EvidenceStore(string workspace,long limit=100_000_000,Action<string>? report=null)
{
    internal const int Quality=80;
    readonly object gate=new();
    readonly Mutex crossProcess=new(false,FrameRelay.PipeName(workspace)+"-evidence");
    DateTime lastWarning;
    void Warn(Exception ex)
    {
        if(DateTime.UtcNow-lastWarning<TimeSpan.FromMinutes(1)) return;
        lastWarning=DateTime.UtcNow;report?.Invoke("증거 캡처 저장/정리 지연 · "+ex.Message);
    }
    internal IEnumerable<string> Roots()
    {
        yield return Path.Combine(workspace,"artifacts");
        var instances=Path.Combine(workspace,"local","instances");
        if(Directory.Exists(instances))
            foreach(var folder in Directory.EnumerateDirectories(instances)) yield return Path.Combine(folder,"artifacts");
    }
    static bool IsEvidence(string path)=>Path.GetExtension(path).ToLowerInvariant() is ".png" or ".webp" &&
        Path.GetFileName(path) is not ("current.png" or "ocr-region.png" or "record.png" or "gacha-verify.png");
    IEnumerable<FileInfo> Files()=>Roots().Where(Directory.Exists).SelectMany(root=>Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories))
        .Where(IsEvidence).Select(path=>new FileInfo(path));
    void Locked(Action action)
    {
        lock(gate)
        {
            var held=false;
            try
            {
                try { held=crossProcess.WaitOne(TimeSpan.FromSeconds(10)); } catch(AbandonedMutexException) { held=true; }
                if(!held) throw new IOException("다른 작업이 증거 캡처를 정리하고 있습니다.");
                action();
            }
            finally { if(held) crossProcess.ReleaseMutex(); }
        }
    }
    internal string Save(byte[] bytes,string requested)
    {
        var target=Path.ChangeExtension(requested,".webp");
        try { Locked(()=>{ Write(bytes,target,null);PruneCore(); }); }
        catch(Exception ex) when(ex is not OutOfMemoryException) { Warn(ex); }
        return target;
    }
    internal void ResetBasicDataVerification(string artifacts)=>Locked(()=>
    {
        foreach(var name in new[]{"basic-data-verified.png","basic-data-verified.webp","basic-data-verified.sent"})
            File.Delete(Path.Combine(artifacts,name));
    });
    void Write(byte[] bytes,string target,DateTime? time)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
        var pending=target+".pending";
        try
        {
            using var image=SKImage.FromEncodedData(bytes) ?? throw new InvalidDataException("캡처를 읽지 못했습니다.");
            using var encoded=image.Encode(SKEncodedImageFormat.Webp,Quality) ?? throw new InvalidDataException("WebP 압축에 실패했습니다.");
            using(var output=File.Create(pending)) encoded.SaveTo(output);
            File.Move(pending,target,true);
            if(time is { } timestamp) File.SetLastWriteTimeUtc(target,timestamp);
        }
        finally { if(File.Exists(pending)) File.Delete(pending); }
    }
    internal void Migrate()
    {
        try
        {
            Locked(()=>
            {
                foreach(var root in Roots().Where(Directory.Exists))
                {
                    var marker=Path.Combine(root,"basic-data-verified.sent");
                    if(!File.Exists(marker) && File.Exists(Path.Combine(root,"basic-data-verified.png")))
                        File.WriteAllText(marker,"migrated");
                }
                foreach(var file in Files().Where(f=>f.Extension.Equals(".png",StringComparison.OrdinalIgnoreCase)).ToArray())
                {
                    try
                    {
                        var target=Path.ChangeExtension(file.FullName,".webp");
                        if(!File.Exists(target) || File.GetLastWriteTimeUtc(target)<file.LastWriteTimeUtc)
                            Write(File.ReadAllBytes(file.FullName),target,file.LastWriteTimeUtc);
                        File.Delete(file.FullName);
                    }
                    catch(Exception ex) when(ex is not OutOfMemoryException) { Warn(ex); }
                }
                PruneCore();
            });
        }
        catch(Exception ex) when(ex is not OutOfMemoryException) { Warn(ex); }
    }
    void PruneCore()
    {
        var files=Files().OrderBy(f=>f.LastWriteTimeUtc).ThenBy(f=>f.FullName,StringComparer.Ordinal).ToArray();
        var bytes=files.Sum(f=>f.Length);
        foreach(var file in files)
        {
            if(bytes<=limit) break;
            try { var length=file.Length;file.Delete();bytes-=length; }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { Warn(ex); }
        }
    }
}

internal static partial class Program
{
    static readonly Lazy<EvidenceStore> EvidenceArchive=new(()=>new(Workspace,report:Console.Error.WriteLine));
    static void StartEvidenceMaintenance()=>_ = Task.Run(()=>EvidenceArchive.Value.Migrate());
    static string SaveEvidenceImage(byte[] bytes,string path)=>EvidenceArchive.Value.Save(bytes,path);
    static string SaveCurrentEvidence(string path)=>SaveEvidenceImage(CurrentFrameBytes(),path);
    static bool BasicDataVerified=>File.Exists("artifacts/basic-data-verified.sent") || File.Exists("artifacts/basic-data-verified.png");
}
