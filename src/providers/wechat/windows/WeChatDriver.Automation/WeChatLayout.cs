using System.Drawing;
using WeChatDriver.Windows;

namespace WeChatDriver.Automation;

/// <summary>Detect the actual pane dividers in the runtime-owned window bitmap, never in an agent screenshot.</summary>
public sealed record WeChatLayout(int Width, int Height, int Dpi, Rectangle List, Rectangle Header, Rectangle Messages, Rectangle Composer)
{
    public bool HasOpenConversation => !Header.IsEmpty && !Messages.IsEmpty && !Composer.IsEmpty;

    /// <summary>
    /// Detect only the navigation/list pane. This remains valid when WeChat has
    /// no selected conversation and the entire right pane is intentionally blank.
    /// </summary>
    public static WeChatLayout DetectList(Bitmap frame, int dpi)
    {
        int Px(int value) => (int)Math.Round(value * dpi / 120.0);
        if (frame.Width < Px(650) || frame.Height < Px(480))
            throw new WindowAutomationException("layout_unverified", "WeChat is too small to identify the conversation list.");
        // The right bound used to be an absolute Px(650), i.e. an assumption that the
        // conversation column stays under ~570px wide (the nav rail is ~78px). Widening that
        // column is a normal thing to do — it is how a person gets a full contact name
        // instead of "周二轮滑1班（16…" — and it pushes the divider past the cap, which made
        // the ENTIRE list unreadable with a misleading "boundary could not be identified".
        // Bound the search by leaving the chat pane a workable width instead of by an
        // absolute list width, so the layout no longer has to match a magic number.
        var start = Math.Max(Px(170), frame.Width / 6);
        var end = Math.Min(frame.Width * 3 / 5, frame.Width - Px(280));
        if (end - start < Px(80)) end = Math.Min(frame.Width - Px(120), start + Px(120));
        var split = FindVertical(frame, start, end, Px(55), frame.Height-Px(40));
        if (split < 0)
            throw new WindowAutomationException("layout_unverified", "The conversation-list boundary could not be identified.");

        // WeChat 4.x keeps the search bar in the first 100 reference pixels.
        // Cropping it out prevents the search placeholder from becoming a row.
        var listTop = Px(100);
        return new(frame.Width, frame.Height, dpi,
            new Rectangle(Px(78), listTop, split-Px(78)-3, frame.Height-listTop-Px(20)),
            Rectangle.Empty, Rectangle.Empty, Rectangle.Empty);
    }

    public static WeChatLayout Detect(Bitmap frame, int dpi)
    {
        int Px(int value) => (int)Math.Round(value * dpi / 120.0);
        var listLayout = DetectList(frame, dpi);
        var split = listLayout.List.Right + 3;
        var left = split+Px(12);
        var right = frame.Width-Px(16);
        var headerBottom = FindHorizontal(frame,left+Px(20),right-Px(20),Px(65),Px(145),false);
        var composerTop = FindHorizontal(frame,left+Px(20),right-Px(20),
            Math.Max(Px(250),frame.Height-Px(340)),frame.Height-Px(110),true);
        if (headerBottom < 0 || composerTop < 0 || composerTop-headerBottom < Px(120))
            throw new WindowAutomationException("layout_unverified", "The chat header or composer boundary could not be identified.");
        return new(frame.Width,frame.Height,dpi,
            listLayout.List,
            new Rectangle(left,Px(40),right-left,headerBottom-Px(40)-3),
            new Rectangle(left,headerBottom+Px(8),right-left,composerTop-headerBottom-Px(16)),
            new Rectangle(left+Px(12),composerTop+Px(12),right-left-Px(24),frame.Height-composerTop-Px(85)));
    }

    private static int Difference(Color a,Color b) => Math.Max(Math.Abs(a.R-b.R),Math.Max(Math.Abs(a.G-b.G),Math.Abs(a.B-b.B)));
    private static bool Neutral(Color c) => Math.Abs(c.R-c.G)<5 && Math.Abs(c.G-c.B)<5;

    private static int FindVertical(Bitmap frame,int start,int end,int top,int bottom)
    {
        var scores=new double[Math.Max(0,end-start)];
        var best=0.0;
        for (var x=start;x<end;x++)
        {
            int count=0,total=0;
            for(var y=top;y<bottom;y+=9)
            {
                var a=frame.GetPixel(x-2,y); var b=frame.GetPixel(x+2,y);
                if(Difference(a,b)>=7 && (Neutral(a)||Neutral(b))) count++;
                total++;
            }
            var fraction=total==0?0:count/(double)total;
            scores[x-start]=fraction;
            if(fraction>best) best=fraction;
        }
        if(best<0.68) return -1;
        // Leftmost near-best, not the single highest score: the conversation divider is the
        // first full-height boundary after the navigation rail, and now that the search range
        // is wider (to tolerate a widened list column) a right-pane artefact must not win on a
        // marginally higher score.
        for (var x=start;x<end;x++) if(scores[x-start]>=best*0.9) return x;
        return -1;
    }

    private static int FindHorizontal(Bitmap frame,int left,int right,int start,int end,bool composer)
    {
        var best=-1;var score=0.0;
        for(var y=Math.Max(3,start);y<Math.Min(frame.Height-3,end);y++)
        {
            int count=0,total=0;
            for(var x=left;x<right;x+=7)
            {
                var c=frame.GetPixel(x,y);var above=frame.GetPixel(x,y-2);var below=frame.GetPixel(x,y+2);
                var edge=Difference(above,below)>=4 || Difference(c,above)>=4 || Difference(c,below)>=4;
                if(edge && Neutral(c) && (!composer || c.R<246)) count++;
                total++;
            }
            var fraction=total==0?0:count/(double)total;
            if(fraction>score){score=fraction;best=y;}
        }
        return score>=0.7?best:-1;
    }
}
