using System.Text.Json;

// MuMu Player (12 and the global build): MuMuManager.exe lists, starts and stops VMs, creates and clones them and
// changes their settings, answering in JSON. VMs are reached over adb at 127.0.0.1:<adb_port> (16384+32*index).
// MuMu's Android 15 reports its display in portrait (540x960) and rotates it to landscape.
internal static partial class Program
{
    const string MuMuSiteUrl="https://www.mumuplayer.com/";
    static string? MuMuToolDir(string? folder)=>folder is not { Length: >0 }?null
        :new[]{"nx_main","shell"}.Select(d=>Path.Combine(folder,d)).FirstOrDefault(d=>File.Exists(Path.Combine(d,"MuMuManager.exe")));
    static string? MuMuFolder=>CachedFolder("mumu",()=>
        UninstallFolders("MuMuPlayerGlobal","MuMuPlayer","MuMuPlayer-12.0","MuMuPlayerGlobal-12.0")
            .Concat(ProcessFolders("MuMuNxMain","MuMuPlayer","MuMuManager").Select(f=>f==null?null:Path.GetDirectoryName(f)))
            .Concat(DriveFolders(@"Program Files\Netease\MuMuPlayer",@"Program Files\Netease\MuMuPlayerGlobal-12.0",@"Program Files\Netease\MuMuPlayer-12.0"))
            .FirstOrDefault(f=>MuMuToolDir(f)!=null)?.TrimEnd('\\'));
    static string MuMuManager=>Path.Combine(MuMuToolDir(RequireFolder("mumu"))!,"MuMuManager.exe");
    static string MuMuAdb=>Path.Combine(MuMuToolDir(RequireFolder("mumu"))!,"adb.exe");
    static string MuMuSerial(int local)=>$"127.0.0.1:{16384+32*local}";
    static int BaseOf(string key)=>Emulators.First(e=>e.Key==key).Base;

    // MuMuManager exits with odd codes even when it worked; its JSON says what happened.
    static async Task<JsonElement> MuMu(TimeSpan timeout,params string[] args)
    {
        var output=System.Text.Encoding.UTF8.GetString(await RunBytesWithin(timeout,MuMuManager,args,false)).Trim();
        try { using var document=JsonDocument.Parse(output); return document.RootElement.Clone(); }
        catch(JsonException) { throw new Exception("MuMuManager: "+output); }
    }
    static void RequireMuMuOk(JsonElement result)
    {
        if(result.TryGetProperty("errcode",out var code) && code.GetInt32()!=0)
            throw new Exception("MuMuManager: "+(result.TryGetProperty("errmsg",out var message)?message.GetString():code.ToString()));
    }

    // `info -v all` answers with {"<index>": {...}}, or with the one VM's object when there is only one.
    static string MuMuList()
    {
        using var document=JsonDocument.Parse(RunText(MuMuManager,"info","-v","all"));
        var root=document.RootElement;
        var vms=root.TryGetProperty("index",out _)?[root]:root.EnumerateObject().Select(p=>p.Value).Where(v=>v.ValueKind==JsonValueKind.Object).ToList();
        var rows=new List<string>();
        foreach(var vm in vms)
        {
            if(!int.TryParse(vm.GetProperty("index").GetString(),out var index)) continue;
            bool Flag(string name)=>vm.TryGetProperty(name,out var v) && v.ValueKind==JsonValueKind.True;
            var on=Flag("is_process_started");
            var pid=vm.TryGetProperty("pid",out var p) && p.ValueKind==JsonValueKind.Number?p.GetInt32():-1;
            if(vm.TryGetProperty("adb_port",out var port) && port.ValueKind==JsonValueKind.Number)
                KnownSerials[BaseOf("mumu")+index]=$"{(vm.TryGetProperty("adb_host_ip",out var ip)?ip.GetString():"127.0.0.1")}:{port.GetInt32()}";
            var name=(vm.TryGetProperty("name",out var n)?n.GetString():null)??$"MuMu {index}";
            rows.Add($"{index},{name.Replace(',',' ')},0,0,{(on?1:0)},{(on?pid:-1)},-1");
        }
        return string.Join("\n",rows);
    }

    static async Task MuMuLaunch(int local)=>RequireMuMuOk(await MuMu(TimeSpan.FromMinutes(3),"control","-v",local.ToString(),"launch"));
    static async Task MuMuQuit(int local)=>RequireMuMuOk(await MuMu(TimeSpan.FromMinutes(3),"control","-v",local.ToString(),"shutdown"));

    // A custom resolution of 960x540 at 160 DPI, and the light profile (custom performance of 2 CPUs and 3 GB, 30 FPS,
    // no vertical sync, system sound off).
    static async Task<string> PrepareMuMu(int local,bool running)
    {
        var resolution=new Dictionary<string,string> { ["resolution_width"]="960",["resolution_height"]="540",["resolution_dpi"]="160" };
        var light=new Dictionary<string,string> { ["performance_mode"]="custom",["performance_cpu.custom"]=LightCpus.ToString(),
            ["performance_mem.custom"]=(LightMemoryMb/1024).ToString(),["max_frame_rate"]=LightFps.ToString(),["vertical_sync"]="false",["system_volume_close"]="true" };
        var current=await MuMu(TimeSpan.FromSeconds(30),["setting","-v",local.ToString(),..resolution.Keys.Concat(light.Keys).SelectMany(k=>new[]{"-k",k})]);
        // Numbers come back as "960.000000".
        bool Same(string key,string wanted)
        {
            var value=current.TryGetProperty(key,out var v)?v.GetString()??"":"";
            return double.TryParse(value,System.Globalization.CultureInfo.InvariantCulture,out var a) && double.TryParse(wanted,System.Globalization.CultureInfo.InvariantCulture,out var b)?a==b:value==wanted;
        }
        var changes=new List<string>();
        if(!resolution.All(r=>Same(r.Key,r.Value)))
        {
            if(running) throw new Exception("Settings differ on a running instance; stop that instance before prepare.");
            changes.AddRange(["-k","resolution_mode","-val","custom","-k","resolution_width.custom","-val","960","-k","resolution_height.custom","-val","540","-k","resolution_dpi.custom","-val","160"]);
        }
        if(!running) foreach(var (key,value) in light.Where(l=>!Same(l.Key,l.Value))) changes.AddRange(["-k",key,"-val",value]);
        if(changes.Count>0) RequireMuMuOk(await MuMu(TimeSpan.FromSeconds(30),["setting","-v",local.ToString(),..changes]));
        return $"960x540 DPI160, {LightCpus} CPU, {LightMemoryMb} MB, {LightFps} FPS{(changes.Count>0?" (MuMu settings updated)":"")}.";
    }

    // create and clone answer with {"<new index>": {"errcode":0,...}}.
    static int NewMuMuIndex(JsonElement result)
    {
        var made=result.EnumerateObject().Where(p=>int.TryParse(p.Name,out _)).ToList();
        if(made.Count!=1) throw new Exception("MuMuManager: "+result);
        RequireMuMuOk(made[0].Value);
        return int.Parse(made[0].Name);
    }
    static async Task<int> AddMuMu(string name)
    {
        var before=(await AllVmRows()).Keys.ToHashSet();
        var index=NewMuMuIndex(await MuMu(TimeSpan.FromMinutes(10),"create","-n","1"));
        RequireMuMuOk(await MuMu(TimeSpan.FromSeconds(30),"rename","-v",index.ToString(),"-n",name));
        return await NewVmIndex(before,name);
    }
    static async Task CopyMuMu(int local,string name)
    {
        var index=NewMuMuIndex(await MuMu(TimeSpan.FromMinutes(60),"clone","-v",local.ToString(),"-n","1"));
        RequireMuMuOk(await MuMu(TimeSpan.FromSeconds(30),"rename","-v",index.ToString(),"-n",name));
    }
    // delete answers with an error (-502, or -201 on a retry) even when it removes the VM a moment later.
    static async Task DeleteMuMu(int local)
    {
        for(var attempt=0;attempt<2;attempt++)
        {
            await MuMu(TimeSpan.FromMinutes(10),"delete","-v",local.ToString());
            for(var i=0;i<20;i++) { await Task.Delay(1000); if(!MuMuList().Split('\n').Any(r=>r.StartsWith(local+","))) return; }
        }
    }
    // Each VM has its folder under vms, named <build>-<android>-<index>.
    static string? MuMuVmDir(int local)
    {
        var vms=Path.Combine(RequireFolder("mumu"),"vms");
        return Directory.Exists(vms)?Directory.GetDirectories(vms,"*-"+local).FirstOrDefault():null;
    }
    static string? MuMuVersionCache;
    static string? MuMuVersion()
    {
        if(MuMuVersionCache!=null || MuMuFolder==null) return MuMuVersionCache;
        try { using var document=JsonDocument.Parse(RunText(MuMuManager,"version")); MuMuVersionCache=document.RootElement.GetProperty("version").GetString(); }
        catch { }
        return MuMuVersionCache;
    }
}
