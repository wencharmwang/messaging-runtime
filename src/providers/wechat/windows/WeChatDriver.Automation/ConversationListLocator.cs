using System.Drawing;
using WeChatDriver.Capture;
using WeChatDriver.Core;

namespace WeChatDriver.Automation;

public sealed record ConversationRow(string Name,int X,int Y,string Preview="",string DisplayTime="",bool HasUnread=false,
    int NameLeft=0,int NameWidth=0,int NameTop=0,int NameHeight=0);

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
            // The title is often several OCR boxes — a group called 新爱书（小规模）&灏天来财税
            // came back as "新爱书" / "（小规模）" / "&灏天来财税", and which box ended up first
            // changed between reads (so the name, and with it the conversation id, changed).
            // Join everything on the title line into one name and keep the lower lines for the
            // preview; the identity must be what a person would read off the row.
            var band=Math.Max(6,Math.Max(anchor.Height/2,dpi/60));
            var title=members
                .Where(r=>Math.Abs(r.CenterY-anchor.CenterY)<=band
                    && r.CenterX>=anchor.X
                    && !IsTime(r.Text.Trim())
                    && r.Text.Trim() is not ("搜索" or "Search"))
                .OrderBy(r=>r.X).ToArray();
            var rawName=title.Length>0?string.Concat(title.Select(r=>r.Text.Trim())):anchor.Text.Trim();
            var preview=string.Join(" ",members
                .Where(r=>r.CenterY>anchor.CenterY+band
                    && r.CenterX>=anchor.X
                    && !IsTime(r.Text.Trim())
                    && r.Text.Trim() is not ("搜索" or "Search"))
                .OrderBy(r=>r.Y).ThenBy(r=>r.X).Select(r=>r.Text.Trim()));
            var (name,inlineTime)=SplitName(rawName);
            var nameLeft=title.Length>0?title.Min(r=>r.X):anchor.X;
            var nameRight=title.Length>0?title.Max(r=>r.X+r.Width):anchor.X+anchor.Width;
            var nameTop=title.Length>0?title.Min(r=>r.Y):anchor.Y;
            var nameHeight=title.Length>0?title.Max(r=>r.Y+r.Height)-nameTop:anchor.Height;
            return new ConversationRow(name,anchor.CenterX,anchor.CenterY,preview,time.Length>0?time:inlineTime,
                bitmap is not null&&HasRedBadge(bitmap,upper,lower,dpi),nameLeft,nameRight-nameLeft,nameTop,nameHeight);
        }).ToArray();
    }

    /// <summary>Exposed so a caller can clean a name it read from a narrower crop.</summary>
    public static string CleanName(string raw) => SplitName(raw).Name;

    /// <summary>
    /// A name without the trailing group member count: a chat header reads "新疆羊肉串( 14"
    /// while the list row says "新疆羊肉串", and comparing those directly never matches.
    /// </summary>
    public static string BareName(string text) =>
        System.Text.RegularExpressions.Regex
            .Replace((text ?? "").Trim(), @"[（(]\s*\d+\s*[)）]?\s*$", "")
            .Trim();

    /// <summary>
    /// The row's right-hand time column is occasionally OCR'd into the same box as the name
    /// (a truncated group name almost touches it), which used to produce names like
    /// "楚萌幼儿园大四..昨天21:39". The conversation id is a hash of this name, so a name that
    /// changes shape silently detaches every rule bound to it. A trailing question mark is the
    /// other kind of noise here: OCR has no glyph for an emoji in the name, so "树袋熊🐻" reads
    /// as "树袋熊?". Both are stripped so the identity is the part a person would type.
    /// </summary>
    private static (string Name,string Time) SplitName(string raw)
    {        var name=raw.Trim();
        var inline="";
        var match=TrailingTimeRegex.Match(name);
        if(match.Success&&match.Groups["name"].Value.Trim().Length>0)
        {
            name=match.Groups["name"].Value.Trim();
            inline=match.Groups["time"].Value.Trim();
        }
        // A long name is truncated by WeChat with an ellipsis, which OCR returns as "." / ".." /
        // "..." on different frames — that alone used to change the conversation id. Collapse
        // every trailing-dots form to one ellipsis so the identity stops flickering.
        name=System.Text.RegularExpressions.Regex.Replace(name,@"[.．。·…]+$","…");
        if(name.Length==0) name=raw.Trim();
        return (name.TrimEnd('?','？','\ufffd','\u25a1').Trim(),inline);
    }

    private const string DayOrDate=@"(?:星期[一二三四五六日天]|昨天|前天|\d{1,2}月\d{1,2}日|\d{4}[/\-]\d{1,2}[/\-]\d{1,2})";

    private static readonly System.Text.RegularExpressions.Regex TrailingTimeRegex=new(
        @"^(?<name>.+?)\s*(?<time>(?:"+DayOrDate+@"\s*)?\d{1,2}:\d{2}|"+DayOrDate+@")$",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static bool IsDecoration(string text) =>
        text is "搜索" or "Search" || text.StartsWith("[",StringComparison.Ordinal) || IsTime(text);

    /// <summary>A pure time cell, including the day+time forms the row may carry.</summary>
    public static bool IsTimeText(string text) => System.Text.RegularExpressions.Regex.IsMatch(text,
        @"^(?:"+DayOrDate+@"\s*)?\d{1,2}:\d{2}$|^"+DayOrDate+@"$");

    private static bool IsTime(string text) => IsTimeText(text);

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
