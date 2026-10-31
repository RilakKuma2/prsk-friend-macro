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

    static async Task ApplyMacSelfUpdate(string bundle)
    {
        if(AnyMacroBusy())
            throw new InvalidOperationException(T(
                "매크로나 VM 세팅이 실행 중이라 지금은 업데이트할 수 없어요. 모두 멈춘 뒤 다시 시도하세요.",
                "マクロまたはVM準備が実行中のため今は更新できません。すべて止めてからやり直してください。",
                "A macro or VM setup is running. Stop all jobs before updating."));
        var info=await ReadUpdateInfo();
        if(info.Status!="available" || info.DownloadUrl is not { } url || !url.EndsWith("/SekaiMacro-mac-arm64.dmg",StringComparison.Ordinal))
            throw new InvalidOperationException(T("설치할 새 버전이 없습니다.","インストールできる新しいバージョンがありません。","No new version is available to install."));
        var staging=Path.Combine(Workspace,"local","update");
        var mnt=Path.Combine(staging,"mnt");
        try { if(Directory.Exists(mnt)) await Run("/usr/bin/hdiutil","detach",mnt,"-force"); } catch { }
        if(Directory.Exists(staging)) Directory.Delete(staging,true);
        Directory.CreateDirectory(mnt);
        var dmg=Path.Combine(staging,"SekaiMacro-mac-arm64.dmg");
        using(var http=new HttpClient { Timeout=TimeSpan.FromMinutes(15) })
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("SekaiMacro/"+CurrentVersion);
            using var response=await http.GetAsync(url,HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            await using var input=await response.Content.ReadAsStreamAsync();
            await using var output=File.Create(dmg);
            await input.CopyToAsync(output);
        }
        await Run("/usr/bin/hdiutil","attach","-nobrowse","-readonly","-mountpoint",mnt,dmg);
        var sourceApp=Path.Combine(mnt,"SekaiMacro.app");
        if(!File.Exists(Path.Combine(sourceApp,"Contents","MacOS","SekaiMacro")))
        {
            try { await Run("/usr/bin/hdiutil","detach",mnt,"-force"); } catch { }
            throw new InvalidDataException("DMG 안에서 SekaiMacro.app을 확인하지 못했습니다.");
        }
        if(AnyMacroBusy())
        {
            try { await Run("/usr/bin/hdiutil","detach",mnt,"-force"); } catch { }
            throw new InvalidOperationException(T(
                "매크로나 VM 세팅이 실행 중입니다. 모두 멈춘 뒤 다시 시도하세요.",
                "マクロまたはVM準備が実行中です。すべて止めてからやり直してください。",
                "A macro or VM setup is running. Stop all jobs before updating."));
        }
        var script=Path.Combine(staging,"apply-mac.sh");
        await File.WriteAllTextAsync(script,"""
            #!/bin/sh
            BUNDLE="$1"
            MNT="$2"
            STAGING="$3"
            PANEL_PID="$4"
            for i in 1 2 3 4 5 6 7 8 9 10 11 12 13 14 15 16 17 18 19 20; do
              kill -0 "$PANEL_PID" 2>/dev/null || break
              sleep 0.25
            done
            for i in 1 2 3 4 5 6 7 8 9 10 11 12; do
              pgrep -f "$BUNDLE/Contents/MacOS/SekaiMacro" >/dev/null 2>&1 || break
              sleep 0.25
            done
            rm -rf "$BUNDLE"
            cp -R "$MNT/SekaiMacro.app" "$BUNDLE"
            xattr -cr "$BUNDLE" 2>/dev/null || true
            /usr/bin/hdiutil detach "$MNT" -force >/dev/null 2>&1 || true
            rm -rf "$STAGING" 2>/dev/null || true
            /usr/bin/open "$BUNDLE"
            """);
        var updater=new ProcessStartInfo("/bin/sh") { UseShellExecute=false,CreateNoWindow=true };
        foreach(var arg in new[]{script,bundle,mnt,staging,Environment.ProcessId.ToString()}) updater.ArgumentList.Add(arg);
        Process.Start(updater)?.Dispose();
        _=Task.Run(async()=>{ await Task.Delay(350); Environment.Exit(0); });
    }
}
