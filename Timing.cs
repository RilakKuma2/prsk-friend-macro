using System.Text.Json;

// Per-instance turn & stage runtime log in artifacts/timing.json so the detail panel can show
// section durations, total turn elapsed time, and inline red restart-recovery spans.
internal static partial class Program
{
    record TimingEntry(string Kind,string? Stage,string? Detail,DateTimeOffset Started,DateTimeOffset? Ended);
    record TimingTurn(int LoopIndex,int LoopTotal,string Mode,DateTimeOffset Started,DateTimeOffset? Ended,string Status,List<TimingEntry> Entries);
    record TimingData(TimingTurn? Current,List<TimingTurn> History);

    const string TimingFile="timing.json";
    static readonly object TimingLock=new();

    static TimingData ReadTiming(string artifacts)
    {
        var path=Path.Combine(artifacts,TimingFile);
        for(var attempt=0;attempt<3;attempt++)
        {
            try
            {
                if(!File.Exists(path)) return new(null,[]);
                return JsonSerializer.Deserialize<TimingData>(File.ReadAllText(path))??new(null,[]);
            }
            catch(IOException) when(attempt<2) { Thread.Sleep(40); }
            catch { return new(null,[]); }
        }
        return new(null,[]);
    }

    static void WriteTiming(TimingData data)
    {
        Directory.CreateDirectory("artifacts");
        var pending=Path.Combine("artifacts",TimingFile+".pending");
        var target=Path.Combine("artifacts",TimingFile);
        File.WriteAllText(pending,JsonSerializer.Serialize(data,JsonOptions));
        File.Move(pending,target,true);
    }

    static void UpdateTiming(Func<TimingData,DateTimeOffset,TimingData?> transform)
    {
        lock(TimingLock)
        {
            try
            {
                var data=ReadTiming("artifacts");
                if(transform(data,DateTimeOffset.Now) is { } next) WriteTiming(next);
            }
            catch { }
        }
    }

    static List<TimingEntry> CloseOpenEntries(List<TimingEntry> entries,DateTimeOffset at)
    {
        var list=new List<TimingEntry>(entries.Count);
        foreach(var e in entries) list.Add(e.Ended==null?e with { Ended=at }:e);
        return list;
    }

    static void StartTimingTurn(int loopIndex,int loopTotal,string mode,bool resumeExisting=false)
    {
        UpdateTiming((data,now)=>
        {
            if(resumeExisting && data.Current is { } existing && existing.Status is "stopped" or "failed" or "running")
            {
                var closedAt=existing.Ended??now;
                var entries=CloseOpenEntries(existing.Entries,closedAt);
                return data with { Current=existing with { LoopIndex=loopIndex,LoopTotal=loopTotal,Ended=null,Status="running",Entries=entries } };
            }
            var history=new List<TimingTurn>(data.History);
            if(data.Current is { } prev)
            {
                var prevEnd=prev.Ended??now;
                var prevEntries=CloseOpenEntries(prev.Entries,prevEnd);
                var prevStatus=prev.Status=="running"?"stopped":prev.Status;
                history.Insert(0,prev with { Ended=prevEnd,Status=prevStatus,Entries=prevEntries });
                if(history.Count>10) history.RemoveRange(10,history.Count-10);
            }
            return new(new TimingTurn(loopIndex,loopTotal,mode,now,null,"running",[]),history);
        });
    }

    static void BeginTimingStage(string stage)
    {
        UpdateTiming((data,now)=>
        {
            var current=data.Current??new TimingTurn(LoopIndex,Math.Max(LoopTotal,1),"run",now,null,"running",[]);
            var entries=new List<TimingEntry>(current.Entries);
            if(entries.Count>0 && entries[^1] is { Kind:"stage",Ended:null } open && open.Stage==stage) return null;
            entries=CloseOpenEntries(entries,now);
            entries.Add(new TimingEntry("stage",stage,null,now,null));
            return data with { Current=current with { Ended=null,Status="running",Entries=entries } };
        });
    }

    static void EndTimingStage(string stage)
    {
        UpdateTiming((data,now)=>
        {
            if(data.Current is not { } current) return null;
            var entries=new List<TimingEntry>(current.Entries);
            var changed=false;
            for(var i=entries.Count-1;i>=0;i--)
            {
                if(entries[i] is { Kind:"stage",Ended:null } e && e.Stage==stage)
                { entries[i]=e with { Ended=now }; changed=true; break; }
            }
            return changed?data with { Current=current with { Entries=entries } }:null;
        });
    }

    static void BeginTimingRestart(string detail,string? stage=null)
    {
        UpdateTiming((data,now)=>
        {
            var current=data.Current??new TimingTurn(LoopIndex,Math.Max(LoopTotal,1),"run",now,null,"running",[]);
            var entries=new List<TimingEntry>(current.Entries);
            if(entries.Count>0 && entries[^1] is { Kind:"restart",Ended:null }) return null;
            entries=CloseOpenEntries(entries,now);
            entries.Add(new TimingEntry("restart",stage??ProgressStage,detail,now,null));
            return data with { Current=current with { Entries=entries } };
        });
    }

    static void EndTimingRestart(string? resumeStage=null)
    {
        UpdateTiming((data,now)=>
        {
            if(data.Current is not { } current) return null;
            var entries=new List<TimingEntry>(current.Entries);
            var changed=false;
            for(var i=entries.Count-1;i>=0;i--)
            {
                if(entries[i] is { Kind:"restart",Ended:null } e)
                { entries[i]=e with { Ended=now }; changed=true; break; }
            }
            if(!changed) return null;
            if(resumeStage!=null) entries.Add(new TimingEntry("stage",resumeStage,null,now,null));
            return data with { Current=current with { Entries=entries } };
        });
    }

    static void FinishTimingTurn(string status)
    {
        UpdateTiming((data,now)=>
        {
            if(data.Current is not { Status:"running" } current) return null;
            var entries=CloseOpenEntries(current.Entries,now);
            return data with { Current=current with { Ended=now,Status=status,Entries=entries } };
        });
    }
}
