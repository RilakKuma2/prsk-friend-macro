using System.Diagnostics;
using System.Text;

// The emulator behind the macro: LDPlayer 9 on Windows, BlueStacks Air on macOS. VM lists keep ldconsole's
// list2 layout for both (index,name,top window,bind window,android started,pid,vbox pid), so callers read row[1]
// as the name and row[4]=="1" as running.
internal static partial class Program
{
    static bool IsBlueStacks=>OperatingSystem.IsMacOS();
    static string Adb=>IsBlueStacks?BlueStacksAdb:Path.Combine(LdRequired,"adb.exe");
    static string SerialOf(int instance)=>IsBlueStacks?BlueStacksSerial(instance):"emulator-"+(5554+instance*2);
    // Shown in the UI and checked before VM setup; null when the emulator is not installed.
    static string? EmulatorFolder=>IsBlueStacks?BlueStacksApp:LdFolder;
    static object EmulatorInfo()=>IsBlueStacks?BlueStacksInfo():LdInfo();

    static async Task<string> VmList()=>IsBlueStacks?await Task.Run(BlueStacksList):await Run(Ld,"list2");
    static string VmListNow()
    {
        if(IsBlueStacks) return BlueStacksList();
        using var process=Process.Start(new ProcessStartInfo(Ld,"list2") { UseShellExecute=false,RedirectStandardOutput=true,CreateNoWindow=true,StandardOutputEncoding=Encoding.UTF8 })!;
        var output=process.StandardOutput.ReadToEnd(); process.WaitForExit(15000);
        return output;
    }
    static async Task<string[]?> VmRow(int instance)=>(await VmList()).Split('\n').Select(x=>x.Trim().Split(',')).SingleOrDefault(x=>x.Length>=7 && x[0]==instance.ToString());

    static async Task VmLaunch(int instance) { if(IsBlueStacks) await BlueStacksLaunch(instance); else await Run(Ld,"launch","--index",instance.ToString()); }
    // Asks the VM to close and returns; QuitVm also waits until it is off.
    static async Task VmQuitRequest(int instance) { if(IsBlueStacks) await BlueStacksQuit(instance); else await Run(Ld,"quit","--index",instance.ToString()); }
    static async Task VmReboot(int instance)
    {
        if(!IsBlueStacks) { await Run(Ld,"reboot","--index",instance.ToString()); return; }
        await QuitVm(instance);
        await VmLaunch(instance);
    }
    // BlueStacks has no window arrangement; returns whether the windows were arranged.
    static async Task<bool> VmArrange()
    {
        if(IsBlueStacks) return false;
        await Run(Ld,"sortWnd");
        return true;
    }
    // BlueStacks VMs are reached over adb connect, which has to be repeated after each boot.
    static async Task ConnectVm(int instance) { if(IsBlueStacks) await Task.Run(()=>ConnectBlueStacks(SerialOf(instance))); }
}
