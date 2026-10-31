using System.Drawing;

internal static partial class Program
{
    static bool ExpectedStoryStep(string step)=>step=="tutorial-download" || step=="movie-skip" ||
        step.StartsWith("story-") || step.StartsWith("after-live-") || step.StartsWith("unit-story-") ||
        step.StartsWith("opening-") || step.StartsWith("event-") || step=="current-event-open";
    static bool StoryMenuExpanded(Bitmap screen)=>Matches(screen,LoadSteps().Single(s=>s.Name=="story-skip"));
    static bool StorySkipConfirmation(Bitmap screen)=>
        Matches(screen,new Step("story-skip-heading","templates/story-skip-heading.png",0,0,358,242,244,20,"checkpoint")) &&
        Matches(screen,LoadSteps().Single(s=>s.Name=="story-skip-confirm"));
    static bool UnexpectedStory(Bitmap screen,string step)=>!ExpectedStoryStep(step) &&
        (IsStoryReader(screen) || StoryMenuExpanded(screen) || StorySkipConfirmation(screen));
    static async Task<bool> EscapeUnexpectedStory(Bitmap first,string step)
    {
        if(!UnexpectedStory(first,step)) return false;
        Console.WriteLine($"Unexpected dialogue during {step}; closing through menu and skip only.");
        var deadline=DateTime.UtcNow.AddSeconds(60);
        var lastAction=""; var actionTime=DateTime.MinValue; var areaFrames=0;
        while(DateTime.UtcNow<deadline)
        {
            if(StopRequested || File.Exists("artifacts/macro.stop")) throw new OperationCanceledException();
            await Snapshot(quiet:true);
            using var screen=LoadCurrentScreen();
            if(HasAreaGlobe(screen))
            {
                if(++areaFrames>=2) { Console.WriteLine("Area restored; original step retained."); return true; }
                await Task.Delay(500); continue;
            }
            areaFrames=0;
            var action=StorySkipConfirmation(screen)?"confirm":StoryMenuExpanded(screen)?"skip":IsStoryReader(screen)?"menu":"";
            if(action.Length>0 && (action!=lastAction || DateTime.UtcNow-actionTime>TimeSpan.FromSeconds(5)))
            {
                await Shell(action=="confirm"?"input tap 570 318":action=="skip"?"input tap 863 226":"input tap 928 32");
                lastAction=action; actionTime=DateTime.UtcNow;
                Console.WriteLine("Dialogue exit action: "+action);
            }
            await Task.Delay(700);
        }
        throw new Exception("Unexpected dialogue did not close; no area-character taps sent.");
    }
}
