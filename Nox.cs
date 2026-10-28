using System.Text.RegularExpressions;

// NoxPlayer: NoxConsole.exe lists, starts and stops VMs, adds, copies and renames them and changes their resolution. It
// prints in the system's ANSI code page. Each VM has an internal name (nox, Nox_<n>) and a title the user sees; copies
// take the source's title. VMs are reached over adb at 62001 (the first VM) or 62024+<index>.
internal static partial class Program
{
    const string NoxSiteUrl="https://www.bignox.com/";
    static string? NoxFolder=>CachedFolder("nox",()=>
        UninstallFolders("Nox").Concat(ProcessFolders("Nox","NoxConsole","MultiPlayerManager"))
            .Concat(DriveFolders(@"Program Files\Nox\bin",@"Program Files (x86)\Nox\bin",@"Nox\bin"))
            .FirstOrDefault(f=>f is { Length: >0 } && File.Exists(Path.Combine(f,"NoxConsole.exe")))?.TrimEnd('\\'));
    static string NoxConsole=>Path.Combine(RequireFolder("nox"),"NoxConsole.exe");
    // Nox starts its own adb server with nox_adb; the same adb keeps that server running unless a newer one is installed.
    static string NoxAdb=>NewerAdb??new[]{"nox_adb.exe","adb.exe"}.Select(f=>Path.Combine(RequireFolder("nox"),f)).First(File.Exists);
    static string NoxConfigDir=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Nox");

    // list: index,name,title,top window,toolbar window,[bind window,]pid,vbox pid; the last two are -1 when off.
    record NoxVm(int Index,string Name,string Title,bool Running,string Pid);
    static List<NoxVm> NoxVms()=>RunTextIn(AnsiEncoding,NoxConsole,"list").Split('\n').Select(l=>l.Trim().Split(','))
        .Where(r=>r.Length>=7 && int.TryParse(r[0],out _)).Select(r=>new NoxVm(int.Parse(r[0]),r[1],r[2],r[^1]!="-1",r[^2])).ToList();
    static string NoxList()
    {
        var vms=NoxVms();
        foreach(var vm in vms) if(NoxPort(vm) is { } port) KnownSerials[BaseOf("nox")+vm.Index]="127.0.0.1:"+port;
        return string.Join("\n",vms.Select(v=>$"{v.Index},{v.Title},0,0,{(v.Running?1:0)},{(v.Running?v.Pid:"-1")},-1"));
    }
    static NoxVm NoxVmOf(int local)=>NoxVms().SingleOrDefault(v=>v.Index==local)
        ??throw new InvalidOperationException(T($"녹스에 {local}번 VM이 없어요.",$"Noxに{local}番のVMがありません。",$"NoxPlayer has no VM #{local}."));
    static string? NoxVmDir(int local)=>NoxFolder is { } folder?Path.Combine(folder,"BignoxVMS",NoxVmOf(local).Name):null;
    // The VM's .vbox forwards a host port to the guest's adb (5555); a VM that never ran has no .vbox yet.
    static string? NoxPort(NoxVm vm)
    {
        try
        {
            var file=Path.Combine(NoxFolder??"","BignoxVMS",vm.Name,vm.Name+".vbox");
            if(File.Exists(file) && Regex.Match(File.ReadAllText(file),@"<Forwarding[^>]*hostport=""(\d+)""[^>]*guestport=""5555""") is { Success: true } m) return m.Groups[1].Value;
        }
        catch { }
        return null;
    }
    static string NoxSerial(int local)=>KnownSerials.TryGetValue(BaseOf("nox")+local,out var serial)?serial:$"127.0.0.1:{(local==0?62001:62024+local)}";

    static async Task NoxLaunch(int local)=>await RunBytesWithin(TimeSpan.FromMinutes(3),NoxConsole,["launch","-index:"+local],false);
    static async Task NoxQuit(int local)=>await RunBytesWithin(TimeSpan.FromMinutes(2),NoxConsole,["quit","-index:"+local],false);

    // The first VM keeps its settings in conf.ini, the others in clone_<name>_conf.ini; h_ is the landscape (tablet) layout.
    // The light profile: 2 CPUs and 3 GB per VM; Nox sets FPS and sound for all its VMs at once, to 30 FPS and off.
    static async Task<string> PrepareNox(int local,bool running)
    {
        var vm=NoxVmOf(local);
        var file=Path.Combine(NoxConfigDir,vm.Name=="nox"?"conf.ini":$"clone_{vm.Name}_conf.ini");
        var text=File.Exists(file)?await File.ReadAllTextAsync(file):"";
        var modify=new List<string>();
        if(!Regex.IsMatch(text,@"(?m)^h_resolution=960x540\s*$") || !Regex.IsMatch(text,@"(?m)^h_dpi=160\s*$"))
        {
            if(running) throw new Exception("Settings differ on a running instance; stop that instance before prepare.");
            modify.Add("-resolution:960,540,160");
        }
        if(!running)
        {
            if(!Regex.IsMatch(text,$@"(?m)^cpu={LightCpus}\s*$")) modify.Add($"-cpu:{LightCpus}");
            if(!Regex.IsMatch(text,$@"(?m)^mem={LightMemoryMb}\s*$")) modify.Add($"-memory:{LightMemoryMb}");
            await RunBytesWithin(TimeSpan.FromSeconds(30),NoxConsole,["globalsetting",$"-fps:{LightFps}","-audio:off"],false);
        }
        if(modify.Count>0) await RunBytesWithin(TimeSpan.FromSeconds(30),NoxConsole,["modify","-index:"+local,..modify],false);
        return $"960x540 DPI160, {LightCpus} CPU, {LightMemoryMb} MB, {LightFps} FPS{(modify.Count>0?" (Nox settings updated)":"")}.";
    }

    // New VMs get the next free internal name and run Android 12 (system type 12), like Nox 7's own first VM.
    static string NextNoxName()
    {
        var names=NoxVms().Select(v=>v.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for(var n=1;;n++) if(!names.Contains($"Nox_{n}") && !Directory.Exists(Path.Combine(NoxFolder??"","BignoxVMS",$"Nox_{n}"))) return $"Nox_{n}";
    }
    static async Task<int> AddNox(string title)
    {
        var before=(await AllVmRows()).Keys.ToHashSet();
        var name=NextNoxName();
        await RunBytesWithin(TimeSpan.FromMinutes(10),NoxConsole,["add","-name:"+name,"-systemtype:12"],false);
        await RunBytesWithin(TimeSpan.FromSeconds(30),NoxConsole,["rename","-name:"+name,"-title:"+title],false);
        return await NewVmIndex(before,title);
    }
    static async Task CopyNox(int local,string title)
    {
        var source=NoxVmOf(local);
        var name=NextNoxName();
        await RunBytesWithin(TimeSpan.FromMinutes(60),NoxConsole,["copy","-name:"+name,"-from:"+source.Title],false);
        if(!NoxVms().Any(v=>v.Name.Equals(name,StringComparison.OrdinalIgnoreCase))) throw new Exception(T("녹스 VM을 복제하지 못했어요.","NoxのVMを複製できませんでした。","The NoxPlayer VM was not copied."));
        await RunBytesWithin(TimeSpan.FromSeconds(30),NoxConsole,["rename","-name:"+name,"-title:"+title],false);
    }
    static string? NoxVersion()=>UninstallVersion("Nox");
}
