using System.Drawing;

internal static partial class Program
{
    static DateTime? TitleVisibleSince;
    static int TitleRestartCount;
    static async Task<bool> RecoverStalledTitle(Bitmap screen)
    {
        if(!Matches(screen,LoadSteps().Single(s=>s.Name=="title-start")))
        { TitleVisibleSince=null; return false; }
        var now=DateTime.UtcNow;
        TitleVisibleSince??=now;
        // This timer belongs to the observed screen, so a stage audit cannot reset it.
        if(now-TitleVisibleSince.Value<TimeSpan.FromSeconds(45)) return false;
        if(TitleRestartCount>=2) throw new Exception("Title remained unresponsive after two game restarts. Latest screenshot saved; no account reset performed by recovery.");
        File.Copy("artifacts/current.png",$"artifacts/title-stall-{TitleRestartCount+1}.png",true);
        Console.WriteLine($"Title unresponsive for 45 seconds; restarting only instance {Instance}'s game ({++TitleRestartCount}/2). Account data retained.");
        await Shell($"am force-stop {Package}");
        await Shell($"monkey -p {Package} -c android.intent.category.LAUNCHER 1");
        TitleVisibleSince=null;
        await Task.Delay(2000);
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
        if(stage=="onboarding" && IsHomeGuide(screen)) return Found("home-guide-live");
        if(stage=="onboarding" && Is("login-bonus")) return Found("login-bonus");
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
                "onboarding"=>"gift-open", "deck"=>"members-open", "music"=>"area-open",
                "boost"=>"boost-settings-open", "invitation"=>"friend-open", "autolive"=>"live-open",
                "eventstory"=>"event-live-menu", _=>null
            };
            return entry==null?null:Found(entry);
        }
        if(stage=="autolive" && Is("live-song-search-focus")) return Found("live-song-search-focus");
        return null;
    }
}
