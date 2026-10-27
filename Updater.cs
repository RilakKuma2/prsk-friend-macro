using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Windows.Forms;

// Self-update from GitHub releases: on every GUI start, compare the latest release tag with this build's version,
// and on the user's yes download the zip, close, copy it over the install and start again. local/ is never touched.
internal static partial class Program
{
    const string ReleaseRepo="RilakKuma2/prsk-friend-macro";
    const string ReleaseAsset="SekaiMacro-win-x64.zip";

    static Version CurrentVersion=>Normalize(typeof(Program).Assembly.GetName().Version??new Version(0,0,0));
    static Version Normalize(Version v)=>new(v.Major,v.Minor,Math.Max(v.Build,0));

    // Only a release install updates itself (the exe sits next to flow.json); a dev build under bin\ is left alone.
    static bool IsReleaseInstall=>string.Equals(Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\'),Path.GetFullPath(Workspace).TrimEnd('\\'),StringComparison.OrdinalIgnoreCase);

    static async Task CheckForUpdate(Form window)
    {
        if(!IsReleaseInstall) return;
        try
        {
            using var http=new HttpClient { Timeout=TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("SekaiMacro/"+CurrentVersion);
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            using var release=JsonDocument.Parse(await http.GetStringAsync($"https://api.github.com/repos/{ReleaseRepo}/releases/latest"));
            var root=release.RootElement;
            var tag=root.GetProperty("tag_name").GetString()??"";
            if(!Version.TryParse(tag.TrimStart('v','V'),out var latest) || Normalize(latest)<=CurrentVersion) return;
            var asset=root.GetProperty("assets").EnumerateArray().FirstOrDefault(a=>a.GetProperty("name").GetString()==ReleaseAsset);
            if(asset.ValueKind!=JsonValueKind.Object) return;
            var notes=root.TryGetProperty("body",out var body)?body.GetString()?.Trim():null;
            if(notes is { Length: >600 }) notes=notes[..600]+"…";

            if(OtherMacroProcesses())
            {
                window.Invoke(()=>MessageBox.Show(window,T($"새 버전 {tag}이(가) 나왔어요 (지금 v{CurrentVersion}).\n매크로가 실행 중이라 지금은 업데이트하지 않아요. 모두 멈춘 뒤 다시 열면 업데이트할 수 있어요.",
                    $"新しいバージョン {tag} があります（現在 v{CurrentVersion}）。\nマクロ実行中のため今は更新しません。すべて止めてから開き直すと更新できます。",
                    $"Version {tag} is available (this is v{CurrentVersion}).\nA macro is running, so it is not updated now. Stop them all and reopen to update."),AppTitle,MessageBoxButtons.OK,MessageBoxIcon.Information));
                return;
            }
            var answer=(DialogResult)window.Invoke(()=>MessageBox.Show(window,T($"새 버전 {tag}이(가) 나왔어요 (지금 v{CurrentVersion}).\n지금 업데이트할까요? 받은 뒤 자동으로 다시 열려요.",
                $"新しいバージョン {tag} があります（現在 v{CurrentVersion}）。\n今すぐ更新しますか？取得後に自動で開き直します。",
                $"Version {tag} is available (this is v{CurrentVersion}).\nUpdate now? The app reopens by itself afterwards.")+(notes is { Length: >0 }?"\n\n"+notes:""),AppTitle,MessageBoxButtons.YesNo,MessageBoxIcon.Information));
            if(answer!=DialogResult.Yes) return;

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

            var script=Path.Combine(staging,"apply.ps1");
            File.WriteAllText(script,ApplyScript);
            var info=new ProcessStartInfo("powershell.exe") { UseShellExecute=false,CreateNoWindow=true };
            foreach(var arg in new[]{"-NoProfile","-ExecutionPolicy","Bypass","-File",script,"-ProcessId",Environment.ProcessId.ToString(),"-Source",source,"-Target",Workspace,"-Staging",staging})
                info.ArgumentList.Add(arg);
            Process.Start(info);
            window.Invoke(window.Close);
        }
        catch(Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            // Offline or rate-limited: checked again on the next start.
        }
        catch(Exception ex)
        {
            window.Invoke(()=>{ window.Text=AppTitle; MessageBox.Show(window,T("업데이트하지 못했어요.\n\n","更新できませんでした。\n\n","The update failed.\n\n")+ex.Message,AppTitle,MessageBoxButtons.OK,MessageBoxIcon.Warning); });
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
