using System.Drawing;

internal static partial class Program
{
    static readonly Step StoryCrystal=new("story-crystal","templates/story-reward-crystal.png",0,0,414,228,42,44,"checkpoint");
    static readonly Step StoryTicket=new("story-ticket","templates/story-reward-ticket.png",0,0,495,239,52,31,"checkpoint");
    // Both item shapes are required in the same frame. Counts, text and event art are excluded.
    static bool IsStoryReward(Bitmap screen)=>Matches(screen,StoryCrystal) && Matches(screen,StoryTicket);
    static readonly Step ReceiptCrystal=StoryCrystal with { Name="receipt-crystal",Left=236,Top=91 };
    static readonly Step ReceiptTicket=StoryTicket with { Name="receipt-ticket",Left=229,Top=190 };
    static readonly Step ReceiptOk=new("receipt-ok","templates/story-receipt-ok.png",480,492,463,481,34,20,"checkpoint");
    static bool IsStoryReceipt(Bitmap screen)=>Matches(screen,ReceiptCrystal) && Matches(screen,ReceiptTicket) && Matches(screen,ReceiptOk);
    static async Task<bool> DismissStoryReceipt(Bitmap screen)
    {
        if(!IsStoryReceipt(screen)) return false;
        await Task.Delay(350);
        await Snapshot(quiet:true);
        using var verify=LoadCurrentScreen();
        if(!IsStoryReceipt(verify)) return false;
        SaveCurrentEvidence("artifacts/story-receipt-confirmed.webp");
        Console.WriteLine("Story reward receipt confirmed: vertical crystal + music ticket + OK; closing once.");
        await Shell("input tap 480 492");
        await Task.Delay(700);
        return true;
    }
    static void VerifyStoryReceipt(string path)
    {
        using var screen=new Bitmap(path);
        Console.WriteLine($"Crystal={Matches(screen,ReceiptCrystal)}, Ticket={Matches(screen,ReceiptTicket)}, OK={Matches(screen,ReceiptOk)}, Receipt={IsStoryReceipt(screen)}, IntroReward={IsStoryReward(screen)}");
        if(!IsStoryReceipt(screen)) return;
        foreach(var item in new[]{ReceiptCrystal,ReceiptTicket,ReceiptOk})
        {
            using var missing=new Bitmap(screen);
            using(var graphics=Graphics.FromImage(missing))
                graphics.FillRectangle(Brushes.Black,item.Left-3,item.Top-3,item.Width+6,item.Height+6);
            if(IsStoryReceipt(missing)) throw new Exception("Receipt accepted with a missing anchor: "+item.Name);
        }
        Console.WriteLine("Verified: each of the three receipt anchors is required; separate from the centered intro reward. No device commands sent.");
    }
    static async Task<bool> DismissStoryReward(Bitmap screen)
    {
        if(!IsStoryReward(screen)) return false;
        await Task.Delay(350);
        await Snapshot(quiet:true);
        using var verify=LoadCurrentScreen();
        if(!IsStoryReward(verify)) return false;
        SaveCurrentEvidence("artifacts/story-reward-confirmed.webp");
        Console.WriteLine("Story reward confirmed: crystal AND music ticket; dismissing once.");
        await Shell("input tap 480 420");
        await Task.Delay(700);
        return true;
    }
    static void VerifyStoryReward(string path)
    {
        using var screen=new Bitmap(path);
        var crystal=Matches(screen,StoryCrystal); var ticket=Matches(screen,StoryTicket);
        Console.WriteLine($"Crystal={crystal}, Ticket={ticket}, Reward={IsStoryReward(screen)}");
        if(!IsStoryReward(screen)) return;
        foreach(var item in new[]{StoryCrystal,StoryTicket})
        {
            using var missing=new Bitmap(screen);
            using(var graphics=Graphics.FromImage(missing))
                graphics.FillRectangle(Brushes.Black,item.Left-3,item.Top-3,item.Width+6,item.Height+6);
            if(IsStoryReward(missing)) throw new Exception("Reward incorrectly accepted with a missing item: "+item.Name);
        }
        using var plain=new Bitmap(960,540);
        using(var graphics=Graphics.FromImage(plain))
            foreach(var item in new[]{StoryCrystal,StoryTicket})
            {
                var rect=new Rectangle(item.Left,item.Top,item.Width,item.Height);
                graphics.DrawImage(screen,rect,rect,GraphicsUnit.Pixel);
            }
        if(!IsStoryReward(plain)) throw new Exception("Reward depends on quantities or the event background.");
        Console.WriteLine("Verified: either missing icon rejects; quantities and background are unnecessary. No device commands sent.");
    }
}
