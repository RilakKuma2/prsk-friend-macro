using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

internal static partial class Program
{
    static WebApplication? LanServer;
    static string LanSetting=>Path.Combine(Workspace,"local","lan-port.txt");

    [DllImport("kernel32.dll")] static extern bool AttachConsole(int processId);

    // `web [--listen 0.0.0.0] [--port 8787]`: standalone web UI. No authentication: anyone on the network can control it.
    static async Task RunWebCommand(string[] args)
    {
        if(OperatingSystem.IsWindows()) AttachConsole(-1);
        var listen=ArgValue(args,"--listen")??"127.0.0.1";
        var port=int.Parse(ArgValue(args,"--port")??"8787");
        var app=BuildWebServer(listen,port);
        await app.StartAsync();
        StartInviteWatcher();
        Console.WriteLine();
        foreach(var url in AccessUrls(app,listen)) Console.WriteLine("  "+url);
        Console.WriteLine("\n  "+T("종료","終了","Quit")+": Ctrl+C\n");
        await app.WaitForShutdownAsync();
    }

    static string? ArgValue(string[] args,string name) { var i=Array.IndexOf(args,name); return i>=0?args.ElementAtOrDefault(i+1):null; }

    // Adapters with a gateway first; host-only virtual adapters (VMware, Hyper-V) have none.
    static IEnumerable<string> LanAddresses()
    {
        var up=NetworkInterface.GetAllNetworkInterfaces()
            .Where(n=>n.OperationalStatus==OperationalStatus.Up && n.NetworkInterfaceType!=NetworkInterfaceType.Loopback).ToList();
        var routed=up.Where(n=>n.GetIPProperties().GatewayAddresses.Any(g=>g.Address.AddressFamily==AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any))).ToList();
        return (routed.Count>0?routed:up).SelectMany(n=>n.GetIPProperties().UnicastAddresses)
            .Where(a=>a.Address.AddressFamily==AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address) && !a.Address.ToString().StartsWith("169.254."))
            .Select(a=>a.Address.ToString()).Distinct();
    }

    static int BoundPort(WebApplication app)=>new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()).Port;

    static List<string> AccessUrls(WebApplication app,string listen)
    {
        var port=BoundPort(app);
        var local=$"http://127.0.0.1:{port}/";
        if(listen is "127.0.0.1" or "localhost") return [local];
        var hosts=listen is "0.0.0.0" or "*" or "+"?LanAddresses().ToList():[listen];
        return [local,..hosts.Select(h=>$"http://{h}:{port}/")];
    }

    static WebApplication BuildWebServer(string listen,int port)
    {
        var builder=WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args=[],ContentRootPath=Workspace });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://{(listen is "0.0.0.0" or "*" or "+"?"0.0.0.0":listen)}:{port}");
        builder.Services.ConfigureHttpJsonOptions(o=>o.SerializerOptions.PropertyNamingPolicy=System.Text.Json.JsonNamingPolicy.CamelCase);
        var app=builder.Build();
        app.UseExceptionHandler(error=>error.Run(async context=>
        {
            var ex=context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
            context.Response.StatusCode=ex is ArgumentException or InvalidOperationException?400:500;
            await context.Response.WriteAsJsonAsync(new { message=ex?.Message??T("오류","エラー","Error") });
        }));

        app.MapGet("/",()=>
        {
            using var stream=typeof(Program).Assembly.GetManifestResourceStream("web.index.html")!;
            using var reader=new StreamReader(stream);
            return Results.Content(reader.ReadToEnd(),"text/html; charset=utf-8");
        });

        app.MapGet("/units/{key}.webp",(string key)=>typeof(Program).Assembly.GetManifestResourceStream($"web.units.{key}.webp") is { } stream
            ?Results.Stream(stream,"image/webp"):Results.NotFound());

        var api=app.MapGroup("/api");
        api.MapGet("/meta",()=>Results.Json(Meta()));
        api.MapGet("/event",async()=>Results.Json(await GetCurrentEvent()));
        // The VM sheet shows one emulator: its folder, version, links and the free space where its VMs live.
        api.MapGet("/vms",(string? emulator)=>
        {
            var key=Emulators.Any(e=>e.Key==emulator)?emulator!:InstalledEmulators.FirstOrDefault()?.Key??Emulators[0].Key;
            return Results.Json(new { Ld=EmulatorInfo(key),Disk=DiskInfo(key),Provision=ProvisionInfo() });
        });
        static object? ProvisionInfo()=>ReadProvision() is { } p?new { p.State,p.Step,Label=ProvisionStepLabel(p.Step),p.Detail,p.Instances,p.Current,p.Error,p.Started,p.Updated,Alive=ProvisionAlive(p) }:null;
        api.MapPost("/vms/provision",(ProvisionRequest request)=>Ok(StartProvision(request)));
        api.MapPost("/vms/game-download",()=>Ok(OpenGameDownload()?T("게임 받기 창을 열었어요.","ゲーム取得ウィンドウを開きました。","Opened the game download window.")
#if WINDOWS
            :T("이 PC의 셐 매크로 창에서만 받을 수 있어요.","このPCのセカマクロのウィンドウでのみ取得できます。","Only the app window on this PC can download it.")));
#else
            // macOS has no app window: the page opens in the default browser and setup picks the file up from Downloads.
            :OpenGamePage()));
#endif
        api.MapPost("/vms/update",()=>Ok(StartProvision(new ProvisionRequest(null,1,null,"none",false,null,false,false,1,null,null,null,true))));
        api.MapPost("/vms/provision/cancel",()=>Ok(CancelProvision()));
        api.MapPost("/instances/{n:int}/remove",async(int n)=>Ok(await RemoveVm(n)));
        api.MapPost("/ldplayer",(HttpContext context,LdPlayerRequest request)=>
            context.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip)?Ok(SetLdFolder(request.Path)):Results.Json(new { message=T("이 PC에서만 바꿀 수 있습니다.","このPCでのみ変更できます。","Only this PC can change this.") },statusCode:403));
        api.MapPost("/language",(LanguageRequest request)=>{ SetLanguage(request.Language); return Results.Json(new { Language }); });
        api.MapGet("/instances",async()=>Results.Json(await GetStatuses()));
        api.MapGet("/instances/{n:int}/log",(int n,int? lines)=>{ RequireInstance(n); return Results.Text(TailLog(n,Math.Clamp(lines??300,10,3000))); });
        api.MapGet("/instances/{n:int}/frame",(int n)=>{ RequireInstance(n); var bytes=LastFrame(n); return bytes==null?Results.NotFound():Results.File(bytes,"image/png"); });
        api.MapGet("/instances/{n:int}/screen",async(int n,bool? save)=>Results.File(await CaptureScreen(n,save==true),"image/png"));
        api.MapPost("/instances/{n:int}/lock",(int n,LockRequest request)=>Ok(SetLock(n,request.Locked)));
        api.MapPost("/instances/{n:int}/start",async(int n,StartRequest request)=>Ok(await StartWorker(n,request)));
        api.MapPost("/instances/{n:int}/stop",(int n)=>Ok(StopWorker(n)));
        api.MapPost("/instances/{n:int}/kill",(int n)=>Ok(KillWorker(n)));
        api.MapPost("/instances/{n:int}/vm/{action}",async(int n,string action)=>Ok(await VmAction(n,action)));
        api.MapPost("/instances/{n:int}/tap",async(int n,TapPoint point)=>
        {
            if(point.X is <0 or >=960 || point.Y is <0 or >=540) throw new ArgumentException(T("좌표가 화면 밖입니다.","座標が画面の外です。","The point is outside the screen."));
            return Ok(await ManualInput(n,$"input tap {point.X} {point.Y}"));
        });
        api.MapPost("/instances/{n:int}/key/{key}",async(int n,string key)=>Ok(await ManualInput(n,"input keyevent "+(key switch { "back"=>4,"home"=>3,"recent"=>187,_=>throw new ArgumentException(T("알 수 없는 키","不明なキー","Unknown key")) }))));
        api.MapGet("/instances/{n:int}/invite",(int n)=>Results.Json(GetInvite(n)));
        api.MapPost("/instances/{n:int}/invite",async(int n,InviteRequest request)=>Ok(await SetInvite(n,request.Code,request.Mode,request.Limit)));
        api.MapPost("/instances/{n:int}/invite/queue",async(int n,InviteRequest request)=>Ok(await QueueInvite(n,request.Code,request.Mode,request.Limit)));
        api.MapPost("/instances/{n:int}/loop",(int n,LoopRequest request)=>Ok(SetLoopTotal(n,request.Total)));
        api.MapPost("/instances/{n:int}/invite/limit",(int n,InviteLimitRequest request)=>Ok(SetInviteLimit(n,request.Index,request.Limit)));
        api.MapPost("/instances/{n:int}/invite/queue/remove",(int n,QueueRemoveRequest request)=>Ok(RemoveQueuedInvite(n,request.Index)));
        api.MapPost("/instances/{n:int}/invite/clear",(int n)=>Ok(ClearInvite(n)));
        api.MapGet("/instances/{n:int}/invites/{file}",(int n,string file)=>InviteImage(n,file) is { } bytes?Results.File(bytes,"image/png"):Results.NotFound());
        api.MapPost("/arrange",async()=>await VmArrange()?Ok(T("창을 정렬했습니다.","ウィンドウを整列しました。","Arranged the windows."))
            :Results.Json(new { message=T("정렬할 수 있는 켜진 VM이 없어요. 블루스택은 창 정렬을 지원하지 않아요.","整列できる起動中のVMがありません。BlueStacksはウィンドウ整列に対応していません。","No running VM can be arranged. BlueStacks cannot arrange windows.") },statusCode:400));
        api.MapPost("/validate",async()=>{ var result=await RunMacro("validate"); return result.Code==0?Ok(T("흐름 검증 통과","フロー検証に合格","Flow validation passed")):Results.Json(new { message=result.Output.Trim() },statusCode:400); });
        api.MapGet("/server",(HttpContext context)=>Results.Json(new
        {
            Local=context.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip),
            Lan=LanServer==null?null:AccessUrls(LanServer,"0.0.0.0").Skip(1)
        }));
        api.MapPost("/server/lan",async(HttpContext context,LanRequest request)=>
        {
            if(context.Connection.RemoteIpAddress is not { } ip || !IPAddress.IsLoopback(ip)) return Results.Json(new { message=T("이 PC에서만 바꿀 수 있습니다.","このPCでのみ変更できます。","Only this PC can change this.") },statusCode:403);
            if(LanServer!=null) { await LanServer.StopAsync(); await LanServer.DisposeAsync(); LanServer=null; }
            if(request.Enabled) { LanServer=BuildWebServer("0.0.0.0",request.Port??8787); await LanServer.StartAsync(); File.WriteAllText(LanSetting,(request.Port??8787).ToString()); }
            else File.Delete(LanSetting);
            return Results.Json(new { Lan=LanServer==null?null:AccessUrls(LanServer,"0.0.0.0").Skip(1) });
        });

        return app;
    }

    record TapPoint(int X,int Y);
    record LanguageRequest(string? Language);
    record LdPlayerRequest(string? Path);
    record LanRequest(bool Enabled,int? Port);
    record InviteRequest(string? Code,string? Mode,int? Limit);
    record QueueRemoveRequest(int Index);
    record LoopRequest(int Total);
    record InviteLimitRequest(int Index,int Limit);
    record LockRequest(bool Locked);
    static IResult Ok(string message)=>Results.Json(new { message });
}
