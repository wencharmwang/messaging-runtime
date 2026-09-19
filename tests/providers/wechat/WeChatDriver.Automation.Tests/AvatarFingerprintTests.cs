using System.Drawing;
using System.Drawing.Imaging;
using WeChatDriver.Windows;
using Xunit;

namespace WeChatDriver.Automation.Tests;

/// <summary>
/// 头像指纹是会话身份的第二把钥匙（第一把是 OCR 读出来的名字，会抖）。
/// 这些测试钉住两件实测出来的事：头像框的位置，以及「真匹配」和「假匹配」之间那条缝。
/// </summary>
public sealed class AvatarFingerprintTests
{
    private static string Fingerprint(Func<int,int,(byte R,byte G,byte B)> paint)
    {
        var size=AvatarFingerprint.Sample;
        var bytes=new byte[size*size*3];
        for(var y=0;y<size;y++)
        for(var x=0;x<size;x++)
        {
            var (r,g,b)=paint(x,y);
            var index=(y*size+x)*3;
            bytes[index]=r; bytes[index+1]=g; bytes[index+2]=b;
        }
        return Convert.ToBase64String(bytes);
    }

    /// <summary>平滑纹理：真实头像（照片、logo）都是平滑的，逐像素噪声平移之后本来就不该像。</summary>
    private static string Texture(double shiftX,double shiftY,double frequency=0.42) =>
        Fingerprint((x,y)=>
        {
            var value=(byte)Math.Clamp(
                128+110*Math.Sin((x+shiftX)*frequency)*Math.Cos((y+shiftY)*frequency*0.8),0,255);
            return (value,(byte)(255-value),(byte)((value+90)%256));
        });

    [Fact] public void AvatarBoxMatchesTheMeasuredPixelGeometry()
    {
        // 1089x962 窗口 @96 DPI 实测：头像 45x45，距列表左边界 21px，垂直居中于行中心。
        var box=AvatarFingerprint.Box(141,96);
        Assert.Equal(21,box.X);
        Assert.Equal(45,box.Width);
        Assert.Equal(45,box.Height);
        Assert.Equal(141,box.Y+box.Height/2);
    }

    [Fact] public void IdenticalFingerprintsCorrelatePerfectly()
    {
        var fingerprint=Texture(0,0);
        Assert.Equal(1.0,AvatarFingerprint.Similarity(fingerprint,fingerprint),3);
        Assert.True(AvatarFingerprint.Matches(fingerprint,fingerprint));
    }

    [Fact] public void WindowSearchAbsorbsTheRowCentreJitter()
    {
        // 行中心来自 OCR，实测偏 3~4px 是常态。定点取样在这个偏移下会掉到 0.78，
        // 窗口搜索必须把它拉回阈值以上 —— 否则这个功能在真实数据上根本不可用。
        var baseline=Texture(0,0);
        foreach(var (dx,dy) in new[]{(3,0),(-3,0),(0,3),(0,-3),(3,-2),(-4,4),(4,4)})
            Assert.True(AvatarFingerprint.Matches(baseline,Texture(dx,dy)),
                $"偏移 ({dx},{dy}) 的同一个头像被判成了不同头像");
    }

    [Fact] public void DifferentAvatarsStayBelowTheThreshold()
    {
        var left=Texture(0,0,0.42);
        var right=Texture(0,0,0.20);
        Assert.False(AvatarFingerprint.Matches(left,right));
        Assert.True(AvatarFingerprint.Similarity(left,right)<AvatarFingerprint.MatchThreshold);
    }

    [Fact] public void MissingOrDegenerateFingerprintsNeverMatch()
    {
        var fingerprint=Texture(0,0);
        Assert.Equal(0.0,AvatarFingerprint.Similarity(null,fingerprint));
        Assert.Equal(0.0,AvatarFingerprint.Similarity(fingerprint,""));
        Assert.Equal(0.0,AvatarFingerprint.Similarity("not base64 !!",fingerprint));
        Assert.Equal(0.0,AvatarFingerprint.Similarity(fingerprint,Convert.ToBase64String(new byte[16])));

        var flat=Fingerprint((_,_)=>(128,128,128));
        Assert.Equal(0.0,AvatarFingerprint.Similarity(flat,flat));
    }

    [Fact] public void CaptureIsStableAndSurvivesASmallRowShift()
    {
        using var list=new Bitmap(400,400,PixelFormat.Format32bppArgb);
        using(var graphics=Graphics.FromImage(list))
        {
            graphics.Clear(Color.White);
            graphics.FillEllipse(Brushes.OrangeRed,21,119,45,45);
        }
        var first=AvatarFingerprint.Capture(list,141,96);
        Assert.NotNull(first);
        Assert.Equal(first,AvatarFingerprint.Capture(list,141,96));
        Assert.True(AvatarFingerprint.Matches(first,AvatarFingerprint.Capture(list,143,96)));

        using var other=new Bitmap(400,400,PixelFormat.Format32bppArgb);
        using(var graphics=Graphics.FromImage(other))
        {
            graphics.Clear(Color.White);
            graphics.FillRectangle(Brushes.RoyalBlue,21,119,45,45);
        }
        Assert.False(AvatarFingerprint.Matches(first,AvatarFingerprint.Capture(other,141,96)));
    }

    [Fact] public void CaptureRefusesRowsThatLeaveThePane()
    {
        using var list=new Bitmap(200,200,PixelFormat.Format32bppArgb);
        Assert.Null(AvatarFingerprint.Capture(list,-100,96));
        Assert.Null(AvatarFingerprint.Capture(list,4000,96));
    }

    [Fact] public void AvatarFindsTheOnlyMatchingRow()
    {
        var wanted=Texture(0,0,0.42);
        var rows=new[]
        {
            new ConversationRow("小璐璐",100,100,Avatar:wanted),
            new ConversationRow("微信团队",100,200,Avatar:Texture(0,0,0.20)),
        };
        var row=ConversationListLocator.UniqueByAvatar(rows,wanted);
        Assert.NotNull(row);
        Assert.Equal("小璐璐",row!.Name);
    }

    [Fact] public void AvatarToleranceNeverResolvesTwoRows()
    {
        // 两个人用同一张头像（或都没设头像）是真实存在的。歧义必须报错，猜错就是把回复发给别人。
        var shared=Texture(0,0);
        var rows=new[]
        {
            new ConversationRow("甲",100,100,Avatar:shared),
            new ConversationRow("乙",100,200,Avatar:shared),
        };
        var error=Assert.Throws<WindowAutomationException>(()=>ConversationListLocator.UniqueByAvatar(rows,shared));
        Assert.Equal("ambiguous_conversation",error.Code);
    }

    [Fact] public void AvatarPathIsSkippedWhenNothingIsKnownYet()
    {
        var rows=new[]{new ConversationRow("甲",100,100,Avatar:Texture(0,0))};
        Assert.Null(ConversationListLocator.UniqueByAvatar(rows,null));
        Assert.Null(ConversationListLocator.UniqueByAvatar(rows,""));
        Assert.Null(ConversationListLocator.UniqueByAvatar(rows,Texture(0,0,0.20)));
    }

    [Fact] public void RowsWithoutAnAvatarNeverMatchByAvatar()
    {
        var rows=new[]{
            new ConversationRow("甲",100,100),
            new ConversationRow("乙",100,200)};
        Assert.Null(ConversationListLocator.UniqueByAvatar(rows,Texture(0,0)));
    }
}
