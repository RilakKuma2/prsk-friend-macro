using System.Drawing;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static partial class Program
{
    enum InvitationState { Unknown, Unregistered, Registered }
    record StoryVerification(int[] Episodes,bool HasSkip,bool HasUnread,bool TopReached,bool BottomReached,int Pages)
    {
        public bool Complete=>TopReached && BottomReached && !HasSkip && !HasUnread && Enumerable.Range(1,20).All(Episodes.Contains);
    }
    static bool ShouldOpenInvitation(StoryVerification story,InvitationState invitation)=>story.Complete && invitation==InvitationState.Unregistered;
    static InvitationState ReadInvitationState(Bitmap screen)
    {
        var flow=LoadSteps();
        var registered=Matches(screen,flow.Single(s=>s.Name=="invite-registered"));
        var unregistered=Matches(screen,flow.Single(s=>s.Name=="invite-code-open"));
        return registered==unregistered?InvitationState.Unknown:registered?InvitationState.Registered:InvitationState.Unregistered;
    }
    static bool HasUnreadStoryBadge(Bitmap screen)
    {
        using var expected=new Bitmap("templates/story-unread-badge.png");
        for(var y=75;y<490;y++) for(var x=878;x<=882;x++)
            if(DifferenceAt(screen,expected,x,y,expected.Width,expected.Height)<9) return true;
        return false;
    }
    static async Task<int[]> ReadVisibleEpisodes(Bitmap screen)
    {
        var text=(await OcrRegion(screen,new Rectangle(511,85,85,415))).Normalize(NormalizationForm.FormKC);
        return Regex.Matches(text,@"第([0-9]{1,2})話").Select(m=>int.Parse(m.Groups[1].Value)).Distinct().Order().ToArray();
    }
    static void CheckPostStoryStop()
    {
        if(StopRequested || File.Exists("artifacts/macro.stop")) throw new OperationCanceledException("Stopped during story verification; game remains open.");
    }
    static async Task<StoryVerification> VerifyAllStoryEpisodes()
    {
        // This runs before consulting invitation status, including already-invited accounts.
        Console.WriteLine("Checking all story pages for SKIP before checking invitation status.");
        await OpenEventStories();
        Directory.CreateDirectory("artifacts/story-check");
        Bitmap? previous=null;
        var stable=0; var topReached=false; var bottomReached=false;
        var episodes=new HashSet<int>(); var hasSkip=false; var unread=false; var pages=0;
        try
        {
            for(var attempt=0;attempt<30;attempt++)
            {
                CheckPostStoryStop();
                await Snapshot("artifacts/current.png",true);
                using var screen=LoadCurrentScreen();
                if(!IsEventStoryList(screen))
                {
                    if(await CloseRewardsBeforeVerification(screen,"event-stories-complete")) { await OpenEventStories(); continue; }
                    throw new Exception("Story list lost while locating its upper edge.");
                }
                stable=previous!=null && DifferenceAt(screen,previous,500,85,390,415)<1?stable+1:0;
                previous?.Dispose(); previous=screen.Clone(new Rectangle(500,85,390,415),screen.PixelFormat);
                if(stable>=2) { topReached=true; break; }
                await Shell("input swipe 700 145 700 435 350"); await Task.Delay(650);
            }
            if(!topReached) throw new Exception("Story list upper edge was not verified; invitation routing withheld.");
            previous?.Dispose(); previous=null; stable=0;
            for(var page=0;page<30;page++)
            {
                CheckPostStoryStop();
                await Snapshot("artifacts/current.png",true);
                using var screen=LoadCurrentScreen();
                if(!IsEventStoryList(screen))
                {
                    if(await CloseRewardsBeforeVerification(screen,"event-stories-complete")) { await OpenEventStories(); continue; }
                    throw new Exception("Story list lost during full SKIP verification.");
                }
                var ids=await ReadVisibleEpisodes(screen);
                episodes.UnionWith(ids);
                var skip=FindBottomSkip(screen)>=0; var isUnread=HasUnreadStoryBadge(screen);
                hasSkip|=skip; unread|=isUnread; pages++;
                File.Copy("artifacts/current.png",$"artifacts/story-check/page-{page:D2}.png",true);
                Console.WriteLine($"Story page {page}: episodes={string.Join(',',ids)}, SKIP={skip}, unread={isUnread}.");
                stable=previous!=null && DifferenceAt(screen,previous,500,85,390,415)<1?stable+1:0;
                previous?.Dispose(); previous=screen.Clone(new Rectangle(500,85,390,415),screen.PixelFormat);
                if(stable>=2) { bottomReached=true; break; }
                // Slow, overlapping drags avoid inertia jumping over a row between captures.
                await Shell("input swipe 700 400 700 240 1200"); await Task.Delay(650);
            }
        }
        finally { previous?.Dispose(); }
        var result=new StoryVerification(episodes.Order().ToArray(),hasSkip,unread,topReached,bottomReached,pages);
        await File.WriteAllTextAsync("artifacts/story-verification.json",JsonSerializer.Serialize(new { Instance,Time=DateTimeOffset.Now,Verification=result },JsonOptions));
        return result;
    }
    // Registering after stories grants the story missions at once: "以下のアイテムを受け取りました。" covers the mission page.
    static readonly Step ReceivedItemsTitle=new("items-received-title","templates/items-received-title.png",0,0,340,42,280,26,"checkpoint");
    static readonly Step ReceivedItemsOk=new("items-received-ok","templates/items-received-ok.png",480,491,420,478,120,28,"tap");
    static bool IsReceivedItems(Bitmap screen)=>Matches(screen,ReceivedItemsTitle) && Matches(screen,ReceivedItemsOk);

    // The dialog appears once the invitation is registered and the stories are read. Success still requires the
    // 20-episode verification. The dialog is always captured and closed; seen before the verification it is
    // remembered until the list is checked, seen after it (invitation check) it ends the loop as successful.
    sealed class RewardsReceived():Exception("Invitation mission rewards received.");
    static string? SeenRewards;
    static bool RewardsEligible(string step)=>CurrentLoopInvite("artifacts")?.Result=="registered" && StepsForStage("eventstory").Any(s=>s.Name==step);
    static string SaveRewardsCapture()
    {
        var image=$"{DateTime.Now:yyyyMMdd-HHmmss}-rewards.png";
        Directory.CreateDirectory("artifacts/invites");
        File.Copy("artifacts/current.png",Path.Combine("artifacts/invites",image),true);
        return image;
    }
    static async Task<bool> CloseRewardsBeforeVerification(Bitmap screen,string step)
    {
        if(!IsReceivedItems(screen) || !RewardsEligible(step)) return false;
        SeenRewards=SaveRewardsCapture();
        await Shell($"input tap {ReceivedItemsOk.X} {ReceivedItemsOk.Y}"); await Task.Delay(1000);
        Console.WriteLine($"Invitation rewards dialog captured ({SeenRewards}) and closed; verifying all 20 episodes before counting success.");
        return true;
    }
    // The dialog can also open in the middle of reading: close it either way and keep reading.
    static async Task<bool> CloseRewardsDuringStories(Bitmap screen)
    {
        if(await CloseRewardsBeforeVerification(screen,"event-stories-complete")) return true;
        if(!IsReceivedItems(screen)) return false;
        await Shell($"input tap {ReceivedItemsOk.X} {ReceivedItemsOk.Y}"); await Task.Delay(1000);
        Console.WriteLine("Received-items dialog closed during stories.");
        return true;
    }
    static async Task CheckRewards(Bitmap screen)
    {
        if(!IsReceivedItems(screen)) return;
        var image=SaveRewardsCapture();
        await Shell($"input tap {ReceivedItemsOk.X} {ReceivedItemsOk.Y}"); await Task.Delay(1000);
        await RecordRewards(image);
        throw new RewardsReceived();
    }
    static async Task RecordRewards(string image)
    {
        var history=ReadInviteHistory("artifacts");
        var started=LoopStarted("artifacts");
        var last=history.FindLastIndex(r=>r.Result=="registered" && (started==null || r.Time>=started));
        if(last>=0) history[last]=history[last] with { Rewards=image };
        else history.Add(new InviteRecord(DateTimeOffset.Now,InviteCode??"",InviteMode??"",null,null,"registered",ReadLoop("artifacts")?.Index,image));
        await File.WriteAllTextAsync(Path.Combine("artifacts",InviteHistoryFile),JsonSerializer.Serialize(history,JsonOptions));
        await SaveCheckpoint("event-stories-complete","complete");
        Console.WriteLine($"Invitation rewards captured and closed: loop counted as successful ({image}).");
    }
    // Applies the reward-dialog success path to the current screen (for a run that stopped at the dialog).
    static async Task CloseRewardsNow()
    {
        await Snapshot("artifacts/current.png",true);
        using(var screen=LoadCurrentScreen())
            if(!await UntilRewards(()=>CheckRewards(screen))) { Console.WriteLine("No received-items dialog on screen; nothing tapped."); return; }
        await Task.Delay(800); await Snapshot("artifacts/current.png",true);
        using var after=LoadCurrentScreen();
        Console.WriteLine($"After closing: ReceivedItems={IsReceivedItems(after)}, Invitation={ReadInvitationState(after)}");
    }
    static async Task<bool> UntilRewards(Func<Task> work)
    {
        try { await work(); return false; }
        catch(RewardsReceived) { return true; }
    }
    static async Task<InvitationState> OpenAndInspectInvitation()
    {
        var flow=LoadSteps(); var previous=InvitationState.Unknown; var stable=0;
        var lastAction=""; var lastTap=DateTime.MinValue;
        var deadline=DateTime.UtcNow.AddMinutes(2);
        while(DateTime.UtcNow<deadline)
        {
            CheckPostStoryStop(); await Snapshot("artifacts/current.png",true);
            using var screen=LoadCurrentScreen();
            await CheckRewards(screen);
            if(await DismissNotice(screen) || await DismissTutorialPanel(screen)) continue;
            if(await EscapeUnexpectedStory(screen,"friend-open")) continue;
            var state=ReadInvitationState(screen);
            if(state!=InvitationState.Unknown)
            {
                stable=state==previous?stable+1:1; previous=state;
                if(stable>=2)
                {
                    File.Copy("artifacts/current.png","artifacts/post-story-invitation.png",true);
                    Console.WriteLine("Invitation status verified from game: "+state);
                    return state;
                }
                await Task.Delay(500); continue;
            }
            previous=InvitationState.Unknown; stable=0;
            Step? action=null;
            if(IsAreaMain(screen)) action=flow.Single(s=>s.Name=="friend-open");
            else if(Matches(screen,flow.Single(s=>s.Name=="invite-mission-open"))) action=flow.Single(s=>s.Name=="invite-mission-open");
            else if(Matches(screen,flow.Single(s=>s.Name=="gift-back-home"))) action=flow.Single(s=>s.Name=="gift-back-home");
            if(action!=null && (lastAction!=action.Name || DateTime.UtcNow-lastTap>TimeSpan.FromSeconds(3)))
            {
                Console.WriteLine("Post-story navigation: "+action.Name);
                await Shell($"input tap {action.X} {action.Y}"); lastAction=action.Name; lastTap=DateTime.UtcNow;
            }
            await Task.Delay(650);
        }
        if(File.Exists("artifacts/current.png")) File.Copy("artifacts/current.png","artifacts/invitation-unverified.png",true);
        throw new Exception("Invitation status could not be verified (screen saved to invitation-unverified.png). Game remains open.");
    }
    static async Task<InvitationState> RegisterInvitation()
    {
        Console.WriteLine("Invitation code available after stories: registering now.");
        SkipInvitation=false;
        await Tutorial(InvitationEntrySteps());
        return await OpenAndInspectInvitation();
    }
    // Late code for a run that already stopped at the open invitation mission ("awaiting-invitation").
    static async Task RegisterAwaitingInvitation()
    {
        if(!RefreshInviteCode()) throw new Exception("No invitation code saved; nothing entered.");
        var state=InvitationState.Unknown;
        if(await UntilRewards(async()=>state=await RegisterInvitation())) return;
        if(state!=InvitationState.Registered) throw new Exception("Invitation still unregistered after entry. Game remains open.");
        await SaveCheckpoint("event-stories-complete","complete");
        Console.WriteLine("Invitation registered from the waiting screen. Game and emulator remain running.");
    }
    static async Task<bool> FinishEventStories()
    {
        var verification=await VerifyAllStoryEpisodes();
        if(!verification.Complete)
        {
            Console.WriteLine($"Story verification incomplete: episodes={string.Join(',',verification.Episodes)}, SKIP={verification.HasSkip}, unread={verification.HasUnread}. Leaving the story list open; no invitation navigation.");
            await SaveCheckpoint("event-stories-complete","story-incomplete");
            return false;
        }
        Console.WriteLine("Episodes 1-20 verified across both scroll edges; no SKIP or unread badges remain.");
        if(SeenRewards!=null)
        {
            await RecordRewards(SeenRewards);
            await File.WriteAllTextAsync("artifacts/post-story-result.json",JsonSerializer.Serialize(new { Instance,Time=DateTimeOffset.Now,Verification=verification,Invitation="Registered",Rewards=SeenRewards,GameKeptOpen=true },JsonOptions));
            return true;
        }
        var invitation=await OpenAndInspectInvitation();
        if(ShouldOpenInvitation(verification,invitation) && RefreshInviteCode()) invitation=await RegisterInvitation();
        if(ShouldOpenInvitation(verification,invitation))
        {
            await SaveCheckpoint("event-stories-complete","awaiting-invitation");
            Console.WriteLine("Invitation not registered: leaving invitation mission open. No code entered; game and emulator remain running.");
        }
        else if(invitation==InvitationState.Registered)
        {
            await Snapshot("artifacts/current.png",true);
            using var screen=LoadCurrentScreen();
            var link=new Step("invite-event-link","templates/invite-event-link.png",815,258,781,248,70,20,"tap");
            if(!Matches(screen,link)) throw new Exception("Registered invitation confirmed, but event return link not recognized. Game remains open.");
            await Shell("input tap 815 258"); await Task.Delay(1000); await OpenEventStories();
            await SaveCheckpoint("event-stories-complete","complete");
            Console.WriteLine("Invitation already registered: returned to the story list. Game and emulator remain running.");
        }
        else throw new Exception("Unknown invitation status; no code entered.");
        await File.WriteAllTextAsync("artifacts/post-story-result.json",JsonSerializer.Serialize(new { Instance,Time=DateTimeOffset.Now,Verification=verification,Invitation=invitation.ToString(),GameKeptOpen=true },JsonOptions));
        return true;
    }
}
