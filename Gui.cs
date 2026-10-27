using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.AspNetCore.Builder;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

// Desktop GUI: the web UI served on a private loopback port, hosted in a WebView2 window.
internal static partial class Program
{
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd,int attribute,ref int value,int size);
    [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int processId);

    static void ShowGui()
    {
        // async Main runs on an MTA thread; WebView2 requires STA.
        var thread=new Thread(GuiThread);
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
    }

    static void GuiThread()
    {
        // One window per user: a second launch brings the open window to the front and exits.
        using var single=new Mutex(true,@"Local\SekaiMacroGui",out var first);
        using var raise=new EventWaitHandle(false,EventResetMode.AutoReset,@"Local\SekaiMacroGuiRaise");
        if(!first) { AllowSetForegroundWindow(-1); raise.Set(); return; }
        Application.EnableVisualStyles();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        var server=BuildWebServer("127.0.0.1",0);
        server.StartAsync().GetAwaiter().GetResult();
        var url=$"http://127.0.0.1:{BoundPort(server)}/";
        StartInviteWatcher();
        // Remote access stays on across restarts once enabled from the UI.
        if(File.Exists(LanSetting) && int.TryParse(File.ReadAllText(LanSetting),out var lanPort))
            try { LanServer=BuildWebServer("0.0.0.0",lanPort); LanServer.StartAsync().GetAwaiter().GetResult(); }
            catch(Exception ex) { LanServer=null; Console.Error.WriteLine(T("원격 접속 시작 실패: ","リモート接続の開始に失敗: ","Remote access failed to start: ")+ex.Message); }
        var dark=IsSystemDark();
        using var window=new Form
        {
            Text=AppTitle,Width=1320,Height=860,MinimumSize=new System.Drawing.Size(420,560),StartPosition=FormStartPosition.CenterScreen,
            BackColor=dark?System.Drawing.Color.FromArgb(17,17,19):System.Drawing.Color.FromArgb(242,244,246),
            Icon=System.Drawing.Icon.ExtractAssociatedIcon(MacroExecutable())
        };
        window.HandleCreated+=(_,_)=>{ var on=dark?1:0; DwmSetWindowAttribute(window.Handle,20,ref on,sizeof(int)); };
        MainWindow=window;
        ThreadPool.RegisterWaitForSingleObject(raise,(_,_)=>window.BeginInvoke(()=>
        {
            if(window.WindowState==FormWindowState.Minimized) window.WindowState=FormWindowState.Normal;
            window.Activate();
        }),null,-1,false);
        var view=new WebView2 { Dock=DockStyle.Fill,DefaultBackgroundColor=window.BackColor };
        window.Controls.Add(view);
        window.Shown+=async (_,_)=>
        {
            try
            {
                var environment=await CoreWebView2Environment.CreateAsync(null,Path.Combine(Workspace,"local","webview2"));
                await view.EnsureCoreWebView2Async(environment);
                BrowserEnvironment=environment;
                view.CoreWebView2.Settings.AreDevToolsEnabled=false;
                view.CoreWebView2.Settings.IsStatusBarEnabled=false;
                view.CoreWebView2.NewWindowRequested+=(_,e)=>{ e.Handled=true; Process.Start(new ProcessStartInfo(e.Uri) { UseShellExecute=true }); };
                view.CoreWebView2.Navigate(url);
            }
            catch(Exception ex)
            {
                // No WebView2 runtime: fall back to the default browser.
                Process.Start(new ProcessStartInfo(url) { UseShellExecute=true });
                MessageBox.Show(window,T("내장 브라우저를 열 수 없어 기본 브라우저로 열었습니다.\n이 창을 닫으면 제어판도 종료됩니다.\n\n","内蔵ブラウザを開けないため既定のブラウザで開きました。\nこのウィンドウを閉じるとコントロールパネルも終了します。\n\n","The built-in browser could not open, so the default browser was used.\nClosing this window also closes the control panel.\n\n")+ex.Message,AppTitle);
            }
            _=Task.Run(()=>CheckForUpdate(window));
        };
        Application.Run(window);
        if(LanServer!=null) LanServer.StopAsync().GetAwaiter().GetResult();
        server.StopAsync().GetAwaiter().GetResult();
    }

    static Form? MainWindow;
    static CoreWebView2Environment? BrowserEnvironment;
    static Form? DownloadWindow;

    // Gets the game XAPK in a built-in browser window: apkpure serves its download link to a normal browser.
    // Nothing is faked or bypassed; a check page apkpure shows stays in the window for the user.
    static bool OpenGameDownload(string key="pjsekai")
    {
        if(MainWindow==null || BrowserEnvironment==null) return false;
        MainWindow.BeginInvoke(new Action(async()=>
        {
            if(DownloadWindow is { IsDisposed: false }) { DownloadWindow.Activate(); return; }
            var title=key=="qooapp"?"QooApp":T("게임 받기 · apkpure","ゲームを取得 · apkpure","Get the game · apkpure");
            var form=new Form { Text=title,Width=1000,Height=720,StartPosition=FormStartPosition.CenterParent,Icon=MainWindow.Icon };
            DownloadWindow=form;
            var view=new WebView2 { Dock=DockStyle.Fill };
            form.Controls.Add(view);
            form.Show(MainWindow);
            try
            {
                await view.EnsureCoreWebView2Async(BrowserEnvironment);
                view.CoreWebView2.DownloadStarting+=(_,e)=>
                {
                    var name=Path.GetFileName(e.ResultFilePath);
                    var version=System.Text.RegularExpressions.Regex.Match(name,@"([0-9]+(?:\.[0-9]+)+)").Groups[1].Value;
                    Directory.CreateDirectory(ApkCache);
                    var target=KeptPath(key,version,name.EndsWith(".apk",StringComparison.OrdinalIgnoreCase));
                    File.WriteAllText(LatestFile(key),version);
                    // The newest version is already kept: nothing to download.
                    if(File.Exists(target)) { e.Cancel=true; form.BeginInvoke(form.Close); return; }
                    e.ResultFilePath=target+".part"; e.Handled=true;
                    var download=e.DownloadOperation;
                    download.BytesReceivedChanged+=(_,_)=>form.Text=$"{title} · {download.BytesReceived>>20}/{(long)(download.TotalBytesToReceive??0)>>20}MB";
                    download.StateChanged+=(_,_)=>
                    {
                        if(download.State==CoreWebView2DownloadState.Completed) { File.Move(target+".part",target,true); form.Close(); }
                        else if(download.State==CoreWebView2DownloadState.Interrupted) form.Text=$"{title} · {T("실패","失敗","Failed")}: {download.InterruptReason}";
                    };
                };
                view.CoreWebView2.Navigate(ApkUrl(key));
            }
            catch(Exception ex) { form.Text=$"{title} · {ex.Message}"; }
        }));
        return true;
    }

    static bool IsSystemDark()
    {
        try { return Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize","AppsUseLightTheme",1) is 0; }
        catch { return false; }
    }
}
