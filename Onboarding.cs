using System.Drawing;

internal static partial class Program
{
    static bool HomeGuidesCompleted;
    static bool HasAreaGlobe(Bitmap screen)
    {
        using var expected=new Bitmap("templates/area-open.png");
        var min=255; var max=0;
        for(var y=0;y<27;y++) for(var x=0;x<27;x++)
        { var p=screen.GetPixel(18+x,18+y); var v=(p.R+p.G+p.B)/3; min=Math.Min(min,v); max=Math.Max(max,v); }
        if(max-min<45) return false;
        var error=0;
        for(var y=0;y<27;y++) for(var x=0;x<27;x++)
        {
            var p=screen.GetPixel(18+x,18+y); var e=expected.GetPixel(x,y);
            if(((p.R+p.G+p.B)/3<(min+max)/2)!=((e.R+e.G+e.B)/3<160)) error++;
        }
        return error<85;
    }
    static bool IsHomeGuide(Bitmap screen)=>IdentifyHomeGuide(screen)!=null;
    static double HomeGuideMinError=255;
    static string? IdentifyHomeGuide(Bitmap screen)
    {
        HomeGuideMinError=255;
        if(!HasAreaGlobe(screen)) return null;
        foreach(var step in LoadSteps().Where(s=>s.Name.StartsWith("home-guide-")))
        {
            using var expected=new Bitmap(step.Image);
            // Match a short prefix of the actual pink lettering, not a count of pink pixels.
            // A solid pink event banner must not qualify as a tutorial.
            for(var dy=-28;dy<=28;dy++) for(var dx=-3;dx<=3;dx++)
            {
                var ink=0; var blank=0; var missing=0; var extra=0;
                for(var y=0;y<step.Height;y+=2) for(var x=0;x<96;x+=2)
                {
                    bool Pink(Color p)=>p.R>185 && p.G<175 && p.B>100 && p.R-p.G>55;
                    var observed=Pink(screen.GetPixel(step.Left+dx+x,step.Top+dy+y));
                    if(Pink(expected.GetPixel(x,y))) { ink++; if(!observed) missing++; }
                    else { blank++; if(observed) extra++; }
                }
                if(ink>20 && blank>20)
                {
                    var score=100.0*missing/ink+50.0*extra/blank;
                    HomeGuideMinError=Math.Min(HomeGuideMinError,score);
                    if(score<22) return step.Name;
                }
            }
        }
        return null;
    }
    static bool IsNameDialog(Bitmap screen)=>
        Matches(screen,new Step("name-heading","templates/name-dialog-heading.png",0,0,337,181,286,20,"checkpoint")) &&
        Matches(screen,LoadSteps().Single(s=>s.Name=="default-name"));
    static bool IsGiftInbox(Bitmap screen)=>!HasAreaGlobe(screen) &&
        Matches(screen,LoadSteps().Single(s=>s.Name=="gift-claim-all")) &&
        Matches(screen,LoadSteps().Single(s=>s.Name=="gift-back-home"));
    static async Task DismissHomeGuides()
    {
        var giftFrames=0; var taps=0; var deadline=DateTime.UtcNow.AddMinutes(3);
        var tapped=new HashSet<string>();
        var lastAction=""; var lastActionTime=DateTime.MinValue;
        while(DateTime.UtcNow<deadline)
        {
            if(StopRequested || File.Exists("artifacts/macro.stop")) throw new OperationCanceledException();
            await Snapshot("artifacts/current.png",true);
            using var screen=LoadCurrentScreen();
            if(IsGiftInbox(screen))
            {
                if(++giftFrames>=2)
                {
                    HomeGuidesCompleted=true;
                    File.Copy("artifacts/current.png","artifacts/home-guides-cleared.png",true);
                    Console.WriteLine($"Area tutorial complete: gift inbox confirmed after {taps} individual gift-position taps."); return;
                }
                await Task.Delay(500); continue;
            }
            giftFrames=0;
            if(await DismissNotice(screen) || await EscapeUnexpectedStory(screen,"home-guide-live")) continue;
            var guide=IdentifyHomeGuide(screen);
            if(guide!=null)
            {
                if(tapped.Add(guide))
                {
                    await Shell("input tap 928 111"); taps++;
                    Console.WriteLine("One tutorial tap: "+guide+"; awaiting a changed instruction.");
                }
            }
            else
            {
                var reward=LoadSteps().FirstOrDefault(s=>s.Name is "login-bonus" or "campaign-bonus" or "beginner-bonus" && Matches(screen,s));
                var action=reward?.Name??(IsAreaMain(screen)?"open-gift":"");
                if(action.Length>0 && (action!=lastAction || DateTime.UtcNow-lastActionTime>TimeSpan.FromSeconds(5)))
                {
                    await Shell(action=="open-gift"?"input tap 928 111":"input tap 850 450");
                    if(action=="open-gift") taps++;
                    lastAction=action; lastActionTime=DateTime.UtcNow;
                    Console.WriteLine("Before gift verification: "+action);
                }
            }
            await Task.Delay(500);
        }
        throw new Exception("Gift inbox was not confirmed; area tutorial is not marked complete.");
    }
    static bool IsBaseDataDialog(Bitmap screen)=>
        Matches(screen,new Step("base-heading","templates/base-heading.png",0,0,307,150,330,20,"checkpoint")) &&
        Matches(screen,LoadSteps().Single(s=>s.Name=="base-data-only"));
    static bool BasicDataSelected(Bitmap screen)
    {
        bool Cyan(int x,int y) { var p=screen.GetPixel(x,y); return p.G>180 && p.B>160 && p.R<160 && p.G-p.R>60; }
        var lower=0; var upper=0;
        for(var y=-6;y<=6;y++) for(var x=-6;x<=6;x++)
        { if(Cyan(340+x,269+y)) lower++; if(Cyan(340+x,220+y)) upper++; }
        return lower>90 && upper<15;
    }
    static bool IsMovieConfirm(Bitmap screen)=>
        Matches(screen,new Step("movie-heading","templates/movie-confirm-heading.png",0,0,369,242,222,20,"checkpoint")) &&
        Matches(screen,LoadSteps().Single(s=>s.Name=="movie-skip"));
    static bool IsDataDownloadProgress(Bitmap screen)
    {
        using var expected=new Bitmap("templates/data-download-progress.png");
        return WhiteGlyphDifferenceAt(screen,expected,20,480,201,22)<12;
    }
    static async Task DownloadBasicData()
    {
        var sent=false;
        for(var attempt=0;attempt<15;attempt++)
        {
            if(StopRequested || File.Exists("artifacts/macro.stop")) throw new OperationCanceledException();
            await Snapshot("artifacts/current.png",true);
            using var screen=LoadCurrentScreen();
            if(!IsBaseDataDialog(screen))
            {
                if(sent) { Console.WriteLine("Verified basic-data download dialog closed."); return; }
                throw new Exception("Basic-data choice dialog disappeared before verification.");
            }
            // The dialog takes a moment to close after the tap, and the game's touch ring may still cover the button:
            // wait for it to close, never tap again.
            if(sent) { await Task.Delay(800); continue; }
            if(!BasicDataSelected(screen))
            {
                await Shell("input tap 340 269");
                Console.WriteLine("Selecting lower basic-data-only option.");
            }
            else
            {
                if(!Matches(screen,LoadSteps().Single(s=>s.Name=="base-data-download")))
                    throw new Exception("Download button not confirmed; no download submitted.");
                File.Copy("artifacts/current.png","artifacts/basic-data-verified.png",true);
                Console.WriteLine("Lower basic-data-only option selected and upper full-voice option unselected; submitting regardless of size.");
                await Shell("input tap 570 406"); sent=true;
            }
            await Task.Delay(800);
        }
        throw new Exception("Basic-data dialog did not finish; stopped without choosing full voice data.");
    }
}
