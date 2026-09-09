using WeChatDriver.Core;
using Xunit;

namespace WeChatDriver.Core.Tests;

public class MessageCleanerTests
{
    [Fact]
    public void FilterUiNoise_PreservesRepeatedBubblesButDropsTimestamp()
    {
        var result = MessageCleaner.FilterUiNoise(new[] { "【对方】08:06", "【对方】你好", "【对方】你好" });
        Assert.Equal(new[] { "【对方】你好", "【对方】你好" }, result);
    }
    [Fact]
    public void Clean_ExactDuplicate_KeepsOne()
    {
        var result = MessageCleaner.Clean(new[] { "你好", "你好" });
        Assert.Single(result);
        Assert.Equal("你好", result[0]);
    }

    [Fact]
    public void Clean_ShortDistinctMessages_AllKept()
    {
        var result = MessageCleaner.Clean(new[] { "好的", "可以", "完成", "再确认" });
        Assert.Equal(4, result.Count);
    }

    [Fact]
    public void Clean_LongTextSubstring_MergesToLonger()
    {
        var short_ = "这是很长的消息内容第一部分，讲环境搭建的细节";
        var long_ = "这是很长的消息内容第一部分，讲环境搭建的细节，然后是第二部分讲项目结构";
        var result = MessageCleaner.Clean(new[] { short_, long_ });
        Assert.Single(result);
        Assert.Equal(long_, result[0]);
    }

    [Fact]
    public void Clean_HighSimilarityLongText_MergesToLonger()
    {
        // 差 1 字，编辑距离相似度 ≈ 0.955 > 0.95，应合并。
        var a = "这是一个很长的测试消息用于验证高相似度合并逻辑";
        var b = "这是一个很长的测试消息用于验证高相似度合并逻缉";
        var result = MessageCleaner.Clean(new[] { a, b });
        Assert.Single(result);
    }

    [Fact]
    public void Clean_AdjacentOverlappingSegments_Merges()
    {
        // a 后缀 "甲乙丙丁戊己庚辛壬癸"（10 字）== b 前缀，应拼接（两段均 ≥20 字）。
        var a = "一二三四五六七八九十甲乙丙丁戊己庚辛壬癸";
        var b = "甲乙丙丁戊己庚辛壬癸子丑寅卯辰巳午未申酉";
        var result = MessageCleaner.Clean(new[] { a, b });
        Assert.Single(result);
        Assert.Equal("一二三四五六七八九十甲乙丙丁戊己庚辛壬癸子丑寅卯辰巳午未申酉", result[0]);
    }

    [Fact]
    public void Clean_ShortTextNoOverlapMerge_KeptSeparate()
    {
        // 短文本（<20 字）不做拼接/相似合并。
        var result = MessageCleaner.Clean(new[] { "今天天气", "天气不错" });
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Clean_EmptyAndWhitespace_Filtered()
    {
        var result = MessageCleaner.Clean(new[] { "", "   ", "有效内容" });
        Assert.Single(result);
        Assert.Equal("有效内容", result[0]);
    }

    [Fact]
    public void Clean_ApproxPrefixWithOcrNoise_Merges()
    {
        // a 是 b 的近似前缀（差 1 字，OCR 噪声），应合并保留更长的 b。
        var a = "这是一条非常长的测试消息用来验证近似前缀合并功能";
        var b = "这是一条非常长的测试消息用来验证近似前缏合并功能，以及后续补充内容";
        var result = MessageCleaner.Clean(new[] { a, b });
        Assert.Single(result);
        Assert.Equal(b, result[0]);
    }

    [Fact]
    public void Clean_TimestampNoise_Filtered()
    {
        // 界面日期分隔线/时间戳（整行）应被过滤。
        var result = MessageCleaner.Clean(new[]
        {
            "星期五14:18",
            "6月4日12:14",
            "昨天",
            "14:18",
            "2026-08-30",
            "有效消息内容"
        });
        Assert.Single(result);
        Assert.Equal("有效消息内容", result[0]);
    }

    [Fact]
    public void Clean_PureNumberNoise_Filtered()
    {
        // 4~8 位纯数字（消息 ID/时间戳界面元素）应被过滤。
        var result = MessageCleaner.Clean(new[] { "504753", "12345", "有效消息" });
        Assert.Single(result);
        Assert.Equal("有效消息", result[0]);
    }

    [Fact]
    public void Clean_TimestampInsideSentence_Kept()
    {
        // 日期/星期嵌在正文里（非整行）不误删。
        var result = MessageCleaner.Clean(new[] { "6月4日我们要开会", "星期五下午见" });
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Clean_PhoneNumber_Kept()
    {
        // 11 位手机号不应被当作 4~8 位纯数字噪声过滤。
        var result = MessageCleaner.Clean(new[] { "13800138000", "这是手机号" });
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Clean_ShortDigits_Kept()
    {
        // 1~3 位数字不按纯数字噪声过滤（音频秒数已上游标注，短数字可能是正文）。
        var result = MessageCleaner.Clean(new[] { "3", "18", "有效消息" });
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void Clean_SenderNameLabelAtBoundary_Stripped()
    {
        // 名字标签拼在消息首/尾（OCR 现象）应被剥离。
        var result = MessageCleaner.Clean(new[]
        {
            "Wencharm 我先了解环境",
            "环境探查完成 pip 26 可用 Wencharm",
            "Wehcharm", // 纯名字标签 → 整行剔除
            "联系人名（Wencharm）" // 正文里的名字（后面有「）」）不应误删
        });
        Assert.Equal(3, result.Count);
        Assert.Equal("我先了解环境", result[0]);
        Assert.Equal("环境探查完成 pip 26 可用", result[1]);
        Assert.Equal("联系人名（Wencharm）", result[2]);
    }

    [Fact]
    public void Clean_TimestampConcatenatedWithName_Filtered()
    {
        // 「时间戳+名字标签」拼一行：剥离名字后剩纯时间戳，应整行过滤。
        var result = MessageCleaner.Clean(new[] { "星期五15:22Wehcharm", "有效消息" });
        Assert.Single(result);
        Assert.Equal("有效消息", result[0]);
    }

    [Fact]
    public void Clean_LongMessageScatteredNoise_Merges()
    {
        // 同一长消息多屏 OCR 噪声分散多处（非前缀、非子串、编辑距离 <0.95），用 bigram 重叠合并。
        var a = "我先了解环境（.NET SDK、微信进程与版本、DPI），再按文档的分阶段顺序搭建项目。环境探查完成";
        var b = "我先了解环境（.NET SDK、微信进程与版本、DPI），再按文档的分阶段顺序搭建项目。环境探杳完成";
        var result = MessageCleaner.Clean(new[] { a, b });
        Assert.Single(result);
    }

    [Fact]
    public void Clean_DirectionPrefixPreserved()
    {
        // 方向标识【我】/【对方】应保留，不同方向同一文本不互相去重。
        var result = MessageCleaner.Clean(new[] { "【我】好的", "【对方】好的" });
        Assert.Equal(2, result.Count);
        Assert.Equal("【我】好的", result[0]);
        Assert.Equal("【对方】好的", result[1]);
    }

    [Fact]
    public void Clean_SenderNameWithDirectionPrefix_Stripped()
    {
        // 方向标识后面的名字标签也应被剥离（名字被方向前缀从行首顶到了中间）。
        var result = MessageCleaner.Clean(new[] { "【对方】Wencharm 好的" });
        Assert.Single(result);
        Assert.Equal("【对方】好的", result[0]);
    }

    [Fact]
    public void Clean_TimestampWithDirectionPrefix_Filtered()
    {
        // 方向标识 + 时间戳拼一行，仍应整行过滤。
        var result = MessageCleaner.Clean(new[] { "【对方】6月4日12:14", "有效消息" });
        Assert.Single(result);
        Assert.Equal("有效消息", result[0]);
    }

    [Fact]
    public void Clean_PureNumberWithDirectionPrefix_Filtered()
    {
        // 方向标识 + 纯数字噪声，仍应过滤。
        var result = MessageCleaner.Clean(new[] { "【对方】504753", "有效消息" });
        Assert.Single(result);
        Assert.Equal("有效消息", result[0]);
    }

    [Theory]
    [InlineData(144, 240, 144, true)]   // 微信「我」气泡浅绿（实测 screen_17/18 采样）
    [InlineData(157, 242, 159, true)]   // 绿色变体
    [InlineData(200, 240, 200, true)]   // 抗锯齿边缘绿
    [InlineData(240, 240, 240, false)]  // 对方气泡/聊天背景近白
    [InlineData(250, 250, 250, false)]  // 纯白
    [InlineData(0, 0, 0, false)]        // 文字黑
    [InlineData(200, 200, 200, false)]  // 中灰
    public void IsMineBubbleGreen_ClassifiesCorrectly(int r, int g, int b, bool expected)
    {
        Assert.Equal(expected, MessageCleaner.IsMineBubbleGreen(r, g, b));
    }
}
