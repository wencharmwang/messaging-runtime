using System.Drawing;
using WeChatDriver.Capture;
using WeChatDriver.Core;

namespace WeChatDriver.Automation;

public sealed record ConversationRow(string Name,int X,int Y,string Preview="",string DisplayTime="",bool HasUnread=false,
    int NameLeft=0,int NameWidth=0,int NameTop=0,int NameHeight=0,string Avatar="");

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
                bitmap is not null&&HasRedBadge(bitmap,upper,lower,dpi),nameLeft,nameRight-nameLeft,nameTop,nameHeight,
                bitmap is null?"":AvatarFingerprint.Capture(bitmap,anchor.CenterY,dpi)??"");
        }).ToArray();
    }

    /// <summary>Exposed so a caller can clean a name it read from a narrower crop.</summary>
    public static string CleanName(string raw) => SplitName(raw).Name;

    /// <summary>
    /// 行名尾部可能粘着时间列残留的日期/时间词。这一条**只用于比对**，不参与身份计算：
    /// 改身份会让所有已绑定的规则脱钩（身份是名字的哈希），成本远大于收益。
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex RowTimeTail=new(
        @"(?:星期[一二三四五六日天]?|周[一二三四五六日天]|昨天|今天|前天|\d{1,2}:\d{2}|\d{1,2}[/\-]\d{1,2})+\s*$");

    /// <summary>
    /// 列表行上的名字，在聊天页头会长成什么样。
    ///
    /// 行的名字会把右侧那一列时间/日期粘进来（实测「王丛喜一果爸爸星期」多出来的就是那一列
    /// 「星期…」的开头），而页头只有名字本身。拿行的名字直接去比页头永远不相等，于是三次点击
    /// 都确实打开了正确的会话，仍然被判成 conversation_unverified —— 用户看到的是"读不到这条"。
    /// </summary>
    public static string HeaderForm(string name)
    {
        var trimmed=RowTimeTail.Replace((name??"").Trim(),"").Trim();
        var cleaned=CleanName(trimmed);
        return cleaned.Length>0?cleaned:CleanName(name);
    }

    /// <summary>
    /// 页头读到的东西是否就是这个会话。只用于核对，不改任何身份。
    /// 三条判断依次是：页头包含目标名（原有行为）、差一个字符（原有行为）、
    /// 以及两边都清掉尾部日期/时间噪声后相等（新增，修的就是上面那个 case）。
    /// </summary>
    public static bool HeaderMatches(string header,string wanted)
    {
        if(string.IsNullOrWhiteSpace(header)||string.IsNullOrWhiteSpace(wanted)) return false;
        var wantedKey=MessageFingerprint.NameKey(wanted);
        if(wantedKey.Length==0) return false;
        if(MessageFingerprint.NameKey(header).Contains(wantedKey,StringComparison.Ordinal)) return true;
        if(ApproximatelySame(header,wanted)) return true;
        var right=HeaderForm(wanted);
        return MessageFingerprint.NameKey(right).Length>=2
            && MessageFingerprint.NameKey(HeaderForm(header))==MessageFingerprint.NameKey(right);
    }

    /// <summary>
    /// A deliberately broader read-only comparison for a list row whose OCR duplicated glyphs
    /// or appended Latin garbage (for example 小璐璐 -> 小璐路璐路ONR). This result is not enough
    /// on its own: the caller must prove that exactly one freshly scanned visible row matches the
    /// header before accepting it. Keeping the uniqueness proof outside this helper prevents this
    /// tolerance from ever being reused as a send recipient check by accident.
    /// </summary>
    public static bool NoisyRowCouldMatchHeader(string header,string rowName)
    {
        var headerKey=MessageFingerprint.NameKey(BareName(header));
        var rowKey=MessageFingerprint.NameKey(HeaderForm(rowName));
        if(headerKey.Length<3||rowKey.Length<headerKey.Length) return false;
        // Refuse very weak common-prefix guesses. The whole header must appear in order and make
        // up at least one third of the noisy row key; duplicated glyphs plus an OCR suffix still pass.
        if(headerKey.Length*3<rowKey.Length) return false;
        var index=0;
        foreach(var ch in rowKey)
            if(index<headerKey.Length&&headerKey[index]==ch) index++;
        return index==headerKey.Length;
    }

    /// <summary>
    /// Compare the list preview with one OCR'd message bubble. Direction markers, sender labels,
    /// and truncation punctuation may differ, so containment is intentional. Very short snippets
    /// are refused because common replies such as “好的” cannot identify a conversation safely.
    /// The caller must additionally prove uniqueness among the freshly scanned visible rows.
    /// </summary>
    public static bool PreviewCouldMatchMessage(string preview,string message)
    {
        var body=(message??"").Replace(MessageCleaner.MinePrefix,"",StringComparison.Ordinal)
            .Replace(MessageCleaner.OtherPrefix,"",StringComparison.Ordinal);
        var previewKey=MessageFingerprint.NameKey(preview);
        var messageKey=MessageFingerprint.NameKey(body);
        if(previewKey.Length<4||messageKey.Length<4) return false;
        return previewKey.Contains(messageKey,StringComparison.Ordinal)
            || messageKey.Contains(previewKey,StringComparison.Ordinal);
    }

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
        // Windows OCR sometimes renders an unsupported trailing emoji as one Chinese full stop
        // (`树袋熊🐻` -> `树袋熊。`). That is OCR noise, not WeChat's truncation marker. If
        // it is turned into an ellipsis, the user-commanded search literally searches for `树袋熊…`
        // and cannot find the contact. Preserve actual dot/ellipsis truncation, but drop `。`.
        name=name.TrimEnd('。');
        var trailingDots=System.Text.RegularExpressions.Regex.Match(name,@"[.．·…]+$");
        if(trailingDots.Success)
        {
            var stem=name[..trailingDots.Index].TrimEnd();
            // WeChat truncates genuinely long rows with an ellipsis. On a short name the same
            // glyphs are instead how OCR represents an unsupported trailing emoji (树袋熊🐻 ->
            // 树袋熊..). Searching for the fabricated ellipsis cannot find the contact.
            name=MessageFingerprint.NameKey(stem).Length<=8 ? stem : stem+"…";
        }
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
        // Compared on NameKey, not Normalize: the OCR engine writes spaces inside a CJK run, so
        // the same row reads as "小璐璐" on one frame and "小 璐 璐" on the next, and an exact
        // string comparison then reports the conversation as missing from its own list.
        var expected=MessageFingerprint.NameKey(name);
        if(expected.Length==0) return null;
        var matches=rows.Where(r=>MessageFingerprint.NameKey(r.Name)==expected).ToArray();
        // Not `SingleOrDefault`: with two matches it throws InvalidOperationException before the
        // `??` is ever reached, so the ambiguous_conversation code never made it out — a duplicate
        // name surfaced as an unmapped error instead of "微信里有多个同名会话，请先改名".
        if(matches.Length==1) return matches[0];
        if(matches.Length>1) throw new WeChatDriver.Windows.WindowAutomationException("ambiguous_conversation","Several visible conversation rows have the same name.");
        // Nothing matched exactly: accept a single row that is one character off. Never guess
        // between two candidates — reporting the miss is better than reading the wrong chat.
        var near=rows.Where(r=>ApproximatelySame(r.Name,name)).ToArray();
        if(near.Length>1) throw new WeChatDriver.Windows.WindowAutomationException("ambiguous_conversation","Several visible conversation rows are close to the requested name.");
        return near.Length==1 ? near[0] : null;
    }

    /// <summary>
    /// 用头像指纹在可见行里找会话。这是名字之外的第二把钥匙：名字是 OCR 的产物，会抖
    /// （小璐璐 → 小璐路璐），头像是一张图，不抖。
    ///
    /// 只在**唯一命中**时返回。头像重复是真实存在的（两个人都没设头像、或用了同一张图），
    /// 有歧义时必须报歧义 —— 猜错会话等于把回复发给另一个人。
    /// </summary>
    public static ConversationRow? UniqueByAvatar(IReadOnlyList<ConversationRow> rows,string? fingerprint)
    {
        if(string.IsNullOrWhiteSpace(fingerprint)) return null;
        var matches=rows.Where(r=>r.Avatar.Length>0&&AvatarFingerprint.Matches(r.Avatar,fingerprint)).ToArray();
        if(matches.Length==1) return matches[0];
        if(matches.Length>1) throw new WeChatDriver.Windows.WindowAutomationException("ambiguous_conversation","Several visible conversation rows share the same avatar.");
        return null;
    }

    /// <summary>
    /// Two reads of the same row can differ by one extra character: the OCR engine returned both
    /// "小璐璐" and "小璐路璐" for one chat, and an exact match then reports that conversation as
    /// missing from its own list. Only an inserted or dropped character counts — a substitution
    /// is deliberately not accepted, because two conversations whose names differ by a single
    /// character would otherwise be silently conflated.
    /// </summary>
    public static bool ApproximatelySame(string left,string right)
    {
        var a=MessageFingerprint.NameKey(left);
        var b=MessageFingerprint.NameKey(right);
        if(a.Length<3||b.Length<3||Math.Abs(a.Length-b.Length)!=1) return false;
        var (shorter,longer)=a.Length<b.Length?(a,b):(b,a);
        int i=0,j=0;
        var skipped=false;
        while(i<shorter.Length&&j<longer.Length)
        {
            if(shorter[i]==longer[j]) { i++; j++; }
            else if(skipped) return false;
            else { skipped=true; j++; }
        }
        return true;
    }

    /// <summary>What the row scan actually produced, for an error a person can act on.</summary>
    public static string Describe(IReadOnlyList<ConversationRow> rows) =>
        string.Join(" / ",rows.Where(r=>r.Name.Length>0).Select(r=>r.Name).Distinct().Take(12));
}
