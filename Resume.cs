using System.Diagnostics;
using System.Drawing;
using System.Text.Json;

internal static partial class Program
{
    static bool ResumeAuto;
    static bool ResumeStory;
    static bool ResumePostStory;
    static string ResolveResumePoint(string? requested)
    {
        var flow=LoadSteps();
        var point=requested??"saved";
        if(point=="saved")
        {
            var checkpoint=new[]{"artifacts/current-step.json","artifacts/current-step.pending.json"}
                .Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if(checkpoint==null) throw new Exception(T("저장된 진행 기록이 없습니다. 재개 지점을 직접 선택하세요.","保存された進行記録がありません。再開地点を選んでください。","No saved progress. Choose a resume point."));
            using var state=JsonDocument.Parse(File.ReadAllText(checkpoint));
            point=state.RootElement.GetProperty("Step").GetString()!;
            if(state.RootElement.TryGetProperty("Status",out var status) && status.GetString()=="executed")
            {
                var index=flow.FindIndex(s=>s.Name==point);
                if(index==flow.Count-1) throw new Exception(T("이미 마지막 단계까지 완료했습니다.","すでに最後の段階まで完了しています。","Already completed the last step."));
                if(index>=0) point=flow[index+1].Name;
            }
        }
        ResumeAuto=point is "auto" or "autolive" or "auto-live-started" or "auto-live-start" or "note-speed-confirm";
        if(ResumeAuto) return "auto-live-started";
        ResumeStory=point is "story" or "story-reading";
        if(ResumeStory) return "story-reading";
        ResumePostStory=point is "post-story" or "event-stories-complete";
        if(ResumePostStory) return "event-stories-complete";
        var stage=LoadStages().FirstOrDefault(s=>s.Name==point);
        if(stage!=null) point=stage.Start;
        if(!flow.Any(s=>s.Name==point)) throw new Exception("Unknown resume point: "+point);
        return point;
    }
    static async Task SaveCheckpoint(string step,string status)
    {
        ActiveStep=step;
        var value=JsonSerializer.Serialize(new { Instance,Step=step,Status=status,Time=DateTimeOffset.Now },JsonOptions);
        await WriteCheckpoint(value,"artifacts/current-step.json");
    }
    static async Task WriteCheckpoint(string value,string path)
    {
        var pending=Path.ChangeExtension(path,"pending.json");
        for(var attempt=0;attempt<20;attempt++)
        {
            try
            {
                await File.WriteAllTextAsync(pending,value);
                File.Move(pending,path,true);
                return;
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
            {
                if(attempt==19)
                {
                    // Readers can temporarily deny replacement on Windows. The pending file
                    // remains a valid resume record; status reporting must not kill the worker.
                    Console.WriteLine($"Checkpoint replacement deferred; latest state retained in {pending}: {ex.Message}");
                    return;
                }
                await Task.Delay(100);
            }
        }
    }
    static async Task VerifyCheckpointContention()
    {
        Directory.CreateDirectory("artifacts");
        var path=Path.Combine("artifacts","checkpoint-test-"+Guid.NewGuid().ToString("N")+".json");
        var pending=Path.ChangeExtension(path,"pending.json");
        try
        {
            await File.WriteAllTextAsync(path,"old");
            Task write;
            using(var reader=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                write=WriteCheckpoint("new",path);
                await Task.Delay(350);
                if(write.IsCompleted) throw new Exception("Checkpoint test did not encounter the intended reader lock.");
            }
            await write;
            if(await File.ReadAllTextAsync(path)!="new" || File.Exists(pending)) throw new Exception("Checkpoint retry did not replace the old record.");
            using(var reader=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
                await WriteCheckpoint("latest",path);
            if(await File.ReadAllTextAsync(pending)!="latest") throw new Exception("Pending resume record was lost under persistent reader contention.");
            Console.WriteLine("Checkpoint verification passed: transient reader lock recovered; persistent lock retained latest pending record without stopping the worker. No device commands sent.");
        }
        finally { File.Delete(path); File.Delete(pending); }
    }
    static async Task EnsureRunning()
    {
        var row=await VmRow(Instance);
        if(row==null || row[1]!=Target) throw new Exception("Instance identity differs; nothing launched.");
        if(row[4]!="1") await VmLaunch(Instance);
        var deadline=DateTime.UtcNow.AddMinutes(3);
        var booted=false;
        while(DateTime.UtcNow<deadline && !StopRequested)
        {
            try { await ConnectVm(Instance); if((await Shell("getprop sys.boot_completed")).Trim()=="1") { booted=true; break; } } catch { }
            await Task.Delay(2000);
        }
        if(!booted) throw new Exception("Emulator boot timeout. Account data was not reset.");
        if(!await LiveResolutionOk(Serial)) await EnsureResolution();
        LastGuard=DateTime.MinValue; await Guard();
        var current=await Shell(ResumedActivity);
        if(!current.Contains(Package) && !current.Contains("com.google.android.gms"))
            await Shell($"monkey -p {Package} -c android.intent.category.LAUNCHER 1");
    }
    static async Task RecoverAutoLive()
    {
        var flow=LoadSteps();
        var deadline=DateTime.UtcNow.AddMinutes(4);
        var pause=new Step("live-active","templates/auto-live-pause.png",0,0,907,22,20,20,"checkpoint");
        await SaveCheckpoint("auto-live-started","waiting");
        Console.WriteLine("Recovering interrupted auto live; no account reset or gacha actions.");
        while(DateTime.UtcNow<deadline)
        {
            if(StopRequested || File.Exists("artifacts/macro.stop")) throw new OperationCanceledException();
            var activity=await Shell(ResumedActivity);
            if(activity.Contains("com.google.android.gms") && (activity.Contains("MinuteMaid") || activity.Contains("SignIn")))
            { await Shell("input keyevent 4"); await Task.Delay(1500); continue; }
            await Snapshot("artifacts/current.png",true);
            using var screen=LoadCurrentScreen();
            if(await DismissNotice(screen)) { await Task.Delay(700); continue; }
            bool Is(string name)=>Matches(screen,flow.Single(s=>s.Name==name));
            if(IsAutoEnd(screen)) { From="auto-end-notice"; Console.WriteLine("Completed auto run found; continuing to completion and story notices."); return; }
            if(Matches(screen,pause)) { From="auto-live-started"; Console.WriteLine("Live already active; monitoring only."); return; }
            if(Is("result-story-unlocks")) { await DismissRepeated(flow.Single(s=>s.Name=="result-story-unlocks")); continue; }
            if(Is("event-from-result")) { await Shell("input tap 850 500"); await Task.Delay(1500); continue; }
            if(Is("live-song-search-focus")) { From="live-song-search-focus"; return; }
            if(Is("auto-repeat-confirm")) { From="auto-repeat-confirm"; return; }
            if(Is("auto-live-start")) { From="auto-repeat-settings"; return; }
            if(Is("solo-live-open")) { From="solo-live-open"; return; }
            if(Is("live-open")) { From="live-open"; return; }
            if(Is("title-start")) { await Shell("input tap 480 464"); await Task.Delay(2000); }
            else if(Is("campaign-bonus")) await Shell("input tap 480 420");
            else if(await DismissTutorialPanel(screen)) { }
            await Task.Delay(1000);
        }
        throw new Exception(T("재개할 게임 화면을 확인하지 못했습니다. artifacts/current.png를 확인하세요. 계정 초기화는 하지 않았습니다.","再開するゲーム画面を確認できませんでした。artifacts/current.pngを確認してください。アカウント初期化は行っていません。","Could not recognize a game screen to resume from. Check artifacts/current.png. The account was not reset."));
    }
    static async Task<bool> DismissNotice(Bitmap screen)
    {
        if(IsFeedbackRequest(screen))
        {
            await Shell("input tap 390 406");
            Console.WriteLine("Closed feedback request with Do not send; no feedback submitted.");
            return true;
        }
        if(IsChallengeUnlock(screen))
        {
            await Shell("input tap 390 318");
            Console.WriteLine("Closed challenge-live unlock notice; continuing current task.");
            return true;
        }
        if(IsRatingPrompt(screen))
        {
            await Shell("input tap 753 30");
            Console.WriteLine("Closed rating survey without submitting a response; retaining current progress.");
            return true;
        }
        var header=new Step("notice-header","templates/notice-header.png",0,0,110,22,86,20,"checkpoint");
        var close=new Step("notice-close","templates/notice-close.png",860,33,852,24,18,18,"tap");
        if(!Matches(screen,header) || !Matches(screen,close)) return false;
        await Shell("input tap 860 33");
        Console.WriteLine("Dismissed notice window; retaining current progress.");
        return true;
    }
    static bool IsRatingPrompt(Bitmap screen)
    {
        var heading=new Step("rating-heading","templates/rating-heading.png",0,0,425,105,112,16,"checkpoint");
        var prefix=new Step("rating-heading-prefix","templates/rating-heading-prefix.png",0,0,425,105,64,16,"checkpoint");
        var close=new Step("rating-close","templates/rating-close.png",0,0,745,22,18,18,"checkpoint");
        return (Matches(screen,heading) || Matches(screen,prefix)) && Matches(screen,close);
    }
    static bool IsChallengeUnlock(Bitmap screen)=>
        Matches(screen,new Step("challenge-heading","templates/challenge-unlock-heading.png",0,0,334,225,292,18,"checkpoint")) &&
        Matches(screen,new Step("challenge-close","templates/challenge-unlock-close.png",0,0,365,309,50,18,"checkpoint"));
    static bool IsFeedbackRequest(Bitmap screen)=>
        Matches(screen,new Step("feedback-heading","templates/feedback-heading.png",0,0,425,153,112,18,"checkpoint")) &&
        Matches(screen,new Step("feedback-decline","templates/feedback-do-not-send.png",0,0,362,397,56,18,"checkpoint"));
    static bool IsAreaMain(Bitmap screen)
    {
        // Opaque globe is the primary home-screen anchor. The translucent gift icon is excluded.
        var globe=new Step("home-globe","templates/area-open.png",0,0,18,18,27,27,"checkpoint");
        if(!Matches(screen,globe)) return false;
        var cyan=0;
        for(var y=477;y<501;y++) for(var x=883;x<929;x++)
        { var p=screen.GetPixel(x,y); if(p.G>170 && p.B>145 && p.R<145 && p.G-p.R>45) cyan++; }
        return cyan>=60;
    }
    static bool IsAutoEnd(Bitmap screen)
    {
        // 20x18 pixels entirely inside Miku's face: no background, event logo or message text.
        using var expected=new Bitmap("templates/auto-end-miku.png");
        for(var dy=-6;dy<=6;dy+=2) for(var dx=-6;dx<=6;dx+=2)
            if(DifferenceAt(screen,expected,464+dx,247+dy,20,18)<10) return true;
        return false;
    }
    static async Task DismissAutoEnd()
    {
        var absent=0; var taps=0;
        var deadline=DateTime.UtcNow.AddSeconds(20);
        while(DateTime.UtcNow<deadline)
        {
            if(StopRequested || File.Exists("artifacts/macro.stop")) throw new OperationCanceledException();
            await Snapshot("artifacts/current.png",true);
            using var screen=LoadCurrentScreen();
            if(IsAutoEnd(screen))
            {
                absent=0; await Shell("input tap 480 400"); taps++;
            }
            else if(++absent>=3) { Console.WriteLine($"Auto completion notice cleared ({taps} taps); next: story unlock notices."); return; }
            await Task.Delay(400);
        }
        throw new Exception("Auto completion notice did not close. No new live was started.");
    }
}
