using System.Drawing;
using System.Text.Json;
using System.Text.RegularExpressions;

// Per-instance invitation codes (artifacts/invite.json) and the nickname captures taken at registration.
// A "permanent" code is used for every loop until it is deleted; a "once" code is cleared after its set number of
// registrations (1 unless set, at most 10). Further codes wait in artifacts/invite-queue.json and the next one is activated when the current one is used up.
internal static partial class Program
{
    record InviteSetting(string Code,string Mode,DateTimeOffset Updated,int? Limit=null);
    record InviteRecord(DateTimeOffset Time,string Code,string Mode,string? Nickname,string? Image,string Result,int? Loop=null,string? Rewards=null);

    static string? InviteMode;
    static bool SkipInvitation;
    const string InviteFile="invite.json";
    const string InviteQueueFile="invite-queue.json";
    const string InviteHistoryFile="invites/history.json";

    const int MaxOnceUses=10;

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

    // Registrations a code serves: null for a permanent code, which has no limit.
    static int? InviteLimit(InviteSetting setting)=>setting.Mode=="once"?Math.Clamp(setting.Limit??1,1,MaxOnceUses):null;
    // Registrations made in this instance since the code became active.
    static int ActiveInviteUses(string artifacts,InviteSetting setting)=>ReadInviteHistory(artifacts).Count(r=>r.Code==setting.Code && r.Result=="registered" && r.Time>=setting.Updated);
    // Registrations the active code and the queue still allow: null once a permanent code is among them.
    static int? InviteCapacity(InviteSetting? active,int used,IEnumerable<InviteSetting> queue)
    {
        var codes=(active==null?queue:queue.Prepend(active)).ToList();
        return codes.Any(s=>s.Mode=="permanent")?null:Math.Max(0,codes.Sum(s=>InviteLimit(s)!.Value)-used);
    }
    static int? InviteCapacity(string artifacts)
    {
        var active=ReadInviteSetting(artifacts);
        return InviteCapacity(active,active==null?0:ActiveInviteUses(artifacts,active),ReadInviteQueue(artifacts));
    }
    // Player names read from the confirmation dialog when a code was entered, on any instance: the code's owner.
    static Dictionary<string,string> InviteOwners()=>AllowedInstances.Keys.SelectMany(n=>ReadInviteHistory(ArtifactsDir(n)))
        .Where(r=>r.Nickname is { Length: >0 }).OrderBy(r=>r.Time).GroupBy(r=>r.Code).ToDictionary(g=>g.Key,g=>g.Last().Nickname!);

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
            Console.WriteLine($"Invite queue verified: once x2 -> once -> permanent, earlier registrations ignored, permanent kept after 12; active {Active()}.");
        }
        finally { Directory.Delete(dir,true); }
    }

    static List<InviteRecord> ReadInviteHistory(string artifacts)
    {
        try { var path=Path.Combine(artifacts,InviteHistoryFile); return File.Exists(path)?JsonSerializer.Deserialize<List<InviteRecord>>(File.ReadAllText(path))!:[]; }
        catch { return []; }
    }

    // A --code argument is fixed for the run; otherwise the saved setting is re-read so a code added mid-run is used.
    static bool RefreshInviteCode()
    {
        if(InviteMode=="cli") return true;
        if(RetireUsedInvite("artifacts") is { } note) Console.WriteLine(note);
        var saved=ReadInviteSetting("artifacts");
        if(saved==null && PromoteQueuedInvite("artifacts") is { } next) { saved=next; Console.WriteLine($"Next queued invitation code activated ({next.Mode}, up to {InviteLimit(next)?.ToString()??"unlimited"})."); }
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
        if(capture && File.Exists("artifacts/current.png"))
        {
            image=$"{DateTime.Now:yyyyMMdd-HHmmss}-{InviteCode}.png";
            File.Copy("artifacts/current.png",Path.Combine("artifacts/invites",image),true);
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
        if(PromoteQueuedInvite("artifacts") is { } next) Console.WriteLine($"Next queued invitation code activated ({next.Mode}, up to {InviteLimit(next)?.ToString()??"unlimited"}).");
    }
}
