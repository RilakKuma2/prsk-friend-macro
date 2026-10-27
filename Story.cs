using System.Drawing;

internal static partial class Program
{
    static Bitmap LoadCurrentScreen()
    {
        // Nested recovery helpers can capture again while their caller still examines a frame.
        using var stream=new MemoryStream(File.ReadAllBytes("artifacts/current.png"));
        using var bitmap=new Bitmap(stream);
        return new Bitmap(bitmap);
    }
    static bool IsStoryReader(Bitmap screen)
    {
        using var expected=new Bitmap("templates/story-reading.png");
        // Only the opaque three-dot menu. Normalize brightness for scene fades/loading overlays.
        var low=255; var high=0;
        for(var y=0;y<12;y++) for(var x=0;x<20;x++)
        { var p=screen.GetPixel(918+x,26+y); var v=(p.R+p.G+p.B)/3; low=Math.Min(low,v); high=Math.Max(high,v); }
        if(high-low<35 || high<85) return false;
        var bad=0; var ink=0;
        for(var y=0;y<12;y++) for(var x=0;x<20;x++)
        {
            var p=screen.GetPixel(918+x,26+y); var e=expected.GetPixel(x,y);
            var observed=(p.R+p.G+p.B)/3<(low+high)/2;
            var glyph=e.R<160 && e.G<160 && e.B<180;
            if(glyph) ink++;
            if(observed!=glyph) bad++;
        }
        return ink>10 && bad<20;
    }
    static bool IsDarkStoryTransition(Bitmap screen)
    {
        var dark=0; var total=0;
        for(var y=60;y<480;y+=20) for(var x=40;x<900;x+=20)
        { var p=screen.GetPixel(x,y); total++; if(p.R<25 && p.G<25 && p.B<25) dark++; }
        return dark>total*0.85;
    }
    static int FindBottomSkip(Bitmap screen)
    {
        if(!IsEventStoryList(screen)) return -1;
        using var expected=new Bitmap("templates/story-skip-badge.png");
        var bottom=-1;
        for(var y=75;y<500-expected.Height;y++) for(var x=838;x<=844;x++)
            if(DifferenceAt(screen,expected,x,y,expected.Width,expected.Height)<10) bottom=Math.Max(bottom,y);
        return bottom;
    }
    static async Task SelectResumeEpisode()
    {
        await OpenEventStories();
        Console.WriteLine("Finding the lowest SKIP-marked episode, starting at the bottom of the list.");
        Bitmap? previous=null;
        try
        {
            var stable=0;
            for(var i=0;i<30;i++)
            {
                if(StopRequested || File.Exists("artifacts/macro.stop")) throw new OperationCanceledException();
                await Snapshot("artifacts/current.png",true);
                using var screen=LoadCurrentScreen();
                if(!IsEventStoryList(screen)) throw new Exception("Story list lost while locating its bottom.");
                using var strip=screen.Clone(new Rectangle(500,85,390,380),screen.PixelFormat);
                stable=previous!=null && DifferenceAt(screen,previous,500,85,390,380)<2?stable+1:0;
                previous?.Dispose(); previous=(Bitmap)strip.Clone();
                if(stable>=2) break;
                await Shell("input swipe 700 440 700 140 350"); await Task.Delay(650);
                if(i==29) throw new Exception("Story list bottom was not reached.");
            }
            for(var page=0;page<30;page++)
            {
                if(StopRequested || File.Exists("artifacts/macro.stop")) throw new OperationCanceledException();
                await Snapshot("artifacts/current.png",true);
                using var screen=LoadCurrentScreen();
                if(!IsEventStoryList(screen)) throw new Exception("Story list lost while locating SKIP badge.");
                var y=FindBottomSkip(screen);
                if(y>=0)
                {
                    File.Copy("artifacts/current.png","artifacts/story-resume-selection.png",true);
                    Console.WriteLine($"Lowest SKIP badge confirmed at y={y}; selecting its episode.");
                    await Shell($"input tap 640 {Math.Min(495,y+32)}");
                    await Task.Delay(1200); return;
                }
                using var strip=screen.Clone(new Rectangle(500,85,390,380),screen.PixelFormat);
                await Shell("input swipe 700 145 700 435 350"); await Task.Delay(650);
                await Snapshot("artifacts/current.png",true);
                using var next=LoadCurrentScreen();
                if(DifferenceAt(next,strip,500,85,390,380)<2)
                    throw new Exception("No SKIP-marked episode exists; left the story list unchanged.");
            }
            throw new Exception("Could not locate a SKIP-marked episode.");
        }
        finally { previous?.Dispose(); }
    }
    static async Task RecoverStory()
    {
        Console.WriteLine("Recovering interrupted story; preserving account and auto-live progress.");
        var flow=LoadSteps(); var deadline=DateTime.UtcNow.AddMinutes(5);
        while(DateTime.UtcNow<deadline)
        {
            if(StopRequested || File.Exists("artifacts/macro.stop")) throw new OperationCanceledException();
            var activity=await Shell("dumpsys activity activities | grep mResumedActivity || true");
            if(activity.Contains("com.google.android.gms")) { await Shell("input keyevent 4"); await Task.Delay(1000); continue; }
            await Snapshot("artifacts/current.png",true);
            using var screen=LoadCurrentScreen();
            bool Is(string name)=>Matches(screen,flow.Single(s=>s.Name==name));
            if(IsStoryReceipt(screen))
            { From="story-reading"; Console.WriteLine("Continuous-story receipt found; finishing rewards before checking the story list."); return; }
            if(await DismissStoryReward(screen)) continue;
            if(await CloseRewardsDuringStories(screen)) continue;
            if(await DismissNotice(screen) || await DismissTutorialPanel(screen)) { await Task.Delay(700); continue; }
            if(IsStoryReader(screen)) { From="story-reading"; Console.WriteLine("Story is still open; continuing dialogue."); return; }
            if(Is("story-no-voice")) { From="story-continuous-on"; return; }
            if(IsEventStoryList(screen)) { From="event-story-first"; return; }
            if(Is("event-story-open")) { await OpenEventStories(); From="event-story-first"; return; }
            if(ReadInvitationState(screen)!=InvitationState.Unknown)
            {
                var link=new Step("invite-event-link","templates/invite-event-link.png",815,258,781,248,70,20,"tap");
                if(Matches(screen,link)) { await Shell("input tap 815 258"); await Task.Delay(1000); continue; }
            }
            if(IsAreaMain(screen)) await Shell("input tap 875 487");
            else if(Is("current-event-open")) await Shell("input tap 830 447");
            else if(Is("title-start")) await Shell("input tap 480 464");
            else if(Is("campaign-bonus")) await Shell("input tap 480 420");
            await Task.Delay(1500);
        }
        throw new Exception("Could not recover the event story screen. Saved current screenshot; no account reset.");
    }
}
