using System.Diagnostics;
using System.Text.RegularExpressions;

// BlueStacks 5 on Windows (BlueStacks.cs is BlueStacks Air on macOS; both provide the same calls). Like Air it has no
// console tool: instances are read from bluestacks.conf, started with `HD-Player --instance <id>` and reached over ADB at
// 127.0.0.1:<adb_port>. Instance numbers follow the multi-instance manager's names: <image> ("Pie64") is 0, <image>_<n> is n.
internal static partial class Program
{
    const string BlueStacksRegistry=@"HKEY_LOCAL_MACHINE\SOFTWARE\BlueStacks_nxt";
    const string BlueStacksSiteUrl="https://www.bluestacks.com/";
    static string? BlueStacksApp=>CachedFolder("bluestacks",()=>new[]{Reg(BlueStacksRegistry,"InstallDir")}.Concat(DriveFolders(@"Program Files\BlueStacks_nxt"))
        .FirstOrDefault(d=>d is { Length: >0 } && File.Exists(Path.Combine(d,"HD-Player.exe")))?.TrimEnd('\\'));
    static string BlueStacksRequired=>RequireFolder("bluestacks");
    static string BlueStacksAdb=>NewerAdb??Path.Combine(BlueStacksRequired,"HD-Adb.exe");
    // The data folder (ProgramData\BlueStacks_nxt unless moved) holds bluestacks.conf and the instances' disks.
    static string BlueStacksData=>Reg(BlueStacksRegistry,"UserDefinedDir") is { Length: >0 } dir?dir
        :Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"BlueStacks_nxt");
    static string BlueStacksConf=>Path.Combine(BlueStacksData,"bluestacks.conf");

    // ---- bluestacks.conf: one bst.key="value" per line, shared by every instance.

    static readonly object BlueStacksConfLock=new();
    static (DateTime Stamp,Dictionary<string,string> Values) BlueStacksConfCache=(DateTime.MinValue,[]);
    static Dictionary<string,string> ReadBlueStacksConf()
    {
        lock(BlueStacksConfLock)
        {
            if(!File.Exists(BlueStacksConf)) return [];
            var stamp=File.GetLastWriteTimeUtc(BlueStacksConf);
            if(stamp==BlueStacksConfCache.Stamp) return BlueStacksConfCache.Values;
            var values=new Dictionary<string,string>();
            using(var stream=new FileStream(BlueStacksConf,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
            using(var reader=new StreamReader(stream))
                for(string? line;(line=reader.ReadLine())!=null;)
                    if(Regex.Match(line,"^([^=]+)=\"(.*)\"$") is { Success: true } m) values[m.Groups[1].Value]=m.Groups[2].Value;
            BlueStacksConfCache=(stamp,values);
            return values;
        }
    }

    record BlueStacksInstance(int Index,string Id,string Title,int Port);
    static List<BlueStacksInstance> BlueStacksInstances()
    {
        var conf=ReadBlueStacksConf();
        var ids=conf.Keys.Select(k=>Regex.Match(k,@"^bst\.instance\.([^.]+)\.display_name$")).Where(m=>m.Success).Select(m=>m.Groups[1].Value).Order(StringComparer.Ordinal);
        var used=new HashSet<int>(); var list=new List<BlueStacksInstance>();
        foreach(var id in ids)
        {
            var index=Regex.Match(id,@"_(\d+)$") is { Success: true } m?int.Parse(m.Groups[1].Value):0;
            // Instances of a second Android image would repeat numbers; they move past 100.
            if(!used.Add(index)) { index=100; while(!used.Add(index)) index++; }
            // A running instance listens on status.adb_port, which differs from adb_port when that port was taken.
            var port=int.TryParse(conf.GetValueOrDefault($"bst.instance.{id}.status.adb_port")??conf.GetValueOrDefault($"bst.instance.{id}.adb_port"),out var p)?p:0;
            list.Add(new(index,id,conf[$"bst.instance.{id}.display_name"],port));
        }
        return list;
    }
    static BlueStacksInstance BlueStacksInstanceOf(int instance)=>BlueStacksInstances().SingleOrDefault(i=>i.Index==instance)
        ??throw new InvalidOperationException(T($"블루스택에 {instance}번 인스턴스가 없어요.",$"BlueStacksに{instance}番のインスタンスがありません。",$"BlueStacks has no instance #{instance}."));
    static string BlueStacksSerial(int instance)=>$"127.0.0.1:{BlueStacksInstances().SingleOrDefault(i=>i.Index==instance)?.Port??0}";
    static string BlueStacksVmDir(int instance)=>Path.Combine(BlueStacksData,"Engine",BlueStacksInstanceOf(instance).Id);

    // ---- Processes

    // Instance id -> pid. HD-Player runs elevated, so its command line is unreadable; its window carries the instance's display name.
    static Dictionary<string,int> BlueStacksProcesses()
    {
        var running=new Dictionary<string,int>();
        var byTitle=BlueStacksInstances().GroupBy(i=>i.Title).ToDictionary(g=>g.Key,g=>g.First().Id);
        foreach(var process in Process.GetProcessesByName("HD-Player"))
            using(process)
                if(byTitle.TryGetValue(process.MainWindowTitle,out var id)) running.TryAdd(id,process.Id);
        return running;
    }

    // list2-style rows; VmListNow connects the running ones to adb.
    static string BlueStacksList()
    {
        if(BlueStacksApp==null) return "";
        var running=BlueStacksProcesses();
        // Commas would split the row.
        return string.Join("\n",BlueStacksInstances().Select(i=>running.TryGetValue(i.Id,out var pid)
            ?$"{i.Index},{i.Title.Replace(',',' ')},0,0,1,{pid},-1":$"{i.Index},{i.Title.Replace(',',' ')},0,0,0,-1,-1"));
    }

    static Task BlueStacksLaunch(int instance)
    {
        var target=BlueStacksInstanceOf(instance);
        if(!BlueStacksProcesses().ContainsKey(target.Id))
            Process.Start(new ProcessStartInfo(Path.Combine(BlueStacksRequired,"HD-Player.exe")) { ArgumentList={ "--instance",target.Id },UseShellExecute=true })?.Dispose();
        return Task.CompletedTask;
    }
    // Closing the window asks for confirmation, so Android is shut down first and the player left behind is closed.
    static async Task BlueStacksQuit(int instance)
    {
        var target=BlueStacksInstanceOf(instance);
        if(!BlueStacksProcesses().TryGetValue(target.Id,out var pid)) return;
        var serial=BlueStacksSerial(instance);
        try
        {
            await Run(BlueStacksAdb,"-s",serial,"shell","reboot -p");
            for(var i=0;i<20 && (await Run(BlueStacksAdb,"-s",serial,"get-state")).Trim()=="device";i++) await Task.Delay(1000);
        }
        catch { }
        try { using var process=Process.GetProcessById(pid); process.Kill(); } catch { }
        try { RunText(BlueStacksAdb,"disconnect",serial); } catch { }
    }

    // ---- Settings

    // The macro needs 960x540 at 160 DPI with ADB on. A running BlueStacks rewrites bluestacks.conf (it holds every
    // instance), so the file is only changed while no player runs; otherwise the settings are checked and reported.
    // The light profile (2 CPUs, 3 GB, 30 FPS, no high frame rate or vertical sync) is written with them.
    static string PrepareBlueStacks(int instance)
    {
        var target=BlueStacksInstanceOf(instance);
        var conf=ReadBlueStacksConf();
        var prefix=$"bst.instance.{target.Id}.";
        var wanted=new Dictionary<string,string> { [prefix+"fb_width"]="960",[prefix+"fb_height"]="540",[prefix+"dpi"]="160",
            [prefix+"custom_resolution_selected"]="1",["bst.enable_adb_access"]="1" };
        var light=new Dictionary<string,string> { [prefix+"cpus"]=LightCpus.ToString(),[prefix+"ram"]=LightMemoryMb.ToString(),[prefix+"max_fps"]=LightFps.ToString(),
            [prefix+"enable_high_fps"]="0",[prefix+"enable_vsync"]="0" };
        var wrong=wanted.Where(w=>conf.GetValueOrDefault(w.Key)!=w.Value).ToList();
        var players=Process.GetProcessesByName("HD-Player").Length;
        if(players==0) wrong.AddRange(light.Where(w=>conf.GetValueOrDefault(w.Key)!=w.Value));
        if(wrong.Count==0) return "960x540 DPI160, ADB enabled.";
        if(players==0)
        {
            lock(BlueStacksConfLock)
            {
                var lines=File.ReadAllLines(BlueStacksConf).ToList();
                foreach(var (key,value) in wrong)
                {
                    var at=lines.FindIndex(l=>l.StartsWith(key+"=",StringComparison.Ordinal));
                    if(at>=0) lines[at]=$"{key}=\"{value}\""; else lines.Add($"{key}=\"{value}\"");
                }
                File.WriteAllLines(BlueStacksConf,lines);
                BlueStacksConfCache=(DateTime.MinValue,[]);
            }
            return "960x540 DPI160, ADB enabled (bluestacks.conf updated).";
        }
        throw new InvalidOperationException(T($"블루스택 {target.Title} 설정을 바꿔야 해요: 해상도 960×540, DPI 160, ADB 켜기. 블루스택을 모두 끄면 매크로가 바꾸고, 켜 둔 채라면 블루스택 설정의 디스플레이·고급 탭에서 바꾼 뒤 다시 시도하세요.",
            $"BlueStacks {target.Title} の設定を変える必要があります: 解像度 960×540、DPI 160、ADBをオン。BlueStacksをすべて閉じるとマクロが変更します。起動したままなら設定のディスプレイ・詳細タブで変えてからやり直してください。",
            $"BlueStacks {target.Title} needs 960×540, DPI 160 and ADB on. Close every BlueStacks window and the macro changes them, or change them in BlueStacks settings (Display, Advanced) and try again."));
    }

    static object BlueStacksInfo()=>new { Kind="bluestacks",Folder=BlueStacksApp,Version=Reg(BlueStacksRegistry,"Version"),VersionUrl=(string?)null,
        Validated=(string?)null,ValidatedUrl=(string?)null,LatestUrl=BlueStacksSiteUrl,Site=BlueStacksSiteUrl,GamePage=GamePageUrl };
}
