using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using Rectangle=System.Drawing.Rectangle;

// macOS: the web UI on a fixed local port, shown by SekaiMacro.app's own window (mac/App.swift, which starts
// `gui --window`) or, from a source checkout, in the default browser; and Japanese OCR through Apple's Vision
// framework (mac/ocr.swift, shipped compiled in the app and compiled on first use otherwise).
internal static partial class Program
{
    // Remote access keeps 8787, as on Windows.
    const int MacPanelPort=8786;

    static void ShowGui()=>RunMacPanel(!Environment.GetCommandLineArgs().Contains("--window")).GetAwaiter().GetResult();

    static async Task RunMacPanel(bool openBrowser)
    {
        var url=$"http://127.0.0.1:{MacPanelPort}/";
        // A second launch shows the panel that is already running.
        if(!PortFree(MacPanelPort)) { if(openBrowser) OpenInBrowser(url); return; }
        var server=BuildWebServer("127.0.0.1",MacPanelPort);
        await server.StartAsync();
        StartInviteWatcher();
        // Remote access stays on across restarts once enabled from the UI.
        if(File.Exists(LanSetting) && int.TryParse(File.ReadAllText(LanSetting),out var lanPort))
            try { LanServer=BuildWebServer("0.0.0.0",lanPort); await LanServer.StartAsync(); }
            catch(Exception ex) { LanServer=null; Console.Error.WriteLine(T("원격 접속 시작 실패: ","リモート接続の開始に失敗: ","Remote access failed to start: ")+ex.Message); }
        Console.WriteLine($"\n  {AppTitle}: {url}\n  {T("종료","終了","Quit")}: Ctrl+C\n");
        if(openBrowser) OpenInBrowser(url);
        await server.WaitForShutdownAsync();
        if(LanServer!=null) await LanServer.StopAsync();
    }

    static bool PortFree(int port)
    {
        try { var listener=new TcpListener(IPAddress.Loopback,port); listener.Start(); listener.Stop(); return true; }
        catch(SocketException) { return false; }
    }
    static void OpenInBrowser(string url) { try { Process.Start(new ProcessStartInfo(url) { UseShellExecute=true }); } catch { } }

    // No built-in browser window: FetchLatest opens the download page in the default browser and watches Downloads.
    static bool OpenGameDownload(string key="pjsekai")=>false;

    static string OcrTool=>Path.Combine(Workspace,"local","bin","sekai-ocr");
    static readonly SemaphoreSlim OcrBuild=new(1,1);
    static async Task<string> EnsureOcrTool()
    {
        var shipped=Path.Combine(AppContext.BaseDirectory,"sekai-ocr");
        if(File.Exists(shipped)) return shipped;
        var source=Path.Combine(Workspace,"mac","ocr.swift");
        await OcrBuild.WaitAsync();
        try
        {
            if(File.Exists(OcrTool) && File.GetLastWriteTimeUtc(OcrTool)>=File.GetLastWriteTimeUtc(source)) return OcrTool;
            Directory.CreateDirectory(Path.GetDirectoryName(OcrTool)!);
            // Workers may start together: each compiles to its own file, then renames it into place.
            var built=$"{OcrTool}.{Environment.ProcessId}";
            try { await RunLong(TimeSpan.FromMinutes(5),"/usr/bin/xcrun","swiftc","-O","-o",built,source); }
            catch(Exception ex) { throw new Exception(T("OCR 도구를 만들지 못했어요. 터미널에서 xcode-select --install로 개발자 도구를 설치하세요. ","OCRツールを作れませんでした。ターミナルで xcode-select --install を実行して開発者ツールを入れてください。","Could not build the OCR tool. Install the developer tools with xcode-select --install. ")+ex.Message); }
            File.Move(built,OcrTool,true);
            return OcrTool;
        }
        finally { OcrBuild.Release(); }
    }
    // The tool enlarges the image by scale first, as OCR reads small text better that way.
    static async Task<string> Ocr(string path,int scale=1)=>(await Run(await EnsureOcrTool(),Path.GetFullPath(path),scale.ToString())).Trim();
    static async Task<string> OcrRegion(Bitmap screen,Rectangle region,int scale=2)
    {
        using var crop=screen.Clone(region,PixelFormat.Format24bppRgb);
        var path="artifacts/ocr-region.png"; crop.Save(path,ImageFormat.Png);
        return Regex.Replace(await Ocr(path,scale),@"\s+","");
    }
}
