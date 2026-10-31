using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Windows.Forms;

// Self-update from GitHub releases: on every GUI start, compare the latest release tag with this build's version,
// and on the user's yes download the zip, close, copy it over the install and start again. local/ is never touched.
internal static partial class Program
{
    const string ReleaseAsset="SekaiMacro-win-x64.zip";

    // Only a release install updates itself (the exe sits next to flow.json); a dev build under bin\ is left alone.
    static bool IsReleaseInstall=>string.Equals(Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\'),Path.GetFullPath(Workspace).TrimEnd('\\'),StringComparison.OrdinalIgnoreCase);
    static int UpdateCheckRunning;
    static volatile bool UpdateDownloading,UpdateApplying;

    static void BindUpdateLifetime(Form window)
    {
        window.FormClosing+=(_,e)=>{ if(UpdateDownloading && !UpdateApplying) e.Cancel=true; };
    }

    static DialogResult ConfirmUpdate(Form owner,string message,string notes)
    {
        using var dialog=new Form { Text=AppTitle,Width=700,Height=640,MinimumSize=new(480,360),StartPosition=FormStartPosition.CenterParent,ShowInTaskbar=false,MinimizeBox=false,MaximizeBox=false,Icon=owner.Icon };
        var layout=new TableLayoutPanel { Dock=DockStyle.Fill,Padding=new Padding(18),ColumnCount=1,RowCount=3 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Percent,100)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var intro=new Label { Text=message,AutoSize=true,MaximumSize=new(640,0),Margin=new Padding(0,0,0,14) };
        var changes=new TextBox { Text=notes.Replace("\n","\r\n"),ReadOnly=true,Multiline=true,WordWrap=true,ScrollBars=ScrollBars.Vertical,Dock=DockStyle.Fill,BackColor=System.Drawing.SystemColors.Window };
        var buttons=new FlowLayoutPanel { FlowDirection=FlowDirection.RightToLeft,AutoSize=true,Dock=DockStyle.Fill,Padding=new Padding(0,14,0,0) };
        var cancel=new Button { Text=T("취소","キャンセル","Cancel"),DialogResult=DialogResult.No,AutoSize=true };
        var apply=new Button { Text=T("업데이트","更新","Update"),DialogResult=DialogResult.Yes,AutoSize=true };
        buttons.Controls.Add(cancel); buttons.Controls.Add(apply);
        layout.Controls.Add(intro,0,0); layout.Controls.Add(changes,0,1); layout.Controls.Add(buttons,0,2);
        dialog.Controls.Add(layout); dialog.AcceptButton=apply; dialog.CancelButton=cancel;
        return dialog.ShowDialog(owner);
    }

    static async Task CheckForUpdate(Form window,bool manual=false)
    {
        if(Interlocked.Exchange(ref UpdateCheckRunning,1)!=0) return;
        var updating=false;
        void Notice(string message) { if(!window.IsDisposed) window.Invoke(()=>MessageBox.Show(window,message,AppTitle,MessageBoxButtons.OK,MessageBoxIcon.Information)); }
        try
        {
            if(!IsReleaseInstall)
            {
                if(manual) Notice(T("개발용 빌드입니다. 배포용 앱에서 업데이트할 수 있어요.","開発用ビルドです。配布版アプリで更新できます。","This is a development build. Use a release package to update."));
                return;
            }
            using var http=new HttpClient { Timeout=TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("SekaiMacro/"+CurrentVersion);
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            using var response=await http.GetAsync($"https://api.github.com/repos/{ReleaseRepo}/releases/latest");
            if(response.StatusCode==System.Net.HttpStatusCode.NotFound)
            {
                if(manual) Notice(T("아직 공개된 릴리스가 없어요.","公開済みリリースがありません。","No public release is available yet."));
                return;
            }
            response.EnsureSuccessStatusCode();
            using var release=JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if(window.IsDisposed || window.Disposing) return;
            var root=release.RootElement;
            var tag=root.GetProperty("tag_name").GetString()??"";
            if(!Version.TryParse(tag.TrimStart('v','V'),out var latest)) throw new InvalidDataException("Invalid release version: "+tag);
            if(Normalize(latest)<=CurrentVersion)
            {
                if(manual) Notice(T($"최신 버전입니다 (v{CurrentVersion}).",$"最新バージョンです（v{CurrentVersion}）。",$"You are up to date (v{CurrentVersion})."));
                return;
            }
            var asset=root.GetProperty("assets").EnumerateArray().FirstOrDefault(a=>a.GetProperty("name").GetString()==ReleaseAsset);
            if(asset.ValueKind!=JsonValueKind.Object)
            {
                if(manual) Notice(T("새 버전의 Windows 설치 파일이 아직 없습니다.","新バージョンのWindows用ファイルがまだありません。","The Windows package is not available yet."));
                return;
            }
            var notes=await ReadPendingReleaseNotes(http,root);

            if(AnyMacroBusy() || OtherMacroProcesses())
            {
                Notice(T($"새 버전 {tag}이(가) 나왔어요 (지금 v{CurrentVersion}).\n매크로가 실행 중이라 지금은 업데이트하지 않아요. 모두 멈춘 뒤 다시 열면 업데이트할 수 있어요.",
                    $"新しいバージョン {tag} があります（現在 v{CurrentVersion}）。\nマクロ実行中のため今は更新しません。すべて止めてから開き直すと更新できます。",
                    $"Version {tag} is available (this is v{CurrentVersion}).\nA macro is running, so it is not updated now. Stop them all and reopen to update."));
                return;
            }
            var answer=(DialogResult)window.Invoke(()=>ConfirmUpdate(window,T($"새 버전 {tag}이(가) 나왔어요 (지금 v{CurrentVersion}).\n지금 업데이트할까요? 받은 뒤 자동으로 다시 열려요.",
                $"新しいバージョン {tag} があります（現在 v{CurrentVersion}）。\n今すぐ更新しますか？取得後に自動で開き直します。",
                $"Version {tag} is available (this is v{CurrentVersion}).\nUpdate now? The app reopens by itself afterwards."),notes));
            if(answer!=DialogResult.Yes) return;
            updating=true;
            UpdateDownloading=true;

            var staging=Path.Combine(Workspace,"local","update");
            if(Directory.Exists(staging)) Directory.Delete(staging,true);
            Directory.CreateDirectory(staging);
            var zip=Path.Combine(staging,ReleaseAsset);
            await DownloadUpdate(asset.GetProperty("browser_download_url").GetString()!,zip,window);
            var extracted=Path.Combine(staging,"files");
            ZipFile.ExtractToDirectory(zip,extracted);
            var source=Path.GetDirectoryName(Directory.GetFiles(extracted,"SekaiMacro.exe",SearchOption.AllDirectories).FirstOrDefault()
                ??throw new InvalidDataException("SekaiMacro.exe is missing from the release."))!;
            if(!File.Exists(Path.Combine(source,"flow.json"))) throw new InvalidDataException("flow.json is missing from the release.");
            if(AnyMacroBusy() || OtherMacroProcesses())
            {
                Notice(T("매크로가 실행 중입니다. 모두 멈춘 뒤 다시 확인해 주세요.","マクロ実行中です。すべて停止してから再度確認してください。","A macro is running. Stop it and check again."));
                return;
            }

            var script=Path.Combine(staging,"apply.ps1");
            File.WriteAllText(script,ApplyScript);
            var info=new ProcessStartInfo("powershell.exe") { UseShellExecute=false,CreateNoWindow=true };
            foreach(var arg in new[]{"-NoProfile","-ExecutionPolicy","Bypass","-File",script,"-ProcessId",Environment.ProcessId.ToString(),"-Source",source,"-Target",Workspace,"-Staging",staging})
                info.ArgumentList.Add(arg);
            Process.Start(info);
            UpdateApplying=true;
            window.Invoke(window.Close);
        }
        catch(Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            if(manual || updating) Notice(T("업데이트를 확인하지 못했어요. 인터넷 연결을 확인해 주세요.\n\n","更新を確認できませんでした。接続を確認してください。\n\n","Could not check for updates. Check your connection.\n\n")+ex.Message);
        }
        catch(Exception ex)
        {
            if(!window.IsDisposed) Notice(T("업데이트하지 못했어요.\n\n","更新できませんでした。\n\n","The update failed.\n\n")+ex.Message);
        }
        finally
        {
            UpdateDownloading=false;
            Interlocked.Exchange(ref UpdateCheckRunning,0);
            if(!window.IsDisposed) window.BeginInvoke(()=>window.Text=AppTitle);
        }
    }

    static async Task DownloadUpdate(string url,string path,Form window)
    {
        using var http=new HttpClient { Timeout=TimeSpan.FromMinutes(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SekaiMacro/"+CurrentVersion);
        using var response=await http.GetAsync(url,HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        var total=response.Content.Headers.ContentLength;
        await using var input=await response.Content.ReadAsStreamAsync();
        await using var output=File.Create(path);
        var buffer=new byte[1<<16]; long done=0; int read,shown=-1;
        while((read=await input.ReadAsync(buffer))>0)
        {
            await output.WriteAsync(buffer.AsMemory(0,read)); done+=read;
            var percent=total>0?(int)(done*100/total.Value):-1;
            if(percent==shown) continue;
            shown=percent;
            window.BeginInvoke(()=>window.Text=$"{AppTitle} · {T("업데이트 받는 중","アップデートを取得中","Downloading the update")} {percent}%");
        }
    }

    // Workers run the same exe; replacing it under them would fail.
    static bool OtherMacroProcesses()
    {
        var exe=MacroExecutable();
        return Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)).Any(p=>
        {
            try { return p.Id!=Environment.ProcessId && string.Equals(p.MainModule?.FileName,exe,StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
            finally { p.Dispose(); }
        });
    }

    // ASCII only: paths arrive as arguments, so the Korean install folder never passes through the script's encoding.
    const string ApplyScript="""
        param([int]$ProcessId,[string]$Source,[string]$Target,[string]$Staging)
        # Wait for the app to exit, copy the new release over it (local\ is untouched), then start it again.
        Wait-Process -Id $ProcessId -Timeout 120 -ErrorAction SilentlyContinue
        $ok=$false
        for($i=0;$i -lt 30 -and -not $ok;$i++){
            robocopy $Source $Target /E /XD (Join-Path $Source 'templates') /R:2 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
            $files=$LASTEXITCODE
            robocopy (Join-Path $Source 'templates') (Join-Path $Target 'templates') /MIR /R:2 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
            if($files -lt 8 -and $LASTEXITCODE -lt 8){ $ok=$true } else { Start-Sleep -Seconds 2 }
        }
        Start-Process -FilePath (Join-Path $Target 'SekaiMacro.exe') -WorkingDirectory $Target
        if($ok){ Remove-Item -LiteralPath $Staging -Recurse -Force -ErrorAction SilentlyContinue }
        """;
}
