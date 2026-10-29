using System.Text.Json;

// A loop is one account: it starts at an account reset (artifacts/loop.json) and ends after the last plan stage.
// Repeating a full run creates a new account each time, so it is only allowed with a permanent invitation code.
internal static partial class Program
{
    record LoopState(int Index,int Total,DateTimeOffset Started,string Unit,string[] Stages);

    const string LoopFile="loop.json";
    static int LoopIndex=1,LoopTotal=1;
    // Once this worker owns loop.json, the control panel may change its Total while it runs.
    static bool LoopFileOwned;
    static int LoopLimit()
    {
        // The panel never sets it below the loop in progress, so it is taken as is (the next index may exceed it).
        if(LoopFileOwned && ReadLoop("artifacts") is { } saved) LoopTotal=saved.Total;
        return LoopTotal;
    }

    static LoopState? ReadLoop(string artifacts)
    {
        try { var path=Path.Combine(artifacts,LoopFile); return File.Exists(path)?JsonSerializer.Deserialize<LoopState>(File.ReadAllText(path)):null; }
        catch { return null; }
    }

    // Older runs have no loop.json; their reset time still marks where the current account began.
    static DateTimeOffset? LoopStarted(string artifacts)
    {
        if(ReadLoop(artifacts) is { } loop) return loop.Started;
        var reset=Path.Combine(artifacts,"reset-before.txt");
        return File.Exists(reset)?new DateTimeOffset(File.GetLastWriteTime(reset)):null;
    }

    static InviteRecord? CurrentLoopInvite(string artifacts)
    {
        var started=LoopStarted(artifacts);
        return started==null?null:ReadInviteHistory(artifacts).LastOrDefault(r=>r.Time>=started);
    }

    static async Task WriteLoop()
    {
        var loop=new LoopState(LoopIndex,LoopLimit(),DateTimeOffset.Now,Unit,SelectedStages);
        LoopFileOwned=true;
        await File.WriteAllTextAsync("artifacts/"+LoopFile+".pending",JsonSerializer.Serialize(loop,JsonOptions));
        File.Move("artifacts/"+LoopFile+".pending","artifacts/"+LoopFile,true);
    }

    static string? RepeatBlocker()
    {
        if(StopRequested || File.Exists("artifacts/macro.stop")) return "stop requested";
        if(StageIncomplete) return "event stories incomplete";
        if(!RefreshInviteCode()) return "no invitation code left";
        if(CurrentLoopInvite("artifacts")?.Result!="registered") return "invitation was not registered in this loop";
        return null;
    }

    static async Task RunLoops(int first)
    {
        for(LoopIndex=first;LoopIndex<=LoopLimit();LoopIndex++)
        {
            if(LoopIndex>1)
            {
                if(RepeatBlocker() is { } reason) { Console.WriteLine($"Repeat stopped before loop {LoopIndex}/{LoopTotal}: {reason}."); return; }
                Console.WriteLine($"\n[{DateTimeOffset.Now:O}] Loop {LoopIndex}/{LoopTotal}");
            }
            for(var fresh=0;;fresh++)
            {
                StageIncomplete=false; SkipInvitation=false;
                if(fresh>0)
                {
                    Console.WriteLine($"Starting loop {LoopIndex} over with a new account ({fresh}/{FreshStarts}); invitation code and loops as they are.");
                    await RebootInstance();
                }
                await Reset();
                await Shell($"monkey -p {Package} -c android.intent.category.LAUNCHER 1");
                if(await ExecuteStagesRecovering(fresh<FreshStarts)) break;
            }
        }
        if(LoopTotal>1) Console.WriteLine($"All {LoopTotal} loops finished.");
    }

    // A stage that fails (a step that timed out, a game that stopped responding) is run again from its start after the
    // instance reboots. Failing again there, the loop starts over with a new account after another reboot, at most
    // FreshStarts times; the invitation code, its uses and the loop count stay as they are. Onboarding starts over at
    // once: its account is new, and the tutorial screens already passed would only be waited for. Returns false when
    // the loop should start over.
    const int FreshStarts=2;
    static async Task<bool> ExecuteStagesRecovering(bool mayStartOver)
    {
        string? resume=null;
        while(true)
        {
            try
            {
                // Every stage after onboarding begins on the home screen.
                if(resume!=null) { await RebootInstance(); await ReturnHome(); }
                await ExecuteStages(resume);
                return true;
            }
            catch(Exception ex) when(!StopRequested && !File.Exists("artifacts/macro.stop") && ProgressStage is { } stage)
            {
                Console.WriteLine($"Loop {LoopIndex} stopped in {stage}: {ex.Message}");
                if(stage!=resume && stage!="onboarding") { Console.WriteLine($"Rebooting the instance and running {stage} again from its start."); resume=stage; continue; }
                if(!mayStartOver) throw;
                return false;
            }
        }
    }
    // Off, settings checked while off (resolution, light profile), on, booted, the game launched.
    static async Task RebootInstance()
    {
        Console.WriteLine($"Rebooting instance {Instance}.");
        await QuitVm(Instance);
        await EnsureResolution();
        await EnsureRunning();
        await Task.Delay(3000);
    }
    // After a reboot: through the title, bonuses and notices to the home screen.
    static async Task ReturnHome()
    {
        var flow=LoadSteps();
        var deadline=DateTime.UtcNow.AddMinutes(5);
        while(DateTime.UtcNow<deadline)
        {
            if(StopRequested || File.Exists("artifacts/macro.stop")) throw new OperationCanceledException();
            var activity=await Shell(ResumedActivity);
            if(activity.Contains("com.google.android.gms") && (activity.Contains("MinuteMaid") || activity.Contains("SignIn")))
            { await Shell("input keyevent 4"); await Task.Delay(1500); continue; }
            await Snapshot("artifacts/current.png",true);
            using(var screen=LoadCurrentScreen())
            {
                if(IsAreaMain(screen)) { Console.WriteLine("Home screen reached after the reboot."); return; }
                if(await DismissNotice(screen) || await DismissTutorialPanel(screen)) continue;
                if(flow.FirstOrDefault(s=>s.Name is "title-start" or "login-bonus" or "campaign-bonus" or "beginner-bonus" && Matches(screen,s)) is { } step)
                    await Shell($"input tap {step.X} {step.Y}");
            }
            await Task.Delay(1500);
        }
        throw new Exception("Home screen not reached within 5 minutes of the reboot.");
    }

    // A resumed loop continues with the remaining loops it was started with.
    static async Task ContinueLoops()
    {
        var loop=ReadLoop("artifacts");
        if(loop==null || loop.Index>=loop.Total) return;
        LoopIndex=loop.Index; LoopTotal=loop.Total; Unit=loop.Unit; SelectedStages=loop.Stages; LoopFileOwned=true;
        await RunLoops(loop.Index+1);
    }
}
