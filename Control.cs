using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

// Shared control layer for the desktop GUI and the web UI: reads worker state from
// each instance's artifacts folder and starts/stops workers as separate processes.
internal static partial class Program
{
    record InstanceStatus(int Instance,string Name,bool VmRunning,bool WorkerRunning,string? Pending,
        string? Stage,string? StageTitle,string? StageStatus,string? Step,string? StepStatus,DateTimeOffset? Updated,
        int StepIndex,int StepCount,string? LastLog,bool HasError,bool HasFrame,InviteSetting? Invite,int InviteUses,int InviteLimit,int QueueCount,InviteRecord? LoopInvite,LoopState? Loop,bool Locked,string? RunUnit);
    // Invite: "keep" leaves the codes as they are, "set" replaces the active code, "queue" appends to the queue,
    // "clear" removes the active code and the queue.
    record StartRequest(string Mode,string? From,string[]? Stages,string? Unit,string? Code,bool BootVm,int Loops=1,
        string? Invite=null,string? InviteCode=null,string? InviteMode=null,int? InviteLimit=null);

    static readonly ConcurrentDictionary<int,string> PendingWork=new();
    static readonly SemaphoreSlim VmListLock=new(1,1);
    static (DateTime At,Dictionary<int,string[]> Rows) VmListCache=(DateTime.MinValue,[]);

    static string Workspace=>ControlRoot??=Directory.GetCurrentDirectory();
    static string? ControlRoot;
    static string InstanceDir(int instance)=>instance==1?Workspace:Path.Combine(Workspace,"local","instances",instance.ToString());
    static string ArtifactsDir(int instance)=>Path.Combine(InstanceDir(instance),"artifacts");
    static string SerialOf(int instance)=>"emulator-"+(5554+instance*2);
    static void RequireInstance(int instance) { if(!AllowedInstances.ContainsKey(instance)) throw new ArgumentException(T("허용되지 않은 인스턴스입니다: ","許可されていないインスタンスです: ","Instance not allowed: ")+instance); }

    // A locked instance is left alone: no starts, VM actions, manual input or automatic invite entry. Stop still works.
    static bool IsLocked(int instance)=>File.Exists(Path.Combine(ArtifactsDir(instance),"locked"));
    static void RequireUnlocked(int instance) { if(IsLocked(instance)) throw new InvalidOperationException(T($"{instance}번은 잠겨 있어요. 잠금을 풀고 다시 시도하세요.",$"{instance}番はロック中です。ロックを解除してからやり直してください。",$"#{instance} is locked. Unlock it and try again.")); }
    static string SetLock(int instance,bool locked)
    {
        RequireInstance(instance);
        Directory.CreateDirectory(ArtifactsDir(instance));
        var path=Path.Combine(ArtifactsDir(instance),"locked");
        if(locked) File.WriteAllText(path,DateTimeOffset.Now.ToString("O")); else File.Delete(path);
        AppendControlLog(instance,locked?T("잠금","ロック","Locked"):T("잠금 해제","ロック解除","Unlocked"));
        return locked?T($"{instance}번을 잠갔어요.",$"{instance}番をロックしました。",$"Locked #{instance}."):T($"{instance}번 잠금을 풀었어요.",$"{instance}番のロックを解除しました。",$"Unlocked #{instance}.");
    }

    static string MacroExecutable()
    {
        var apphost=Path.Combine(AppContext.BaseDirectory,"SekaiMacro.exe");
        return File.Exists(apphost)?apphost:Environment.ProcessPath!;
    }

    // A worker holds instance.lock with FileShare.None for its whole run.
    static bool IsWorkerRunning(int instance)
    {
        var path=Path.Combine(InstanceDir(instance),"instance.lock");
        if(!File.Exists(path)) return false;
        try { using var probe=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None); return false; }
        catch(IOException) { return true; }
        catch(UnauthorizedAccessException) { return true; }
    }

    static async Task<Dictionary<int,string[]>> VmRows()
    {
        await VmListLock.WaitAsync();
        try
        {
            if(DateTime.UtcNow-VmListCache.At<TimeSpan.FromSeconds(2)) return VmListCache.Rows;
            Dictionary<int,string[]> rows=[];
            try
            {
                foreach(var row in (await Run(Ld,"list2")).Split('\n').Select(x=>x.Trim().Split(',')).Where(x=>x.Length>=7))
                    if(int.TryParse(row[0],out var index)) rows[index]=row;
            }
            catch(Exception ex) { Console.Error.WriteLine("ldconsole list2 failed: "+ex.Message); }
            VmListCache=(DateTime.UtcNow,rows);
            return rows;
        }
        finally { VmListLock.Release(); }
    }

    static async Task<bool> IsVmRunning(int instance)
    {
        var rows=await VmRows();
        return rows.TryGetValue(instance,out var row) && row[1]==AllowedInstances[instance] && row[4]=="1";
    }

    static JsonElement? ReadJson(string path)
    {
        try
        {
            if(!File.Exists(path)) return null;
            using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
            using var document=JsonDocument.Parse(stream);
            return document.RootElement.Clone();
        }
        catch { return null; }
    }
    static string? Str(JsonElement? element,string name)=>element is { } e && e.TryGetProperty(name,out var value) && value.ValueKind==JsonValueKind.String?value.GetString():null;

    static string TailLog(int instance,int lines)
    {
        var path=Path.Combine(ArtifactsDir(instance),"runner.log");
        if(!File.Exists(path)) return "";
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        var length=Math.Min(stream.Length,Math.Max(4096,lines*400));
        stream.Seek(-length,SeekOrigin.End);
        var buffer=new byte[length]; stream.ReadExactly(buffer);
        var text=Encoding.UTF8.GetString(buffer).Replace("\r","");
        var all=text.Split('\n');
        return string.Join("\n",all.Skip(Math.Max(1,all.Length-lines)));
    }

    static async Task<List<InstanceStatus>> GetStatuses()
    {
        var rows=await VmRows();
        var flow=LoadFlowCached();
        var stages=LoadStagesCached();
        var list=new List<InstanceStatus>();
        foreach(var (instance,name) in AllowedInstances)
        {
            var dir=ArtifactsDir(instance);
            var progress=ReadJson(Path.Combine(dir,"progress.json"));
            var checkpoint=new[]{"current-step.json","current-step.pending.json"}.Select(f=>Path.Combine(dir,f))
                .Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).Select(ReadJson).FirstOrDefault();
            var stage=Str(progress,"Stage"); var step=Str(checkpoint,"Step");
            DateTimeOffset? updated=null;
            foreach(var time in new[]{Str(checkpoint,"Time"),Str(progress,"Time")})
                if(DateTimeOffset.TryParse(time,out var parsed) && (updated==null || parsed>updated)) updated=parsed;
            var log=TailLog(instance,8).Split('\n').Where(l=>l.Trim().Length>0).ToArray();
            var last=log.LastOrDefault();
            var vm=rows.TryGetValue(instance,out var row) && row[1]==name && row[4]=="1";
            var invite=ReadInviteSetting(dir);
            list.Add(new InstanceStatus(instance,name,vm,IsWorkerRunning(instance),PendingWork.GetValueOrDefault(instance)??ProvisionPending(instance),
                stage,stages.FirstOrDefault(s=>s.Name==stage) is { } stageInfo?StageTitle(stageInfo):null,Str(progress,"Status"),step,Str(checkpoint,"Status"),updated,
                step==null?-1:flow.FindIndex(s=>s.Name==step),flow.Count,last,
                log.Any(l=>l.Contains("Exception") || l.Contains("Timed out")),File.Exists(Path.Combine(dir,"current.png")),
                invite,invite==null?0:ActiveInviteUses(dir,invite),invite==null?0:InviteLimit(invite),ReadInviteQueue(dir).Count,
                CurrentLoopInvite(dir),ReadLoop(dir),IsLocked(instance),RunningUnit(instance)));
        }
        return list;
    }

    // Unit of the running worker: from its start request, else (resume, older starts) from the current loop.
    static string? RunningUnit(int instance)
    {
        if(!IsWorkerRunning(instance)) return null;
        var dir=ArtifactsDir(instance);
        return Str(ReadJson(Path.Combine(dir,"worker.json")),"Unit")??ReadLoop(dir)?.Unit;
    }

    static (DateTime Stamp,List<Step> Steps) FlowCache=(DateTime.MinValue,[]);
    static List<Step> LoadFlowCached()
    {
        var path=Path.Combine(Workspace,"flow.json");
        var stamp=File.GetLastWriteTimeUtc(path);
        if(stamp!=FlowCache.Stamp) FlowCache=(stamp,JsonSerializer.Deserialize<List<Step>>(File.ReadAllText(path))!);
        return FlowCache.Steps;
    }
    static List<Stage> LoadStagesCached()=>JsonSerializer.Deserialize<List<Stage>>(File.ReadAllText(Path.Combine(Workspace,"stages.json")))!;

    static object Meta()
    {
        var plan=JsonSerializer.Deserialize<Plan>(File.ReadAllText(Path.Combine(Workspace,"plan.json")))!;
        return new
        {
            Instances=AllowedInstances.Select(p=>new { Instance=p.Key,Name=p.Value }),
            Stages=LoadStagesCached().Select(s=>new { s.Name,Title=StageTitle(s),s.Start,s.End }),
            Language,LdPlayer=LdFolder,
            Plan=plan.Stages,
            Steps=LoadFlowCached().Select(s=>s.Name),
            Units=UnitLabels(),
            ResumePoints=new[]
            {
                new { Key="saved",Label=T("마지막 저장 지점에서 이어하기","最後の保存地点から再開","Resume from the last saved point") },
                new { Key="auto",Label=T("오토 중단 복구 · 게임 재실행 후 오토 이어하기","オート中断から復旧 · ゲーム再起動後にオートを再開","Recover auto live · restart the game and continue auto") },
                new { Key="story",Label=T("스토리 중단 복구 · 가장 아래 SKIP 표시 화부터","ストーリー中断から復旧 · 一番下のSKIP表示の話から","Recover stories · from the lowest episode marked SKIP") },
                new { Key="event-stories-complete",Label=T("스토리 전체 SKIP 확인 · 초대 미완료면 초대 창 열기","全ストーリーのSKIP確認 · 招待未完了なら招待画面を開く","Check every story is SKIP · open invitations if not registered") }
            }
        };
    }

    static List<string> BuildWorkerArgs(int instance,StartRequest request,string? inviteMode)
    {
        if(request.Code is { Length: >0 } code && !System.Text.RegularExpressions.Regex.IsMatch(code,"^[A-Za-z0-9]{15}$"))
            throw new ArgumentException(T("초대 코드는 영문/숫자 15자여야 합니다.","招待コードは英数字15文字です。","Invitation codes are 15 letters or digits."));
        var unit=request.Unit??"mmj";
        var stages=(request.Stages??[]).Where(s=>s.Length>0).ToArray();
        var known=LoadStagesCached().Select(s=>s.Name).ToHashSet();
        if(stages.Any(s=>!known.Contains(s))) throw new ArgumentException(T("알 수 없는 단계가 포함되어 있습니다.","不明な段階が含まれています。","Contains an unknown stage."));
        List<string> args=request.Mode switch
        {
            "resume" => ["resume","--from",request.From is { Length: >0 } from?from:"saved"],
            "stages" when stages.Length>0 => ["tutorial","--stages",string.Join(',',stages),"--unit",unit],
            "run" => stages.Length>0?["run","--stages",string.Join(',',stages),"--unit",unit]:["run","--unit",unit],
            _ => throw new ArgumentException(T("실행 방식이 올바르지 않습니다.","実行方法が正しくありません。","Invalid run mode."))
        };
        if(request.Mode=="run" && stages.Length>0 && stages[0]!="onboarding") throw new ArgumentException(T("처음부터 실행은 onboarding 단계로 시작해야 합니다.","最初から実行はonboarding段階から始める必要があります。","A run from scratch must start with the onboarding stage."));
        if(request.Loops is <1 or >99) throw new ArgumentException(T("반복 횟수는 1~99회예요.","繰り返し回数は1〜99回です。","Loops must be 1 to 99."));
        if(request.Loops>1)
        {
            if(request.Mode!="run") throw new ArgumentException(T("반복은 처음부터 실행에서만 할 수 있어요.","繰り返しは最初から実行でのみ使えます。","Loops are only available for runs from scratch."));
            if(inviteMode!="permanent") throw new ArgumentException(T($"{instance}번은 영구 초대 코드가 없어 반복할 수 없어요.",$"{instance}番は永久招待コードがないため繰り返せません。",$"#{instance} has no permanent invitation code, so it cannot loop."));
            args.AddRange(["--loops",request.Loops.ToString()]);
        }
        args.AddRange(["--instance",instance.ToString(),"--background"]);
        if(request.Code is { Length: >0 }) args.AddRange(["--code",request.Code]);
        return args;
    }

    static async Task<string> StartWorker(int instance,StartRequest request)
    {
        RequireInstance(instance); RequireUnlocked(instance);
        if(IsWorkerRunning(instance)) throw new InvalidOperationException(T($"{instance}번은 이미 매크로가 실행 중입니다.",$"{instance}番はすでにマクロ実行中です。",$"#{instance} is already running a macro."));
        if(PendingWork.ContainsKey(instance)) throw new InvalidOperationException(T($"{instance}번은 다른 요청을 처리 중입니다.",$"{instance}番は別のリクエストを処理中です。",$"#{instance} is busy with another request."));
        var invite=request.Invite??"keep";
        if(invite is "set" or "queue") ValidateInvite(request.InviteCode,request.InviteMode,request.InviteLimit);
        else if(invite is not ("keep" or "clear")) throw new ArgumentException(T("초대 코드 설정이 올바르지 않습니다.","招待コードの設定が正しくありません。","Invalid invitation code setting."));
        var dir=ArtifactsDir(instance);
        var current=ReadInviteSetting(dir)?.Mode??ReadInviteQueue(dir).FirstOrDefault()?.Mode;
        var mode=invite switch { "set"=>request.InviteMode,"queue"=>current??request.InviteMode,"clear"=>null,_=>current };
        var args=BuildWorkerArgs(instance,request,mode);
        if(invite=="set") SaveInvite(instance,request.InviteCode!.Trim(),request.InviteMode!,request.InviteLimit);
        else if(invite=="queue") EnqueueInvite(instance,request.InviteCode!.Trim(),request.InviteMode!,request.InviteLimit);
        else if(invite=="clear") ClearAllInvites(instance);
        if(!await IsVmRunning(instance))
        {
            if(!request.BootVm) throw new InvalidOperationException(T($"{instance}번 VM이 꺼져 있습니다. 'VM 켜고 시작'을 선택하거나 먼저 VM을 켜세요.",$"{instance}番のVMがオフです。「オフのVMは起動して開始」を選ぶか、先にVMを起動してください。",$"#{instance} VM is off. Turn on 'Boot VMs that are off' or start the VM first."));
            PendingWork[instance]=T("VM 부팅 대기 중","VM起動待ち","Waiting for VM boot");
            _=Task.Run(async()=>
            {
                try { await BootVm(instance); SpawnWorker(instance,args); }
                catch(Exception ex) { AppendControlLog(instance,T("VM 부팅 후 시작 실패: ","VM起動後の開始に失敗: ","Start after VM boot failed: ")+ex.Message); }
                finally { PendingWork.TryRemove(instance,out _); }
            });
            return T($"{instance}번 VM을 켜는 중입니다. 부팅이 끝나면 자동으로 시작합니다.",$"{instance}番のVMを起動中です。起動が終わると自動で開始します。",$"Booting #{instance} VM. The macro starts when it is ready.");
        }
        var pid=SpawnWorker(instance,args);
        return T($"{instance}번 시작 (PID {pid})",$"{instance}番を開始 (PID {pid})",$"Started #{instance} (PID {pid})");
    }

    static int SpawnWorker(int instance,List<string> args)
    {
        // No worker owns the instance, so stop requests left from an earlier run are stale.
        Directory.CreateDirectory(ArtifactsDir(instance));
        foreach(var stop in new[]{"macro.stop","story.stop"}) File.Delete(Path.Combine(ArtifactsDir(instance),stop));
        var info=new ProcessStartInfo(MacroExecutable()) { WorkingDirectory=Workspace,UseShellExecute=false,CreateNoWindow=true };
        foreach(var arg in args) info.ArgumentList.Add(arg);
        var process=Process.Start(info)!;
        LastSpawn[instance]=DateTime.UtcNow;
        File.WriteAllText(Path.Combine(ArtifactsDir(instance),"worker.pid"),process.Id.ToString());
        var unit=args.IndexOf("--unit");
        WriteJsonAtomic(Path.Combine(ArtifactsDir(instance),"worker.json"),new { Unit=unit>=0?args[unit+1]:null,Started=DateTimeOffset.Now });
        var code=args.IndexOf("--code");
        AppendControlLog(instance,T("시작: ","開始: ","Start: ")+string.Join(' ',args.Select((a,i)=>code>=0 && i==code+1?"***":a)));
        return process.Id;
    }

    static void AppendControlLog(int instance,string message)
    {
        Directory.CreateDirectory(ArtifactsDir(instance));
        using var writer=new StreamWriter(new FileStream(Path.Combine(ArtifactsDir(instance),"runner.log"),FileMode.Append,FileAccess.Write,FileShare.ReadWrite),new UTF8Encoding(false));
        writer.WriteLine($"[{DateTime.Now:HH:mm:ss}] {T("[제어판]","[コントロール]","[Control]")} {message}");
    }

    static string StopWorker(int instance)
    {
        RequireInstance(instance);
        var dir=ArtifactsDir(instance); Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir,"macro.stop"),"stop");
        File.WriteAllText(Path.Combine(dir,"story.stop"),"stop");
        AppendControlLog(instance,T("중지 요청","停止リクエスト","Stop requested"));
        return T($"{instance}번 중지 요청. 현재 단계가 끝나는 지점에서 멈추며 게임과 계정은 유지됩니다.",$"{instance}番に停止をリクエストしました。現在の段階が終わった時点で止まり、ゲームとアカウントはそのままです。",$"Stop requested for #{instance}. It stops when the current stage ends; the game and account are kept.");
    }

    static string KillWorker(int instance)
    {
        RequireInstance(instance);
        var pidFile=Path.Combine(ArtifactsDir(instance),"worker.pid");
        if(!File.Exists(pidFile) || !int.TryParse(File.ReadAllText(pidFile),out var pid)) throw new InvalidOperationException(T("이 제어판에서 시작한 작업 기록이 없습니다. '중지'를 사용하세요.","このコントロールパネルで開始した記録がありません。「停止」を使ってください。","No job started from this panel was found. Use Stop instead."));
        try
        {
            using var process=Process.GetProcessById(pid);
            if(!process.ProcessName.Equals("SekaiMacro",StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException(T("기록된 PID가 매크로 프로세스가 아닙니다.","記録されたPIDはマクロのプロセスではありません。","The recorded PID is not a macro process."));
            process.Kill(true);
        }
        catch(ArgumentException) { return T($"{instance}번 작업은 이미 종료되었습니다.",$"{instance}番の作業はすでに終了しています。",$"#{instance} job has already ended."); }
        AppendControlLog(instance,T("강제 종료","強制終了","Force stopped"));
        return T($"{instance}번 매크로를 강제 종료했습니다.",$"{instance}番のマクロを強制終了しました。",$"Force stopped the #{instance} macro.");
    }

    static async Task BootVm(int instance)
    {
        for(var attempt=0;;attempt++)
        {
            PendingWork[instance]=T("VM 설정 확인 중","VM設定を確認中","Checking VM settings");
            var prepare=await RunMacro("prepare","--instance",instance.ToString());
            if(prepare.Code!=0) throw new Exception(T("prepare 실패: ","prepare失敗: ","prepare failed: ")+prepare.Output.Trim());
            PendingWork[instance]=T("VM 켜는 중","VM起動中","Booting VM");
            await Run(Ld,"launch","--index",instance.ToString());
            PendingWork[instance]=T("VM 부팅 대기 중","VM起動待ち","Waiting for VM boot");
            var deadline=DateTime.UtcNow.AddMinutes(3); var booted=false;
            while(DateTime.UtcNow<deadline && !booted)
            {
                try { booted=(await Run(Adb,"-s",SerialOf(instance),"shell","getprop sys.boot_completed")).Trim()=="1"; }
                catch { }
                if(!booted) await Task.Delay(2000);
            }
            if(!booted) break;
            VmListCache=(DateTime.MinValue,[]);
            if(await LiveResolutionOk(SerialOf(instance))) { ScheduleArrange(); return; }
            if(attempt>0) throw new Exception(T("VM 해상도가 960x540 DPI160으로 맞춰지지 않아요.","VMの解像度が960x540 DPI160になりません。","The VM resolution does not become 960x540 DPI160."));
            AppendControlLog(instance,T("해상도가 달라서 설정을 고치고 VM을 다시 켜요.","解像度が違うため設定を直してVMを再起動します。","Resolution differs; fixing settings and restarting the VM."));
            await QuitVm(instance);
        }
        throw new TimeoutException(T("부팅 시간 초과. 계정 초기화는 보내지 않았습니다.","起動がタイムアウトしました。アカウント初期化は行っていません。","Boot timed out. The account was not reset."));
    }

    static async Task<string> VmAction(int instance,string action)
    {
        RequireInstance(instance); RequireUnlocked(instance);
        if(PendingWork.ContainsKey(instance)) throw new InvalidOperationException(T($"{instance}번은 다른 요청을 처리 중입니다.",$"{instance}番は別のリクエストを処理中です。",$"#{instance} is busy with another request."));
        switch(action)
        {
            case "launch":
                if(await IsVmRunning(instance)) return T($"{instance}번 VM은 이미 켜져 있습니다.",$"{instance}番のVMはすでに起動しています。",$"#{instance} VM is already on.");
                PendingWork[instance]=T("VM 켜는 중","VM起動中","Booting VM");
                _=Task.Run(async()=>
                {
                    try { await BootVm(instance); AppendControlLog(instance,T("VM 부팅 완료","VM起動完了","VM booted")); }
                    catch(Exception ex) { AppendControlLog(instance,T("VM 켜기 실패: ","VM起動失敗: ","VM boot failed: ")+ex.Message); }
                    finally { PendingWork.TryRemove(instance,out _); }
                });
                return T($"{instance}번 VM을 켜는 중입니다.",$"{instance}番のVMを起動中です。",$"Booting #{instance} VM.");
            case "quit":
                if(IsWorkerRunning(instance)) StopWorker(instance);
                await Run(Ld,"quit","--index",instance.ToString());
                VmListCache=(DateTime.MinValue,[]);
                return T($"{instance}번 VM을 껐습니다.",$"{instance}番のVMを停止しました。",$"Turned off #{instance} VM.");
            case "reboot":
                if(IsWorkerRunning(instance)) throw new InvalidOperationException(T("매크로 실행 중에는 재부팅할 수 없습니다. 먼저 중지하세요.","マクロ実行中は再起動できません。先に停止してください。","Cannot reboot while a macro is running. Stop it first."));
                await Run(Ld,"reboot","--index",instance.ToString());
                VmListCache=(DateTime.MinValue,[]);
                return T($"{instance}번 VM을 재부팅합니다.",$"{instance}番のVMを再起動します。",$"Rebooting #{instance} VM.");
            case "launch-game":
                await RequireVm(instance);
                await Run(Adb,"-s",SerialOf(instance),"shell",$"monkey -p {Package} -c android.intent.category.LAUNCHER 1");
                return T($"{instance}번 게임을 실행했습니다.",$"{instance}番でゲームを起動しました。",$"Launched the game on #{instance}.");
            default: throw new ArgumentException(T("알 수 없는 VM 동작: ","不明なVM操作: ","Unknown VM action: ")+action);
        }
    }

    static async Task RequireVm(int instance)
    {
        RequireInstance(instance);
        if(!await IsVmRunning(instance)) throw new InvalidOperationException(T($"{instance}번 VM이 꺼져 있습니다.",$"{instance}番のVMがオフです。",$"#{instance} VM is off."));
    }

    static async Task<byte[]> CaptureScreen(int instance,bool save)
    {
        await RequireVm(instance);
        var bytes=await RunBytes(Adb,"-s",SerialOf(instance),"exec-out","screencap","-p");
        if(bytes.Length<8 || bytes[0]!=137 || bytes[1]!=80) throw new Exception(T("화면 캡쳐 결과가 올바르지 않습니다.","画面キャプチャの結果が正しくありません。","The screen capture was invalid."));
        if(save)
        {
            var dir=Path.Combine(ArtifactsDir(instance),"captures"); Directory.CreateDirectory(dir);
            await File.WriteAllBytesAsync(Path.Combine(dir,$"{DateTime.Now:yyyyMMdd-HHmmss}.png"),bytes);
        }
        return bytes;
    }

    static byte[]? LastFrame(int instance)
    {
        var path=Path.Combine(ArtifactsDir(instance),"current.png");
        try
        {
            using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
            using var copy=new MemoryStream(); stream.CopyTo(copy); return copy.ToArray();
        }
        catch { return null; }
    }

    // Manual input is only accepted while no worker owns the instance, and the lock is held during the input.
    static async Task<string> ManualInput(int instance,string command)
    {
        await RequireVm(instance); RequireUnlocked(instance);
        var path=Path.Combine(InstanceDir(instance),"instance.lock");
        Directory.CreateDirectory(InstanceDir(instance));
        FileStream hold;
        try { hold=new FileStream(path,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None); }
        catch(IOException) { throw new InvalidOperationException(T("매크로 실행 중에는 직접 조작할 수 없습니다. 먼저 중지하세요.","マクロ実行中は手動操作できません。先に停止してください。","Manual control is unavailable while a macro runs. Stop it first.")); }
        using(hold) await Run(Adb,"-s",SerialOf(instance),"shell",command);
        return "ok";
    }

    static async Task<(int Code,string Output)> RunMacro(params string[] args)
    {
        var info=new ProcessStartInfo(MacroExecutable()) { WorkingDirectory=Workspace,UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true };
        foreach(var arg in args) info.ArgumentList.Add(arg);
        using var process=Process.Start(info)!;
        var output=process.StandardOutput.ReadToEndAsync(); var error=process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode,await output+await error);
    }

    // Changes how many loops a run makes, including one that is running; a loop already started cannot be removed.
    static string SetLoopTotal(int instance,int total)
    {
        RequireInstance(instance);
        var dir=ArtifactsDir(instance);
        var loop=ReadLoop(dir)??throw new InvalidOperationException(T($"{instance}번은 반복 기록이 없어요. 처음부터 실행으로 시작하세요.",$"{instance}番には繰り返しの記録がありません。最初から実行で始めてください。",$"#{instance} has no loop record. Start it with a run from scratch."));
        if(total<loop.Index || total>99) throw new ArgumentException(T($"회차는 {loop.Index}~99회로 정할 수 있어요.",$"周回は{loop.Index}〜99回にできます。",$"Loops can be set from {loop.Index} to 99."));
        if(total>loop.Index && (ReadInviteSetting(dir)??ReadInviteQueue(dir).FirstOrDefault())?.Mode!="permanent")
            throw new InvalidOperationException(T($"{instance}번은 영구 초대 코드가 없어 회차를 늘릴 수 없어요.",$"{instance}番は永久招待コードがないため周回を増やせません。",$"#{instance} has no permanent invitation code, so loops cannot be added."));
        WriteJsonAtomic(Path.Combine(dir,LoopFile),loop with { Total=total });
        AppendControlLog(instance,T($"반복 회차 변경: {loop.Total} → {total}",$"周回数を変更: {loop.Total} → {total}",$"Loops changed: {loop.Total} → {total}"));
        return T($"{instance}번 반복을 총 {total}회로 바꿨어요.",$"{instance}番の繰り返しを合計{total}回にしました。",$"#{instance} now runs {total} loops in total.");
    }

    // Changes the registration count of the active code (index -1) or a queued one; never below what is already used.
    static string SetInviteLimit(int instance,int index,int limit)
    {
        RequireInstance(instance);
        var dir=ArtifactsDir(instance);
        if(limit is <1 or >MaxInviteUses) throw new ArgumentException(T($"횟수는 1~{MaxInviteUses}회예요.",$"回数は1〜{MaxInviteUses}回です。",$"Uses must be 1 to {MaxInviteUses}."));
        InviteSetting Adjust(InviteSetting s)=>limit>1 || s.Mode=="permanent"?s with { Mode="permanent",Limit=limit }:s with { Limit=null };
        var label=WithInviteLock(dir,()=>
        {
            if(index<0)
            {
                var setting=ReadInviteSetting(dir)??throw new InvalidOperationException(T("지금 쓰는 초대 코드가 없어요.","使用中の招待コードがありません。","There is no active invitation code."));
                if(limit<ActiveInviteUses(dir,setting)) throw new ArgumentException(T($"이미 {ActiveInviteUses(dir,setting)}번 썼어요.",$"すでに{ActiveInviteUses(dir,setting)}回使っています。",$"Already used {ActiveInviteUses(dir,setting)} times."));
                var next=Adjust(setting); WriteInviteSetting(dir,next); return InviteLabel(next);
            }
            var queue=ReadInviteQueue(dir);
            if(index>=queue.Count) throw new ArgumentException(T("대기열에 없는 항목이에요.","待機列にない項目です。","That item is not in the queue."));
            queue[index]=Adjust(queue[index]); WriteInviteQueue(dir,queue); return InviteLabel(queue[index]);
        });
        AppendControlLog(instance,T($"초대 코드 횟수 변경 ({label})",$"招待コードの回数を変更 ({label})",$"Invitation code uses changed ({label})"));
        // Lowering the active code to what it has already used retires it and starts the next queued code.
        if(!IsWorkerRunning(instance)) { RetireUsedInvite(dir); PromoteQueuedInvite(dir); }
        return T($"{instance}번 초대 코드를 {label}로 바꿨어요.",$"{instance}番の招待コードを{label}にしました。",$"Set #{instance} invitation code to {label}.");
    }

    static object GetInvite(int instance)
    {
        RequireInstance(instance);
        var history=ReadInviteHistory(ArtifactsDir(instance));
        history.Reverse();
        var dir=ArtifactsDir(instance);
        var setting=ReadInviteSetting(dir);
        return new
        {
            Setting=setting,Uses=setting==null?0:ActiveInviteUses(dir,setting),Limit=setting==null?0:InviteLimit(setting),
            Total=setting==null?0:InviteUses(setting.Code),Max=MaxInviteUses,
            Queue=ReadInviteQueue(dir).Select(q=>new { q.Code,q.Mode,Limit=InviteLimit(q),Total=InviteUses(q.Code) }),
            History=history
        };
    }

    static void ValidateInvite(string? code,string? mode,int? limit)
    {
        code=code?.Trim();
        if(!ValidInviteCode(code)) throw new ArgumentException(T("초대 코드는 영문/숫자 15자여야 합니다.","招待コードは英数字15文字です。","Invitation codes are 15 letters or digits."));
        if(InviteUses(code!)>=MaxInviteUses) throw new ArgumentException(T($"이 코드는 이미 {MaxInviteUses}번 등록됐어요.",$"このコードはすでに{MaxInviteUses}回登録されています。",$"This code is already registered {MaxInviteUses} times."));
        if(mode is not ("once" or "permanent")) throw new ArgumentException(T("일회용 또는 영구를 선택하세요.","使い捨てか永久を選んでください。","Choose single-use or permanent."));
        if(limit is <1 or >MaxInviteUses) throw new ArgumentException(T($"횟수는 1~{MaxInviteUses}회예요.",$"回数は1〜{MaxInviteUses}回です。",$"Uses must be 1 to {MaxInviteUses}."));
    }
    static InviteSetting NewInvite(string code,string mode,int? limit)=>new(code,mode,DateTimeOffset.Now,mode=="permanent"?limit??MaxInviteUses:null);
    static string InviteLabel(InviteSetting s)=>s.Mode=="once"?T("일회용","使い捨て","single-use"):T($"영구 {InviteLimit(s)}회",$"永久 {InviteLimit(s)}回",$"permanent ×{InviteLimit(s)}");

    static async Task<string> SetInvite(int instance,string? code,string? mode,int? limit)
    {
        RequireInstance(instance);
        ValidateInvite(code,mode,limit);
        SaveInvite(instance,code!.Trim(),mode!,limit);
        return await TryAutoInvite(instance)??T($"{instance}번 초대 코드를 저장했습니다.",$"{instance}番の招待コードを保存しました。",$"Saved the invitation code for #{instance}.");
    }

    static void SaveInvite(int instance,string code,string mode,int? limit)
    {
        var dir=ArtifactsDir(instance);
        var setting=NewInvite(code,mode,limit);
        WithInviteLock(dir,()=>{ WriteInviteSetting(dir,setting); return 0; });
        AppendControlLog(instance,T($"초대 코드 저장 ({InviteLabel(setting)})",$"招待コードを保存 ({InviteLabel(setting)})",$"Invitation code saved ({InviteLabel(setting)})"));
    }

    static async Task<string> QueueInvite(int instance,string? code,string? mode,int? limit)
    {
        RequireInstance(instance);
        ValidateInvite(code,mode,limit);
        var position=EnqueueInvite(instance,code!.Trim(),mode!,limit);
        if(position==0) return await TryAutoInvite(instance)??T($"{instance}번에 바로 쓸 코드로 저장했어요.",$"{instance}番ですぐ使うコードとして保存しました。",$"Saved as the active code for #{instance}.");
        return T($"{instance}번 대기열 {position}번째에 추가했어요.",$"{instance}番の待機列の{position}番目に追加しました。",$"Added to #{instance} queue at position {position}.");
    }

    // Returns the queue position, or 0 when nothing was active and the code was activated at once.
    static int EnqueueInvite(int instance,string code,string mode,int? limit)
    {
        var dir=ArtifactsDir(instance);
        var setting=NewInvite(code,mode,limit);
        var position=WithInviteLock(dir,()=>{ var queue=ReadInviteQueue(dir); queue.Add(setting); WriteInviteQueue(dir,queue); return queue.Count; });
        AppendControlLog(instance,T($"초대 코드 대기열 추가 ({InviteLabel(setting)})",$"招待コードを待機列に追加 ({InviteLabel(setting)})",$"Invitation code queued ({InviteLabel(setting)})"));
        return PromoteQueuedInvite(dir)!=null?0:position;
    }

    static string RemoveQueuedInvite(int instance,int index)
    {
        RequireInstance(instance);
        var dir=ArtifactsDir(instance);
        var removed=WithInviteLock(dir,()=>
        {
            var queue=ReadInviteQueue(dir);
            if(index<0 || index>=queue.Count) return false;
            queue.RemoveAt(index); WriteInviteQueue(dir,queue); return true;
        });
        if(!removed) throw new ArgumentException(T("대기열에 없는 항목이에요.","待機列にない項目です。","That item is not in the queue."));
        AppendControlLog(instance,T("초대 코드 대기열에서 삭제","招待コードを待機列から削除","Invitation code removed from queue"));
        return T($"{instance}번 대기열에서 뺐어요.",$"{instance}番の待機列から外しました。",$"Removed from #{instance} queue.");
    }

    // Clearing the active code moves the next queued code up.
    static string ClearInvite(int instance)
    {
        RequireInstance(instance);
        var dir=ArtifactsDir(instance);
        WithInviteLock(dir,()=>{ File.Delete(Path.Combine(dir,InviteFile)); return 0; });
        AppendControlLog(instance,T("초대 코드 삭제","招待コードを削除","Invitation code cleared"));
        return PromoteQueuedInvite(dir)!=null?T($"{instance}번 코드를 지우고 대기열의 다음 코드로 바꿨어요.",$"{instance}番のコードを消して待機列の次のコードに切り替えました。",$"Cleared #{instance} code and switched to the next queued code."):T($"{instance}번 초대 코드를 지웠습니다.",$"{instance}番の招待コードを消しました。",$"Cleared the invitation code for #{instance}.");
    }

    static void ClearAllInvites(int instance)
    {
        var dir=ArtifactsDir(instance);
        if(ReadInviteSetting(dir)==null && ReadInviteQueue(dir).Count==0) return;
        WithInviteLock(dir,()=>{ File.Delete(Path.Combine(dir,InviteFile)); WriteInviteQueue(dir,[]); return 0; });
        AppendControlLog(instance,T("초대 코드와 대기열 삭제","招待コードと待機列を削除","Invitation code and queue cleared"));
    }

    static byte[]? InviteImage(int instance,string file)
    {
        RequireInstance(instance);
        if(!System.Text.RegularExpressions.Regex.IsMatch(file,@"^[\w-]+\.png$")) return null;
        var path=Path.Combine(ArtifactsDir(instance),"invites",file);
        return File.Exists(path)?File.ReadAllBytes(path):null;
    }

    // A run that finished without a code leaves the invitation mission open ("awaiting-invitation").
    // Once a code is saved, a short job enters it from that screen; each code/checkpoint pair is tried once.
    static readonly HashSet<string> AutoInviteAttempts=[];
    static readonly ConcurrentDictionary<int,DateTime> LastSpawn=new();
    static async Task<string?> TryAutoInvite(int instance)
    {
        var dir=ArtifactsDir(instance);
        var invite=ReadInviteSetting(dir);
        // A just-spawned worker takes a moment to acquire instance.lock.
        if(invite==null || IsLocked(instance) || IsWorkerRunning(instance) || PendingWork.ContainsKey(instance)
            || DateTime.UtcNow-LastSpawn.GetValueOrDefault(instance)<TimeSpan.FromSeconds(20)) return null;
        var checkpoint=new[]{"current-step.json","current-step.pending.json"}.Select(f=>Path.Combine(dir,f))
            .Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).Select(ReadJson).FirstOrDefault();
        if(Str(checkpoint,"Step")!="event-stories-complete" || Str(checkpoint,"Status")!="awaiting-invitation") return null;
        if(!await IsVmRunning(instance)) return null;
        lock(AutoInviteAttempts) if(!AutoInviteAttempts.Add($"{instance}|{invite.Code}|{Str(checkpoint,"Time")}")) return null;
        var pid=SpawnWorker(instance,["register-invite","--instance",instance.ToString(),"--background"]);
        return T($"{instance}번 초대 대기 화면에서 코드를 입력합니다. (PID {pid})",$"{instance}番の招待待ち画面でコードを入力します。(PID {pid})",$"Entering the code on #{instance} invitation screen. (PID {pid})");
    }

    static int InviteWatcherStarted;
    // VMs turned on outside the macro (LDPlayer's own manager) are measured once after boot; a wrong one is fixed and restarted.
    static readonly HashSet<int> ResolutionChecked=[],SeenOff=[];
    static async Task CheckTurnedOnVm(int instance)
    {
        if(!await IsVmRunning(instance)) { ResolutionChecked.Remove(instance); SeenOff.Add(instance); return; }
        if(ResolutionChecked.Contains(instance) || IsWorkerRunning(instance) || PendingWork.ContainsKey(instance) || ProvisionUses(instance)) return;
        try { if((await Run(Adb,"-s",SerialOf(instance),"shell","getprop sys.boot_completed")).Trim()!="1") return; }
        catch { return; }
        ResolutionChecked.Add(instance);
        // Only VMs that turned on while the control panel runs; restarting the panel leaves windows where they are.
        if(SeenOff.Remove(instance)) ScheduleArrange();
        if(await LiveResolutionOk(SerialOf(instance))) return;
        AppendControlLog(instance,T("해상도가 달라서 설정을 고치고 VM을 다시 켜요.","解像度が違うため設定を直してVMを再起動します。","Resolution differs; fixing settings and restarting the VM."));
        PendingWork[instance]=T("해상도 맞추는 중","解像度を調整中","Fixing resolution");
        _=Task.Run(async()=>
        {
            try { await QuitVm(instance); await BootVm(instance); AppendControlLog(instance,T("해상도를 맞추고 다시 켰어요.","解像度を合わせて再起動しました。","Resolution fixed and VM restarted.")); }
            catch(Exception ex) { AppendControlLog(instance,T("해상도 맞추기 실패: ","解像度の調整に失敗: ","Fixing resolution failed: ")+ex.Message); }
            finally { PendingWork.TryRemove(instance,out _); }
        });
    }

    // Several VMs booting one after another are arranged once, a few seconds after the last one is up.
    static CancellationTokenSource? ArrangeDelay;
    static void ScheduleArrange()
    {
        var delay=new CancellationTokenSource();
        Interlocked.Exchange(ref ArrangeDelay,delay)?.Cancel();
        _=Task.Run(async()=>
        {
            try
            {
                await Task.Delay(5000,delay.Token);
                if((await VmRows()).Values.Count(r=>r[4]=="1")>=2) await Run(Ld,"sortWnd");
            }
            catch(OperationCanceledException) { }
            catch(Exception ex) { Console.Error.WriteLine("Arrange failed: "+ex.Message); }
        });
    }

    static void StartInviteWatcher()
    {
        if(Interlocked.Exchange(ref InviteWatcherStarted,1)==1) return;
        _=Task.Run(async()=>
        {
            while(true)
            {
                // A setup job asks for the game file; the built-in browser fetches it.
                try { var flag=Path.Combine(ProvisionDir,"need-game"); if(File.Exists(flag) && OpenGameDownload(File.ReadAllText(flag).Trim() is "qooapp"?"qooapp":"pjsekai")) File.Delete(flag); } catch { }
                foreach(var instance in AllowedInstances.Keys)
                    try
                    {
                        await CheckTurnedOnVm(instance);
                        if(await IsVmRunning(instance)) try { await MeasureUsage(instance); } catch { }
                        // Workers advance their own queue; this keeps idle instances current after a code is used up.
                        if(!IsWorkerRunning(instance))
                        {
                            if(RetireUsedInvite(ArtifactsDir(instance))!=null) AppendControlLog(instance,T("다 쓴 초대 코드를 지웠어요.","使い切った招待コードを消しました。","Cleared a used-up invitation code."));
                            if(PromoteQueuedInvite(ArtifactsDir(instance)) is { } next) AppendControlLog(instance,T($"대기열의 다음 초대 코드로 바꿨어요 ({InviteLabel(next)}).",$"待機列の次の招待コードに切り替えました ({InviteLabel(next)})。",$"Switched to the next queued invitation code ({InviteLabel(next)})."));
                        }
                        if(await TryAutoInvite(instance) is { } message) AppendControlLog(instance,message);
                    }
                    catch(Exception ex) { Console.Error.WriteLine($"Auto invite {instance}: {ex.Message}"); }
                await Task.Delay(5000);
            }
        });
    }
}
