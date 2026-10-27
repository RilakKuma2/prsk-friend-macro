using System.Drawing;
using System.Text.Json;
using System.Text.RegularExpressions;

// Per-instance invitation codes (artifacts/invite.json) and the nickname captures taken at registration.
// "once" codes are cleared after one registration; "permanent" codes stay until their limit (at most 10).
// Further codes wait in artifacts/invite-queue.json and the next one is activated when the current one is used up.
internal static partial class Program
{
    record InviteSetting(string Code,string Mode,DateTimeOffset Updated,int? Limit=null);
    record InviteRecord(DateTimeOffset Time,string Code,string Mode,string? Nickname,string? Image,string Result,int? Loop=null,string? Rewards=null);

    static string? InviteMode;
    static bool SkipInvitation;
    const string InviteFile="invite.json";
    const string InviteQueueFile="invite-queue.json";
    const string InviteHistoryFile="invites/history.json";

    // The game accepts at most 10 registrations per invitation code; histories of every instance count.
    const int MaxInviteUses=10;
    static int InviteUses(string code)=>AllowedInstances.Keys.Sum(n=>ReadInviteHistory(ArtifactsDir(n)).Count(r=>r.Code==code && r.Result=="registered"));
    static void ClearExhaustedCode(string code)
    {
        foreach(var n in AllowedInstances.Keys)
            if(ReadInviteSetting(ArtifactsDir(n))?.Code==code)
            {
                File.Delete(Path.Combine(ArtifactsDir(n),InviteFile));
                AppendControlLog(n,T($"초대 코드가 {MaxInviteUses}번 등록되어 지웠어요.",$"招待コードが{MaxInviteUses}回登録されたため消しました。",$"Cleared the invitation code after {MaxInviteUses} registrations."));
            }
    }

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

    static int InviteLimit(InviteSetting setting)=>setting.Mode=="once"?1:Math.Clamp(setting.Limit??MaxInviteUses,1,MaxInviteUses);
    // Registrations made in this instance since the code became active.
    static int ActiveInviteUses(string artifacts,InviteSetting setting)=>ReadInviteHistory(artifacts).Count(r=>r.Code==setting.Code && r.Result=="registered" && r.Time>=setting.Updated);

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

    // Clears the active code once it reached its own limit or the game's 10-registration cap.
    static string? RetireUsedInvite(string artifacts)=>WithInviteLock(artifacts,()=>
    {
        var setting=ReadInviteSetting(artifacts);
        if(setting==null) return null;
        if(InviteUses(setting.Code) is var total && total>=MaxInviteUses) { ClearExhaustedCode(setting.Code); return $"Invitation code reached {total} registrations and was cleared."; }
        if(ActiveInviteUses(artifacts,setting) is var used && used>=InviteLimit(setting))
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
        InviteSetting? next=null;
        while(queue.Count>0 && next==null)
        {
            var item=queue[0]; queue.RemoveAt(0);
            if(InviteUses(item.Code)<MaxInviteUses) next=item with { Updated=DateTimeOffset.Now };
        }
        WriteInviteQueue(artifacts,queue);
        if(next!=null) WriteInviteSetting(artifacts,next);
        return next;
    });

    // Offline check in a temporary folder: a limited code retires after its registrations and the queue advances.
    static void VerifyInviteQueue()
    {
        var dir=Path.Combine(Path.GetTempPath(),"sekai-invite-check-"+Environment.ProcessId);
        Directory.CreateDirectory(Path.Combine(dir,"invites"));
        try
        {
            var start=DateTimeOffset.Now.AddMinutes(-5);
            WriteInviteSetting(dir,new InviteSetting("CHECKAAAAAAAAA1","permanent",start,2));
            WriteInviteQueue(dir,[new InviteSetting("CHECKBBBBBBBBB2","once",start),new InviteSetting("CHECKCCCCCCCCC3","permanent",start,5)]);
            void Register(string code,int minutes)
            {
                var history=ReadInviteHistory(dir);
                history.Add(new InviteRecord(DateTimeOffset.Now.AddMinutes(minutes),code,"",null,null,"registered"));
                WriteJsonAtomic(Path.Combine(dir,InviteHistoryFile),history);
            }
            string Active()=>ReadInviteSetting(dir) is { } s?$"{s.Code[..6]}({ActiveInviteUses(dir,s)}/{InviteLimit(s)})":"none";
            Register("CHECKAAAAAAAAA1",-10);
            if(RetireUsedInvite(dir)!=null) throw new Exception("A registration before activation was counted.");
            Register("CHECKAAAAAAAAA1",-1);
            if(RetireUsedInvite(dir)!=null || PromoteQueuedInvite(dir)!=null) throw new Exception("Code retired before its limit: "+Active());
            Register("CHECKAAAAAAAAA1",-1);
            if(RetireUsedInvite(dir)==null || PromoteQueuedInvite(dir)?.Code!="CHECKBBBBBBBBB2") throw new Exception("Limit 2 did not advance to the once code: "+Active());
            Register("CHECKBBBBBBBBB2",1);
            if(RetireUsedInvite(dir)==null || PromoteQueuedInvite(dir)?.Code!="CHECKCCCCCCCCC3" || ReadInviteQueue(dir).Count!=0) throw new Exception("Once code did not advance: "+Active());
            Console.WriteLine($"Invite queue verified: limit 2 -> once -> limit 5, earlier registrations ignored; active {Active()}.");
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
        if(saved==null && PromoteQueuedInvite("artifacts") is { } next) { saved=next; Console.WriteLine($"Next queued invitation code activated ({next.Mode}, up to {InviteLimit(next)})."); }
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
        if(PromoteQueuedInvite("artifacts") is { } next) Console.WriteLine($"Next queued invitation code activated ({next.Mode}, up to {InviteLimit(next)}).");
    }
}
