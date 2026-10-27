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
        if(!RefreshInviteCode() || InviteMode!="permanent") return "no permanent invitation code";
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
            StageIncomplete=false; SkipInvitation=false;
            await Reset();
            await Shell($"monkey -p {Package} -c android.intent.category.LAUNCHER 1");
            await ExecuteStages();
        }
        if(LoopTotal>1) Console.WriteLine($"All {LoopTotal} loops finished.");
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
