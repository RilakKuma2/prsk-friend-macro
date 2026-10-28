using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Drawing;
#if WINDOWS
using System.Drawing.Imaging;
#endif
using System.Xml.Linq;
using System.Text.RegularExpressions;

internal static partial class Program
{
    static int Instance = 1;
    static string Target = "셐 계정생성직전-1";
    static string Serial => SerialOf(Instance);
    // Every emulator VM is an instance (index -> exact name). The list is re-read so added VMs appear;
    // workers still pin the name they started with and stop if it changes.
    static readonly object InstancesLock=new();
    static (DateTime At,Dictionary<int,string> List) InstanceCache=(DateTime.MinValue,[]);
    static Dictionary<int,string> AllowedInstances
    {
        get
        {
            lock(InstancesLock)
            {
                if(DateTime.UtcNow-InstanceCache.At<TimeSpan.FromSeconds(10)) return InstanceCache.List;
                var found=new Dictionary<int,string>();
                try
                {
                    foreach(var row in VmListNow().Split('\n').Select(x=>x.Trim().Split(',')).Where(x=>x.Length>=7))
                        if(int.TryParse(row[0],out var index) && row[1].Length>0) found[index]=row[1];
                }
                catch(Exception ex) { Console.Error.WriteLine("VM list failed: "+ex.Message); }
                // A failed read keeps the last good list.
                InstanceCache=(DateTime.UtcNow,found.Count>0?found:InstanceCache.List);
                return InstanceCache.List;
            }
        }
    }
    // The foreground activity: Android 9 (LDPlayer) names it mResumedActivity, Android 13 (BlueStacks Air) topResumedActivity.
    const string ResumedActivity = "dumpsys activity activities | grep -E 'mResumedActivity|topResumedActivity' || true";
    const string Package = "com.sega.pjsekai";
    const string Data = "/sdcard/Android/data/" + Package;
    const string Saved = Data + ".sekai-macro-preserved";
    static string Unit = "mmj";
    static bool StopRequested;
    static PosixSignalRegistration? HangupIgnored;
    static string? From;
    static string? Until;
    static string? InviteCode;
    static string? VerifiedInviteCode;
    static string? ActiveStep;
    static bool StageIncomplete;
    static DateTime LastSingleTap=DateTime.MinValue;
    static readonly HashSet<string> RapidStorySteps=["story-menu","after-live-menu","unit-story-menu","opening-menu","event-intro-menu","story-reading"];
    static string[] SelectedStages=[];
    static DateTime LastGuard=DateTime.MinValue;
    record Stage(string Name, string Start, string End, string? Title=null);
    record Plan(string[] Stages);
    static List<Stage> LoadStages()=>JsonSerializer.Deserialize<List<Stage>>(File.ReadAllText("stages.json"))!;
    [STAThread]
    static async Task<int> Main(string[] args)
    {
        try
        {
            Console.OutputEncoding=Encoding.UTF8;
            // Jobs the panel starts (workers, VM setup) stop through their stop files. On macOS they share the panel's
            // Terminal, so its Ctrl+C or closing that window must not stop them, as on Windows where they have no console.
            var job=args.Contains("--background") || args.FirstOrDefault()=="provision";
            Console.CancelKeyPress+=(_,e)=>{ e.Cancel=true; if(!job) StopRequested=true; };
            if(job && !OperatingSystem.IsWindows()) HangupIgnored=PosixSignalRegistration.Create(PosixSignal.SIGHUP,c=>c.Cancel=true);
            if(args.Length==0) args=["launcher"];
            var codeOption=Array.IndexOf(args,"--code");
            InviteCode=codeOption>=0?args.ElementAtOrDefault(codeOption+1):null;
            if(codeOption>=0 && !Regex.IsMatch(InviteCode??"", "^[A-Za-z0-9]{15}$")) throw new Exception("Invitation code must be exactly 15 ASCII letters/digits. No changes made.");
            if(InviteCode!=null) InviteMode="cli";
            if(!File.Exists("flow.json") && File.Exists(Path.Combine(AppContext.BaseDirectory,"flow.json"))) Directory.SetCurrentDirectory(AppContext.BaseDirectory);
            if(!File.Exists("flow.json"))
            {
                var folder=new DirectoryInfo(AppContext.BaseDirectory);
                while(folder!=null && !File.Exists(Path.Combine(folder.FullName,"flow.json"))) folder=folder.Parent;
                if(folder==null) throw new Exception("Cannot locate flow.json.");
                Directory.SetCurrentDirectory(folder.FullName);
            }
            ControlRoot=Directory.GetCurrentDirectory();
            if(args[0] is "launcher" or "gui") { ShowGui(); return 0; }
            if(args[0]=="web") { await RunWebCommand(args); return 0; }
            if(args[0]=="provision") { await RunProvision(); return 0; }
            if(args[0]=="check-vms") { foreach(var n in AllowedInstances.Keys) { string game; try { game=(await Run(AdbOf(n),"-s",SerialOf(n),"shell",$"pm path {Package}")).Trim().Replace('\n',' '); } catch(Exception ex) { game="ERR "+ex.Message; } Console.WriteLine($"{n} {AllowedInstances[n]} running={await IsVmRunning(n)} game={game}"); } return 0; }
            if(args[0]=="check-checkpoint") { await VerifyCheckpointContention(); return 0; }
            if(args[0]=="check-story-reward") { VerifyStoryReward(args[1]); return 0; }
            if(args[0]=="check-story-receipt") { VerifyStoryReceipt(args[1]); return 0; }
            if(args[0]=="check-story-page") { using var shot=new Bitmap(args[1]); Console.WriteLine($"Episodes={string.Join(',',await ReadVisibleEpisodes(shot))}, SKIP={FindBottomSkip(shot)}, unread={HasUnreadStoryBadge(shot)}"); return 0; }
            if(args[0]=="check-music-shop") { using var shot=new Bitmap(args[1]); Console.WriteLine(FindMusicShop(shot,LoadSteps().Single(s=>s.Name=="music-shop-open"))+", AllSongsTab="+AllSongsTab(shot)); return 0; }
            if(args[0]=="check-plan")
            {
                ValidateFlow();
                var plan=JsonSerializer.Deserialize<Plan>(File.ReadAllText("plan.json"))!;
                foreach(var name in plan.Stages) Console.WriteLine($"{name}: {EffectiveSteps(StepsForStage(name)).Count} steps");
                return 0;
            }
            if(args[0]=="check-invite-name") { using var shot=new Bitmap(args[1]); Directory.CreateDirectory("artifacts"); Console.WriteLine((await OcrRegion(shot,new Rectangle(250,288,470,34),3))+" => "+(await ReadInviteNickname(shot)??"(none)")); return 0; }
            if(args[0]=="check-received") { using var shot=new Bitmap(args[1]); Console.WriteLine($"ReceivedItems={IsReceivedItems(shot)}, Invitation={ReadInvitationState(shot)}"); return 0; }
            if(args[0]=="check-invite-queue") { VerifyInviteQueue(); return 0; }
            if(args[0]=="check-home") { using var shot=new Bitmap(args[1]); Console.WriteLine(IsAreaMain(shot)); return 0; }
            if(args[0]=="check-auto-end") { using var shot=new Bitmap(args[1]); Console.WriteLine(IsAutoEnd(shot)); return 0; }
            if(args[0]=="check-event") { using var shot=new Bitmap(args[1]); Console.WriteLine($"Button={Matches(shot,LoadSteps().Single(s=>s.Name=="event-story-open"))}, List={IsEventStoryList(shot)}, Episode1={Matches(shot,LoadSteps().Single(s=>s.Name=="event-story-first"))}"); return 0; }
            if(args[0]=="check-story") { using var shot=new Bitmap(args[1]); Console.WriteLine($"Reader={IsStoryReader(shot)}, Dark={IsDarkStoryTransition(shot)}, SkipY={FindBottomSkip(shot)}, Checked={StoryContinuousChecked(shot)}, Choice={IsStoryChoice(shot)}"); return 0; }
            if(args[0]=="check-rating") { using var shot=new Bitmap(args[1]); Console.WriteLine(IsRatingPrompt(shot)); return 0; }
            if(args[0]=="check-title") { using var shot=new Bitmap(args[1]); Console.WriteLine(Matches(shot,LoadSteps().Single(s=>s.Name=="title-start"))); return 0; }
            if(args[0]=="check-guide") { using var shot=new Bitmap(args[1]); Console.WriteLine($"Globe={HasAreaGlobe(shot)}, Guide={IsHomeGuide(shot)}, Score={HomeGuideMinError:F2}, Name={IsNameDialog(shot)}"); return 0; }
            if(args[0]=="check-dialogue") { using var shot=new Bitmap(args[1]); Console.WriteLine($"Unexpected={UnexpectedStory(shot,args[2])}, Expanded={StoryMenuExpanded(shot)}, Confirm={StorySkipConfirmation(shot)}"); return 0; }
            if(args[0]=="check-route") { using var shot=new Bitmap(args[1]); Console.WriteLine(FindRecoveryStep(shot,args[2],LoadSteps())??"unrecognized"); return 0; }
            if(args[0]=="check-matches")
            {
                using var shot=new Bitmap(args[1]);
                foreach(var s in LoadSteps()) if(Matches(shot,s)) Console.WriteLine(s.Name);
                Console.WriteLine("generic-next="+Matches(shot,new Step("panel-next","templates/tutorial-next-button.png",720,500,650,482,140,33,"tap")));
                Console.WriteLine("rating="+IsRatingPrompt(shot));
                return 0;
            }
            if(args[0]=="check-onboarding") { using var shot=new Bitmap(args[1]); Console.WriteLine($"BaseDialog={IsBaseDataDialog(shot)}, BasicSelected={BasicDataSelected(shot)}, MovieConfirm={IsMovieConfirm(shot)}, Downloading={IsDataDownloadProgress(shot)}"); return 0; }
            var instanceOption=Array.IndexOf(args,"--instance");
            if(instanceOption>=0)
            {
                Instance=int.Parse(args[instanceOption+1]);
                if(!AllowedInstances.TryGetValue(Instance,out var target)) throw new Exception("Instance is not explicitly authorized.");
                Target=target;
            }
            if(Instance!=1)
            {
                var source=Directory.GetCurrentDirectory();
                var work=Path.Combine(source,"local","instances",Instance.ToString()); Directory.CreateDirectory(work);
                foreach(var file in new[]{"flow.json","stages.json","plan.json","handlers.json"}) File.Copy(Path.Combine(source,file),Path.Combine(work,file),true);
                Directory.CreateDirectory(Path.Combine(work,"templates"));
                foreach(var file in Directory.GetFiles(Path.Combine(source,"templates"),"*.png")) File.Copy(file,Path.Combine(work,"templates",Path.GetFileName(file)),true);
                Directory.SetCurrentDirectory(work);
            }
            if(args[0]=="validate") { ValidateFlow(); Console.WriteLine("Flow, images, stage boundaries and plan validated; no device commands sent."); return 0; }
            if(args[0]=="check-touch") { Console.WriteLine(await StoryTouchDevice()+" (no input sent)"); return 0; }
            if(args.Contains("--background"))
            {
                Directory.CreateDirectory("artifacts");
                var writer=new StreamWriter(new FileStream("artifacts/runner.log",FileMode.Append,FileAccess.Write,FileShare.ReadWrite),new UTF8Encoding(false)) { AutoFlush=true };
                Console.SetOut(writer); Console.SetError(writer);
                Console.WriteLine($"\n[{DateTimeOffset.Now:O}] {args[0]}, instance {Instance}");
            }
            if(args[0]=="resume")
            {
                ValidateFlow();
                var option=Array.IndexOf(args,"--from");
                From=ResolveResumePoint(option>=0?args.ElementAtOrDefault(option+1):"saved");
                RefreshInviteCode();
            }
            if(args.FirstOrDefault() is "run" or "tutorial" or "stage")
            {
                ValidateFlow();
                var stagesOption=Array.IndexOf(args,"--stages");
                SelectedStages=args[0]=="stage" ? [args.ElementAtOrDefault(1)??throw new Exception("stage requires a stage name")]
                    : stagesOption>=0 ? (args.ElementAtOrDefault(stagesOption+1)??"").Split(',')
                    : JsonSerializer.Deserialize<Plan>(File.ReadAllText("plan.json"))!.Stages;
                foreach(var stage in SelectedStages) if(!LoadStages().Any(s=>s.Name==stage)) throw new Exception("Unknown stage: "+stage);
                if(SelectedStages.Distinct().Count()!=SelectedStages.Length) throw new Exception("Duplicate stages are not allowed.");
                if(args[0]=="run" && SelectedStages.FirstOrDefault()!="onboarding") throw new Exception("A reset run must begin with onboarding.");
                var fromOption=Array.IndexOf(args,"--from");
                From=fromOption>=0?args.ElementAtOrDefault(fromOption+1):null;
                var untilOption=Array.IndexOf(args,"--until");
                Until=untilOption>=0?args.ElementAtOrDefault(untilOption+1):null;
                if(Until!=null && (From==null || LoadSteps().FindIndex(s=>s.Name==Until)<LoadSteps().FindIndex(s=>s.Name==From))) throw new Exception("--until must identify a step at or after --from.");
                if(fromOption>=0 && (args[0]!="tutorial" || !LoadSteps().Any(s=>s.Name==From))) throw new Exception("--from requires tutorial and a valid step name.");
                var option=Array.IndexOf(args,"--unit");
                string? input=option>=0?args.ElementAtOrDefault(option+1):null;
                if(input==null && SelectedStages.Contains("onboarding") && From==null) { Console.Write("유닛 선택 (레오니/모모점/비배스/원더쇼/니고): "); input=Console.ReadLine(); }
                input??="mmj";
                Unit=input?.Trim().ToLowerInvariant() switch
                {
                    "레오니" or "leo" or "leoneed" => "leo",
                    "모모점" or "mmj" => "mmj",
                    "비배스" or "vbs" => "vbs",
                    "원더쇼" or "wxs" => "wxs",
                    "니고" or "n25" => "n25",
                    _ => throw new Exception("Unknown unit. No device changes made.")
                };
                var loopsOption=Array.IndexOf(args,"--loops");
                if(loopsOption>=0)
                {
                    if(!int.TryParse(args.ElementAtOrDefault(loopsOption+1),out LoopTotal) || LoopTotal<1 || LoopTotal>99) throw new Exception("--loops must be 1-99.");
                    if(LoopTotal>1 && args[0]!="run") throw new Exception("--loops requires run.");
                    if(LoopTotal>1 && (!RefreshInviteCode() || InviteCapacity("artifacts")<LoopTotal)) throw new Exception("Repeating needs invitation codes in artifacts/invite.json for every loop: a permanent code, or single-use codes for as many loops. No changes made.");
                }
                if(!RefreshInviteCode()) Console.WriteLine("No invitation code yet; artifacts/invite.json is checked again when the invitation step is reached.");
            }
            var needsLock=args[0] is "run" or "register-invite" or "close-rewards" or "tutorial" or "stage" or "resume" or "reset" or "restore" or "record" or "tap" or "swipe" or "skip-gacha" or "read-story" or "select-memories" or "prepare" or "exit-area-chat";
            // File locking survives async thread changes (a named Mutex is thread-affine).
            using var controlLock=needsLock?new FileStream("instance.lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None):null;
            Directory.CreateDirectory("artifacts");
            if(args[0]=="prepare") { await PrepareInstance(); return 0; }
            if(args[0] is "run" or "tutorial" or "stage" or "resume" or "register-invite") await EnsureResolution();
            if(args[0]=="resume")
            {
                File.Delete("artifacts/macro.stop");
                await EnsureRunning();
                if(ResumeAuto) await RecoverAutoLive();
                if(ResumeStory) await RecoverStory();
                if(ResumePostStory)
                {
                    await RecoverStory();
                    if(From!="event-story-first") throw new Exception("Story playback has not reached its list; completion verification was not started.");
                    From="event-stories-complete";
                }
                await UntilRewards(()=>Tutorial());
                if(ProgressStage!=null) await WriteProgress(ProgressStage,StageIncomplete?"story-incomplete":"complete");
                await ContinueLoops();
                return 0;
            }
            await Guard();
            switch (args.FirstOrDefault() ?? "inspect")
            {
                case "inspect":
                    Console.WriteLine(await Shell("getprop sys.boot_completed; pm list packages pjsekai; ls -ld " + Data + "; du -sk " + Data + "; df -k /data /sdcard; cat /proc/meminfo | head -5"));
                    break;
                case "snap": await Snapshot(args.ElementAtOrDefault(1) ?? "artifacts/current.png"); break;
                case "exit-area-chat":
                    ActiveStep="music-area-right"; await Snapshot("artifacts/current.png",true);
                    using(var shot=LoadCurrentScreen()) await EscapeUnexpectedStory(shot,ActiveStep);
                    break;
                case "tap":
                    await Shell($"input tap {int.Parse(args[1])} {int.Parse(args[2])}");
                    await Task.Delay(1000);
                    await Snapshot("artifacts/current.png");
                    break;
                case "launch":
                    Console.WriteLine(await Shell($"monkey -p {Package} -c android.intent.category.LAUNCHER 1")); break;
                case "reset": await Reset(); break;
                case "restore": await Restore(); break;
                case "record": await Record(args); break;
                case "skip-gacha": await SkipGacha(); break;
                case "read-story": await ReadStory(); break;
                case "ocr": Console.WriteLine(await Ocr("artifacts/current.png")); break;
                case "select-memories": await SelectMemories(); break;
                case "stop-story": await Shell("touch /data/local/tmp/sekai-story.stop"); await File.WriteAllTextAsync("artifacts/story.stop", "stop"); break;
                case "swipe": await Shell($"input swipe {int.Parse(args[1])} {int.Parse(args[2])} {int.Parse(args[3])} {int.Parse(args[4])} 500"); await Task.Delay(800); await Snapshot("artifacts/current.png"); break;
                case "tutorial": if(From!=null) await Tutorial(); else await ExecuteStages(); break;
                case "stage": await ExecuteStages(); break;
                case "register-invite": await RegisterAwaitingInvitation(); break;
                case "close-rewards": await CloseRewardsNow(); break;
                case "run": File.Delete("artifacts/macro.stop"); File.Delete("artifacts/story.stop"); await RunLoops(1); break;
                default: throw new Exception("Commands: run --unit mmj | tutorial --unit mmj [--from step] | inspect | snap [file] | tap x y | launch | reset | restore");
            }
            return 0;
        }
        catch(Exception ex) { Console.Error.WriteLine(ex.ToString()); return 1; }
    }
    static List<Step> StepsForStage(string name)
    {
        var stage=LoadStages().Single(s=>s.Name==name);
        var flow=LoadSteps();
        var first=flow.FindIndex(s=>s.Name==stage.Start); var last=flow.FindIndex(s=>s.Name==stage.End);
        if(first<0 || last<first) throw new Exception("Invalid stage boundaries: "+name);
        return flow.GetRange(first,last-first+1);
    }
    static async Task ExecuteStages()
    {
        foreach(var name in SelectedStages)
        {
            if(name=="invitation" && !RefreshInviteCode())
            {
                Console.WriteLine("Skipped invitation stage: no code supplied.");
                continue;
            }
            Console.WriteLine("Stage: "+name);
            StageIncomplete=false;
            await WriteProgress(name,"running");
            var rewarded=await UntilRewards(()=>Tutorial(StepsForStage(name)));
            await WriteProgress(name,StageIncomplete?"story-incomplete":"complete");
            if(StageIncomplete || rewarded) return;
        }
    }
    static string? ProgressStage;
    static Task WriteProgress(string stage,string status)
    {
        ProgressStage=stage;
        return File.WriteAllTextAsync("artifacts/progress.json",JsonSerializer.Serialize(new { Stage=stage, Status=status, Time=DateTimeOffset.Now },JsonOptions));
    }
    static string? StageOf(string step)=>LoadStages().Select(s=>s.Name).FirstOrDefault(name=>StepsForStage(name).Any(s=>s.Name==step));
    static List<Step> EffectiveSteps(IEnumerable<Step> steps)
    {
        if(InviteCode!=null) return steps.ToList();
        var invitation=StepsForStage("invitation").Select(s=>s.Name).ToHashSet();
        return steps.Where(s=>!invitation.Contains(s.Name)).ToList();
    }
    static void ValidateFlow()
    {
        var flow=LoadSteps();
        if(flow.Count==0 || flow.Select(s=>s.Name).Distinct().Count()!=flow.Count) throw new Exception("Empty flow or duplicate step names.");
        string[] actions=["tap","back","swipe","checkpoint","stop","gacha-once","map-end","area-right","song-query","cans-max","invite-code","invite-submit","dismiss-unlocks","read-story","wait-autolive","select-memories","dismiss-live-guide","dismiss-auto-end"];
        foreach(var step in flow)
        {
            if(!actions.Contains(step.Action)) throw new Exception("Unknown action: "+step.Name);
            if(!File.Exists(step.Image) || step.Left<0 || step.Top<0 || step.Width<=0 || step.Height<=0 || step.Left+step.Width>960 || step.Top+step.Height>540)
                throw new Exception("Invalid template: "+step.Name);
            using var image=new Bitmap(step.Image);
            if(image.Width!=step.Width || image.Height!=step.Height) throw new Exception("Template dimensions differ: "+step.Name);
        }
        var covered=LoadStages().SelectMany(s=>StepsForStage(s.Name)).Select(s=>s.Name).ToList();
        if(covered.Count!=flow.Count || covered.Distinct().Count()!=flow.Count || flow.Any(s=>!covered.Contains(s.Name))) throw new Exception("Stage coverage has gaps or overlaps.");
        var plan=JsonSerializer.Deserialize<Plan>(File.ReadAllText("plan.json"))!;
        if(plan.Stages.Distinct().Count()!=plan.Stages.Length || plan.Stages.Any(n=>!LoadStages().Any(s=>s.Name==n))) throw new Exception("Invalid stage order.");
        if(plan.Stages.Contains("eventstory") && Array.IndexOf(plan.Stages,"eventstory")<Array.IndexOf(plan.Stages,"autolive")) throw new Exception("Event stories must follow auto live.");
    }
    static async Task Guard()
    {
        if(DateTime.UtcNow-LastGuard<TimeSpan.FromSeconds(30)) return;
        for(var attempt=0;attempt<3;attempt++)
        {
        // Match both immutable session index and exact name; no wildcard/broadcast commands.
        var row = await VmRow(Instance);
        if(row!=null && row[1]!=Target) throw new Exception("Instance name changed. Stopped.");
        if(row!=null && row[4]=="1")
        {
            try
            {
                if(attempt>0) await ConnectVm(Instance);
                if((await Run(Adb,"-s",Serial,"get-state")).Trim()=="device") { LastGuard=DateTime.UtcNow; return; }
            }
            catch when(attempt<2) { }
        }
        if(attempt<2) await Task.Delay(1000);
        }
        throw new Exception("Target instance "+Instance+" unavailable after 3 checks.");
    }
    static async Task PrepareInstance()
    {
        var row=await VmRow(Instance);
        if(row==null || row[1]!=Target) throw new Exception("Instance identity differs. Nothing changed.");
        Console.WriteLine($"Prepared {Instance} ({KindOf(Instance)}): {Target}, {await PrepareVm(Instance,row[4]=="1")}");
    }
    // Every VM must run at 960x540 DPI 160; LDPlayer can lose a VM's settings file (one came back at 1280x720).
    // A VM that is off gets its settings fixed before launch; one that is on is measured and, if wrong, restarted with fixed settings.
    static async Task<bool> LiveResolutionOk(int instance)
    {
        string output;
        try { output=await Run(AdbOf(instance),"-s",SerialOf(instance),"shell","wm size; wm density"); }
        catch { return true; } // Not reachable yet: the boot wait or Guard reports that.
        // A reply cut short (the adb server restarting) tells nothing; only a reported size can be wrong.
        return !output.Contains("Physical size") || ResolutionMatches(output);
    }
    static async Task EnsureResolution()
    {
        var row=await VmRow(Instance);
        if(row==null) return;
        if(row[4]!="1") { await PrepareInstance(); return; }
        await ConnectVm(Instance);
        if(await LiveResolutionOk(Instance)) return;
        if(!FixesSettings(Instance)) throw new Exception("VM is not running at 960x540 DPI160. Change it in BlueStacks settings (Display) and restart the instance.");
        Console.WriteLine("VM is not running at 960x540 DPI160; restarting it with fixed settings.");
        await QuitVm(Instance);
        await PrepareInstance();
        await VmLaunch(Instance);
        var deadline=DateTime.UtcNow.AddMinutes(3);
        while(DateTime.UtcNow<deadline)
        {
            try { await ConnectVm(Instance); if((await Shell("getprop sys.boot_completed")).Trim()=="1") break; } catch { }
            await Task.Delay(2000);
        }
        if(!await LiveResolutionOk(Instance)) throw new Exception("VM resolution is still not 960x540 DPI160 after restarting with fixed settings.");
        LastGuard=DateTime.MinValue;
    }
#if WINDOWS
    // macOS reads text with Vision instead (MacApp.cs).
    static async Task<string> Ocr(string path)
    {
        var engine=Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("ja"))??throw new Exception("Windows Japanese OCR is unavailable.");
        var file=await Windows.Storage.StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
        using var stream=await file.OpenReadAsync();
        var decoder=await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
        using var bitmap=await decoder.GetSoftwareBitmapAsync(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);
        var result=await engine.RecognizeAsync(bitmap);
        return string.Join("\n",result.Lines.Select(line=>line.Text));
    }
    static async Task<string> OcrRegion(Bitmap screen,Rectangle region,int scale=2)
    {
        using var crop=screen.Clone(region,PixelFormat.Format24bppRgb);
        using var enlarged=new Bitmap(crop.Width*scale,crop.Height*scale);
        using(var graphics=Graphics.FromImage(enlarged)) { graphics.InterpolationMode=System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic; graphics.DrawImage(crop,0,0,enlarged.Width,enlarged.Height); }
        var path="artifacts/ocr-region.png"; enlarged.Save(path,ImageFormat.Png);
        return Regex.Replace(await Ocr(path),@"\s+","");
    }
#endif
    static async Task SelectMemories()
    {
        // Reset the left list to its upper edge, then inspect entries in visual order.
        for(var i=0;i<5;i++) await Shell("input swipe 65 140 65 440 250");
        var freeDraw=LoadSteps().Single(s=>s.Name=="memories-ten");
        for(var page=0;page<8;page++)
        {
            foreach(var y in new[]{100,167,234,301,368,435})
            {
                if(StopRequested) throw new OperationCanceledException();
                await Shell($"input tap 60 {y}"); await Task.Delay(700);
                await Snapshot("artifacts/current.png",true);
                using var screen=LoadCurrentScreen();
                // User rule: first banner from the top with the ordinary ten-draw button.
                // Compare only the small lower-right button; ignore unit artwork and names.
                using var button=new Bitmap(freeDraw.Image);
                var difference=Difference(screen,button,freeDraw);
                await File.AppendAllTextAsync("artifacts/gacha-selection.log",$"Page={page} Y={y} OrdinaryButtonDifference={difference:F2}\n");
                Console.WriteLine($"Gacha candidate page {page}, row {y}: ordinary-button difference={difference:F2}");
                if(difference<12)
                {
                    await Task.Delay(400); await Snapshot("artifacts/gacha-verify.png",true);
                    using var verify=new Bitmap("artifacts/gacha-verify.png");
                    if(!Matches(verify,freeDraw)) continue;
                    File.Copy("artifacts/current.png","artifacts/selected-memories.png",true);
                    Console.WriteLine("Selected the topmost Memories banner with the ordinary 3000-crystal ten-draw button. No draw submitted."); return;
                }
            }
            await Shell("input swipe 65 435 65 100 500"); await Task.Delay(500);
        }
        throw new Exception("No unambiguous non-paid-only Memories gacha found. No draw submitted.");
    }
    static async Task<string> Shell(string command)
    {
        var taps=Regex.Matches(command,@"\binput\s+tap\b").Count;
        if(taps>0 && !RapidStorySteps.Contains(ActiveStep??""))
        {
            if(taps>1) throw new Exception("Multiple taps outside a story step are prohibited.");
            var wait=900-(DateTime.UtcNow-LastSingleTap).TotalMilliseconds;
            if(wait>0) await Task.Delay((int)wait);
            LastSingleTap=DateTime.UtcNow;
        }
        return await Run(Adb,"-s",Serial,"shell",command);
    }
    static async Task Reset()
    {
        if((await Shell($"pm path {Package}")).Trim().Length == 0) throw new Exception("Package not installed.");
        Console.WriteLine(await Shell($"am force-stop {Package}"));
        var before = (await Shell($"du -sk {Data}")).Trim();
        // Never clear until mv succeeds. Never overwrite an existing recovery directory.
        await File.WriteAllTextAsync("artifacts/reset-before.txt",before);
        var moved = await Shell($"test -d {Data} && test ! -e {Saved} && mv {Data} {Saved} && echo PRESERVED");
        if(!moved.Contains("PRESERVED")) throw new Exception("Folder preservation failed; app data was NOT cleared.");
        try
        {
            var result = await Shell($"pm clear {Package}");
            if(result.Trim() != "Success") throw new Exception("pm clear failed: " + result);
            Console.WriteLine("App data cleared.");
        }
        finally { await Restore(); }
        Console.WriteLine("Before: " + before);
        var after=(await Shell($"du -sk {Data}")).Trim();
        Console.WriteLine("After: " + after);
        if(before!=after) throw new Exception("Restored folder size differs; inspect before launching.");
        foreach(var name in new[]{"memories-confirm-once","guaranteed-confirm-once"}) File.Delete($"artifacts/{name}.sent");
        File.Delete("artifacts/basic-data-verified.png");
        await WriteLoop();
        if(StopRequested) throw new OperationCanceledException("Stopped after restoring external data.");
    }
    static async Task Restore()
    {
        var result = await Shell($"test -d {Saved} && test ! -e {Data} && mv {Saved} {Data} && echo RESTORED");
        if(!result.Contains("RESTORED")) throw new Exception("Restore stopped. Preserved data remains at " + Saved + "; inspect before retry. No files overwritten.");
        Console.WriteLine("External data folder restored.");
    }
    static async Task Snapshot(string path, bool quiet=false)
    {
        for(var attempt=0;attempt<3;attempt++)
        {
            try
            {
                var bytes=await RunBytes(Adb,"-s",Serial,"exec-out","screencap","-p");
                if(bytes.Length<8 || bytes[0]!=137 || bytes[1]!=80) throw new Exception("Invalid screenshot output.");
                using(var stream=new MemoryStream(bytes))
                using(var decoded=new Bitmap(stream))
                {
                    if(decoded.Width!=960 || decoded.Height!=540) throw new Exception($"Transient screenshot size {decoded.Width}x{decoded.Height}; recapturing.");
                    _=decoded.GetPixel(959,539);
                }
                await File.WriteAllBytesAsync(path+".pending",bytes);
                File.Move(path+".pending",path,true);
                if(!quiet) Console.WriteLine(Path.GetFullPath(path));
                return;
            }
            catch(Exception ex) when(attempt<2 && !StopRequested) { Console.WriteLine($"Recapturing frame ({attempt+1}/3): {ex.Message}"); await Task.Delay(700); }
        }
    }
    record Step(string Name, string Image, int X, int Y, int Left, int Top, int Width, int Height, string Action, int EndX=0, int EndY=0);
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    static List<Step> LoadSteps() => File.Exists("flow.json") ? JsonSerializer.Deserialize<List<Step>>(File.ReadAllText("flow.json"))! : [];
    static async Task Record(string[] args)
    {
        var name=args[1]; var x=int.Parse(args[2]); var y=int.Parse(args[3]);
        var left=args.Length>4?int.Parse(args[4]):Math.Clamp(x-80,0,800);
        var top=args.Length>5?int.Parse(args[5]):Math.Clamp(y-20,0,500);
        var width=args.Length>6?int.Parse(args[6]):160;
        var height=args.Length>7?int.Parse(args[7]):40;
        var action=args.ElementAtOrDefault(8)??"tap";
        Directory.CreateDirectory("templates");
        await Snapshot("artifacts/record.png");
        Directory.CreateDirectory("artifacts/frames");
        File.Copy("artifacts/record.png",$"artifacts/frames/{name}.png",true);
        using(var screen=new Bitmap("artifacts/record.png"))
        using(var crop=screen.Clone(new Rectangle(left,top,width,height),PixelFormat.Format24bppRgb))
            crop.Save($"templates/{name}.png",ImageFormat.Png);
        var steps=LoadSteps();
        var step=new Step(name,$"templates/{name}.png",x,y,left,top,width,height,action);
        var existing=steps.FindIndex(s=>s.Name==name);
        if(existing>=0) steps[existing]=step; else steps.Add(step);
        await File.WriteAllTextAsync("flow.json",JsonSerializer.Serialize(steps,JsonOptions));
        if(action is "tap" or "gacha-once") await Shell($"input tap {x} {y}");
        else if(action=="back") await Shell("input keyevent 4");
        else if(action is "map-end" or "area-right") await ScrollToEdge(action);
        else if(action=="song-query") await EnterSongQuery();
        else if(action=="cans-max") await Shell("input swipe 400 187 565 187 400; input tap 595 186");
        else if(action=="invite-code") await EnterInvitationCode();
        else if(action=="invite-submit") await SubmitInvitation();
        else if(action=="dismiss-unlocks") await DismissRepeated(step);
        if(action=="gacha-once") await SkipGacha();
        await Task.Delay(1500);
        await Snapshot("artifacts/current.png");
    }
    static async Task Tutorial(List<Step>? stageSteps=null)
    {
        var steps=LoadSteps();
        if(steps.Count==0) throw new Exception("No recorded flow.");
        Step? previous=null;
        var completedHelpGroups=new HashSet<string>();
        var selectedSteps=stageSteps??(From==null?steps:steps.SkipWhile(s=>s.Name!=From).ToList());
        if(Until!=null) selectedSteps=selectedSteps.Take(selectedSteps.FindIndex(s=>s.Name==Until)+1).ToList();
        var invitationSteps=StepsForStage("invitation").Select(s=>s.Name).ToHashSet();
        var invitationStart=LoadStages().Single(s=>s.Name=="invitation").Start;
        for(var stepIndex=0;stepIndex<selectedSteps.Count;stepIndex++)
        {
            var step=selectedSteps[stepIndex];
            var onboardingStep=StepsForStage("onboarding").Any(s=>s.Name==step.Name);
            var eventStoryStep=StepsForStage("eventstory").Any(s=>s.Name==step.Name);
            if(invitationSteps.Contains(step.Name))
            {
                // Decided when the stage begins, so a code saved mid-run is still used.
                if(step.Name==invitationStart) SkipInvitation=!RefreshInviteCode();
                if(SkipInvitation) { if(step.Name==invitationStart) Console.WriteLine("No invitation code: invitation stage skipped."); continue; }
            }
            // A resumed run goes on through the later stages: keep progress.json, whose stage the panel shows, current.
            if(stageSteps==null && StageOf(step.Name) is { } stepStage && stepStage!=ProgressStage) await WriteProgress(stepStage,"running");
            await SaveCheckpoint(step.Name,"waiting");
            if(HomeGuidesCompleted && (step.Name.StartsWith("home-guide-") || step.Name is "login-bonus" or "campaign-bonus" or "beginner-bonus" or "gift-open"))
            { await SaveCheckpoint(step.Name,"executed"); continue; }
            if(File.Exists("artifacts/basic-data-verified.png") && (step.Name.StartsWith("opening-") || step.Name is "base-data-only" or "base-data-download"))
            { await SaveCheckpoint(step.Name,"executed"); continue; }
            if(step.Action=="dismiss-auto-end") { await DismissAutoEnd(); await SaveCheckpoint(step.Name,"executed"); previous=null; continue; }
            if(step.Action=="dismiss-unlocks") { await DismissRepeated(step); previous=step; continue; }
            if(step.Action=="select-memories") { await SelectMemories(); previous=null; continue; }
            if(step.Action=="read-story") { await ReadStory(); await SaveCheckpoint(step.Name,"executed"); previous=null; continue; }
            if(step.Action=="dismiss-live-guide") { await DismissLiveGuide(); previous=null; continue; }
            if(step.Action=="wait-autolive") { await WaitAutoLive(); previous=null; continue; }
            if(step.Name=="event-story-open") { await OpenEventStories(); await SaveCheckpoint(step.Name,"executed"); previous=null; continue; }
            if(step.Name=="event-story-first" && ResumeStory) { await SelectResumeEpisode(); await SaveCheckpoint(step.Name,"executed"); previous=null; continue; }
            if(step.Name.Contains("-help-") || step.Name.EndsWith("-help"))
            {
                var split=step.Name.IndexOf("-help-");
                var group=split>=0?step.Name[..split]:step.Name;
                if(completedHelpGroups.Add(group))
                {
                    var after=steps.SkipWhile(s=>s.Name!=step.Name).SkipWhile(s=>s.Name==step.Name || s.Name.StartsWith(group+"-help-")).FirstOrDefault();
                    await DismissTutorialPanels(after);
                }
                previous=null; continue;
            }
            if(step.Name=="music-shop-open") { await OpenMusicShop(step); previous=step; continue; }
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Waiting: {step.Name}");
            var deadline=DateTime.UtcNow.AddMinutes(10);
            var lastMovieTap=DateTime.UtcNow;
            var lastRetry=DateTime.UtcNow;
            var lastActivityCheck=DateTime.MinValue;
            var storyScrolls=0;
            var matches=0;
            var skipOptional=false;
            var recovered=false;
            var lastRecovery=DateTime.UtcNow;
            // A story's SKIP shows once its menu is open; a step reached without one (a resume, a recovery) taps the menu again.
            if(previous==null && step.Name.EndsWith("-skip") && steps.FirstOrDefault(s=>s.Name==step.Name[..^"skip".Length]+"menu") is { } storyMenu) previous=storyMenu;
            // The screen after a story's menu, SKIP and confirmation: a short story can end under fast taps before its menu is seen.
            var afterStory=step.Name.EndsWith("-menu") && step.Name!="event-live-menu"?selectedSteps.Skip(stepIndex+1)
                .SkipWhile(s=>s.Name.StartsWith(step.Name[..^"menu".Length]) && (s.Name.EndsWith("-skip") || s.Name.EndsWith("-confirm"))).FirstOrDefault():null;
            // Another story's menu looks the same, so it tells nothing.
            if(afterStory!=null && RapidStorySteps.Contains(afterStory.Name)) afterStory=null;
            using var expected=new Bitmap(step.Image);
            while(true)
            {
                if(StopRequested || File.Exists("artifacts/macro.stop")) throw new OperationCanceledException("Stopped. Current progress preserved.");
                if(DateTime.UtcNow>deadline) throw new Exception($"Timed out waiting for {step.Name}; current screenshot saved.");
                await Guard();
                if(DateTime.UtcNow-lastActivityCheck>TimeSpan.FromSeconds(5))
                {
                    var activity=await Shell(ResumedActivity);
                    if(activity.Contains("com.google.android.gms") && (activity.Contains("MinuteMaid") || activity.Contains("SignIn")))
                    { await Shell("input keyevent 4"); await Task.Delay(1200); }
                    lastActivityCheck=DateTime.UtcNow;
                }
                await Snapshot("artifacts/current.png",true);
                double difference;
                using(var screen=LoadCurrentScreen())
                {
                    if(screen.Width!=960 || screen.Height!=540) throw new Exception("Flow requires 960x540. No tap sent.");
                    if(eventStoryStep && await DismissStoryReceipt(screen)) continue;
                    if(eventStoryStep && await DismissStoryReward(screen))
                    {
                        var help=selectedSteps.FindIndex(s=>s.Name=="event-help-1");
                        if(help>=0 && stepIndex<help)
                        {
                            Console.WriteLine("Intro reward cleared; continuing with event help and story list.");
                            stepIndex=help-1; previous=null; recovered=true; break;
                        }
                        continue;
                    }
                    // An installed game older than the server's asks for a store update and goes no further.
                    if(Matches(screen,new Step("app-update","templates/app-update.png",480,318,410,304,140,30,"checkpoint")))
                        throw new Exception("The game must be updated (新しいアプリがあります). Update it in VM management (game update) and start again.");
                    if(await RecoverStalledTitle(screen)) { lastActivityCheck=DateTime.MinValue; previous=null; continue; }
                    if(onboardingStep && steps.IndexOf(step)<steps.FindIndex(s=>s.Name=="gift-claim-all") && IsGiftInbox(screen))
                    { HomeGuidesCompleted=true; skipOptional=true; break; }
                    if(await EscapeUnexpectedStory(screen,step.Name)) { previous=null; continue; }
                    // Waiting for a story's menu or its SKIP at a choice: pick it; the menu tapped at the choice did not open
                    // and is tapped again once it shows.
                    if((step.Name.EndsWith("-menu") || step.Name.EndsWith("-skip")) && IsStoryChoice(screen))
                    {
                        Console.WriteLine("Story choice shown; picking the first one.");
                        await Shell($"input tap {StoryChoiceX} 270"); await Task.Delay(800);
                        lastRetry=DateTime.UtcNow;
                        continue;
                    }
                    if(await CloseRewardsBeforeVerification(screen,step.Name)) { previous=null; continue; }
                    if(DateTime.UtcNow-lastRecovery>TimeSpan.FromSeconds(30))
                    {
                        lastRecovery=DateTime.UtcNow;
                        var actual=FindRecoveryStep(screen,step.Name,selectedSteps);
                        Console.WriteLine($"State audit: waiting={step.Name}, current={actual??"unrecognized"}.");
                        File.Copy("artifacts/current.png","artifacts/state-audit.png",true);
                        var target=actual==null?-1:selectedSteps.FindIndex(s=>s.Name==actual);
                        if(target>=0 && target!=stepIndex)
                        {
                            await Task.Delay(500); await Snapshot("artifacts/current.png",true);
                            using var verify=LoadCurrentScreen();
                            if(FindRecoveryStep(verify,step.Name,selectedSteps)==actual)
                            {
                                Console.WriteLine($"Recovered current screen: {step.Name} -> {actual}.");
                                stepIndex=target-1; previous=null; recovered=true; break;
                            }
                        }
                    }
                    if(onboardingStep && IsHomeGuide(screen))
                    {
                        await DismissHomeGuides();
                        if(step.Name is "song-default" or "base-data-only" or "base-data-download" || step.Name.StartsWith("home-guide-"))
                        { skipOptional=true; break; }
                        continue;
                    }
                    if(step.Name is "tutorial-download" or "movie-skip" or "story-menu" or "story-skip" or "story-skip-confirm" && IsNameDialog(screen))
                    { Console.WriteLine("Name dialog reached; skipping completed introductory story step."); skipOptional=true; break; }
                    if(IsBaseDataDialog(screen))
                    {
                        await DownloadBasicData();
                        if(step.Name is "base-data-only" or "base-data-download" || step.Name.StartsWith("opening-")) { skipOptional=true; break; }
                        continue;
                    }
                    // A first install downloads all game data here; a moving download is progress, not a stall.
                    if(IsDataDownloadProgress(screen)) { deadline=DateTime.UtcNow.AddMinutes(10); await Task.Delay(2000); continue; }
                    if((step.Name is "story-menu" or "movie-skip" or "tutorial-download" || step.Name.StartsWith("opening-")) && IsMovieConfirm(screen))
                    { await Shell("input tap 570 318"); await Task.Delay(700); continue; }
                    if(await DismissNotice(screen)) { await Task.Delay(500); continue; }
                    if(step.Name.StartsWith("event-") || step.Name=="current-event-open")
                    {
                        if(IsAutoEnd(screen)) { await DismissAutoEnd(); continue; }
                        var unlock=steps.Single(s=>s.Name=="result-story-unlocks");
                        if(Matches(screen,unlock)) { await DismissRepeated(unlock); continue; }
                    }
                    difference=Difference(screen,expected,step);
                    if(afterStory!=null && difference>=12 && Matches(screen,afterStory))
                    {
                        Console.WriteLine($"The story ended before its menu was seen; continuing with {afterStory.Name}.");
                        stepIndex=selectedSteps.IndexOf(afterStory)-1; previous=null; recovered=true; break;
                    }
                    if(await DismissTutorialPanel(screen)) { await Task.Delay(400); continue; }
                    if(step.Name=="event-story-first" && !IsEventStoryList(screen))
                    { await OpenEventStories(); lastRetry=DateTime.UtcNow; continue; }
                    if(step.Name=="solo-live-open" && Matches(screen,steps.Single(s=>s.Name=="live-song-search-focus"))) skipOptional=true;
                    // The song shop may open on one unit's tab, which hides the searched song: search under すべて.
                    if(step.Name is "song-search-focus" or "song-open" && DateTime.UtcNow-lastRetry>TimeSpan.FromSeconds(2) &&
                       Matches(screen,steps.Single(s=>s.Name=="song-search-focus")) && !AllSongsTab(screen))
                    {
                        Console.WriteLine("Song shop is showing one unit's tab; selecting すべて.");
                        await Shell("input tap 123 84"); lastRetry=DateTime.UtcNow; await Task.Delay(700); continue;
                    }
                    if(step.Name=="members-open" && difference>=12 && DateTime.UtcNow-lastRetry>TimeSpan.FromSeconds(4) && Matches(screen,steps.Single(s=>s.Name=="memories-tab")))
                    { await Shell("input tap 32 32"); lastRetry=DateTime.UtcNow; continue; }
                    if(step.Name is "base-data-only" or "base-data-download")
                        if(File.Exists("artifacts/basic-data-verified.png") || IsAreaMain(screen)) skipOptional=true;
                    if(step.Name=="song-default" && IsAreaMain(screen)) skipOptional=true;
                    // A daily reward may interrupt any stage when the server date changes.
                    if(difference>=12 && step.Name is not ("campaign-bonus" or "event-intro-reward") && Matches(screen,steps.Single(s=>s.Name=="campaign-bonus")))
                    { await Shell("input tap 480 420"); await Task.Delay(800); continue; }
                    if(step.Name=="gacha-download" && Matches(screen,steps.Single(s=>s.Name=="memories-tab"))) skipOptional=true;
                    if(step.Name=="boost-plus-ten" && Matches(screen,steps.Single(s=>s.Name=="boost-confirm-ten"))) skipOptional=true;
                    if(step.Name=="boost-set-ten" && Matches(screen,steps.Single(s=>s.Name=="large-cans-max")))
                    { await Shell("input tap 350 32"); await Task.Delay(700); continue; }
                    if(step.Name=="boost-refill-tab" && Matches(screen,steps.Single(s=>s.Name=="large-cans-max"))) skipOptional=true;
                    if(step.Name is "auto-enable" or "auto-help-next" or "auto-help-close")
                        if(Matches(screen,new Step("auto-enabled","templates/auto-enabled.png",0,0,424,488,35,23,"checkpoint"))) skipOptional=true;
                    if(step.Name=="note-speed-confirm" && Matches(screen,steps.Single(s=>s.Name=="auto-live-started"))) skipOptional=true;
                    if(step.Name=="note-speed-confirm" && Matches(screen,new Step("live-active","templates/auto-live-pause.png",0,0,907,22,20,20,"checkpoint"))) skipOptional=true;
                    if(step.Name=="difficulty-more" && Matches(screen,steps.Single(s=>s.Name=="append-confirm"))) skipOptional=true;
                    if(step.Name=="tutorial-download" && (Matches(screen,steps.Single(s=>s.Name=="movie-skip")) || Matches(screen,steps.Single(s=>s.Name=="story-menu")))) skipOptional=true;
                    if(step.Name=="movie-skip" && Matches(screen,steps.Single(s=>s.Name=="story-menu"))) skipOptional=true;
                    if(step.Name=="event-data-download" && Matches(screen,steps.Single(s=>s.Name=="event-intro-menu"))) skipOptional=true;
                    if(step.Name is "event-live-menu" or "current-event-open")
                        if(new[]{"event-data-download","event-intro-menu","event-story-open"}.Any(n=>Matches(screen,steps.Single(s=>s.Name==n)))) skipOptional=true;
                    if(step.Name.StartsWith("event-intro-") || step.Name=="event-data-download")
                        if(Matches(screen,steps.Single(s=>s.Name=="event-story-open"))) skipOptional=true;
                    if(step.Name=="story-continuous-on" && StoryContinuousChecked(screen)) skipOptional=true;
                    if(step.Name=="story-no-voice" && difference<12 && !StoryContinuousChecked(screen))
                    {
                        Console.WriteLine("Continuous reading is unchecked; enabling and verifying before starting.");
                        await Shell("input tap 372 355"); await Task.Delay(500); continue;
                    }
                    if(step.Name=="invite-register")
                    {
                        var invalid=JsonSerializer.Deserialize<Step>(File.ReadAllText("handlers.json"))!;
                        if(Matches(screen,invalid)) { await RecordInvite("rejected",true); await Shell("input tap 480 420"); throw new Exception("Invitation rejected by the server. Field was verified; correct --code before resuming invite-code-focus."); }
                    }
                    if(step.Name=="google-back" && Matches(screen,steps.Single(s=>s.Name=="title-start"))) skipOptional=true;
                    // Campaigns change over time. Advance only when a later known reward screen is visible.
                    if(difference>=12 && step.Name is ("login-bonus" or "campaign-bonus" or "beginner-bonus"))
                    {
                        foreach(var next in steps.SkipWhile(s=>s.Name!=step.Name).Skip(1).TakeWhile(s=>s.Name!="gift-open"))
                            if(Matches(screen,next)) { skipOptional=true; break; }
                        if(IsAreaMain(screen)) skipOptional=true;
                    }
                    // Menu icons can appear briefly during scene transitions before accepting input.
                    var safeRetry=previous!=null && (previous.Name.EndsWith("-menu") || previous.Name.EndsWith("back-home") || previous.Name is "title-start" or "memories-result-ok" or "guaranteed-result-ok" or "gift-receipt-ok" or "deck-back-members" or "shop-back");
                    if(difference>=12 && safeRetry && DateTime.UtcNow-lastRetry>TimeSpan.FromSeconds(4) && Matches(screen,previous!))
                    { await Shell($"input tap {previous!.X} {previous.Y}"); lastRetry=DateTime.UtcNow; }
                }
                if(skipOptional) break;
                if(step.Name=="event-story-first" && difference>=12 && DateTime.UtcNow-lastRetry>TimeSpan.FromSeconds(2))
                {
                    if(++storyScrolls>15) throw new Exception("Episode 1 was not found in the story list.");
                    await Shell("input swipe 820 440 820 140 400"); lastRetry=DateTime.UtcNow;
                }
                matches=difference<(step.Name.StartsWith("live-help-")?5:12)?matches+1:0;
                if(matches>=2) break;
                // This step follows only the tutorial download. Tapping the movie opens its skip dialog.
                if(step.Name.EndsWith("-menu") && step.Name!="event-live-menu" && DateTime.UtcNow-lastMovieTap>TimeSpan.FromSeconds(1))
                {
                    await AdvanceNarration(step,afterStory);
                    lastMovieTap=DateTime.UtcNow;
                    continue;
                }
                if(step.Name is "movie-skip" or "tutorial-download" or "opening-skip" or "opening-confirm" && DateTime.UtcNow-lastMovieTap>TimeSpan.FromSeconds(3))
                {
                    var activity=await Shell(ResumedActivity);
                    if(activity.Contains(Package)) await Shell("input tap 480 270");
                    lastMovieTap=DateTime.UtcNow;
                }
                if(DateTime.UtcNow>deadline) throw new Exception($"Timed out waiting for {step.Name}. Screenshot: artifacts/current.png. No blind input sent.");
                await Task.Delay(1500);
            }
            if(recovered) continue;
            if(skipOptional) { Console.WriteLine("Optional screen absent: "+step.Name); continue; }
            Console.WriteLine("Matched: "+step.Name);
            if(step.Name=="invite-register") await RecordInvite("confirming",true);
            if(step.Name=="invite-success-dismiss") await CompleteInvite();
            if(step.Name=="story-no-voice")
            {
                using var screen=LoadCurrentScreen();
                if(!StoryContinuousChecked(screen)) throw new Exception("Continuous story reading is not checked; playback was not started.");
                File.Copy("artifacts/current.png","artifacts/story-continuous-verified.png",true);
                Console.WriteLine("Continuous story reading verified; starting without voice download.");
            }
            if(step.Name=="deck-complete") File.Copy("artifacts/current.png","artifacts/event-deck-result.png",true);
            if(step.Name=="deck-apply")
            {
                using var screen=LoadCurrentScreen();
                bool Selected(int x,int y) { var p=screen.GetPixel(x,y); return p.G>170 && p.B>140 && p.R<170; }
                if(!Selected(629,139) || !Selected(242,222)) throw new Exception("Event member and recommended type are not both selected. Deck was not applied.");
                File.Copy("artifacts/current.png","artifacts/event-deck-settings.png",true);
            }
            if(step.Action=="stop")
            {
                if(step.Name=="event-stories-complete" && !await FinishEventStories()) { StageIncomplete=true; return; }
                File.Copy("artifacts/current.png","artifacts/completed.png",true);
                Console.WriteLine("Complete: "+step.Name); return;
            }
            if(step.Name is "unit-select" or "unit-color" or "unit-scenery")
            {
                var (x,y)=Unit switch { "leo"=>(830,310), "mmj"=>(90,96), "vbs"=>(120,310), "wxs"=>(607,460), "n25"=>(870,95), _=>throw new Exception("Invalid unit") };
                await Shell($"input tap {x} {y}");
            }
            else if(step.Name=="event-story-first")
            {
                using var screen=LoadCurrentScreen(); using var template=new Bitmap(step.Image);
                var best=(Y:step.Top,Error:255.0);
                for(var top=90;top<465;top++) { var error=EpisodeDifferenceAt(screen,template,step.Left,top,step.Width,step.Height); if(error<best.Error) best=(top,error); }
                await Shell($"input tap {step.X} {best.Y+step.Height/2}");
            }
            else if(step.Action=="tap") await Shell($"input tap {step.X} {step.Y}");
            else if(step.Action=="back") await Shell("input keyevent 4");
            else if(step.Action is "map-end" or "area-right") await ScrollToEdge(step.Action);
            else if(step.Action=="song-query") await EnterSongQuery();
            else if(step.Action=="cans-max") await Shell("input swipe 400 187 565 187 400; input tap 595 186");
            else if(step.Action=="invite-code") await EnterInvitationCode();
            else if(step.Action=="invite-submit") await SubmitInvitation();
            else if(step.Action=="dismiss-unlocks") await DismissRepeated(step);
            else if(step.Action=="read-story") await ReadStory();
            else if(step.Action=="wait-autolive") await WaitAutoLive();
            else if(step.Action=="swipe") await Shell($"input swipe {step.X} {step.Y} {step.EndX} {step.EndY} 500");
            else if(step.Action=="gacha-once")
            {
                // Write before spending. An interrupted run must never blindly repeat a purchase.
                var receipt=$"artifacts/{step.Name}.sent";
                if(File.Exists(receipt)) throw new Exception("Draw already submitted in this reset session: "+step.Name);
                await File.WriteAllTextAsync(receipt,DateTimeOffset.Now.ToString("O"));
                await Shell($"input tap {step.X} {step.Y}");
                await SkipGacha();
            }
            previous=step;
            await SaveCheckpoint(step.Name,"executed");
            await Task.Delay(1800);
        }
    }
    static bool Matches(Bitmap screen,Step step)
    {
        using var expected=new Bitmap(step.Image);
        return Difference(screen,expected,step)<12;
    }
    static async Task WaitAutoLive()
    {
        var flow=LoadSteps(); var result=flow.Single(s=>s.Name=="event-from-result"); var unlock=flow.Single(s=>s.Name=="result-story-unlocks");
        var stable=0; var deadline=DateTime.UtcNow.AddHours(2);
        while(DateTime.UtcNow<deadline)
        {
            // Deliberately one screenshot each minute, not per song/frame.
            for(var i=0;i<60 && !StopRequested;i++) { if(File.Exists("artifacts/macro.stop")) StopRequested=true; await Task.Delay(1000); }
            if(StopRequested) throw new OperationCanceledException();
            await Guard(); await Snapshot("artifacts/current.png",true);
            using var screen=LoadCurrentScreen();
            if(await DismissNotice(screen)) continue;
            if(IsAutoEnd(screen)) { Console.WriteLine("Auto completion detected from Miku face fragment."); await SaveCheckpoint("auto-live-started","executed"); return; }
            if(Matches(screen,unlock)) return;
            stable=Matches(screen,result)?stable+1:0;
            if(stable>=2) return;
            Console.WriteLine("Auto live: checked at "+DateTime.Now.ToString("HH:mm:ss"));
        }
        throw new Exception("Auto live did not finish within two hours.");
    }
    static async Task EnterSongQuery()
    {
        for(var attempt=0;attempt<3;attempt++)
        {
            var existing=await FocusedInput();
            if(existing==null) throw new Exception("Song search is not focused. Nothing entered.");
            if(existing.Length>4096) throw new Exception("Unexpectedly long search field; no submission.");
            if(!await ClearFocusedInput(existing.Length+4)) continue;
            await Shell("input text 0.0000034");
            if(await SettledInput()=="0.0000034")
            {
                await File.WriteAllTextAsync("artifacts/song-search-verified.txt",$"Replaced {existing.Length} existing characters; verified exact query 0.0000034. Attempt {attempt+1}.");
                await Shell("input keyevent 66"); return;
            }
        }
        throw new Exception("Song query did not match after 3 replacements.");
    }
    static async Task ReadStory()
    {
        ActiveStep="story-reading";
        var reward=LoadSteps().Single(s=>s.Name=="event-intro-reward");
        var lastKnown=DateTime.UtcNow;
        var taps=0; var listFrames=0;
        File.Delete("artifacts/story.stop");
        // A long, recognized story is valid progress. Only actual completion or a stop
        // request ends reading; elapsed session time must never terminate a healthy run.
        while(true)
        {
            if(StopRequested || File.Exists("artifacts/story.stop") || File.Exists("artifacts/macro.stop")) throw new OperationCanceledException();
            var bytes=await RunBytes(Adb,"-s",Serial,"exec-out","screencap","-p");
            using var stream=new MemoryStream(bytes); using var screen=new Bitmap(stream);
            await File.WriteAllBytesAsync("artifacts/current.png",bytes);
            if(await DismissStoryReceipt(screen)) { lastKnown=DateTime.UtcNow; listFrames=0; continue; }
            if(await DismissNotice(screen)) { lastKnown=DateTime.UtcNow; continue; }
            // With the code entered beforehand, this dialog opens once the stories are read: capture and close it,
            // then the next step verifies all 20 episodes and counts the loop as done.
            if(await CloseRewardsBeforeVerification(screen,"event-stories-complete")) { Console.WriteLine($"Story reading ended with the invitation rewards after approximately {taps} dialogue taps."); return; }
            if(await CloseRewardsDuringStories(screen)) { lastKnown=DateTime.UtcNow; listFrames=0; continue; }
            if(IsStoryReader(screen) || IsDarkStoryTransition(screen))
            {
                if(taps==0) Console.WriteLine("Story playback confirmed; continuous dialogue taps with 120-second screen checks.");
                lastKnown=DateTime.UtcNow; listFrames=0;
                // One persistent ADB shell; no per-tap adb/Java process or image recognition.
                // The user explicitly requested two-minute screen checks during this phase.
                await ContinuousStoryTaps(); taps+=960; lastKnown=DateTime.UtcNow;
            }
            else if(Matches(screen,reward))
            { lastKnown=DateTime.UtcNow; listFrames=0; await Shell("input tap 480 420"); }
            else if(IsEventStoryList(screen))
            {
                if(++listFrames>=3) { Console.WriteLine($"Story list reached after approximately {taps} dialogue taps."); return; }
            }
            else
            {
                listFrames=0;
                if(DateTime.UtcNow-lastKnown>TimeSpan.FromSeconds(45)) throw new Exception("Unknown story screen for 45 seconds; paused. See artifacts/current.png.");
            }
            await Task.Delay(120);
        }
    }
    // Kernel input events: a timeval of two longs (8 bytes each for 64-bit Android, 4 for 32-bit), type, code and value.
    static byte[] EventData(bool wide,params (short type,short code,int value)[] events)
    {
        using var memory=new MemoryStream(); using var writer=new BinaryWriter(memory);
        foreach(var e in events)
        {
            if(wide) { writer.Write(0L); writer.Write(0L); } else { writer.Write(0); writer.Write(0); }
            writer.Write(e.type); writer.Write(e.code); writer.Write(e.value);
        }
        return memory.ToArray();
    }
    static string EventBytes(params (short type,short code,int value)[] events)=>string.Concat(EventData(true,events).Select(b=>"\\"+Convert.ToString(b,8).PadLeft(3,'0')));

    // LDPlayer (x86_64): /dev/input/event2 spans the 960x540 screen and printf writes whole events.
    // Other emulators: the touchscreen is the input device with multi-touch X/Y axes. Its range is read from the device:
    // BlueStacks spans 0-32767, MuMu 540x960 in portrait (rotated by Android; the centre is the centre either way),
    // MEmu and Nox 960x540. Devices with a tracking id take slot-based (type B) events, Nox's take type A events ended
    // by SYN_MT_REPORT. printf on newer Android splits its writes, which input devices reject, so the events are prepared
    // as files and written with cat.
    record StoryTouch(string Device,int MaxX,int MaxY,bool Pressure,bool Files,bool Tracking=true,bool Wide=true);
    static async Task<StoryTouch> StoryTouchDevice()
    {
        var abi=(await Shell("getprop ro.product.cpu.abi")).Trim();
        if(KindOf(Instance)=="ldplayer" && abi=="x86_64")
        {
            var info=await Shell("getevent -lp /dev/input/event2");
            if(info.Contains("max 959") && info.Contains("max 539") && info.Contains("ABS_MT_TRACKING_ID")) return new("/dev/input/event2",959,539,true,false);
        }
        // BlueStacks Air (arm64): "BlueStacks Virtual Touch" spans 0-32767 on both axes and has no pressure axis.
        if(IsBlueStacks)
        {
            var air=(await Shell("getevent -lp")).Split("add device").FirstOrDefault(d=>Regex.IsMatch(d,"name:\\s*\"BlueStacks Virtual Touch\""));
            var airPath=air==null?null:Regex.Match(air,@"^\s*\d+:\s*(/dev/input/event\d+)").Groups[1].Value;
            if(airPath is { Length: >0 } && Regex.Matches(air!,@"ABS_MT_POSITION_[XY]\s*: value -?\d+, min 0, max 32767").Count==2) return new(airPath,32767,32767,false,true);
            throw new Exception("Continuous touch device differs from the validated BlueStacks layout.");
        }
        foreach(var device in (await Shell("getevent -lp")).Split("add device").Skip(1))
        {
            var path=Regex.Match(device,@"^\s*\d+:\s*(/dev/input/event\d+)").Groups[1].Value;
            int Max(string axis)=>Regex.Match(device,axis+@"\s*: value -?\d+, min 0, max (\d+)") is { Success: true } m?int.Parse(m.Groups[1].Value):0;
            int maxX=Max("ABS_MT_POSITION_X"),maxY=Max("ABS_MT_POSITION_Y");
            if(path.Length==0 || maxX<100 || maxY<100 || Regex.IsMatch(device,@"name:\s*""[^""]*[Mm]ouse")) continue;
            return new(path,maxX,maxY,Max("ABS_MT_PRESSURE")>0,true,device.Contains("ABS_MT_TRACKING_ID"),abi.Contains("64"));
        }
        throw new Exception("No multi-touch input device was found; continuous taps were not started.");
    }
    static async Task AdvanceNarration(Step menu,Step? afterStory=null)
    {
        if(!RapidStorySteps.Contains(menu.Name)) throw new Exception("Rapid dialogue taps are restricted to named story steps.");
        var activity=await Shell(ResumedActivity);
        if(!activity.Contains(Package)) return;
        var tapping=ContinuousStoryTaps(15000);
        try
        {
            while(!tapping.IsCompleted && !StopRequested)
            {
                await Task.Delay(350);
                await Snapshot("artifacts/current.png",true);
                using var screen=LoadCurrentScreen();
                if(IsStoryChoice(screen)) { Console.WriteLine("Story choice shown; picking the first one."); await Shell($"input tap {StoryChoiceX} 270"); continue; }
                if(Matches(screen,menu) || IsMovieConfirm(screen) || IsBaseDataDialog(screen) || IsNameDialog(screen) || afterStory!=null && Matches(screen,afterStory))
                { await Shell("touch /data/local/tmp/sekai-story.stop"); break; }
            }
        }
        finally { await Shell("touch /data/local/tmp/sekai-story.stop"); await tapping; }
    }
    // Two story choices side by side: white buttons with dark lettering left and right of the centre (the unit intro's
    // 『セカイのわたし達？』 and 『想いの持ち主達？』). Centre taps miss them and the menu does not open while they are shown,
    // so the first one is picked; it changes nothing later.
    const int StoryChoiceX=250;
    static bool IsStoryChoice(Bitmap screen)
    {
        bool Button(int left)
        {
            int white=0,total=0,ink=0;
            for(var y=252;y<=288;y+=2) for(var x=left;x<left+220;x+=4)
            {
                total++; var p=screen.GetPixel(x,y);
                if(p.R>240 && p.G>240 && p.B>240) white++;
                else if(y is >=262 and <=276 && p.R+p.G+p.B<450) ink++;
            }
            return white>=total*7/10 && ink>=15;
        }
        return Button(140) && Button(600);
    }
    static async Task ContinuousStoryTaps(int durationMs=120000)
    {
        if(!RapidStorySteps.Contains(ActiveStep??"")) throw new Exception("Rapid taps blocked outside a story step.");
        var touch=await StoryTouchDevice();
        // The screen center (480,270) in the device's units: half of each axis, so a portrait device needs no rotation.
        List<(short,short,int)> down,up;
        if(touch.Tracking)
        {
            down=[(3,47,0),(3,57,1),(3,53,(touch.MaxX+1)/2),(3,54,(touch.MaxY+1)/2)];
            up=[(3,47,0),(3,57,-1)];
            if(touch.Pressure) { down.Add((3,58,1)); up.Add((3,58,0)); }
        }
        else
        {
            down=[(3,53,(touch.MaxX+1)/2),(3,54,(touch.MaxY+1)/2)];
            if(touch.Pressure) down.Add((3,58,1));
            down.Add((0,2,0)); up=[(0,2,0)];
        }
        down.AddRange([(1,330,1),(0,0,0)]); up.AddRange([(1,330,0),(0,0,0)]);
        string writeDown=$"printf '{EventBytes([..down])}'",writeUp=$"printf '{EventBytes([..up])}'",rest="0.100";
        if(touch.Files)
        {
            // Each cat takes about 13 ms, so the rest after a tap is shorter to keep 8 taps a second (measured on BlueStacks Air).
            rest="0.075";
            await File.WriteAllBytesAsync("artifacts/story-touch-down.bin",EventData(touch.Wide,[..down]));
            await File.WriteAllBytesAsync("artifacts/story-touch-up.bin",EventData(touch.Wide,[..up]));
            await Run(Adb,"-s",Serial,"push","artifacts/story-touch-down.bin","/data/local/tmp/sekai-touch-down.bin");
            await Run(Adb,"-s",Serial,"push","artifacts/story-touch-up.bin","/data/local/tmp/sekai-touch-up.bin");
            writeDown="cat /data/local/tmp/sekai-touch-down.bin"; writeUp="cat /data/local/tmp/sekai-touch-up.bin";
        }
        var script=$"rm -f /data/local/tmp/sekai-story.stop\nexec 3>{touch.Device} || exit 1\ni=0\nwhile [ $i -lt {durationMs/125} ] && [ ! -e /data/local/tmp/sekai-story.stop ]; do\n{writeDown} >&3\nsleep 0.025\n{writeUp} >&3\nsleep {rest}\ni=$((i+1))\ndone\n{writeUp} >&3\n";
        await File.WriteAllTextAsync("artifacts/story-taps.sh",script,new UTF8Encoding(false));
        await Run(Adb,"-s",Serial,"push","artifacts/story-taps.sh","/data/local/tmp/sekai-story-taps.sh");
        using var process=new Process { StartInfo=new ProcessStartInfo(Adb) { UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true } };
        foreach(var arg in new[]{"-s",Serial,"shell","sh /data/local/tmp/sekai-story-taps.sh"}) process.StartInfo.ArgumentList.Add(arg);
        process.Start(); var output=process.StandardOutput.ReadToEndAsync(); var error=process.StandardError.ReadToEndAsync();
        Console.WriteLine(durationMs==120000?"Continuous center taps: 8/sec target; next image check in 2 minutes.":"Fast-forwarding introductory narration until the story menu appears.");
        var until=DateTime.UtcNow.AddMilliseconds(durationMs);
        try
        {
            while(!process.HasExited && DateTime.UtcNow<until && !StopRequested && !File.Exists("artifacts/story.stop")) await Task.Delay(200);
        }
        finally
        {
            await Shell("touch /data/local/tmp/sekai-story.stop");
            using var timeout=new CancellationTokenSource(5000);
            try { await process.WaitForExitAsync(timeout.Token); } catch(OperationCanceledException) { process.Kill(true); }
        }
        await output; var stderr=await error;
        if(process.ExitCode!=0) throw new Exception("Continuous taps failed: "+stderr);
    }
    static async Task ScrollToEdge(string action)
    {
        for(var i=0;i<5;i++)
        {
            await Shell(action=="map-end" ? "input swipe 780 400 250 140 400" : "input swipe 800 300 170 300 400");
            await Task.Delay(200);
        }
    }
    static (int X,int Y,int Width,int Height,double Error) FindMusicShop(Bitmap screen,Step step)
    {
        var result=(X:0,Y:0,Width:0,Height:0,Error:255.0);
        foreach(var image in new[]{step.Image,"templates/music-shop-open.png"}.Distinct())
        {
        using var expected=new Bitmap(image);
        var best=(X:0,Y:0,Error:255.0);
        // The shop badge can shift by a single pixel as the area camera settles.
        for(var y=135;y<230;y+=2) for(var x=280;x<800;x+=2)
        {
            var error=DifferenceAt(screen,expected,x,y,expected.Width,expected.Height);
            if(error<best.Error) best=(x,y,error);
        }
        var coarse=best;
        for(var y=coarse.Y-2;y<=coarse.Y+2;y++) for(var x=coarse.X-2;x<=coarse.X+2;x++)
        {
            var error=DifferenceAt(screen,expected,x,y,expected.Width,expected.Height);
            if(error<best.Error) best=(x,y,error);
        }
        if(best.Error<result.Error) result=(best.X,best.Y,expected.Width,expected.Height,best.Error);
        if(result.Error<12) break;
        }
        return result;
    }
    static async Task OpenMusicShop(Step step)
    {
        for(var attempt=0;attempt<12;attempt++)
        {
            if(StopRequested) throw new OperationCanceledException();
            await Snapshot("artifacts/current.png",true);
            using var screen=LoadCurrentScreen();
            if(await EscapeUnexpectedStory(screen,"music-shop-open")) continue;
            var best=FindMusicShop(screen,step);
            if(best.Error<12) { await Shell($"input tap {best.X+best.Width/2} {best.Y+best.Height/2}"); return; }
            await Shell("input swipe 800 300 170 300 400"); await Task.Delay(500);
        }
        throw new Exception("Music shop sign not found after moving to the right edge.");
    }
    static async Task DismissLiveGuide()
    {
        var absent=0;
        for(var attempt=0;attempt<40;attempt++)
        {
            if(StopRequested) throw new OperationCanceledException();
            await Snapshot("artifacts/current.png",true);
            using var screen=LoadCurrentScreen();
            if(Matches(screen,LoadSteps().Single(s=>s.Name=="live-song-search-focus"))) return;
            if(Matches(screen,LoadSteps().Single(s=>s.Name=="solo-live-open"))) return;
            int pink=0,dim=0;
            // Overlay colors only: no wording, selected song, or mode name required.
            for(var y=60;y<430;y+=3) for(var x=440;x<945;x+=3)
            { var p=screen.GetPixel(x,y); if(p.R>190 && p.G<155 && p.B>100 && p.R>p.G*1.4) pink++; if(p.R<170 && p.G<180 && p.B<210) dim++; }
            if(pink>80 && dim>3000) { absent=0; await Shell("input tap 160 420"); }
            else if(++absent>=3) return;
            await Task.Delay(400);
        }
        throw new Exception("Live guidance did not dismiss after repeated neutral taps.");
    }
    static async Task<bool> DismissTutorialPanel(Bitmap screen)
    {
        var next=new Step("panel-next","templates/tutorial-next-button.png",720,500,650,482,140,33,"tap");
        var close=new Step("panel-close","templates/live-help-close.png",720,500,650,482,140,33,"tap");
        if(Matches(screen,next) || Matches(screen,close)) { await Shell("input tap 720 500"); return true; }
        var center=new Step("panel-center-close","templates/music-shop-help.png",480,490,400,470,160,40,"tap");
        if(Matches(screen,center)) { await Shell("input tap 480 490"); return true; }
        return false;
    }
    static bool IsEventStoryList(Bitmap screen)
    {
        var marker=LoadSteps().Single(s=>s.Name=="event-stories-complete");
        using var expected=new Bitmap(marker.Image);
        // The info button belongs to each episode row; row positions vary with unlocks.
        for(var y=85;y<465;y++)
            if(DifferenceAt(screen,expected,marker.Left,y,marker.Width,marker.Height)<10) return true;
        return false;
    }
    static bool StoryContinuousChecked(Bitmap screen)
    {
        var pink=0;
        for(var y=346;y<366;y++) for(var x=361;x<383;x++)
        {
            var p=screen.GetPixel(x,y);
            if(p.R>210 && p.G<155 && p.B>110 && p.R-p.G>70) pink++;
        }
        return pink>=25;
    }
    static async Task OpenEventStories()
    {
        var button=LoadSteps().Single(s=>s.Name=="event-story-open");
        var deadline=DateTime.UtcNow.AddSeconds(90);
        var listFrames=0; var buttonFrames=0;
        var lastTap=DateTime.MinValue;
        while(DateTime.UtcNow<deadline)
        {
            if(StopRequested || File.Exists("artifacts/macro.stop")) throw new OperationCanceledException();
            await Snapshot("artifacts/current.png",true);
            using var screen=LoadCurrentScreen();
            if(await DismissStoryReceipt(screen)) { listFrames=buttonFrames=0; continue; }
            if(await CloseRewardsBeforeVerification(screen,"event-stories-complete")) { listFrames=buttonFrames=0; continue; }
            if(await DismissStoryReward(screen)) { listFrames=buttonFrames=0; continue; }
            if(await DismissNotice(screen) || await DismissTutorialPanel(screen))
            { listFrames=buttonFrames=0; await Task.Delay(500); continue; }
            if(IsEventStoryList(screen))
            {
                buttonFrames=0;
                if(++listFrames>=2) { Console.WriteLine("Event story list entry confirmed."); return; }
            }
            else
            {
                listFrames=0;
                buttonFrames=Matches(screen,button)?buttonFrames+1:0;
                if(buttonFrames>=2 && DateTime.UtcNow-lastTap>TimeSpan.FromSeconds(2))
                {
                    Console.WriteLine("Event story button recognized; opening list.");
                    await Shell($"input tap {button.X} {button.Y}");
                    lastTap=DateTime.UtcNow; buttonFrames=0;
                }
            }
            await Task.Delay(500);
        }
        throw new Exception("Event story list did not open; no episode scrolling sent.");
    }
    static async Task DismissTutorialPanels(Step? after)
    {
        var seen=false; var absent=0; var deadline=DateTime.UtcNow.AddSeconds(90);
        while(DateTime.UtcNow<deadline)
        {
            if(StopRequested) throw new OperationCanceledException();
            await Snapshot("artifacts/current.png",true);
            using var screen=LoadCurrentScreen();
            if(after?.Name=="event-story-open" && await DismissStoryReward(screen)) continue;
            if(await DismissTutorialPanel(screen)) { seen=true; absent=0; }
            else if(after!=null && Matches(screen,after)) return;
            else if(seen && ++absent>=3) return;
            await Task.Delay(500);
        }
        throw new Exception("Tutorial panel did not reach its next screen.");
    }
    static async Task DismissRepeated(Step step)
    {
        var absent=0; var taps=0;
        var deadline=DateTime.UtcNow.AddMinutes(2);
        while(DateTime.UtcNow<deadline)
        {
            var bytes=await RunBytes(Adb,"-s",Serial,"exec-out","screencap","-p");
            using var stream=new MemoryStream(bytes); using var screen=new Bitmap(stream);
            if(IsAutoEnd(screen))
            { absent=0; await Shell("input tap 480 400"); taps++; }
            else if(Matches(screen,step))
            {
                absent=0;
                // Neutral point: never overlaps Result OK/Event/Song buttons.
                await Shell("input tap 480 400"); taps++;
            }
            else if(++absent>=8) { Console.WriteLine($"Unlock notices dismissed ({taps} taps)."); return; }
            if(StopRequested) return;
            await Task.Delay(300);
        }
        throw new Exception("Unlock notices did not clear within 2 minutes.");
    }
    static async Task<string?> FocusedInput()
    {
        // MuMu's Android 15 crashes in uiautomator (exit 139), usually after writing the dump and sometimes before: the
        // exit code is ignored, the old file is removed first so only a fresh dump is read, and a missing one is retried.
        for(var attempt=0;;attempt++)
        {
            await Shell("rm -f /sdcard/sekai-window.xml; uiautomator dump /sdcard/sekai-window.xml >/dev/null 2>&1; true");
            XDocument xml;
            try { xml=XDocument.Parse(await Shell("cat /sdcard/sekai-window.xml 2>/dev/null; true")); }
            catch(System.Xml.XmlException) when(attempt<4) { await Task.Delay(500); continue; }
            var inputs=xml.Descendants("node").Where(n=>(string?)n.Attribute("class")=="android.widget.EditText" && (string?)n.Attribute("package")==Package && (string?)n.Attribute("focused")=="true").ToList();
            return inputs.Count==1?(string?)inputs[0].Attribute("text"):null;
        }
    }
    // Android 15 (MuMu) applies injected keys some time after `input` returns, so a field is read until it stops changing.
    static async Task<string?> SettledInput()
    {
        var last=await FocusedInput();
        for(var i=0;i<8;i++)
        {
            await Task.Delay(300);
            var now=await FocusedInput();
            if(now==last) return now;
            last=now;
        }
        return last;
    }
    // Clears the focused field (cursor to the end, then backspaces) and waits until it is empty.
    static async Task<bool> ClearFocusedInput(int length)
    {
        await Shell("input keyevent 123; input keyevent "+string.Join(" ",Enumerable.Repeat("67",length)));
        return await SettledInput()=="";
    }
    static async Task EnterInvitationCode()
    {
        if(!Regex.IsMatch(InviteCode??"","^[A-Za-z0-9]{15}$")) throw new Exception("Use --code with exactly 15 ASCII letters/digits.");
        for(var attempt=1;attempt<=3;attempt++)
        {
            var current=await FocusedInput();
            if(current==null) throw new Exception("Invitation input is not focused. Nothing submitted.");
            if(!await ClearFocusedInput(Math.Max(32,current.Length+4))) { Console.WriteLine($"Field did not clear on attempt {attempt}; retrying."); continue; }
            if(attempt==1) await Shell("input text "+InviteCode);
            else await TypeInRuns(InviteCode!);
            var actual=await SettledInput();
            if(actual!=null && actual==InviteCode && actual.Length==15)
            {
                await File.WriteAllTextAsync("artifacts/invite-input-verified.txt",$"Verified exact match, 15 characters, attempt {attempt}.");
                await Shell("input keyevent 66");
                VerifiedInviteCode=InviteCode;
                Console.WriteLine("Invitation input verified (15 characters, exact case match).");
                return;
            }
            Console.WriteLine($"Input mismatch on attempt {attempt}; replacing field.");
        }
        throw new Exception("Invitation input failed verification after 3 attempts. Not submitted.");
    }
    // MEmu's IME holds typed letters as composing text and a digit typed next replaces them (abc1 becomes 1). Enter
    // commits the text and closes the input bar; tapping the field reopens it with the text and the cursor at its end.
    // So the code is typed up to each letter that a digit follows, committed, and continued in the reopened field.
    static async Task TypeInRuns(string code)
    {
        var field=LoadSteps().Single(s=>s.Name=="invite-code-focus");
        var start=0;
        for(var i=0;i<code.Length;i++)
        {
            if(i<code.Length-1 && !(char.IsLetter(code[i]) && char.IsDigit(code[i+1]))) continue;
            await Shell("input text "+code[start..(i+1)]);
            start=i+1;
            if(start>=code.Length) break;
            await Shell("input keyevent 66"); await Task.Delay(1000);
            await Shell($"input tap {field.X} {field.Y}");
            for(var wait=0;wait<10 && await FocusedInput()==null;wait++) await Task.Delay(500);
            await Shell("input keyevent 123");
        }
    }
    static async Task SubmitInvitation()
    {
        if(VerifiedInviteCode==null || VerifiedInviteCode!=InviteCode)
        {
            await Shell("input tap 501 267");
            await Task.Delay(400);
            await EnterInvitationCode();
        }
        await Shell("input tap 570 318");
        VerifiedInviteCode=null;
    }
    static async Task SkipGacha()
    {
        var skip=new Step("skip","templates/memories-animation-skip.png",928,32,913,17,30,30,"tap");
        var result=new Step("result","templates/memories-result-ok.png",860,500,817,482,95,30,"stop");
        var deadline=DateTime.UtcNow.AddSeconds(100);
        var lastTap=DateTime.MinValue;
        var lastNeutralTap=DateTime.UtcNow;
        while(DateTime.UtcNow<deadline)
        {
            if(StopRequested) return;
            var bytes=await RunBytes(Adb,"-s",Serial,"exec-out","screencap","-p");
            using var stream=new MemoryStream(bytes);
            using var screen=new Bitmap(stream);
            if(Matches(screen,result)) { await File.WriteAllBytesAsync("artifacts/gacha-result.png",bytes); Console.WriteLine("Gacha result reached."); return; }
            if(Matches(screen,skip) && DateTime.UtcNow-lastTap>TimeSpan.FromMilliseconds(600))
            { await Shell("input tap 928 32"); lastTap=DateTime.UtcNow; Console.WriteLine("Skipped gacha animation."); }
            else if(DateTime.UtcNow-lastNeutralTap>TimeSpan.FromSeconds(3))
            {
                // During an already-submitted draw only: dismiss card/costume reveals.
                // This neutral point is outside both draw/repeat buttons and purchase confirmation buttons.
                await Shell("input tap 480 450"); lastNeutralTap=DateTime.UtcNow;
            }
            await Task.Delay(200);
        }
        throw new Exception("Gacha skip timed out. The draw is NOT retried.");
    }
    // Unity's text input bar along the bottom edge. Its button shows Android's "ok" string in the system language:
    // "확인" in Korean (the template, from LDPlayer), "OK" in English and Japanese, "确定"/"確定" in Chinese.
    // Any label is lettering at the right end of the same white strip.
    static bool IsTextInputBar(Bitmap screen)
    {
        int white=0,total=0,ink=0;
        for(var y=462;y<538;y+=4) for(var x=0;x<860;x+=8) { total++; var p=screen.GetPixel(x,y); if(p.R>245 && p.G>245 && p.B>245) white++; }
        for(var y=486;y<516;y++) for(var x=870;x<950;x++) { var p=screen.GetPixel(x,y); if(p.R+p.G+p.B<300) ink++; }
        return white>=total*9/10 && ink>=20;
    }
    // The selected tab of the song shop is a light pill; the others are the darker bar.
    static bool AllSongsTab(Bitmap screen)
    {
        var light=0;
        for(var x=88;x<168;x+=4) { var p=screen.GetPixel(x,98); if(p.R>210 && p.G>210 && p.B>220) light++; }
        return light>=18;
    }
    static double Difference(Bitmap screen,Bitmap expected,Step step)
    {
        if(step.Action is "song-query" or "invite-code" && IsTextInputBar(screen)) return 0;
        if(step.Name=="event-stories-complete") return IsEventStoryList(screen)?0:255;
        if(step.Name=="event-intro-reward") return IsStoryReward(screen)?0:255;
        if(step.Name=="result-story-unlocks")
        {
            var best=255.0;
            for(var dy=-4;dy<=4;dy++) for(var dx=-20;dx<=20;dx++)
            {
                var error=WhiteGlyphDifferenceAt(screen,expected,step.Left+dx,step.Top+dy,step.Width,step.Height);
                if(error<10) return error;
                best=Math.Min(best,error);
            }
            return best;
        }
        if(step.Name is "live-open" or "event-live-menu")
        {
            return IsAreaMain(screen)?0:255;
        }
        if(step.Name is "members-open" or "gacha-open" or "live-open" or "event-live-menu" or "result-story-unlocks")
        {
            int foreground=0,background=0,missing=0,extra=0;
            for(var y=0;y<step.Height;y++) for(var x=0;x<step.Width;x++)
            {
                var a=screen.GetPixel(step.Left+x,step.Top+y); var b=expected.GetPixel(x,y);
                bool observed=a.R>220 && a.G>220 && a.B>220, glyph=b.R>220 && b.G>220 && b.B>220;
                if(glyph) { foreground++; if(!observed) missing++; } else { background++; if(observed) extra++; }
            }
            if(foreground>12 && background>12) return 100.0*missing/foreground+50.0*extra/background;
        }
        // Home guidance text gently bobs. Its pink foreground is the stable marker;
        // characters, area background, and the banner animation are irrelevant.
        if(step.Name.StartsWith("home-guide-")) return IdentifyHomeGuide(screen)==step.Name?0:255;
        if(step.Name=="live-guide-dismiss")
        {
            var pink=0;
            for(var y=Math.Max(0,step.Top-12);y<Math.Min(540,step.Top+step.Height+12);y+=2)
                for(var x=step.Left;x<step.Left+step.Width;x+=2)
                { var p=screen.GetPixel(x,y); if(p.R>180 && p.G<160 && p.B>90 && p.R>p.G*1.4) pink++; }
            return pink>=100?0:255;
        }
        // The song shop's filter button sits on a background that differs between emulators (the template's corners
        // came from LDPlayer); only the round button is compared.
        if(step.Name=="song-search-focus")
        {
            double best=255;
            for(var dy=-2;dy<=2;dy++) for(var dx=-2;dx<=2;dx++)
            {
                long error=0; int samples=0;
                for(var y=0;y<step.Height;y++) for(var x=0;x<step.Width;x++)
                {
                    if((x-step.Width/2)*(x-step.Width/2)+(y-step.Height/2)*(y-step.Height/2)>11*11) continue;
                    var a=screen.GetPixel(step.Left+dx+x,step.Top+dy+y); var b=expected.GetPixel(x,y);
                    error+=Math.Abs(a.R-b.R)+Math.Abs(a.G-b.G)+Math.Abs(a.B-b.B); samples+=3;
                }
                best=Math.Min(best,(double)error/samples);
            }
            return best;
        }
        // Episode count changes row positions; match only 第1話 over the story list.
        if(step.Name is "event-story-first")
        {
            double best=255;
            for(var top=90;top<465;top++) best=Math.Min(best,EpisodeDifferenceAt(screen,expected,step.Left,top,step.Width,step.Height));
            return best;
        }
        var original=DifferenceAt(screen,expected,step.Left,step.Top,step.Width,step.Height);
        if(original<12 || step.Width<=20) return original;
        var bestLocal=original;
        foreach(var dx in new[]{-2,0,2}) foreach(var dy in new[]{-2,0,2})
            if(step.Left+dx>=0 && step.Top+dy>=0 && step.Left+dx+step.Width<=screen.Width && step.Top+dy+step.Height<=screen.Height)
                bestLocal=Math.Min(bestLocal,DifferenceAt(screen,expected,step.Left+dx,step.Top+dy,step.Width,step.Height));
        return bestLocal;
    }
    static double DifferenceAt(Bitmap screen,Bitmap expected,int left,int top,int width,int height)
    {
        if(left<0 || top<0 || left+width>screen.Width || top+height>screen.Height || width>expected.Width || height>expected.Height) return 255;
        long error=0; int samples=0;
        for(int y=0;y<height;y+=2) for(int x=0;x<width;x+=2)
        { var a=screen.GetPixel(left+x,top+y); var b=expected.GetPixel(x,y); error+=Math.Abs(a.R-b.R)+Math.Abs(a.G-b.G)+Math.Abs(a.B-b.B); samples+=3; }
        return (double)error/samples;
    }
    static double WhiteGlyphDifferenceAt(Bitmap screen,Bitmap expected,int left,int top,int width,int height)
    {
        if(left<0 || top<0 || left+width>screen.Width || top+height>screen.Height || width>expected.Width || height>expected.Height) return 255;
        int ink=0,bg=0,missing=0,extra=0;
        for(var y=0;y<height;y+=2) for(var x=0;x<width;x+=2)
        {
            var a=screen.GetPixel(left+x,top+y); var b=expected.GetPixel(x,y);
            bool observed=a.R>220 && a.G>220 && a.B>220, glyph=b.R>220 && b.G>220 && b.B>220;
            if(glyph) { ink++; if(!observed) missing++; } else { bg++; if(observed) extra++; }
        }
        return ink<8 || bg<8?255:100.0*missing/ink+50.0*extra/bg;
    }
    static double EpisodeDifferenceAt(Bitmap screen,Bitmap expected,int left,int top,int width,int height)
    {
        if(left<0 || top<0 || left+width>screen.Width || top+height>screen.Height || width>expected.Width || height>expected.Height) return 255;
        int glyph=0,background=0,missing=0,extra=0;
        for(var y=0;y<height;y++) for(var x=0;x<width;x++)
        {
            var a=screen.GetPixel(left+x,top+y); var b=expected.GetPixel(x,y);
            bool actual=a.R<150 && a.G<150 && a.B<170, ink=b.R<150 && b.G<150 && b.B<170;
            if(ink) { glyph++; if(!actual) missing++; } else { background++; if(actual) extra++; }
        }
        return glyph<10?255:100.0*missing/glyph+100.0*extra/Math.Max(1,background);
    }
    static async Task<string> Run(string file, params string[] args) => Encoding.UTF8.GetString(await RunBytes(file,args));
    static Task<byte[]> RunBytes(string file, params string[] args) => RunBytesWithin(TimeSpan.FromSeconds(45), file, args);
    // Creating or copying a VM and installing the game take minutes.
    static async Task<string> RunLong(TimeSpan timeout, string file, params string[] args) => Encoding.UTF8.GetString(await RunBytesWithin(timeout, file, args));
    // ldconsole add/copy exit with the new VM's index, so callers check the VM list instead.
    static async Task RunLdManage(params string[] args) => await RunBytesWithin(TimeSpan.FromMinutes(60), Ld, args, false);
    // A network VM drops off adb whenever the adb server restarts (an emulator's own tools may restart it, several times
    // while it boots): the call is repeated after reconnecting, up to three times.
    static async Task<byte[]> RunBytesWithin(TimeSpan timeout, string file, string[] args, bool checkExit=true)
    {
        for(var attempt=1;;attempt++)
        {
            try { return await RunBytesOnce(timeout,file,args,checkExit); }
            // A client cut off with the server prints nothing at all; such a call may have run, so input (a tap) is not repeated then.
            catch(Exception ex) when(attempt<4 && args is ["-s",var serial,..] && Path.GetFileName(file).Contains("adb",StringComparison.OrdinalIgnoreCase)
                && (ex.Message.TrimEnd().EndsWith("):") && !(args.Length>3 && args[2]=="shell" && args[3].StartsWith("input "))
                    || new[]{"not found","offline","doesn't match","protocol fault","connection reset","daemon"}.Any(ex.Message.Contains)))
            {
                await Task.Delay(attempt*1500);
                if(serial.StartsWith("127.0.0.1:")) try { await Task.Run(()=>ConnectSerials(file,[serial])); } catch { }
            }
        }
    }
    static async Task<byte[]> RunBytesOnce(TimeSpan timeout, string file, string[] args, bool checkExit)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo(file) { UseShellExecute=false, RedirectStandardOutput=true, RedirectStandardError=true, CreateNoWindow=true } };
        foreach(var arg in args) process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        using var output = new MemoryStream();
        var copy = process.StandardOutput.BaseStream.CopyToAsync(output);
        var error = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(timeout);
        try { await process.WaitForExitAsync(deadline.Token); }
        catch { process.Kill(true); throw; }
        await copy;
        var stderr = await error;
        if(checkExit && process.ExitCode != 0) throw new Exception($"{Path.GetFileName(file)} failed ({process.ExitCode}): {stderr}");
        return output.ToArray();
    }
}
