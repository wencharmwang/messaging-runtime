using System.Drawing;
using WeChatDriver.Capture;
using WeChatDriver.Core;

namespace WeChatDriver.Automation;

public sealed record ConversationRow(string Name,int X,int Y,string Preview="",string DisplayTime="",bool HasUnread=false);

/// <summary>Keep fresh OCR row coordinates with names. Neither search results nor historical list positions are locators.</summary>
public static class ConversationListLocator
{
    public static IReadOnlyList<ConversationRow> Parse(IReadOnlyList<OcrTextRect> rectangles,int dpi,Bitmap? bitmap=null)
    {
        var gap=40*dpi/120.0;
        var sorted=rectangles.Where(r=>!string.IsNullOrWhiteSpace(r.Text)).OrderBy(r=>r.Y).ThenBy(r=>r.X).ToArray();
        var anchors=new List<OcrTextRect>();
        int? rowTop=null;
        foreach(var r in sorted)
        {
            var text=r.Text.Trim();
            if(string.IsNullOrWhiteSpace(text) || IsDecoration(text)) continue;
            if(rowTop is not null && r.Y-rowTop.Value <= gap) continue;
            rowTop=r.Y;
            anchors.Add(r);
        }
        return anchors.Select((anchor,index)=>
        {
            var upper=index==0?0:(anchors[index-1].CenterY+anchor.CenterY)/2;
            var lower=index+1==anchors.Count
                ? bitmap?.Height ?? sorted.Select(r=>r.Y+r.Height).DefaultIfEmpty(anchor.Y+anchor.Height+(int)gap).Max()
                : (anchor.CenterY+anchors[index+1].CenterY)/2;
            var members=sorted.Where(r=>r.CenterY>=upper&&r.CenterY<lower).ToArray();
            var time=members.Select(r=>r.Text.Trim()).FirstOrDefault(IsTime)??"";
            var preview=string.Join(" ",members
                .Where(r=>r.CenterY>anchor.CenterY+Math.Max(2,dpi/60)
                    && r.CenterX>=anchor.X
                    && !IsTime(r.Text.Trim())
                    && r.Text.Trim() is not ("搜索" or "Search"))
                .OrderBy(r=>r.Y).ThenBy(r=>r.X).Select(r=>r.Text.Trim()));
            return new ConversationRow(anchor.Text.Trim(),anchor.CenterX,anchor.CenterY,preview,time,
                bitmap is not null&&HasRedBadge(bitmap,upper,lower,dpi));
        }).ToArray();
    }

    private static bool IsDecoration(string text) =>
        text is "搜索" or "Search" || text.StartsWith("[",StringComparison.Ordinal) || IsTime(text);

    private static bool IsTime(string text) => System.Text.RegularExpressions.Regex.IsMatch(text,
        @"^(\d{1,2}:\d{2}|星期[一二三四五六日天]|昨天|前天|\d{1,2}月\d{1,2}日|\d{4}[/\-]\d{1,2}[/\-]\d{1,2})$");

    private static bool HasRedBadge(Bitmap bitmap,int upper,int lower,int dpi)
    {
        var scale=dpi/120.0;
        var left=Math.Clamp((int)Math.Round(38*scale),0,bitmap.Width);
        var right=Math.Clamp((int)Math.Round(72*scale),left,bitmap.Width);
        var top=Math.Clamp(upper,0,bitmap.Height);
        var bottom=Math.Clamp(Math.Min(lower,upper+(int)Math.Round(32*scale)),top,bitmap.Height);
        var red=0;
        for(var y=top;y<bottom;y+=2)
        for(var x=left;x<right;x+=2)
        {
            var color=bitmap.GetPixel(x,y);
            if(color.R>=190&&color.R-color.G>=75&&color.R-color.B>=55) red++;
        }
        return red>=4;
    }

    public static ConversationRow? Unique(IReadOnlyList<ConversationRow> rows,string name)
    {
        var expected=MessageFingerprint.Normalize(name);
        var matches=rows.Where(r=>MessageFingerprint.Normalize(r.Name)==expected).ToArray();
        if(matches.Length>1) throw new WeChatDriver.Windows.WindowAutomationException("ambiguous_conversation","Several visible conversation rows have the same name.");
        return matches.SingleOrDefault();
    }
}
