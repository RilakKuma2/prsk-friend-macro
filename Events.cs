using System.Text.Json;

// The game's current event, read from public master data. A "<unit> Memories" event belongs to that unit,
// so the UI picks it and warns before running with another.
internal static partial class Program
{
    static readonly string[] EventSources=["https://api.rilaksekai.com/api/events","https://sekai-world.github.io/sekai-master-db-diff/events.json"];
    static string EventCacheFile=>Path.Combine(Workspace,"local","events.json");
    static readonly SemaphoreSlim EventLock=new(1,1);
    static (DateTime Fetched,JsonElement[] Events)? EventCache;

    record CurrentEvent(int Id,string Name,string? Unit,DateTimeOffset StartAt,DateTimeOffset ClosedAt);

    static async Task<CurrentEvent?> GetCurrentEvent()
    {
        var events=await LoadEvents();
        var now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var current=events.Where(e=>Long(e,"startAt")<=now && now<Long(e,"closedAt")).OrderByDescending(e=>Long(e,"startAt")).FirstOrDefault();
        if(current.ValueKind!=JsonValueKind.Object) return null;
        var name=current.TryGetProperty("name",out var n)?n.GetString()??"":"";
        return new CurrentEvent(current.GetProperty("id").GetInt32(),name,MemoriesUnit(current,name),
            DateTimeOffset.FromUnixTimeMilliseconds(Long(current,"startAt")),DateTimeOffset.FromUnixTimeMilliseconds(Long(current,"closedAt")));
    }

    static long Long(JsonElement e,string name)=>e.TryGetProperty(name,out var v) && v.TryGetInt64(out var x)?x:0;

    static string? MemoriesUnit(JsonElement e,string name)
    {
        if(!name.Contains("Memories",StringComparison.Ordinal)) return null;
        var byName=name switch
        {
            _ when name.Contains("Leo/need",StringComparison.OrdinalIgnoreCase) => "leo",
            _ when name.Contains("MORE MORE JUMP",StringComparison.OrdinalIgnoreCase) => "mmj",
            _ when name.Contains("Vivid BAD SQUAD",StringComparison.OrdinalIgnoreCase) => "vbs",
            _ when name.Contains("ワンダーランズ") || name.Contains("Wonderlands",StringComparison.OrdinalIgnoreCase) => "wxs",
            _ when name.Contains("25時") || name.Contains("Nightcord",StringComparison.OrdinalIgnoreCase) => "n25",
            _ => null
        };
        if(byName!=null) return byName;
        // Banner character unit ids run in fours per unit: 1-4 Leo/need ... 17-20 25-ji.
        var id=(int)Long(e,"bannerGameCharacterUnitId");
        return id is >=1 and <=20?new[]{"leo","mmj","vbs","wxs","n25"}[(id-1)/4]:null;
    }

    // Refetched hourly, or as soon as the cached current event has closed; the last copy on disk covers offline starts.
    static async Task<JsonElement[]> LoadEvents()
    {
        await EventLock.WaitAsync();
        try
        {
            if(EventCache is { } c && DateTime.UtcNow-c.Fetched<TimeSpan.FromHours(1) && !CurrentClosed(c.Events)) return c.Events;
            using var http=new HttpClient { Timeout=TimeSpan.FromSeconds(15) };
            foreach(var url in EventSources)
            {
                try
                {
                    var bytes=await http.GetByteArrayAsync(url);
                    var events=ParseEvents(bytes);
                    if(events.Length==0) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(EventCacheFile)!);
                    await File.WriteAllBytesAsync(EventCacheFile,bytes);
                    EventCache=(DateTime.UtcNow,events);
                    return events;
                }
                catch(Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { }
            }
            if(EventCache is { } stale) return stale.Events;
            if(File.Exists(EventCacheFile))
                try { var events=ParseEvents(await File.ReadAllBytesAsync(EventCacheFile)); EventCache=(DateTime.UtcNow-TimeSpan.FromMinutes(55),events); return events; }
                catch(JsonException) { }
            return [];
        }
        finally { EventLock.Release(); }
    }

    static bool CurrentClosed(JsonElement[] events)
    {
        var now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return !events.Any(e=>Long(e,"startAt")<=now && now<Long(e,"closedAt"));
    }

    static JsonElement[] ParseEvents(byte[] json)
    {
        using var document=JsonDocument.Parse(json);
        return document.RootElement.ValueKind==JsonValueKind.Array?document.RootElement.EnumerateArray().Select(e=>e.Clone()).ToArray():[];
    }
}
