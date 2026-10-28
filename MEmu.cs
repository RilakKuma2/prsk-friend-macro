using System.Text.RegularExpressions;

// MEmu: memuc.exe lists, starts and stops VMs, creates, clones and renames them and changes their settings. It prints
// in the system's ANSI code page. VMs are reached over adb at the port forwarded to the guest's 5555 (21503+10*index).
internal static partial class Program
{
    const string MEmuSiteUrl="https://www.memuplay.com/";
    static string? MEmuFolder=>CachedFolder("memu",()=>
        UninstallFolders("MEmu").SelectMany(f=>new[]{f,f==null?null:Path.Combine(f,"MEmu")})
            .Concat(ProcessFolders("MemuService","MEmu","MEmuConsole"))
            .Concat(DriveFolders(@"Program Files\Microvirt\MEmu",@"Microvirt\MEmu"))
            .FirstOrDefault(f=>f is { Length: >0 } && File.Exists(Path.Combine(f,"memuc.exe")) && File.Exists(Path.Combine(f,"adb.exe")))?.TrimEnd('\\'));
    static string MEmuConsole=>Path.Combine(RequireFolder("memu"),"memuc.exe");
    static string MEmuText(params string[] args)=>RunTextIn(AnsiEncoding,MEmuConsole,args);
    static async Task<string> MEmuRun(TimeSpan timeout,params string[] args)
    {
        var output=AnsiEncoding.GetString(await RunBytesWithin(timeout,MEmuConsole,args,false));
        if(output.Contains("ERROR")) throw new Exception("memuc: "+output.Trim());
        return output;
    }

    // listvms: index,title,top window,running,pid[,disk usage].
    static string MEmuList()=>string.Join("\n",MEmuText("listvms").Split('\n').Select(l=>l.Trim().Split(',')).Where(r=>r.Length>=5 && int.TryParse(r[0],out _))
        .Select(r=>$"{r[0]},{r[1]},0,0,{(r[3]=="1"?1:0)},{(r[3]=="1"?r[4]:"-1")},-1"));

    // VM folders are MEmu (index 0) and MEmu_<index>; each .memu file forwards a host port to the guest's adb.
    static string? MEmuVmDir(int local)=>MEmuFolder is { } folder?Path.Combine(folder,"MemuHyperv VMs",local==0?"MEmu":$"MEmu_{local}"):null;
    static string MEmuSerial(int local)
    {
        try
        {
            if(MEmuVmDir(local) is { } dir && Directory.GetFiles(dir,"*.memu").FirstOrDefault(f=>!Path.GetFileName(f).StartsWith("done")) is { } file
                && Regex.Match(File.ReadAllText(file),@"<Forwarding name=""ADB""[^>]*hostport=""(\d+)""") is { Success: true } m)
                return "127.0.0.1:"+m.Groups[1].Value;
        }
        catch { }
        return $"127.0.0.1:{21503+10*local}";
    }

    static async Task MEmuLaunch(int local)=>await MEmuRun(TimeSpan.FromMinutes(3),"start","-i",local.ToString());
    static async Task MEmuQuit(int local)=>await MEmuRun(TimeSpan.FromMinutes(2),"stop","-i",local.ToString());

    // custom_resolution holds "width height dpi". The light profile: 2 CPUs, 3 GB, 30 FPS and no sound.
    static async Task<string> PrepareMEmu(int local,bool running)
    {
        var index=local.ToString();
        var changed=false;
        if(!Regex.IsMatch(MEmuText("getconfigex","-i",index,"custom_resolution"),@"Value:\s*960 540 160\b"))
        {
            if(running) throw new Exception("Settings differ on a running instance; stop that instance before prepare.");
            await MEmuRun(TimeSpan.FromSeconds(30),"setconfigex","-i",index,"custom_resolution","960","540","160");
            changed=true;
        }
        if(!running)
            foreach(var (key,value) in new[]{("cpus",LightCpus.ToString()),("memory",LightMemoryMb.ToString()),("fps",LightFps.ToString()),("enable_audio","0")})
                if(!Regex.IsMatch(MEmuText("getconfigex","-i",index,key),$@"Value:\s*{value}\b"))
                { await MEmuRun(TimeSpan.FromSeconds(30),"setconfigex","-i",index,key,value); changed=true; }
        return $"960x540 DPI160, {LightCpus} CPU, {LightMemoryMb} MB, {LightFps} FPS{(changed?" (MEmu settings updated)":"")}.";
    }

    // A new VM runs Android 9 (64-bit), like MEmu's own first VM; create prints "index:<n>".
    static async Task<int> AddMEmu(string name)
    {
        var before=(await AllVmRows()).Keys.ToHashSet();
        var output=await MEmuRun(TimeSpan.FromMinutes(10),"create","96");
        var index=Regex.Match(output,@"index:(\d+)") is { Success: true } m?m.Groups[1].Value:throw new Exception("memuc: "+output.Trim());
        await MEmuRun(TimeSpan.FromSeconds(30),"rename","-i",index,name);
        return await NewVmIndex(before,name);
    }
    // clone does not print the new index; the VM that was not there before is the copy.
    static async Task CopyMEmu(int local,string name)
    {
        var before=MEmuList().Split('\n').Select(r=>r.Split(',')[0]).ToHashSet();
        await MEmuRun(TimeSpan.FromMinutes(60),"clone","-i",local.ToString());
        var made=MEmuList().Split('\n').Select(r=>r.Split(',')[0]).Where(i=>i.Length>0 && !before.Contains(i)).ToList();
        if(made.Count!=1) throw new Exception(T("복제한 미뮤 VM을 찾지 못했어요.","複製したMEmuのVMが見つかりません。","The copied MEmu VM was not found."));
        await MEmuRun(TimeSpan.FromSeconds(30),"rename","-i",made[0],name);
    }
    static string? MEmuVersion()=>UninstallVersion("MEmu");
    static string? UninstallVersion(string name)=>new[]{@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\",@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\",@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Uninstall\"}
        .Select(root=>Reg(root+name,"DisplayVersion")).FirstOrDefault(v=>v is { Length: >0 });
}
