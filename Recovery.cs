using System.Drawing;
using System.Text.RegularExpressions;

internal static partial class Program
{
    static readonly HashSet<string> SafeOverlayPackages=new(StringComparer.OrdinalIgnoreCase)
    {
        Package,"com.android.systemui","android","com.android.shell","com.google.android.gms",
        "com.android.permissioncontroller","com.google.android.permissioncontroller"
    };
    const string WindowOverlayQuery="dumpsys window windows | grep -E 'Window #|package=|appop=|ty=|mViewVisibility=|mHasSurface=|isReadyForDisplay' || true";
    static DateTime LastOverlayCheck=DateTime.MinValue;

    static IReadOnlyList<string> FindVisibleOverlayPackages(string dumpsysOutput)
    {
        var found=new List<string>();
        foreach(var block in Regex.Split(dumpsysOutput,@"(?m)^(?=\s*Window #\d+)"))
        {
            if(!block.Contains("Window #")) continue;
            var isOverlay=block.Contains("ty=APPLICATION_OVERLAY") || block.Contains("ty=SYSTEM_ALERT")
                || block.Contains("ty=SYSTEM_OVERLAY") || block.Contains("ty=PHONE")
                || block.Contains("ty=PRIORITY_PHONE") || block.Contains("ty=SYSTEM_ERROR")
                || block.Contains("appop=SYSTEM_ALERT_WINDOW");
            if(!isOverlay || block.Contains("ty=INPUT_METHOD")) continue;
            if(!block.Contains("mViewVisibility=0x0") || !block.Contains("mHasSurface=true")) continue;
            if(block.Contains("isReadyForDisplay()=false")) continue;
            var m=Regex.Match(block,@"package=([A-Za-z0-9._]+)");
            if(!m.Success) m=Regex.Match(block,@"Window\{[0-9a-fA-F]+\s+u\d+\s+([A-Za-z0-9._]+)");
            if(!m.Success) continue;
            var pkg=m.Groups[1].Value;
            if(SafeOverlayPackages.Contains(pkg)) continue;
            if(!found.Contains(pkg,StringComparer.OrdinalIgnoreCase)) found.Add(pkg);
        }
        return found;
    }

    // Dismisses full-screen / popup emulator ad overlays (e.g. MuMu's com.mumu.store SYSTEM_ALERT_WINDOW)
    // that cover the game while leaving the game's ResumedActivity unchanged.
    static async Task<bool> DismissEmulatorOverlays(bool force=false)
    {
        if(!force && DateTime.UtcNow-LastOverlayCheck<TimeSpan.FromSeconds(4)) return false;
        LastOverlayCheck=DateTime.UtcNow;
        string output;
        try { output=await Shell(WindowOverlayQuery); }
        catch { return false; }
        var overlays=FindVisibleOverlayPackages(output);
        if(overlays.Count==0) return false;
        foreach(var pkg in overlays)
        {
            Console.WriteLine($"Emulator overlay detected ({pkg}); force-stopping to dismiss.");
            try { await Shell($"am force-stop {pkg}"); } catch { }
        }
        await Task.Delay(500);
        return true;
    }

    static async Task<bool> DismissForeignForeground(string activity,bool relaunchGame=true)
    {
        if(string.IsNullOrWhiteSpace(activity)) return false;
        if(activity.Contains("com.google.android.gms") && (activity.Contains("MinuteMaid") || activity.Contains("SignIn")))
        {
            await Shell("input keyevent 4");
            await Task.Delay(1200);
            return true;
        }
        if(activity.Contains(Package) || activity.Contains("com.google.android.gms") || activity.Contains("permissioncontroller"))
            return false;
        var m=Regex.Match(activity,@"\bu\d+\s+([A-Za-z0-9._]+)/");
        var pkg=m.Success?m.Groups[1].Value:"";
        var lower=pkg.ToLowerInvariant();
        if(pkg.Length>0 && !SafeOverlayPackages.Contains(pkg)
            && !lower.Contains("launcher") && !lower.Contains("lawnchair") && !lower.Contains("home") && !lower.Contains("settings"))
        {
            Console.WriteLine($"Foreign activity detected ({pkg}); force-stopping and returning to game.");
            try { await Shell($"am force-stop {pkg}"); } catch { }
        }
        else
        {
            Console.WriteLine($"Foreground left game ({(pkg.Length>0?pkg:"unknown")}); returning to game.");
        }
        if(relaunchGame)
        {
            await Shell($"monkey -p {Package} -c android.intent.category.LAUNCHER 1");
            await Task.Delay(1500);
        }
        return true;
    }

    static DateTime? TitleVisibleSince;
    static int TitleRestartCount;
    static bool TitleVmRebooted;
    static async Task<bool> RecoverStalledTitle(Bitmap screen)
    {
        if(!Matches(screen,LoadSteps().Single(s=>s.Name=="title-start")))
        { TitleVisibleSince=null; return false; }
        var now=DateTime.UtcNow;
        TitleVisibleSince??=now;
        // This timer belongs to the observed screen, so a stage audit cannot reset it.
        if(now-TitleVisibleSince.Value<TimeSpan.FromSeconds(45)) return false;
        if(TitleRestartCount>=3)
        {
            if(TitleVmRebooted)
                throw new Exception("Title remained unresponsive after 3 game restarts and instance reboot. Latest screenshot saved; no account reset performed by recovery.");
            TitleVmRebooted=true;
            SaveCurrentEvidence($"artifacts/title-stall-reboot.webp");
            Console.WriteLine($"Title remained unresponsive after 3 game restarts; rebooting instance {Instance}. Account data retained.");
            var stage=ProgressStage??"onboarding";
            BeginTimingRestart("vm",stage);
            await RebootInstance();
            await Shell($"monkey -p {Package} -c android.intent.category.LAUNCHER 1");
            TitleVisibleSince=null;
            await Task.Delay(2500);
            EndTimingRestart(stage);
            return true;
        }
        SaveCurrentEvidence($"artifacts/title-stall-{TitleRestartCount+1}.webp");
        Console.WriteLine($"Title unresponsive for 45 seconds; restarting only instance {Instance}'s game ({++TitleRestartCount}/3). Account data retained.");
        BeginTimingRestart("title",ProgressStage??"onboarding");
        await Shell($"am force-stop {Package}");
        await Shell($"monkey -p {Package} -c android.intent.category.LAUNCHER 1");
        TitleVisibleSince=null;
        await Task.Delay(2000);
        EndTimingRestart(ProgressStage??"onboarding");
        return true;
    }
    static string? FindRecoveryStep(Bitmap screen,string waiting,IReadOnlyList<Step> allowed)
    {
        string? Found(string name)=>allowed.Any(s=>s.Name==name)?name:null;
        var all=LoadSteps();
        bool Is(string name)=>Matches(screen,all.Single(s=>s.Name==name));
        var stage=LoadStages().FirstOrDefault(s=>StepsForStage(s.Name).Any(p=>p.Name==waiting))?.Name;
        if(stage=="eventstory" && IsStoryReward(screen)) return Found("event-intro-reward");
        // Reconcile only distinct screen anchors; shared confirmation buttons never select a stage.
        if(IsBaseDataDialog(screen)) return Found("base-data-only");
        if(IsNameDialog(screen)) return Found("default-name");
        if(stage=="onboarding")
        {
            foreach(var onb in new[]{"terms-agree","live-skip-button","unit-confirm-dialog","unit-confirm","unit-scenery","unit-color","unit-select","gift-claim-all","gifts-empty"})
                if(Is(onb) && Found(onb) is { } foundOnb) return foundOnb;
            if(IsHomeGuide(screen)) return Found("home-guide-live");
            if(Is("login-bonus")) return Found("login-bonus");
        }
        if(Is("title-start")) return Found("title-start");
        if(IsMovieConfirm(screen) && stage=="onboarding") return Found("movie-skip");
        if(IsAutoEnd(screen)) return Found("auto-end-notice");
        if(IsEventStoryList(screen) && stage=="eventstory") return Found("event-story-first");
        if(Is("event-story-open") && stage=="eventstory") return Found("event-story-open");
        if(IsStoryReader(screen) && (stage=="onboarding" || stage=="eventstory"))
        {
            var index=all.FindIndex(s=>s.Name==waiting);
            var menu=stage=="eventstory"?(index<all.FindIndex(s=>s.Name=="event-story-first")?"event-intro-menu":"story-reading"):index>=all.FindIndex(s=>s.Name=="opening-menu")?"opening-menu":
                index>=all.FindIndex(s=>s.Name=="unit-story-menu")?"unit-story-menu":
                index>=all.FindIndex(s=>s.Name=="after-live-menu")?"after-live-menu":"story-menu";
            return Found(menu);
        }
        if(IsAreaMain(screen))
        {
            var entry=stage switch
            {
                "onboarding"=>"gift-open", "gacha"=>"gacha-open", "deck"=>"members-open", "music"=>"area-open",
                "boost"=>"boost-settings-open", "invitation"=>"friend-open", "autolive"=>"live-open",
                "eventstory"=>"event-live-menu", _=>null
            };
            return entry==null?null:Found(entry);
        }
        if(stage=="gacha")
        {
            foreach(var g in new[]{"memories-result-ok","guaranteed-result-ok","memories-ten","memories-tab"})
                if(Is(g) && Found(g) is { } fg) return fg;
        }
        if(stage=="deck")
        {
            foreach(var d in new[]{"deck-complete","deck-recommend"})
                if(Is(d) && Found(d) is { } fd) return fd;
        }
        if(stage=="music")
        {
            foreach(var m in new[]{"song-search-focus","music-area"})
                if(Is(m) && Found(m) is { } fm) return fm;
        }
        if(stage=="boost")
        {
            foreach(var b in new[]{"large-cans-max","boost-plus-ten"})
                if(Is(b) && Found(b) is { } fb) return fb;
        }
        if(stage=="invitation" && Is("invite-code-focus")) return Found("invite-code-focus");
        if(stage=="autolive")
        {
            foreach(var a in new[]{"live-song-search-focus","solo-live-open"})
                if(Is(a) && Found(a) is { } fa) return fa;
        }
        return null;
    }

    // After restarting the game or rebooting the VM during any step: pass title/bonuses and land on either a known recovery step or home.
    static async Task<string?> AdvanceAfterRestart(string waiting,IReadOnlyList<Step> allowed)
    {
        var flow=LoadSteps();
        var deadline=DateTime.UtcNow.AddSeconds(90);
        while(DateTime.UtcNow<deadline)
        {
            if(StopRequested || File.Exists("artifacts/macro.stop")) throw new OperationCanceledException();
            if(await DismissEmulatorOverlays()) continue;
            var activity=await Shell(ResumedActivity);
            if(await DismissForeignForeground(activity)) continue;
            if(!activity.Contains(Package))
            { await Shell($"monkey -p {Package} -c android.intent.category.LAUNCHER 1"); await Task.Delay(2500); continue; }
            await Snapshot(quiet:true);
            using(var screen=LoadCurrentScreen())
            {
                if(IsAppUpdatePrompt(screen)) { await HandleAppUpdatePrompt(); deadline=DateTime.UtcNow.AddSeconds(90); continue; }
                if(allowed.FirstOrDefault(s=>s.Name==waiting) is { } w && Matches(screen,w)) return waiting;
                if(IsAreaMain(screen)) { ResetAutoUpdateGuard(); return FindRecoveryStep(screen,waiting,allowed); }
                if(flow.FirstOrDefault(s=>s.Name is "title-start" or "login-bonus" or "campaign-bonus" or "beginner-bonus" && Matches(screen,s)) is { } tapStep)
                {
                    await Shell($"input tap {tapStep.X} {tapStep.Y}");
                    await Task.Delay(2000);
                    continue;
                }
                if(await DismissNotice(screen) || await DismissTutorialPanel(screen)) { await Task.Delay(600); continue; }
                if(FindRecoveryStep(screen,waiting,allowed) is { } rec && rec!="title-start") return rec;
            }
            await Task.Delay(1500);
        }
        return null;
    }
}
