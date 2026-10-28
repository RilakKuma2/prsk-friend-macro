using System.Diagnostics;
using Microsoft.Win32;

// LDPlayer location: a folder saved from the GUI (local/ldplayer.txt), else the installer's registry entries,
// a running emulator, or common install folders on each fixed drive. Nothing is hardcoded per PC.
internal static partial class Program
{
    static string LdSetting=>Path.Combine(Workspace,"local","ldplayer.txt");
    static readonly object LdLock=new();
    static (DateTime At,string? Folder) LdCache=(DateTime.MinValue,null);

    static string? LdFolder
    {
        get
        {
            lock(LdLock)
            {
                // A missing install is looked up again every 30 seconds, so installing LDPlayer needs no restart.
                if(LdCache.Folder==null && DateTime.UtcNow-LdCache.At>TimeSpan.FromSeconds(30)) LdCache=(DateTime.UtcNow,FindLdFolder());
                return LdCache.Folder;
            }
        }
    }
    static string LdMissing=>T("LD플레이어를 찾지 못했어요. 화면 위쪽에서 LD플레이어 폴더를 지정하세요.","LDPlayerが見つかりません。画面上部でLDPlayerのフォルダを指定してください。","LDPlayer was not found. Set its folder at the top of the page.");
    static string LdRequired=>LdFolder??throw new InvalidOperationException(LdMissing);
    static string Ld=>Path.Combine(LdRequired,"ldconsole.exe");

    static bool IsLdFolder(string folder)=>File.Exists(Path.Combine(folder,"ldconsole.exe")) && File.Exists(Path.Combine(folder,"adb.exe"));
    static string? Reg(string key,string name) { if(!OperatingSystem.IsWindows()) return null; try { return Registry.GetValue(key,name,null) as string; } catch { return null; } }
    const string LdRegistry=@"HKEY_CURRENT_USER\Software\XuanZhi\LDPlayer9";

    static string? FindLdFolder()
    {
        IEnumerable<string?> Candidates()
        {
            string? saved=null;
            try { if(File.Exists(LdSetting)) saved=File.ReadAllText(LdSetting).Trim(); } catch { }
            yield return saved;
            yield return Reg(LdRegistry,"InstallDir");
            yield return Reg(@"HKEY_CURRENT_USER\Software\XuanZhi\LDPlayer","InstallDir");
            foreach(var uninstall in new[]{@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\LDPlayer9",@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\LDPlayer9"})
                if(Reg(uninstall,"DisplayIcon") is { } icon) yield return Path.GetDirectoryName(icon.Trim('"').Split(',')[0]);
            foreach(var process in Process.GetProcessesByName("dnplayer"))
            {
                string? folder=null;
                try { folder=Path.GetDirectoryName(process.MainModule?.FileName); } catch { }
                process.Dispose();
                yield return folder;
            }
            foreach(var drive in DriveInfo.GetDrives().Where(d=>d.DriveType==DriveType.Fixed && d.IsReady))
                foreach(var sub in new[]{@"LDPlayer\LDPlayer9",@"LDPlayer9",@"Program Files\LDPlayer\LDPlayer9",@"leidian\LDPlayer9"})
                    yield return Path.Combine(drive.RootDirectory.FullName,sub);
        }
        foreach(var candidate in Candidates())
            try { if(candidate is { Length: >0 } && IsLdFolder(candidate)) return Path.GetFullPath(candidate).TrimEnd('\\'); }
            catch { }
        return null;
    }

    // VM configs live in the installer's DataDir when it belongs to this install, else in <install>\vms.
    static string LdConfig(int instance)
    {
        var folder=LdRequired;
        var data=Reg(LdRegistry,"DataDir"); var install=Reg(LdRegistry,"InstallDir");
        var vms=data!=null && install!=null && Path.GetFullPath(install).TrimEnd('\\').Equals(folder,StringComparison.OrdinalIgnoreCase) && Directory.Exists(data)?data:Path.Combine(folder,"vms");
        return Path.Combine(vms,"config",$"leidian{instance}.config");
    }

    static string SetLdFolder(string? path)
    {
        path=path?.Trim().Trim('"');
        if(path is { Length: >0 } && File.Exists(path)) path=Path.GetDirectoryName(path);
        if(path is not { Length: >0 } || !IsLdFolder(path)) throw new ArgumentException(T("이 폴더에 ldconsole.exe와 adb.exe가 없어요.","このフォルダにldconsole.exeとadb.exeがありません。","This folder has no ldconsole.exe and adb.exe."));
        path=Path.GetFullPath(path).TrimEnd('\\');
        Directory.CreateDirectory(Path.GetDirectoryName(LdSetting)!);
        File.WriteAllText(LdSetting,path);
        lock(LdLock) LdCache=(DateTime.UtcNow,path);
        lock(InstancesLock) InstanceCache=(DateTime.MinValue,[]);
        return T($"LD플레이어 폴더를 {path}로 정했어요.",$"LDPlayerのフォルダを{path}にしました。",$"LDPlayer folder set to {path}.");
    }

    static string LdList()
    {
        using var process=Process.Start(new ProcessStartInfo(Ld,"list2") { UseShellExecute=false,RedirectStandardOutput=true,CreateNoWindow=true,StandardOutputEncoding=System.Text.Encoding.UTF8 })!;
        var output=process.StandardOutput.ReadToEnd(); process.WaitForExit(15000);
        return output;
    }

    // ldconsole add/copy exit with the new VM's index, so callers check the VM list instead.
    static async Task<int> AddLd(string name)
    {
        var before=(await AllVmRows()).Keys.ToHashSet();
        await RunLdManage("add","--name",name);
        return await NewVmIndex(before,name);
    }

    static async Task<string> PrepareLd(int local,bool running)
    {
        var path=LdConfig(local);
        var config=System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        var matches=config["advancedSettings.resolution"]?["width"]?.GetValue<int>()==960 && config["advancedSettings.resolution"]?["height"]?.GetValue<int>()==540
            && config["advancedSettings.resolutionDpi"]?.GetValue<int>()==160 && config["advancedSettings.cpuCount"]?.GetValue<int>()==2 && config["advancedSettings.memorySize"]?.GetValue<int>()==3072 && config["basicSettings.adbDebug"]?.GetValue<int>()==1;
        // Lighter rendering: 30 FPS without vertical sync (applied while the VM is off).
        var light=config["basicSettings.fps"]?.GetValue<int>()==LightFps && config["basicSettings.verticalSync"]?.GetValue<bool>()==false;
        if(!matches && running) throw new Exception("Settings differ on a running instance; stop that instance before prepare.");
        if(!matches || !light && !running)
        {
            var backup=$"artifacts/leidian{local}.before.config";
            if(!File.Exists(backup)) File.Copy(path,backup);
            config["advancedSettings.resolution"]=new System.Text.Json.Nodes.JsonObject { ["width"]=960,["height"]=540 };
            config["advancedSettings.resolutionDpi"]=160; config["advancedSettings.cpuCount"]=2; config["advancedSettings.memorySize"]=3072; config["basicSettings.adbDebug"]=1;
            config["basicSettings.fps"]=LightFps; config["basicSettings.verticalSync"]=false;
            await File.WriteAllTextAsync(path,config.ToJsonString(JsonOptions));
        }
        return $"960x540 DPI160, 2 CPU, 3072 MB, {LightFps} FPS, ADB enabled. Original config backed up.";
    }
}
