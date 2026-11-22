using System.Drawing;
using System.Text.Json;
using System.Text.RegularExpressions;

// Per-instance invitation codes (artifacts/invite.json) and the nickname captures taken at registration.
// A "permanent" code is used for every loop until it is deleted; a "once" code is cleared after its set number of
// registrations (1 unless set, at most 10). Further codes wait in artifacts/invite-queue.json and the next one is activated when the current one is used up.
internal static partial class Program
{
    record InviteSetting(string Code,string Mode,DateTimeOffset Updated,int? Limit=null,bool Global=false);
    record InviteRecord(DateTimeOffset Time,string Code,string Mode,string? Nickname,string? Image,string Result,int? Loop=null,string? Rewards=null);

    static string? InviteMode;
    static bool SkipInvitation;
    const string InviteFile="invite.json";
    const string InviteQueueFile="invite-queue.json";
    const string InviteHistoryFile="invites/history.json";

    const int MaxOnceUses=10;
    const int MaxGlobalOnceUses=99;

    static bool ValidInviteCode(string? code)=>Regex.IsMatch(code??"","^[A-Za-z0-9]{15}$");

    static InviteSetting? ReadInviteSetting(string artifacts)
    {
        try
        {
            var path=Path.Combine(artifacts,InviteFile);
            if(!File.Exists(path)) return null;
            var setting=JsonSerializer.Deserialize<InviteSetting>(File.ReadAllText(path));
            return setting!=null && ValidInviteCode(setting.Code) && setting.Mode is "once" or "permanent"?setting:null;
        }
        catch { return null; }
    }

    // Registrations a code serves: global assignments reserve 1 registration at a time so the next loop re-checks pool priorities.
    static int? InviteLimit(InviteSetting setting)=>setting.Global?1:setting.Mode=="once"?Math.Clamp(setting.Limit??1,1,MaxOnceUses):null;
    // Registrations made in this instance since the code became active.
    static int ActiveInviteUses(string artifacts,InviteSetting setting)=>ReadInviteHistory(artifacts).Count(r=>r.Code==setting.Code && r.Result=="registered" && r.Time>=setting.Updated);
    // Registrations the active code and the queue still allow: null once a permanent code is among them.
    static int? InviteCapacity(InviteSetting? active,int used,IEnumerable<InviteSetting> queue)
    {
        var codes=(active==null?queue:queue.Prepend(active)).ToList();
        return codes.Any(s=>!s.Global && s.Mode=="permanent")?null:Math.Max(0,codes.Sum(s=>InviteLimit(s)!.Value)-used);
    }
    static int? InviteCapacity(string artifacts)
    {
        if(ReadGlobalPool().Enabled) return GlobalPoolCapacity();
        var active=ReadInviteSetting(artifacts);
        return InviteCapacity(active,active==null?0:ActiveInviteUses(artifacts,active),ReadInviteQueue(artifacts));
    }
    // Player names read from the confirmation dialog when a code was entered, on any instance: the code's owner.
    static Dictionary<string,string> InviteOwners()=>AllowedInstances.Keys.SelectMany(n=>ReadInviteHistory(ArtifactsDir(n)))
        .Where(r=>r.Nickname is { Length: >0 }).OrderBy(r=>r.Time).GroupBy(r=>r.Code).ToDictionary(g=>g.Key,g=>g.Last().Nickname!);

    record GlobalInviteEntry(int Instance,string VmName,DateTimeOffset Time,string Code,string Mode,string? Nickname,string? Image,string Result,int? Loop,string? Rewards,int? CodeOrder,int CodeTotal);
    record InviteCodeSummary(string Code,string? Owner,int Count,DateTimeOffset LastTime);

    // Every invitation record across all instances ordered newest-first, with global 1-based CodeOrder per registered code.
    static List<GlobalInviteEntry> AllInviteEntries()
    {
        var all=AllowedInstances.SelectMany(kv=>ReadInviteHistory(ArtifactsDir(kv.Key))
            .Select(r=>(Instance:kv.Key,VmName:kv.Value,Record:r)))
            .OrderBy(x=>x.Record.Time)
            .ToList();
        var counts=new Dictionary<string,int>();
        var orders=new int?[all.Count];
        for(var i=0;i<all.Count;i++)
        {
            if(all[i].Record.Result=="registered" && ValidInviteCode(all[i].Record.Code))
            {
                var next=counts.GetValueOrDefault(all[i].Record.Code)+1;
                counts[all[i].Record.Code]=next;
                orders[i]=next;
            }
        }
        var list=new List<GlobalInviteEntry>(all.Count);
        for(var i=all.Count-1;i>=0;i--)
        {
            var (inst,vmName,r)=all[i];
            list.Add(new GlobalInviteEntry(inst,vmName,r.Time,r.Code,r.Mode,r.Nickname,r.Image,r.Result,r.Loop,r.Rewards,
                orders[i],counts.GetValueOrDefault(r.Code)));
        }
        return list;
    }

    static List<InviteCodeSummary> InviteCodeSummaries(IReadOnlyList<GlobalInviteEntry> entries)
    {
        var owners=InviteOwners();
        return entries.Where(e=>e.Result=="registered" && ValidInviteCode(e.Code))
            .GroupBy(e=>e.Code)
            .Select(g=>new InviteCodeSummary(g.Key,owners.GetValueOrDefault(g.Key),g.Count(),g.Max(x=>x.Time)))
            .OrderByDescending(s=>s.LastTime)
            .ToList();
    }

    // Active code can swap with queued codes only if it has not been registered yet and is not currently being entered.
    static bool CanReorderActiveInvite(int instance,string dir,InviteSetting? setting)
    {
        if(setting==null) return false;
        if(ActiveInviteUses(dir,setting)>0) return false;
        if(CurrentLoopInvite(dir) is { } cli && cli.Code==setting.Code && cli.Time>=setting.Updated && cli.Result is "confirming" or "registered")
            return false;
        if(IsWorkerRunning(instance))
        {
            var stage=Str(ReadJson(Path.Combine(dir,"progress.json")),"Stage");
            var step=Str(ReadJson(Path.Combine(dir,"current-step.json")),"Step");
            if(stage=="invitation" || (step?.StartsWith("invite-") ?? false)) return false;
        }
        return true;
    }

    static List<InviteSetting> ReadInviteQueue(string artifacts)
    {
        try
        {
            var path=Path.Combine(artifacts,InviteQueueFile);
            return File.Exists(path)?JsonSerializer.Deserialize<List<InviteSetting>>(File.ReadAllText(path))!.Where(q=>ValidInviteCode(q.Code) && q.Mode is "once" or "permanent").ToList():[];
        }
        catch { return []; }
    }
    static void WriteJsonAtomic(string path,object value)
    {
        File.WriteAllText(path+".pending",JsonSerializer.Serialize(value,JsonOptions));
        File.Move(path+".pending",path,true);
    }
    static void WriteInviteSetting(string artifacts,InviteSetting setting)=>WriteJsonAtomic(Path.Combine(artifacts,InviteFile),setting);
    static void WriteInviteQueue(string artifacts,List<InviteSetting> queue)
    {
        var path=Path.Combine(artifacts,InviteQueueFile);
        if(queue.Count==0) File.Delete(path); else WriteJsonAtomic(path,queue);
    }

    // Workers and the control panel both advance the queue, so changes to invite files are serialized per instance.
    static T WithInviteLock<T>(string artifacts,Func<T> action)
    {
        Directory.CreateDirectory(artifacts);
        for(var attempt=0;;attempt++)
        {
            try { using var hold=new FileStream(Path.Combine(artifacts,"invite.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None); return action(); }
            catch(IOException) when(attempt<60) { Thread.Sleep(50); }
        }
    }

    // Clears the active single-use code once it served its set number of registrations.
    static string? RetireUsedInvite(string artifacts)=>WithInviteLock(artifacts,()=>
    {
        var setting=ReadInviteSetting(artifacts);
        if(setting==null || InviteLimit(setting) is not { } limit) return null;
        if(ActiveInviteUses(artifacts,setting) is var used && used>=limit)
        {
            File.Delete(Path.Combine(artifacts,InviteFile));
            return $"Invitation code used {used} time(s) as set and cleared.";
        }
        return null;
    });

    static InviteSetting? PromoteQueuedInvite(string artifacts)=>WithInviteLock(artifacts,()=>
    {
        if(ReadInviteSetting(artifacts)!=null) return null;
        var queue=ReadInviteQueue(artifacts);
        if(queue.Count==0) return null;
        var next=queue[0] with { Updated=DateTimeOffset.Now };
        queue.RemoveAt(0);
        WriteInviteQueue(artifacts,queue);
        WriteInviteSetting(artifacts,next);
        return next;
    });

    // Offline check in a temporary folder: a single-use code retires after its registrations, the queue advances and a
    // permanent code is never retired.
    static void VerifyInviteQueue()
    {
        var dir=Path.Combine(Path.GetTempPath(),"sekai-invite-check-"+Environment.ProcessId);
        Directory.CreateDirectory(Path.Combine(dir,"invites"));
        try
        {
            var start=DateTimeOffset.Now.AddMinutes(-5);
            WriteInviteSetting(dir,new InviteSetting("CHECKAAAAAAAAA1","once",start,2));
            WriteInviteQueue(dir,[new InviteSetting("CHECKBBBBBBBBB2","once",start),new InviteSetting("CHECKCCCCCCCCC3","permanent",start,5)]);
            void Register(string code,int minutes)
            {
                var history=ReadInviteHistory(dir);
                history.Add(new InviteRecord(DateTimeOffset.Now.AddMinutes(minutes),code,"",null,null,"registered"));
                WriteJsonAtomic(Path.Combine(dir,InviteHistoryFile),history);
            }
            string Active()=>ReadInviteSetting(dir) is { } s?$"{s.Code[..6]}({ActiveInviteUses(dir,s)}/{InviteLimit(s)?.ToString()??"∞"})":"none";
            if(InviteCapacity(dir)!=null) throw new Exception("A queued permanent code must make the capacity unlimited.");
            if(InviteCapacity(new InviteSetting("CHECKAAAAAAAAA1","once",start,2),1,[new InviteSetting("CHECKBBBBBBBBB2","once",start,3)])!=4) throw new Exception("Single-use capacity is not the remaining registrations.");
            Register("CHECKAAAAAAAAA1",-10);
            if(RetireUsedInvite(dir)!=null) throw new Exception("A registration before activation was counted.");
            Register("CHECKAAAAAAAAA1",-1);
            if(RetireUsedInvite(dir)!=null || PromoteQueuedInvite(dir)!=null) throw new Exception("Code retired before its limit: "+Active());
            Register("CHECKAAAAAAAAA1",-1);
            if(RetireUsedInvite(dir)==null || PromoteQueuedInvite(dir)?.Code!="CHECKBBBBBBBBB2") throw new Exception("Limit 2 did not advance to the once code: "+Active());
            Register("CHECKBBBBBBBBB2",1);
            if(RetireUsedInvite(dir)==null || PromoteQueuedInvite(dir)?.Code!="CHECKCCCCCCCCC3" || ReadInviteQueue(dir).Count!=0) throw new Exception("Once code did not advance: "+Active());
            for(var i=0;i<12;i++) Register("CHECKCCCCCCCCC3",2);
            if(RetireUsedInvite(dir)!=null || ReadInviteSetting(dir)?.Code!="CHECKCCCCCCCCC3") throw new Exception("A permanent code was retired: "+Active());
            // Verify active-code reorder guard when active has 0 uses vs >0 uses.
            var idleDir=Path.Combine(dir,"idle");
            Directory.CreateDirectory(Path.Combine(idleDir,"invites"));
            var idleCode=new InviteSetting("CHECKDDDDDDDDD4","once",start,2);
            WriteInviteSetting(idleDir,idleCode);
            if(!CanReorderActiveInvite(-999,idleDir,idleCode)) throw new Exception("Idle unused active code should be reorderable.");
            WriteJsonAtomic(Path.Combine(idleDir,InviteHistoryFile),new[]{new InviteRecord(DateTimeOffset.Now,"CHECKDDDDDDDDD4","once",null,null,"registered")});
            if(CanReorderActiveInvite(-999,idleDir,idleCode)) throw new Exception("Already registered active code must not be reorderable.");
            // Verify global pool 1-use slot limit and saturation skip logic across instances.
            var origRoot=ControlRoot;
            var origCache=InstanceCache;
            var gRoot=Path.Combine(dir,"global");
            try
            {
                ControlRoot=gRoot;
                lock(InstancesLock) InstanceCache=(DateTime.UtcNow.AddMinutes(5),new Dictionary<int,string> { [1]="Test1",[2]="Test2" });
                var art1=ArtifactsDir(1);
                var art2=ArtifactsDir(2);
                Directory.CreateDirectory(Path.Combine(art1,"invites"));
                Directory.CreateDirectory(Path.Combine(art2,"invites"));
                var t0=DateTimeOffset.Now.AddMinutes(-2);
                WriteGlobalPool(new GlobalPoolFile(true,[
                    new GlobalPoolItem("GLOBALAAAAAAAA1","once",1,t0),
                    new GlobalPoolItem("GLOBALBBBBBBBB2","once",3,t0)
                ]));
                // VM 1 claims Priority #1 as a 1-use slot; while VM 1 is running/pending, VM 2 must skip saturated #1 and claim #2.
                var first=EnsureGlobalInvite(art1,1);
                if(first?.Code!="GLOBALAAAAAAAA1" || InviteLimit(first)!=1) throw new Exception("Global pool did not assign priority #1 as 1-use slot.");
                PendingWork[1]="running";
                var stats1=ComputeGlobalPoolStats(ReadGlobalPool());
                if(stats1[0].Projected!=1 || stats1[0].Done!=0) throw new Exception($"Expected 1(0), got {stats1[0].Projected}({stats1[0].Done}).");
                var second=EnsureGlobalInvite(art2,2);
                if(second?.Code!="GLOBALBBBBBBBB2") throw new Exception("Global pool did not skip saturated priority #1 for VM 2.");
                // Completing registration on VM 1 updates Done to 1 and retires VM 1's 1-use slot.
                PendingWork.TryRemove(1,out _);
                WriteJsonAtomic(Path.Combine(art1,InviteHistoryFile),new[]{new InviteRecord(DateTimeOffset.Now,"GLOBALAAAAAAAA1","once",null,null,"registered")});
                RetireUsedInvite(art1);
                var next1=EnsureGlobalInvite(art1,1);
                if(next1?.Code!="GLOBALBBBBBBBB2") throw new Exception("Global pool did not advance VM 1 to priority #2 after quota completion.");
            }
            finally
            {
                PendingWork.TryRemove(1,out _);
                lock(InstancesLock) InstanceCache=origCache;
                ControlRoot=origRoot;
            }
            Console.WriteLine($"Invite queue verified: once x2 -> once -> permanent, earlier registrations ignored, permanent kept after 12, reorder guard & global pool ok; active {Active()}.");
        }
        finally { Directory.Delete(dir,true); }
    }

    static List<InviteRecord> ReadInviteHistory(string artifacts)
    {
        try { var path=Path.Combine(artifacts,InviteHistoryFile); return File.Exists(path)?JsonSerializer.Deserialize<List<InviteRecord>>(File.ReadAllText(path))!:[]; }
        catch { return []; }
    }

    record GlobalPoolItem(string Code,string Mode,int? Limit,DateTimeOffset Added);
    record GlobalPoolFile(bool Enabled,List<GlobalPoolItem> Items);
    record GlobalPoolItemStats(string Code,string Mode,int? Limit,DateTimeOffset Added,string? Owner,int Done,int InFlight,int Projected,int[] RunningVms,bool Completed);

    static string GlobalPoolPath=>Path.Combine(Workspace,"local","global-invites.json");
    static T WithGlobalInviteLock<T>(Func<T> action)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(GlobalPoolPath)!);
        for(var attempt=0;;attempt++)
        {
            try { using var hold=new FileStream(Path.Combine(Path.GetDirectoryName(GlobalPoolPath)!,"global-invites.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None); return action(); }
            catch(IOException) when(attempt<60) { Thread.Sleep(50); }
        }
    }
    static GlobalPoolFile ReadGlobalPool()
    {
        try
        {
            if(!File.Exists(GlobalPoolPath)) return new(false,[]);
            var pool=JsonSerializer.Deserialize<GlobalPoolFile>(File.ReadAllText(GlobalPoolPath));
            if(pool==null) return new(false,[]);
            var valid=(pool.Items??[]).Where(i=>ValidInviteCode(i.Code) && i.Mode is "once" or "permanent")
                .Select(i=>i.Mode=="once"?i with { Limit=Math.Clamp(i.Limit??1,1,MaxGlobalOnceUses) }:i with { Limit=null }).ToList();
            return new(pool.Enabled,valid);
        }
        catch { return new(false,[]); }
    }
    static void WriteGlobalPool(GlobalPoolFile pool) { Directory.CreateDirectory(Path.GetDirectoryName(GlobalPoolPath)!); WriteJsonAtomic(GlobalPoolPath,pool); }

    static int GlobalCodeDone(GlobalPoolItem item,IEnumerable<InviteRecord> allRecords)=>
        allRecords.Count(r=>r.Code==item.Code && r.Result=="registered" && r.Time>=item.Added);

    static List<GlobalPoolItemStats> ComputeGlobalPoolStats(GlobalPoolFile pool,int? excludeInstance=null)
    {
        var allHist=AllowedInstances.Keys.ToDictionary(n=>n,n=>ReadInviteHistory(ArtifactsDir(n)));
        var allRecords=allHist.Values.SelectMany(h=>h).ToList();
        var owners=allRecords.Where(r=>r.Nickname is { Length: >0 }).OrderBy(r=>r.Time).GroupBy(r=>r.Code).ToDictionary(g=>g.Key,g=>g.Last().Nickname!);
        var runningVms=new Dictionary<string,List<int>>();
        foreach(var n in AllowedInstances.Keys.Where(n=>n!=excludeInstance))
        {
            if(!IsWorkerRunning(n) && !PendingWork.ContainsKey(n)) continue;
            var dir=ArtifactsDir(n);
            if(ReadInviteSetting(dir) is not { } s) continue;
            var used=ActiveInviteUses(dir,s);
            if(s.Global?used==0:(InviteLimit(s) is not { } lim || used<lim))
                (runningVms.TryGetValue(s.Code,out var list)?list:(runningVms[s.Code]=[])).Add(n);
        }
        return pool.Items.Select(item=>
        {
            var done=GlobalCodeDone(item,allRecords);
            var vms=runningVms.TryGetValue(item.Code,out var l)?l.ToArray():[];
            var inFlight=vms.Length;
            var projected=done+inFlight;
            var completed=item.Mode=="once" && item.Limit is { } lim && done>=lim && inFlight==0;
            return new GlobalPoolItemStats(item.Code,item.Mode,item.Limit,item.Added,owners.GetValueOrDefault(item.Code),done,inFlight,projected,vms,completed);
        }).ToList();
    }

    static int? GlobalPoolCapacity(int? excludeInstance=null)
    {
        var pool=ReadGlobalPool();
        if(pool.Items.Any(i=>i.Mode=="permanent")) return null;
        var stats=ComputeGlobalPoolStats(pool,excludeInstance);
        return stats.Sum(s=>s.Limit is { } lim?Math.Max(0,lim-s.Projected):0);
    }

    static InviteSetting? EnsureGlobalInvite(string artifacts,int instance)=>WithGlobalInviteLock(()=>
    {
        var pool=ReadGlobalPool();
        if(!pool.Enabled) return ReadInviteSetting(artifacts);
        var current=WithInviteLock(artifacts,()=>ReadInviteSetting(artifacts));
        var allRecords=AllowedInstances.Keys.SelectMany(n=>ReadInviteHistory(ArtifactsDir(n))).ToList();
        if(current!=null)
        {
            if(ActiveInviteUses(artifacts,current)>0 ||
                (CurrentLoopInvite(artifacts) is { } cli && cli.Code==current.Code && cli.Time>=current.Updated && cli.Result=="confirming"))
                return current;
            if(current.Global && pool.Items.FirstOrDefault(i=>i.Code==current.Code) is { } match)
            {
                var done=GlobalCodeDone(match,allRecords);
                if(match.Mode=="permanent" || (match.Limit is { } lim && done<lim)) return current;
            }
        }
        var stats=ComputeGlobalPoolStats(pool,excludeInstance:instance);
        var nextItem=stats.FirstOrDefault(s=>s.Mode=="permanent" || (s.Limit is { } lim && s.Projected<lim));
        InviteSetting? assigned=null;
        WithInviteLock(artifacts,()=>
        {
            if(nextItem==null) { if(current?.Global==true) File.Delete(Path.Combine(artifacts,InviteFile)); }
            else
            {
                assigned=new InviteSetting(nextItem.Code,nextItem.Mode,DateTimeOffset.Now,1,Global:true);
                WriteInviteSetting(artifacts,assigned);
            }
            return 0;
        });
        return assigned;
    });

    // A --code argument is fixed for the run; otherwise the saved setting is re-read so a code added mid-run is used.
    static bool RefreshInviteCode()
    {
        if(InviteMode=="cli") return true;
        if(RetireUsedInvite("artifacts") is { } note) Console.WriteLine(note);
        InviteSetting? saved;
        if(ReadGlobalPool().Enabled) saved=EnsureGlobalInvite("artifacts",Instance);
        else
        {
            saved=ReadInviteSetting("artifacts");
            if(saved==null && PromoteQueuedInvite("artifacts") is { } next) { saved=next; Console.WriteLine($"Next queued invitation code activated ({next.Mode}, up to {InviteLimit(next)?.ToString()??"unlimited"})."); }
        }
        InviteCode=saved?.Code; InviteMode=saved?.Mode;
        return InviteCode!=null;
    }

    // Ends at the success dialog: after stories, reward popups can cover the "入力済み" label,
    // so the registered state is verified afterwards by OpenAndInspectInvitation (which clears notices and times out).
    static List<Step> InvitationEntrySteps()
    {
        var flow=LoadSteps();
        var first=flow.FindIndex(s=>s.Name=="invite-code-open"); var last=flow.FindIndex(s=>s.Name=="invite-success-dismiss");
        return flow.GetRange(first,last-first+1);
    }

    static async Task RecordInvite(string result,bool capture)
    {
        Directory.CreateDirectory("artifacts/invites");
        string? image=null,nickname=null;
        if(capture && HasCurrentFrame)
        {
            image=$"{DateTime.Now:yyyyMMdd-HHmmss}-{InviteCode}.webp";
            SaveCurrentEvidence(Path.Combine("artifacts/invites",image));
            if(result=="confirming")
                try { using var screen=LoadCurrentScreen(); nickname=await ReadInviteNickname(screen); }
                catch(Exception ex) { Console.WriteLine("Nickname OCR skipped: "+ex.Message); }
        }
        var history=ReadInviteHistory("artifacts");
        history.Add(new InviteRecord(DateTimeOffset.Now,InviteCode??"",InviteMode??"",nickname,image,result,ReadLoop("artifacts")?.Index));
        await File.WriteAllTextAsync(Path.Combine("artifacts",InviteHistoryFile),JsonSerializer.Serialize(history,JsonOptions));
        if(nickname!=null) Console.WriteLine($"Invitation target: {nickname} ({image})");
    }

    // OCR the whole "プレイヤー名 | name" row: the label anchors recognition of short Latin names.
    static async Task<string?> ReadInviteNickname(Bitmap screen)
    {
        var text=(await OcrRegion(screen,new Rectangle(250,288,470,34),3)).Normalize(System.Text.NormalizationForm.FormKC);
        var label=text.IndexOf('名');
        var name=(label>=0?text[(label+1)..]:text).TrimStart('|','｜',' ','ー','一','－','-');
        return name.Length>0?name:null;
    }
    static async Task CompleteInvite()
    {
        var history=ReadInviteHistory("artifacts");
        var last=history.FindLastIndex(r=>r.Code==InviteCode && r.Result=="confirming");
        if(last>=0) history[last]=history[last] with { Result="registered" };
        else history.Add(new InviteRecord(DateTimeOffset.Now,InviteCode??"",InviteMode??"",null,null,"registered",ReadLoop("artifacts")?.Index));
        await File.WriteAllTextAsync(Path.Combine("artifacts",InviteHistoryFile),JsonSerializer.Serialize(history,JsonOptions));
        if(RetireUsedInvite("artifacts") is { } note) Console.WriteLine(note);
        if(!ReadGlobalPool().Enabled && PromoteQueuedInvite("artifacts") is { } next) Console.WriteLine($"Next queued invitation code activated ({next.Mode}, up to {InviteLimit(next)?.ToString()??"unlimited"}).");
    }
}
