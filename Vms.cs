using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;

// VM management from the GUI: LDPlayer download links, creating/copying/removing VMs, installing the game,
// and a provisioning job (one VM gets the game and the onboarding stage, is copied to the requested count,
// then macros start on the copies). The job is its own process (`provision`) so restarting the GUI does not
// cut it; it reports in local/provision/status.json.
internal static partial class Program
{
    const string ValidatedLdVersion="9.5.6.0";
    const string LdLatestUrl="https://res.ldrescdn.com/download/package/LDPlayer9.0.exe";
    const string LdSiteUrl="https://www.ldplayer.net/";
    const string GamePageUrl="https://apkpure.com/kr/hatsune-miku-colorful-stage-2024-japan/com.sega.pjsekai";
    const string GameXapkUrl="https://d.apkpure.com/b/XAPK/com.sega.pjsekai?version=latest";
    const string QooAppPackage="com.qooapp.qoohelper";
    // QooApp comes from its own site; Project Sekai from apkpure.
    const string QooAppApkUrl="https://d.qoo-apk.com/QooApp.apk";
    static string LdVersionUrl(string version)=>$"https://setup.ldrescdn.com/download/package/LDPlayer_{version}.exe";

    static string? LdVersion()
    {
        try { return LdFolder is { } folder && File.Exists(Path.Combine(folder,"dnplayer.exe"))?FileVersionInfo.GetVersionInfo(Path.Combine(folder,"dnplayer.exe")).FileVersion:null; }
        catch { return null; }
    }
    static object LdInfo()
    {
        var version=LdVersion();
        return new { Kind="ldplayer",Folder=LdFolder,Version=version,VersionUrl=version==null?null:LdVersionUrl(version),
            Validated=ValidatedLdVersion,ValidatedUrl=LdVersionUrl(ValidatedLdVersion),LatestUrl=LdLatestUrl,Site=LdSiteUrl,GamePage=GamePageUrl };
    }

    // ---- VMs

    static void ForgetVmLists() { lock(InstancesLock) InstanceCache=(DateTime.MinValue,[]); VmListCache=(DateTime.MinValue,[]); }
    static async Task<Dictionary<int,string[]>> LdRows()
    {
        var rows=new Dictionary<int,string[]>();
        foreach(var row in (await VmList()).Split('\n').Select(x=>x.Trim().Split(',')).Where(x=>x.Length>=7))
            if(int.TryParse(row[0],out var index)) rows[index]=row;
        return rows;
    }
    static string UniqueVmName(string wanted,IEnumerable<string> taken)
    {
        var names=taken.ToHashSet();
        if(!names.Contains(wanted)) return wanted;
        for(var n=2;;n++) if(!names.Contains($"{wanted}-{n}")) return $"{wanted}-{n}";
    }
    static async Task<int> NewVmIndex(HashSet<int> before,string name)
    {
        ForgetVmLists();
        var created=(await LdRows()).Where(r=>!before.Contains(r.Key) && r.Value[1]==name).Select(r=>r.Key).ToList();
        if(created.Count!=1) throw new Exception(T("새 VM을 찾지 못했어요. LD플레이어 멀티 매니저를 확인하세요.","新しいVMが見つかりません。LDPlayerのマルチプレイヤーを確認してください。","The new VM was not found. Check LDPlayer's multi-instance manager."));
        // LDPlayer reuses freed indexes; files left from an earlier VM at this index are moved aside.
        ArchiveInstanceFiles(created[0]);
        return created[0];
    }
    // BlueStacks makes, copies and removes instances only in its multi-instance manager.
    static void RequireVmManagement()
    {
        if(IsBlueStacks) throw new InvalidOperationException(T("블루스택 VM은 멀티 인스턴스 매니저에서 만들고 복제하고 지워요. 만든 VM을 골라서 게임 설치·세팅을 하세요.","BlueStacksのVMはマルチインスタンスマネージャーで作成・複製・削除します。作ったVMを選んでゲームのインストールと準備をしてください。","BlueStacks VMs are created, copied and removed in its multi-instance manager. Pick a VM made there to install and set up the game."));
    }
    static async Task<int> CreateVm(string name)
    {
        RequireVmManagement();
        var before=(await LdRows()).Keys.ToHashSet();
        await RunLdManage("add","--name",name);
        return await NewVmIndex(before,name);
    }
    static async Task<int> CopyVm(int from,string name)
    {
        RequireVmManagement();
        await QuitVm(from);
        var before=(await LdRows()).Keys.ToHashSet();
        await RunLdManage("copy","--name",name,"--from",from.ToString());
        return await NewVmIndex(before,name);
    }
    static async Task QuitVm(int instance)
    {
        if(!(await LdRows()).TryGetValue(instance,out var row) || row[4]!="1") return;
        await VmQuitRequest(instance);
        for(var i=0;i<60;i++)
        {
            await Task.Delay(1000);
            if(!(await LdRows()).TryGetValue(instance,out row) || row[4]!="1") { ForgetVmLists(); await Task.Delay(2000); return; }
        }
        throw new TimeoutException(T($"{instance}번 VM이 꺼지지 않아요.",$"{instance}番のVMが停止しません。",$"#{instance} VM does not shut down."));
    }
    static void ArchiveInstanceFiles(int instance)
    {
        var dir=instance==1?Path.Combine(Workspace,"artifacts"):InstanceDir(instance);
        if(!Directory.Exists(dir)) return;
        var target=Path.Combine(Workspace,"local","removed",$"{instance}-{DateTime.Now:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        Directory.Move(dir,target);
    }

    // Removes the LDPlayer VM. Its macro files are kept in local/removed, not deleted.
    static async Task<string> RemoveVm(int instance)
    {
        RequireInstance(instance); RequireUnlocked(instance); RequireVmManagement();
        if(IsWorkerRunning(instance) || PendingWork.ContainsKey(instance) || ProvisionUses(instance))
            throw new InvalidOperationException(T($"{instance}번은 작업 중이라 지울 수 없어요.",$"{instance}番は作業中のため削除できません。",$"#{instance} is busy and cannot be removed."));
        var name=AllowedInstances[instance];
        await QuitVm(instance);
        await RunLdManage("remove","--index",instance.ToString());
        ForgetVmLists();
        if((await LdRows()).ContainsKey(instance)) throw new Exception(T($"{instance}번 VM이 지워지지 않았어요.",$"{instance}番のVMが削除されませんでした。",$"#{instance} VM was not removed."));
        ArchiveInstanceFiles(instance);
        return T($"{instance}번 VM({name})을 지웠어요. 매크로 기록은 local/removed에 남겨뒀어요.",$"{instance}番のVM({name})を削除しました。マクロの記録はlocal/removedに残しています。",$"Removed #{instance} VM ({name}). Its macro records are kept in local/removed.");
    }

    // ---- Disk space: a copy needs as much room as its source VM's folder, plus a margin.

    const long DiskMargin=2L<<30;
    static string VmsDir=>IsBlueStacks?Path.Combine(BlueStacksData,"Engine"):Path.GetDirectoryName(Path.GetDirectoryName(LdConfig(0))!)!;
    static string VmDir(int instance)=>IsBlueStacks?Path.Combine(VmsDir,BlueStacksInstanceOf(instance).Id):Path.Combine(VmsDir,$"leidian{instance}");
    static long FolderBytes(string dir)
    {
        try { return Directory.Exists(dir)?new DirectoryInfo(dir).EnumerateFiles("*",SearchOption.AllDirectories).Sum(f=>f.Length):0; }
        catch { return 0; }
    }
    static long FreeBytes()=>new DriveInfo(Path.GetPathRoot(Path.GetFullPath(VmsDir))!).AvailableFreeSpace;
    // A new VM with the game and its data ends up about as large as the largest existing one (16 GB if none).
    // A new VM needs about what Android uses inside the fullest VM (its shared storage holds the game data),
    // measured while VMs run and kept in local/vm-usage.json for VMs that are off; 9 GB until one was measured.
    static string UsageFile=>Path.Combine(Workspace,"local","vm-usage.json");
    static Dictionary<string,long> ReadUsage()
    {
        try { return File.Exists(UsageFile)?JsonSerializer.Deserialize<Dictionary<string,long>>(File.ReadAllText(UsageFile))!:[]; }
        catch { return []; }
    }
    static readonly Dictionary<int,DateTime> UsageMeasured=[];
    static async Task MeasureUsage(int instance)
    {
        if(UsageMeasured.TryGetValue(instance,out var at) && DateTime.UtcNow-at<TimeSpan.FromMinutes(5)) return;
        UsageMeasured[instance]=DateTime.UtcNow;
        var line=(await Run(Adb,"-s",SerialOf(instance),"shell","df -k /data/media | tail -1")).Trim().Split(' ',StringSplitOptions.RemoveEmptyEntries);
        if(line.Length<3 || !long.TryParse(line[2],out var used)) return;
        var usage=ReadUsage(); usage[instance.ToString()]=used*1024;
        WriteJsonAtomic(UsageFile,usage);
    }
    static long NewVmBytes()
    {
        var usage=ReadUsage();
        var used=AllowedInstances.Keys.Select(n=>usage.GetValueOrDefault(n.ToString())).DefaultIfEmpty(0).Max();
        return used>0?used:9L<<30;
    }
    static string Gb(long bytes)=>$"{bytes/(double)(1L<<30):0.#}GB";
    static void RequireDisk(long each,int count)
    {
        if(count<=0) return;
        var need=each*count+DiskMargin; var free=FreeBytes();
        if(free<need) throw new InvalidOperationException(T($"디스크 공간이 부족해요. {count}대에 약 {Gb(need)} 필요한데 {Gb(free)} 남았어요.",$"ディスク容量が足りません。{count}台に約{Gb(need)}必要ですが、残りは{Gb(free)}です。",$"Not enough disk space: {count} VM(s) need about {Gb(need)}, {Gb(free)} is free."));
    }
    static object DiskInfo()
    {
        // A copy duplicates the source VM's disk files, so copies are sized by its folder.
        try { return new { Free=FreeBytes(),Drive=Path.GetPathRoot(Path.GetFullPath(VmsDir)),NewVm=NewVmBytes(),Vms=AllowedInstances.Keys.ToDictionary(n=>n.ToString(),n=>FolderBytes(VmDir(n))) }; }
        catch { return new { Free=0L,Drive=(string?)null,NewVm=0L,Vms=new Dictionary<string,long>() }; }
    }

    // ---- Game install

    static async Task<bool> GameInstalled(int instance)
    {
        try { return (await Run(Adb,"-s",SerialOf(instance),"shell",$"pm path {Package}")).Contains("package:"); }
        catch { return false; }
    }
    static string ApkCache=>Path.Combine(Workspace,"local","apk");
    // adb garbles non-ASCII host paths (this folder is named in Korean), so files it reads or writes pass through
    // an ASCII-only folder: the temp folder when its path is ASCII, else ProgramData.
    static string AdbStage()
    {
        var temp=Path.Combine(Path.GetTempPath(),"SekaiMacro-adb");
        var dir=temp.All(c=>c<128)?temp:Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"SekaiMacro","adb");
        Directory.CreateDirectory(dir);
        return dir;
    }
    static string Staged(string file)
    {
        if(Path.GetFullPath(file).All(c=>c<128)) return file;
        var copy=Path.Combine(AdbStage(),Path.GetFileName(file));
        File.Copy(file,copy,true);
        return copy;
    }

    // Split APKs from a running VM that already has the game; no download needed.
    static async Task<int?> GameSourceVm(int target)
    {
        foreach(var n in AllowedInstances.Keys.Where(n=>n!=target))
            if(await IsVmRunning(n) && await GameInstalled(n)) return n;
        return null;
    }
    static string KeptSplits=>Path.Combine(ApkCache,"from-vm");
    static async Task<List<string>> PullGameFromVm(int target,int? source,Action<string> report)
    {
        if(source==null) throw new InvalidOperationException(T("게임이 설치된 켜진 VM이 없어요.","ゲームが入った起動中のVMがありません。","No running VM has the game."));
        report(T($"{source}번 VM에서 게임 파일 복사 중",$"{source}番のVMからゲームファイルをコピー中",$"Copying game files from #{source}"));
        var dir=KeptSplits;
        if(Directory.Exists(dir)) Directory.Delete(dir,true);
        Directory.CreateDirectory(dir);
        var files=new List<string>();
        foreach(var line in (await Run(Adb,"-s",SerialOf(source.Value),"shell",$"pm path {Package}")).Split('\n').Select(l=>l.Trim()).Where(l=>l.StartsWith("package:")))
        {
            var remote=line["package:".Length..];
            var local=Path.Combine(dir,Path.GetFileName(remote));
            var staged=Path.Combine(AdbStage(),Path.GetFileName(remote));
            await RunLong(TimeSpan.FromMinutes(10),Adb,"-s",SerialOf(source.Value),"pull",remote,staged);
            File.Move(staged,local,true);
            files.Add(local);
        }
        return files;
    }

    // apkpure serves an XAPK (zip of split APKs, sometimes OBB files). Downloads are cached per version.
    // apkpure may refuse automated downloads. Then the file the user downloads in a browser is picked up
    // from the Downloads folder (the job waits for it); the site's protection is not worked around.
    static async Task<string?> DownloadXapk(Action<string> report)
    {
        try
        {
            using var http=new HttpClient { Timeout=Timeout.InfiniteTimeSpan };
            report(T("apkpure에서 최신 버전 확인 중","apkpureで最新版を確認中","Checking the latest version on apkpure"));
            using var response=await http.GetAsync(GameXapkUrl,HttpCompletionOption.ResponseHeadersRead);
            if(!response.IsSuccessStatusCode) return null;
            var name=System.Web.HttpUtility.ParseQueryString(response.RequestMessage?.RequestUri?.Query??"")["filename"]??"pjsekai.xapk";
            var version=System.Text.RegularExpressions.Regex.Match(name,@"_([0-9][0-9.]*)_APKPure").Groups[1].Value;
            var xapk=Path.Combine(ApkCache,$"pjsekai-{(version.Length>0?version:"latest")}.xapk");
            if(File.Exists(xapk)) return xapk;
            var total=response.Content.Headers.ContentLength;
            await using(var input=await response.Content.ReadAsStreamAsync())
            await using(var output=File.Create(xapk+".part"))
            {
                var buffer=new byte[1<<20]; long done=0; var shown=DateTime.MinValue;
                for(int read;(read=await input.ReadAsync(buffer))>0;)
                {
                    await output.WriteAsync(buffer.AsMemory(0,read)); done+=read;
                    if(DateTime.UtcNow-shown>TimeSpan.FromSeconds(2))
                    {
                        shown=DateTime.UtcNow;
                        report(T($"게임 받는 중 {version} · {done>>20}/{(total??0)>>20}MB",$"ゲームをダウンロード中 {version} · {done>>20}/{(total??0)>>20}MB",$"Downloading the game {version} · {done>>20}/{(total??0)>>20}MB"));
                    }
                }
            }
            File.Move(xapk+".part",xapk,true);
            return xapk;
        }
        catch(HttpRequestException) { return null; }
    }
    // The Downloads folder can be moved; Explorer keeps its location under User Shell Folders.
    static string DownloadsFolder=>Reg(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders","{374DE290-123F-4565-9164-39C4925E467B}") is { } moved
        ?Environment.ExpandEnvironmentVariables(moved):Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"Downloads");
    static string? FindDownloadedXapk(bool keptOnly=false)=>
        (keptOnly?new[]{ApkCache}:new[]{DownloadsFolder,ApkCache}).Where(Directory.Exists)
            .SelectMany(d=>Directory.EnumerateFiles(d,"*.xapk").Concat(Directory.EnumerateFiles(d,"*.apk")))
            .Where(f=>{ var n=Path.GetFileName(f); return n.Contains("pjsekai",StringComparison.OrdinalIgnoreCase) || n.Contains("プロジェクトセカイ"); })
            .OrderByDescending(File.GetLastWriteTime).FirstOrDefault();
    // The browser download is copied into local/apk so later installs (and other PCs with this folder) need no download.
    static string Keep(string file)
    {
        if(Path.GetDirectoryName(Path.GetFullPath(file))==Path.GetFullPath(ApkCache)) return file;
        var kept=Path.Combine(ApkCache,Path.GetFileName(file));
        File.Copy(file,kept,true);
        return kept;
    }
    // Latest package from apkpure, fetched by the control panel's built-in browser (it takes the request file):
    // the browser reports the version it is about to download and skips it when that file is already kept.
    // Without the panel (web mode), the apkpure page opens in the default browser and Downloads is watched.
    static string ApkUrl(string key)=>key=="qooapp"?QooAppApkUrl:GameXapkUrl;
    static string LatestFile(string key)=>Path.Combine(ApkCache,$"latest-{key}.txt");
    static string KeptPath(string key,string version,bool apk)=>Path.Combine(ApkCache,$"{key}-{(version.Length>0?version:"latest")}{(apk?".apk":".xapk")}");
    static string? KeptVersionFile(string key,string version)=>new[]{KeptPath(key,version,false),KeptPath(key,version,true)}.FirstOrDefault(File.Exists);
    static string? FileVersion(string file)=>System.Text.RegularExpressions.Regex.Match(Path.GetFileName(file),@"-([0-9][0-9.]*)\.x?apk$").Groups[1].Value is { Length: >0 } v?v:null;
    static async Task<string> FetchLatest(string key,Action<string> report)
    {
        Directory.CreateDirectory(ApkCache);
        var since=DateTime.Now.AddSeconds(-2);
        File.Delete(LatestFile(key));
        var flag=Path.Combine(ProvisionDir,"need-game");
        File.WriteAllText(flag,key);
        var deadline=DateTime.UtcNow.AddMinutes(60); var asked=DateTime.UtcNow; var external=false;
        var label=key=="qooapp"?"QooApp":T("게임","ゲーム","the game");
        var site=key=="qooapp"?T("QooApp 사이트","QooAppのサイト","the QooApp site"):"apkpure";
        while(DateTime.UtcNow<deadline)
        {
            if(File.Exists(flag) && DateTime.UtcNow-asked>TimeSpan.FromSeconds(15))
            {
                File.Delete(flag); external=true;
                try { Process.Start(new ProcessStartInfo(key=="qooapp"?QooAppApkUrl:GamePageUrl) { UseShellExecute=true }); } catch { }
            }
            if(File.Exists(LatestFile(key)) && File.GetLastWriteTime(LatestFile(key))>=since
                && KeptVersionFile(key,File.ReadAllText(LatestFile(key)).Trim()) is { } kept) return kept;
            if(external && key=="pjsekai" && FindDownloadedXapk() is { } file && File.GetLastWriteTime(file)>=since && DateTime.Now-File.GetLastWriteTime(file)>TimeSpan.FromSeconds(10)) return Keep(file);
            report(T($"{site}에서 {label} 최신 버전을 받는 중이에요. 받기 창에 확인 화면이 뜨면 직접 눌러주세요",
                $"{site}から{label}の最新版を取得中です。取得ウィンドウに確認画面が出たら操作してください",
                $"Getting the latest {label} from {site}. If its window shows a check, complete it there"));
            await Task.Delay(3000);
        }
        throw new TimeoutException(T("파일을 1시간 동안 기다렸지만 받지 못했어요.","ファイルを1時間待ちましたが取得できませんでした。","No file arrived within an hour."));
    }
    static string? GameSignature(string output)=>System.Text.RegularExpressions.Regex.Match(output,@"signatures:\[([0-9a-f, ]+)\]").Groups[1].Value is { Length: >0 } v?v:null;
    static async Task<string?> SignatureOf(int instance)
    {
        try { return GameSignature(await Run(Adb,"-s",SerialOf(instance),"shell",$"dumpsys package {Package} | grep -m1 signatures")); }
        catch { return null; }
    }

    static async Task<(List<string> Apks,List<(string File,string Path)> Obbs)> UnpackGame(string xapk,int target,Action<string> report)
    {
        report(T("게임 파일 푸는 중","ゲームファイルを展開中","Unpacking the game files"));
        var dir=Path.ChangeExtension(xapk,null);
        if(Directory.Exists(dir)) Directory.Delete(dir,true);
        Directory.CreateDirectory(dir);
        using var zip=ZipFile.OpenRead(xapk);
        var manifest=zip.GetEntry("manifest.json");
        if(manifest==null)
        {
            // A plain APK rather than an XAPK.
            var apk=Path.Combine(dir,"base.apk"); File.Copy(xapk,apk,true);
            return ([apk],[]);
        }
        using var doc=JsonDocument.Parse(manifest.Open());
        var abis=(await Run(Adb,"-s",SerialOf(target),"shell","getprop ro.product.cpu.abilist")).Trim().Split(',').Select(a=>a.Replace('-','_')).ToList();
        var splits=doc.RootElement.TryGetProperty("split_apks",out var list)?list.EnumerateArray().Select(e=>(File:e.GetProperty("file").GetString()!,Id:e.GetProperty("id").GetString()??"")).ToList():[];
        // Keep the one ABI split this VM prefers; the other ABI splits would be rejected or wasted.
        string[] known=["x86_64","x86","arm64_v8a","armeabi_v7a","armeabi"];
        var abiSplits=splits.Where(s=>known.Any(k=>s.Id==$"config.{k}")).ToList();
        var chosen=abis.Select(a=>abiSplits.FirstOrDefault(s=>s.Id==$"config.{a}")).FirstOrDefault(s=>s.File!=null);
        var apks=new List<string>();
        foreach(var split in splits.Where(s=>!abiSplits.Contains(s) || s==chosen))
        {
            var path=Path.Combine(dir,Path.GetFileName(split.File));
            zip.GetEntry(split.File)!.ExtractToFile(path,true); apks.Add(path);
        }
        var obbs=new List<(string,string)>();
        if(doc.RootElement.TryGetProperty("expansions",out var expansions))
            foreach(var e in expansions.EnumerateArray())
            {
                var file=e.GetProperty("file").GetString()!; var path=Path.Combine(dir,Path.GetFileName(file));
                zip.GetEntry(file)!.ExtractToFile(path,true); obbs.Add((path,e.GetProperty("install_path").GetString()!));
            }
        return (apks,obbs);
    }

    // Project Sekai is installed and updated from apkpure's latest XAPK ("auto" and "apkpure"); "copy" takes it from another VM.
    static async Task InstallGame(int instance,string source,Action<string> report)
    {
        if(await GameInstalled(instance)) { report(T("게임이 이미 설치돼 있어요","ゲームはインストール済みです","The game is already installed")); return; }
        Directory.CreateDirectory(ApkCache);
        List<string> apks; List<(string File,string Path)> obbs=[];
        if(source=="copy") apks=await PullGameFromVm(instance,await GameSourceVm(instance),report);
        else (apks,obbs)=await UnpackGame(await FetchLatest("pjsekai",report),instance,report);
        await InstallGameFiles(instance,apks,obbs,null,report);
    }
    static async Task InstallGameFiles(int instance,List<string> apks,List<(string File,string Path)> obbs,string? reference,Action<string> report)
    {
        foreach(var (file,path) in obbs)
        {
            report(T("추가 데이터 넣는 중","追加データを転送中","Pushing expansion files"));
            await RunLong(TimeSpan.FromMinutes(30),Adb,"-s",SerialOf(instance),"push",Staged(file),"/sdcard/"+path);
        }
        report(T("게임 설치 중","ゲームをインストール中","Installing the game"));
        var output=await RunLong(TimeSpan.FromMinutes(15),Adb,["-s",SerialOf(instance),apks.Count>1?"install-multiple":"install","-r","-g",..apks.Select(Staged)]);
        try { Directory.Delete(AdbStage(),true); } catch { }
        if(!output.Contains("Success")) throw new Exception(T("게임 설치 실패: ","ゲームのインストールに失敗: ","Game install failed: ")+output.Trim());
        // A downloaded package must be signed like the official game (another VM's copy, or this VM's before an update).
        if(reference==null)
            foreach(var n in AllowedInstances.Keys.Where(n=>n!=instance))
                if(await IsVmRunning(n) && await SignatureOf(n) is { } sig) { reference=sig; break; }
        var installed=await SignatureOf(instance);
        if(reference!=null && installed!=reference)
        {
            await Run(Adb,"-s",SerialOf(instance),"uninstall",Package);
            throw new Exception(T("설치한 게임의 서명이 공식 앱과 달라서 지웠어요.","インストールしたゲームの署名が公式アプリと違うため削除しました。","The installed game's signature differs from the official app, so it was removed."));
        }
        report(reference==null?T("게임 설치 완료 (비교할 공식 앱이 없어 서명 확인은 건너뜀)","インストール完了（比較する公式アプリがないため署名確認は省略）","Game installed (no official copy to compare signatures with)"):T("게임 설치 완료 · 공식 앱과 서명 일치","インストール完了 · 公式アプリと署名一致","Game installed · signature matches the official app"));
    }

    // QooApp is installed for the user's own use; a failure does not stop the setup.
    static async Task InstallQooApp(int instance,Action<string> report)
    {
        try
        {
            // pm path exits with 1 when the package is missing.
            if((await Run(Adb,"-s",SerialOf(instance),"shell",$"pm path {QooAppPackage} || true")).Contains("package:")) return;
            var apk=await FetchLatest("qooapp",report);
            report(T("QooApp 설치 중","QooAppをインストール中","Installing QooApp"));
            var output=await RunLong(TimeSpan.FromMinutes(5),Adb,"-s",SerialOf(instance),"install","-r",Staged(apk));
            try { Directory.Delete(AdbStage(),true); } catch { }
            if(!output.Contains("Success")) report(T("QooApp 설치 실패: ","QooAppのインストールに失敗: ","QooApp install failed: ")+output.Trim());
        }
        catch(OperationCanceledException) { throw; }
        catch(Exception ex) { report(T("QooApp 설치 실패: ","QooAppのインストールに失敗: ","QooApp install failed: ")+ex.Message); }
    }

    static async Task<Version?> InstalledGameVersion(int instance)
    {
        try { return Version.TryParse(System.Text.RegularExpressions.Regex.Match(await Run(Adb,"-s",SerialOf(instance),"shell",$"dumpsys package {Package} | grep -m1 versionName"),@"versionName=([0-9.]+)").Groups[1].Value,out var v)?v:null; }
        catch { return null; }
    }

    // Updates the game on every VM that has it and is not running a macro (an update closes the game).
    // VMs that are off are turned on for it and off again; the account and downloaded data stay.
    static async Task<List<int>> UpdateGames(Action<int?,string> report)
    {
        var xapk=await FetchLatest("pjsekai",d=>report(null,d));
        var latest=Version.TryParse(FileVersion(xapk)??"",out var v)?v:null;
        var updated=new List<int>();
        foreach(var n in AllowedInstances.Keys.ToList())
        {
            if(IsWorkerRunning(n) || IsLocked(n)) { report(n,T("실행 중이거나 잠겨 있어 건너뜀","実行中またはロック中のためスキップ","Skipped: running or locked")); continue; }
            var wasOff=!await IsVmRunning(n);
            if(wasOff) { report(n,T("VM 켜는 중","VM起動中","Booting VM")); await BootVm(n); }
            try
            {
                if(!await GameInstalled(n)) continue;
                var current=await InstalledGameVersion(n);
                if(latest!=null && current!=null && current>=latest) { report(n,T($"이미 최신 ({current})",$"最新です ({current})",$"Already latest ({current})")); continue; }
                var reference=await SignatureOf(n);
                await Run(Adb,"-s",SerialOf(n),"shell",$"am force-stop {Package}");
                var (apks,obbs)=await UnpackGame(xapk,n,d=>report(n,d));
                await InstallGameFiles(n,apks,obbs,reference,d=>report(n,d));
                updated.Add(n);
            }
            finally { if(wasOff) await QuitVm(n); }
        }
        return updated;
    }

    // ---- Provisioning job

    record ProvisionRequest(string? Name,int Count,int? Base,string Install,bool Setup,string? Unit,bool Start,bool StartBase,int Loops,
        string? InviteCode,string? InviteMode,int? InviteLimit,bool Update=false);
    record ProvisionStatus(string State,string Step,string? Detail,int[] Instances,int? Current,string? Error,int Pid,DateTimeOffset Started,DateTimeOffset Updated,ProvisionRequest Request);

    static string ProvisionDir=>Path.Combine(Workspace,"local","provision");
    static ProvisionStatus? ReadProvision()
    {
        try { var path=Path.Combine(ProvisionDir,"status.json"); return File.Exists(path)?JsonSerializer.Deserialize<ProvisionStatus>(File.ReadAllText(path)):null; }
        catch { return null; }
    }
    static bool ProvisionAlive(ProvisionStatus? status)
    {
        if(status?.State!="running") return false;
        try { using var process=Process.GetProcessById(status.Pid); return process.ProcessName.Equals("SekaiMacro",StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }
    static bool ProvisionUses(int instance)=>ReadProvision() is { } s && ProvisionAlive(s) && s.Instances.Contains(instance);
    static string? ProvisionPending(int instance)
    {
        var s=ReadProvision();
        if(!ProvisionAlive(s) || s!.Current!=instance) return null;
        return s.Detail??ProvisionStepLabel(s.Step);
    }
    static string ProvisionStepLabel(string step)=>step switch
    {
        "create"=>T("VM 만드는 중","VMを作成中","Creating the VM"),
        "boot"=>T("VM 켜는 중","VM起動中","Booting VM"),
        "install"=>T("게임 설치 중","ゲームをインストール中","Installing the game"),
        "setup"=>T("튜토리얼·데이터 세팅 중","チュートリアル・データ準備中","Running the tutorial and data download"),
        "copy"=>T("복제 중","複製中","Copying"),
        "qooapp"=>T("QooApp 설치 중","QooAppをインストール中","Installing QooApp"),
        "update"=>T("게임 업데이트 중","ゲームを更新中","Updating the game"),
        "start"=>T("매크로 시작 중","マクロを開始中","Starting macros"),
        _=>step
    };

    static string StartProvision(ProvisionRequest request)
    {
        if(ProvisionAlive(ReadProvision())) throw new InvalidOperationException(T("이미 VM 세팅 작업이 진행 중이에요.","すでにVMの準備作業が進行中です。","A VM setup job is already running."));
        if(IsBlueStacks) _=BlueStacksRequired; else _=LdRequired;
        if(request.Update) goto start;
        if(request.Base==null || request.Count>1) RequireVmManagement();
        if(request.Count is <1 or >30) throw new ArgumentException(T("VM 수는 1~30대예요.","VMの数は1〜30台です。","The VM count must be 1 to 30."));
        if(request.Install is not ("auto" or "apkpure" or "copy" or "none")) throw new ArgumentException(T("게임 설치 방법이 올바르지 않아요.","ゲームのインストール方法が正しくありません。","Invalid game install source."));
        if(request.Base is { } b)
        {
            RequireInstance(b); RequireUnlocked(b);
            if(IsWorkerRunning(b)) throw new InvalidOperationException(T($"{b}번은 매크로 실행 중이라 복제할 수 없어요. 먼저 중지하세요.",$"{b}番はマクロ実行中のため複製できません。先に停止してください。",$"#{b} is running a macro and cannot be copied. Stop it first."));
            if(request.Count<2 && request.Install=="none" && !request.Setup) throw new ArgumentException(T("복제할 개수를 1개 이상 정하세요.","複製する数を1つ以上にしてください。","Choose at least one copy."));
        }
        if(request.Start)
        {
            if(request.Loops is <1 or >99) throw new ArgumentException(T("반복 횟수는 1~99회예요.","繰り返し回数は1〜99回です。","Loops must be 1 to 99."));
            if(request.InviteCode is { Length: >0 }) ValidateInvite(request.InviteCode,request.InviteMode,request.InviteLimit);
            if(request.Loops>1 && (request.InviteCode is not { Length: >0 } ? 0 : InviteCapacity(NewInvite(request.InviteCode,request.InviteMode!,request.InviteLimit),0,[])) is { } room && request.Loops>room)
                throw new ArgumentException(room==0?T("반복하려면 초대 코드를 넣으세요.","繰り返すには招待コードを入れてください。","Loops need an invitation code.")
                    :T($"일회용 코드는 {room}회까지만 반복할 수 있어요.",$"使い捨てコードは{room}回までしか繰り返せません。",$"The single-use code allows only {room} loops."));
        }
        var copies=request.Count-1;
        if(request.Base is { } source) RequireDisk(FolderBytes(VmDir(source)),copies);
        else RequireDisk(request.Install!="none" || request.Setup?NewVmBytes():1L<<30,request.Count);
        start:
        Directory.CreateDirectory(ProvisionDir);
        File.Delete(Path.Combine(ProvisionDir,"cancel"));
        WriteJsonAtomic(Path.Combine(ProvisionDir,"request.json"),request);
        var info=new ProcessStartInfo(MacroExecutable()) { WorkingDirectory=Workspace,UseShellExecute=false,CreateNoWindow=true };
        info.ArgumentList.Add("provision");
        var process=Process.Start(info)!;
        WriteJsonAtomic(Path.Combine(ProvisionDir,"status.json"),new ProvisionStatus("running","queued",null,request.Base is { } x?[x]:[],request.Base,null,process.Id,DateTimeOffset.Now,DateTimeOffset.Now,request));
        return T("VM 세팅을 시작했어요. 진행 상황은 VM 관리에서 볼 수 있어요.","VMの準備を開始しました。進行状況はVM管理で確認できます。","VM setup started. Follow it in VM management.");
    }
    static string CancelProvision()
    {
        if(!ProvisionAlive(ReadProvision())) throw new InvalidOperationException(T("진행 중인 VM 세팅이 없어요.","進行中のVM準備はありません。","No VM setup is running."));
        File.WriteAllText(Path.Combine(ProvisionDir,"cancel"),"cancel");
        return T("지금 단계가 끝나면 멈춰요.","現在の段階が終わったら止まります。","It stops after the current step.");
    }

    static async Task RunProvision()
    {
        var request=JsonSerializer.Deserialize<ProvisionRequest>(File.ReadAllText(Path.Combine(ProvisionDir,"request.json")))!;
        var started=DateTimeOffset.Now; var instances=new List<int>(); int? current=request.Base; var step="queued";
        var log=Path.Combine(ProvisionDir,"log.txt");
        void Save(string state,string? detail=null,string? error=null)=>
            WriteJsonAtomic(Path.Combine(ProvisionDir,"status.json"),new ProvisionStatus(state,step,detail,[..instances],current,error,Environment.ProcessId,started,DateTimeOffset.Now,request));
        void Report(string next,string? detail=null)
        {
            if(File.Exists(Path.Combine(ProvisionDir,"cancel"))) throw new OperationCanceledException();
            step=next; Save("running",detail);
            File.AppendAllText(log,$"[{DateTime.Now:HH:mm:ss}] {current} {ProvisionStepLabel(next)}{(detail!=null?" · "+detail:"")}\n");
        }
        try
        {
            if(request.Update)
            {
                Report("update");
                var updated=await UpdateGames((n,d)=>{ current=n; if(n is { } i && !instances.Contains(i)) instances.Add(i); Report("update",d); });
                step="done"; Save("done",T($"{updated.Count}대 업데이트",$"{updated.Count}台を更新",$"Updated {updated.Count}"));
                return;
            }
            var unit=request.Unit??"mmj";
            int baseVm;
            if(request.Base is { } existing) baseVm=existing;
            else
            {
                Report("create");
                baseVm=await CreateVm(UniqueVmName(request.Name is { Length: >0 } n?n.Trim():"셐 매크로",AllowedInstances.Values));
            }
            instances.Add(baseVm); current=baseVm;
            if(request.Install!="none" || request.Setup) { Report("boot"); if(!await IsVmRunning(baseVm)) await BootVm(baseVm); }
            if(request.Install!="none")
            {
                Report("install"); await InstallGame(baseVm,request.Install,d=>Report("install",d));
                Report("qooapp"); await InstallQooApp(baseVm,d=>Report("qooapp",d));
            }
            if(request.Setup)
            {
                Report("setup");
                if(!await GameInstalled(baseVm)) throw new InvalidOperationException(T("게임이 설치돼 있지 않아요.","ゲームがインストールされていません。","The game is not installed."));
                // Once the game has created its data folder (it was launched before, maybe a stopped setup), the
                // onboarding starts from a reset that keeps the downloaded data; a never-launched install starts directly.
                var launched=(await Run(Adb,"-s",SerialOf(baseVm),"shell",$"test -d /sdcard/Android/data/{Package} && echo yes || true")).Contains("yes");
                if(!launched) await Run(Adb,"-s",SerialOf(baseVm),"shell",$"monkey -p {Package} -c android.intent.category.LAUNCHER 1");
                var pid=SpawnWorker(baseVm,[launched?"run":"tutorial","--stages","onboarding","--unit",unit,"--instance",baseVm.ToString(),"--background"]);
                using var worker=Process.GetProcessById(pid);
                while(!worker.HasExited)
                {
                    if(File.Exists(Path.Combine(ProvisionDir,"cancel"))) { StopWorker(baseVm); await worker.WaitForExitAsync(); throw new OperationCanceledException(); }
                    await Task.Delay(3000);
                }
                // The worker is not this process's child, so its exit code is unreadable; its stage record tells the result.
                if(Str(ReadJson(Path.Combine(ArtifactsDir(baseVm),"progress.json")),"Status")!="complete" || Str(ReadJson(Path.Combine(ArtifactsDir(baseVm),"progress.json")),"Stage")!="onboarding") throw new Exception(T($"{baseVm}번 튜토리얼 세팅이 멈췄어요. 그 VM의 로그를 확인하세요.",$"{baseVm}番のチュートリアル準備が止まりました。そのVMのログを確認してください。",$"The tutorial setup on #{baseVm} stopped. Check that VM's log."));
            }
            var baseName=AllowedInstances.GetValueOrDefault(baseVm)??request.Name??"셐 매크로";
            var copies=new List<int>();
            for(var k=1;k<request.Count;k++)
            {
                Report("copy",T($"{k}/{request.Count-1}대 복제 중",$"{k}/{request.Count-1}台を複製中",$"Copying {k}/{request.Count-1}"));
                ForgetVmLists();
                await QuitVm(baseVm);
                RequireDisk(FolderBytes(VmDir(baseVm)),1);
                var copy=await CopyVm(baseVm,UniqueVmName($"{baseName}-{k+1}",AllowedInstances.Values));
                copies.Add(copy); instances.Add(copy);
            }
            if(request.Start)
            {
                var plan=JsonSerializer.Deserialize<Plan>(File.ReadAllText(Path.Combine(Workspace,"plan.json")))!.Stages;
                var targets=request.StartBase || copies.Count==0?instances.ToList():copies;
                foreach(var n in targets)
                {
                    current=n; Report("start");
                    ForgetVmLists();
                    if(!await IsVmRunning(n)) await BootVm(n);
                    var invite=request.InviteCode is { Length: >0 }?"set":"keep";
                    await StartWorker(n,new StartRequest("run",null,plan,unit,null,true,request.Loops,invite,request.InviteCode,request.InviteMode,request.InviteLimit));
                }
            }
            if(request.Start && instances.Count>1) try { await VmArrange(); } catch { }
            step="done"; Save("done");
            File.AppendAllText(log,$"[{DateTime.Now:HH:mm:ss}] done: {string.Join(',',instances)}\n");
        }
        catch(OperationCanceledException) { step="cancelled"; Save("cancelled"); }
        catch(Exception ex) { Save("failed",null,ex.Message); File.AppendAllText(log,$"[{DateTime.Now:HH:mm:ss}] failed: {ex}\n"); }
    }
}
