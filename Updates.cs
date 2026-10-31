using System.Text.Json;
using System.Text.RegularExpressions;

internal static partial class Program
{
    const string ReleaseRepo="RilakKuma2/prsk-friend-macro";
    static Version CurrentVersion=>Normalize(typeof(Program).Assembly.GetName().Version??new Version(0,0,0));
    static Version Normalize(Version v)=>v.Revision>0
        ?new(v.Major,v.Minor,Math.Max(v.Build,0),v.Revision)
        :new(v.Major,v.Minor,Math.Max(v.Build,0));

    sealed record UpdateInfo(string Current,string? Latest,string Status,string? DownloadUrl,string Notes="",bool CanSelfUpdate=false);

    static string? MacAppBundlePath()
    {
        if(!OperatingSystem.IsMacOS()) return null;
        var bundle=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..",".."));
        return bundle.EndsWith(".app",StringComparison.OrdinalIgnoreCase) &&
            File.Exists(Path.Combine(bundle,"Contents","MacOS","SekaiMacro"))?bundle:null;
    }

    static bool AnyMacroBusy()=>AllowedInstances.Keys.Any(IsWorkerRunning) || !PendingWork.IsEmpty || ProvisionAlive(ReadProvision());

    static async Task<UpdateInfo> ReadUpdateInfo()
    {
        using var http=new HttpClient { Timeout=TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SekaiMacro/"+CurrentVersion);
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        using var response=await http.GetAsync($"https://api.github.com/repos/{ReleaseRepo}/releases/latest");
        if(response.StatusCode==System.Net.HttpStatusCode.NotFound) return new(CurrentVersion.ToString(),null,"unreleased",null);
        response.EnsureSuccessStatusCode();
        using var document=JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var info=ParseUpdateInfo(document.RootElement,OperatingSystem.IsMacOS()) with { CanSelfUpdate=MacAppBundlePath()!=null };
        return info.Status=="available"?info with { Notes=await ReadPendingReleaseNotes(http,document.RootElement) }:info;
    }

    static UpdateInfo ParseUpdateInfo(JsonElement release,bool mac)
    {
        var tag=release.GetProperty("tag_name").GetString()??"";
        if(!Version.TryParse(tag.TrimStart('v','V'),out var latest)) throw new InvalidDataException("Invalid release version: "+tag);
        latest=Normalize(latest);
        if(latest<=CurrentVersion) return new(CurrentVersion.ToString(),latest.ToString(),"current",null);
        var name=mac?"SekaiMacro-mac-arm64.dmg":"SekaiMacro-win-x64.zip";
        var asset=release.GetProperty("assets").EnumerateArray().FirstOrDefault(a=>a.GetProperty("name").GetString()==name);
        var url=asset.ValueKind==JsonValueKind.Object?asset.GetProperty("browser_download_url").GetString():null;
        if(url==null) return new(CurrentVersion.ToString(),latest.ToString(),"unavailable",null);
        if(!url.StartsWith($"https://github.com/{ReleaseRepo}/releases/download/",StringComparison.Ordinal))
            throw new InvalidDataException("Unexpected release download URL.");
        var notes=LegacyChangesFromReleaseNotes(release.TryGetProperty("body",out var body)?body.GetString():null);
        return new(CurrentVersion.ToString(),latest.ToString(),"available",url,notes);
    }

    static async Task<string> ReadPendingReleaseNotes(HttpClient http,JsonElement latestRelease)
    {
        var latest=Normalize(Version.Parse(latestRelease.GetProperty("tag_name").GetString()!.TrimStart('v','V')));
        var releases=new List<JsonElement> { latestRelease.Clone() };
        try
        {
            for(var page=1;;page++)
            {
                using var response=await http.GetAsync($"https://api.github.com/repos/{ReleaseRepo}/releases?per_page=100&page={page}");
                response.EnsureSuccessStatusCode();
                using var document=JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                releases.AddRange(document.RootElement.EnumerateArray().Select(r=>r.Clone()));
                if(!response.Headers.TryGetValues("Link",out var links) || !links.Any(l=>l.Contains("rel=\"next\"",StringComparison.Ordinal))) break;
            }
            return PendingReleaseNotes(releases,CurrentVersion,latest);
        }
        catch(Exception ex) when(ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return T("이전 버전의 변경 내역을 불러오지 못했습니다.","以前のバージョンの変更履歴を取得できませんでした。","Earlier release notes could not be loaded.")+"\n\n"+
                PendingReleaseNotes([latestRelease],CurrentVersion,latest);
        }
    }

    static string ChangesFromReleaseNotes(string? body)
    {
        if(string.IsNullOrWhiteSpace(body)) return "";
        var lines=body.Replace("\r\n","\n").Split('\n');
        var changes=new List<string>();
        var section=0;
        var fenced=false;
        foreach(var line in lines)
        {
            if(line.TrimStart().StartsWith("```")) { fenced=!fenced; continue; }
            var heading=fenced?Match.Empty:Regex.Match(line,@"^\s{0,3}(#{1,6})[ \t]*(.*?)[ \t]*#*[ \t]*$");
            if(heading.Success)
            {
                if(section==0)
                {
                    if(Regex.Replace(heading.Groups[2].Value,@"\s+","") is "수정사항" or "변경사항" or "주요변경사항")
                        section=heading.Groups[1].Length;
                    continue;
                }
                if(heading.Groups[1].Length<=section) break;
                continue;
            }
            if(section!=0) changes.Add(line);
        }
        var text=string.Join('\n',changes).Trim();
        text=Regex.Replace(text,@"\[([^\]]+)\]\([^\)]+\)","$1");
        text=text.Replace("**","").Replace("__","").Replace("`","");
        return Regex.Replace(text,@"(?m)^\s*[-*]\s+","• ").Trim();
    }

    static string LegacyChangesFromReleaseNotes(string? body)
    {
        var changes=ChangesFromReleaseNotes(body);
        if(changes.Length>0 || string.IsNullOrWhiteSpace(body)) return changes;
        var cleaned=Regex.Replace(body.Replace("\r\n","\n").Trim(),@"\[([^\]]+)\]\([^\)]+\)","$1")
            .Replace("**","").Replace("__","").Replace("`","");
        return Regex.Replace(cleaned,@"(?m)^\s*[-*]\s+","• ").Trim();
    }

    static string PendingReleaseNotes(IEnumerable<JsonElement> releases,Version current,Version latest)
    {
        var versions=new Dictionary<Version,string>();
        foreach(var release in releases)
        {
            if(release.TryGetProperty("draft",out var draft) && draft.GetBoolean() ||
               release.TryGetProperty("prerelease",out var preview) && preview.GetBoolean()) continue;
            if(!release.TryGetProperty("tag_name",out var tag) || !Version.TryParse(tag.GetString()?.TrimStart('v','V'),out var version)) continue;
            version=Normalize(version);
            if(version<=Normalize(current) || version>Normalize(latest) || versions.ContainsKey(version)) continue;
            var notes=LegacyChangesFromReleaseNotes(release.TryGetProperty("body",out var body)?body.GetString():null);
            versions[version]=notes.Length>0?notes:T("변경사항 설명이 없습니다.","変更内容の説明がありません。","No change description is available.");
        }
        return string.Join("\n\n",versions.OrderByDescending(p=>p.Key).Select(p=>$"v{p.Key} {T("변경사항","変更内容","Changes")}\n{p.Value}"));
    }
}
