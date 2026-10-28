using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

// BlueStacks Air on macOS. It has no console tool like ldconsole: instances are read from bluestacks.conf, started
// with `--instance <id>`, recognized by their processes and reached over ADB at 127.0.0.1:<adb_port>.
// Instance numbers follow the multi-instance manager's names: <image> ("BlueStacks Air") is 0, <image>_<n> is n.
internal static partial class Program
{
    const string BlueStacksData="/Users/Shared/Library/Application Support/BlueStacks";
    const string ValidatedBlueStacksVersion="5.21.790";
    const string BlueStacksSiteUrl="https://www.bluestacks.com/mac";
    static string BlueStacksConf=>Path.Combine(BlueStacksData,"bluestacks.conf");
    static string? BlueStacksApp=>new[]{"/Applications/BlueStacks.app",Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"Applications","BlueStacks.app")}.FirstOrDefault(Directory.Exists);
    static string BlueStacksRequired=>BlueStacksApp??throw new InvalidOperationException(T("블루스택을 찾지 못했어요. BlueStacks Air를 응용 프로그램 폴더에 설치하세요.","BlueStacksが見つかりません。BlueStacks Airをアプリケーションフォルダにインストールしてください。","BlueStacks was not found. Install BlueStacks Air in Applications."));

    // An adb from the Android SDK or Homebrew is preferred, so the user's own adb server is shared; BlueStacks ships hd-adb.
    static string BlueStacksAdb
    {
        get
        {
            var home=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string?[] candidates=[Environment.GetEnvironmentVariable("ANDROID_HOME") is { Length: >0 } sdk?Path.Combine(sdk,"platform-tools","adb"):null,
                "/opt/homebrew/bin/adb","/usr/local/bin/adb",Path.Combine(home,"Library","Android","sdk","platform-tools","adb"),
                BlueStacksApp is { } app?Path.Combine(app,"Contents","MacOS","hd-adb"):null];
            return candidates.FirstOrDefault(File.Exists)??throw new InvalidOperationException(T("adb를 찾지 못했어요.","adbが見つかりません。","adb was not found."));
        }
    }

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

    record BlueStacksInstance(int Index,string Id,string Name,int Port);
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
            var port=int.TryParse(conf.GetValueOrDefault($"bst.instance.{id}.status.adb_port")??conf.GetValueOrDefault($"bst.instance.{id}.adb_port"),out var p)?p:0;
            // Commas would split the list2-style row.
            list.Add(new(index,id,conf[$"bst.instance.{id}.display_name"].Replace(',',' '),port));
        }
        return list;
    }
    static BlueStacksInstance BlueStacksInstanceOf(int instance)=>BlueStacksInstances().SingleOrDefault(i=>i.Index==instance)
        ??throw new InvalidOperationException(T($"블루스택에 {instance}번 인스턴스가 없어요.",$"BlueStacksに{instance}番のインスタンスがありません。",$"BlueStacks has no instance #{instance}."));
    static string BlueStacksSerial(int instance)=>$"127.0.0.1:{BlueStacksInstances().SingleOrDefault(i=>i.Index==instance)?.Port??0}";

    // ---- Processes

    // Instance id -> pid. The app opened without --instance (from the Dock, or after an update) runs the first image's instance.
    static Dictionary<string,int> BlueStacksProcesses()
    {
        var running=new Dictionary<string,int>();
        if(BlueStacksApp is not { } app) return running;
        var exe=Path.Combine(app,"Contents","MacOS","BlueStacks");
        var main=ReadBlueStacksConf().GetValueOrDefault("bst.installed_images")?.Split(',')[0] is { Length: >0 } image?image:"Tiramisu64";
        foreach(var line in RunText("/bin/ps","-axo","pid=,args=").Split('\n'))
        {
            var row=line.Trim(); var space=row.IndexOf(' ');
            if(space<0 || !int.TryParse(row[..space],out var pid)) continue;
            var args=row[(space+1)..];
            if(!args.StartsWith(exe,StringComparison.Ordinal) || args.Length>exe.Length && args[exe.Length]!=' ') continue;
            var id=Regex.Match(args,@"--instance\s+(\S+)") is { Success: true } m?m.Groups[1].Value:main;
            running.TryAdd(id,pid);
        }
        return running;
    }

    static Dictionary<string,string> AdbDevices()=>RunText(BlueStacksAdb,"devices").Split('\n').Skip(1)
        .Select(l=>l.Trim().Split('\t')).Where(p=>p.Length==2).GroupBy(p=>p[0]).ToDictionary(g=>g.Key,g=>g.First()[1]);
    static void ConnectBlueStacks(string serial,string? state=null)
    {
        state??=AdbDevices().GetValueOrDefault(serial);
        if(state=="device") return;
        // A restarted instance leaves an offline entry that adb does not reconnect by itself.
        if(state!=null) RunText(BlueStacksAdb,"disconnect",serial);
        RunText(BlueStacksAdb,"connect",serial);
    }

    // list2-style rows. Running instances are connected to adb here, so `adb -s` reaches them as soon as they are seen.
    static string BlueStacksList()
    {
        if(BlueStacksApp==null) return "";
        var running=BlueStacksProcesses();
        Dictionary<string,string>? devices=null;
        var rows=new List<string>();
        foreach(var instance in BlueStacksInstances())
        {
            var on=running.TryGetValue(instance.Id,out var pid);
            if(on && instance.Port>0)
                try { devices??=AdbDevices(); var serial=$"127.0.0.1:{instance.Port}"; ConnectBlueStacks(serial,devices.GetValueOrDefault(serial)); }
                catch(Exception ex) { Console.Error.WriteLine("adb connect failed: "+ex.Message); }
            rows.Add($"{instance.Index},{instance.Name},0,0,{(on?1:0)},{(on?pid:-1)},-1");
        }
        return string.Join("\n",rows);
    }

    static async Task BlueStacksLaunch(int instance)
    {
        var target=BlueStacksInstanceOf(instance);
        if(BlueStacksProcesses().ContainsKey(target.Id)) return;
        await Run("/usr/bin/open","-n","-a",BlueStacksRequired,"--args","--instance",target.Id);
    }
    // Like closing the instance's window: BlueStacks saves the instance and exits.
    static async Task BlueStacksQuit(int instance)
    {
        var target=BlueStacksInstanceOf(instance);
        if(!BlueStacksProcesses().TryGetValue(target.Id,out var pid)) return;
        await Run("/bin/kill","-TERM",pid.ToString());
        try { RunText(BlueStacksAdb,"disconnect",BlueStacksSerial(instance)); } catch { }
    }

    // ---- Settings

    // The macro needs 960x540 at 160 DPI with ADB on. A running BlueStacks rewrites bluestacks.conf (it holds every
    // instance), so the settings are only checked here and changed in BlueStacks' own settings window.
    static void RequireBlueStacksSettings(int instance)
    {
        var target=BlueStacksInstanceOf(instance);
        var conf=ReadBlueStacksConf();
        string Value(string key)=>conf.GetValueOrDefault($"bst.instance.{target.Id}.{key}")??"";
        var wrong=new List<string>();
        if(Value("fb_width")!="960" || Value("fb_height")!="540") wrong.Add(T("해상도 960×540","解像度 960×540","resolution 960×540"));
        if(Value("dpi")!="160") wrong.Add("DPI 160");
        if(conf.GetValueOrDefault("bst.enable_adb_access")!="1") wrong.Add(T("ADB 켜기","ADBをオン","ADB on"));
        if(wrong.Count>0) throw new InvalidOperationException(T($"블루스택 {target.Name} 설정을 바꿔야 해요: {string.Join(", ",wrong)}. 블루스택 설정의 디스플레이·고급 탭에서 바꾼 뒤 다시 시도하세요.",
            $"BlueStacks {target.Name} の設定を変える必要があります: {string.Join("、",wrong)}。BlueStacksの設定のディスプレイ・詳細タブで変えてからやり直してください。",
            $"BlueStacks {target.Name} needs: {string.Join(", ",wrong)}. Change them in BlueStacks settings (Display, Advanced) and try again."));
    }

    static string PrepareBlueStacks(int instance) { RequireBlueStacksSettings(instance); return "960x540 DPI160, ADB enabled."; }
    static string BlueStacksVmDir(int instance)=>Path.Combine(BlueStacksData,"Engine",BlueStacksInstanceOf(instance).Id);

    static string? BlueStacksVersionCache;
    static string? BlueStacksVersion()
    {
        if(BlueStacksVersionCache!=null || BlueStacksApp is not { } app) return BlueStacksVersionCache;
        try { BlueStacksVersionCache=RunText("/usr/bin/plutil","-extract","CFBundleShortVersionString","raw","-o","-",Path.Combine(app,"Contents","Info.plist")).Trim(); }
        catch { }
        return BlueStacksVersionCache;
    }
    static string OpenGamePage()
    {
        try { Process.Start(new ProcessStartInfo(GamePageUrl) { UseShellExecute=true }); } catch { }
        return T("기본 브라우저에서 apkpure 게임 페이지를 열었어요. 받은 파일은 다운로드 폴더에서 가져가요.","既定のブラウザでapkpureのゲームページを開きました。取得したファイルはダウンロードフォルダから使います。","Opened the apkpure game page in the default browser. The downloaded file is taken from Downloads.");
    }
    static object BlueStacksInfo()=>new { Kind="bluestacks",Folder=BlueStacksApp,Version=BlueStacksVersion(),VersionUrl=(string?)null,
        Validated=ValidatedBlueStacksVersion,ValidatedUrl=(string?)null,LatestUrl=BlueStacksSiteUrl,Site=BlueStacksSiteUrl,GamePage=GamePageUrl };
}
