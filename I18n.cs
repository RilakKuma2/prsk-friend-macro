// UI language (ko/ja/en) for server messages and control logs; saved in local/language.txt and shared by every client.
// The web UI keeps its own strings and reads the language from /api/meta.
internal static partial class Program
{
    static readonly string[] Languages=["ko","ja","en"];
    static string LanguageFile=>Path.Combine(Workspace,"local","language.txt");
    static string? language;
    static string Language
    {
        get
        {
            if(language!=null) return language;
            try { var saved=File.Exists(LanguageFile)?File.ReadAllText(LanguageFile).Trim():null; language=Languages.Contains(saved)?saved:"ko"; }
            catch { language="ko"; }
            return language!;
        }
    }
    static void SetLanguage(string? value)
    {
        if(!Languages.Contains(value)) throw new ArgumentException("Unknown language: "+value);
        Directory.CreateDirectory(Path.GetDirectoryName(LanguageFile)!);
        File.WriteAllText(LanguageFile,value);
        language=value;
    }

    static string AppTitle=>T("셐 매크로","セカマクロ","Sekai Macro");
    static string T(string ko,string ja,string en)=>Language switch { "ja"=>ja,"en"=>en,_=>ko };

    static readonly Dictionary<string,(string Ja,string En)> StageTitles=new()
    {
        ["onboarding"]=("初期化・チュートリアル・報酬","Reset, tutorial and rewards"),
        ["gacha"]=("ガチャ2種","Two gacha pulls"),
        ["deck"]=("おすすめ編成","Recommended deck"),
        ["music"]=("楽曲購入","Buy songs"),
        ["boost"]=("ライブボーナス","Live bonus"),
        ["invitation"]=("友達招待","Friend invitation"),
        ["autolive"]=("連続オートライブ","Repeated auto live"),
        ["eventstory"]=("解放通知整理・イベントストーリー連続閲覧","Clear unlock notices, read event stories")
    };
    static string StageTitle(Stage stage)=>StageTitles.TryGetValue(stage.Name,out var t)?T(stage.Title??stage.Name,t.Ja,t.En):stage.Title??stage.Name;

    static object[] UnitLabels()=>
    [
        new { Key="mmj",Label=T("모모점","モモジャン","MMJ") },new { Key="leo",Label=T("레오니","レオニ","Leo/need") },
        new { Key="vbs",Label=T("비배스","ビビバス","VBS") },new { Key="wxs",Label=T("원더쇼","ワンダショ","WxS") },new { Key="n25",Label=T("니고","ニーゴ","N25") }
    ];
    // How messages name a VM: by its number in the first emulator's block (LDPlayer, or BlueStacks Air), else with its
    // emulator and its number there ("뮤뮤 플레이어 0번", "MuMu Player #0").
    static string VmNo(int instance)=>Emulators.FirstOrDefault(e=>instance>=e.Base && instance<e.Base+EmulatorBlock) is { Base: >0 } e?$"{EmulatorLabel(e.Key)} {instance-e.Base}":instance.ToString();
    static string VmHash(int instance)=>Emulators.FirstOrDefault(e=>instance>=e.Base && instance<e.Base+EmulatorBlock) is { Base: >0 } e?$"{EmulatorLabel(e.Key)} #{instance-e.Base}":$"#{instance}";
    static string InstanceLabel(int instance)=>T($"{VmNo(instance)}번",$"{VmNo(instance)}番",VmHash(instance));
}
