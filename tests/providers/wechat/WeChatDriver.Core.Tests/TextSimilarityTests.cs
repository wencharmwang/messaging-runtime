using WeChatDriver.Core;
using Xunit;

namespace WeChatDriver.Core.Tests;

public class TextSimilarityTests
{
    [Fact]
    public void Dice_Identical_ReturnsOne()
    {
        Assert.Equal(1.0, TextSimilarity.Dice("你好", "你好"));
    }

    [Fact]
    public void Dice_Empty_ReturnsZero()
    {
        Assert.Equal(0.0, TextSimilarity.Dice("", "abc"));
    }

    [Fact]
    public void Levenshtein_KnownCase()
    {
        Assert.Equal(3, TextSimilarity.Levenshtein("kitten", "sitting"));
        Assert.Equal(0, TextSimilarity.Levenshtein("相同", "相同"));
        Assert.Equal(2, TextSimilarity.Levenshtein("", "ab"));
    }

    [Fact]
    public void EditSimilarity_SingleCharSub_MidString_IsHigh()
    {
        // 「下」→「不」，编辑距离 1，相似度 0.875。
        Assert.Equal(1 - 1.0 / 8, TextSimilarity.EditSimilarity("明天下午三点开会", "明天不午三点开会"), 6);
    }

    [Fact]
    public void IsDuplicate_SingleCharSub_MidString_TrueViaEdit()
    {
        // bigram Dice 因中间单字替换断裂（< 0.78），但编辑距离相似度 ≥ 0.85 → 判重。
        Assert.True(TextSimilarity.Dice("明天下午三点开会", "明天不午三点开会") < 0.78);
        Assert.True(TextSimilarity.IsDuplicate("明天下午三点开会", "明天不午三点开会"));
    }

    [Fact]
    public void IsDuplicate_DifferentText_False()
    {
        Assert.False(TextSimilarity.IsDuplicate("明天下午三点开会", "发票已经开好了"));
    }

    [Fact]
    public void IsDuplicate_ShortString_SingleSub_NotDuplicate()
    {
        // 短字符串（<4 字）编辑距离兜底不生效，避免误判。
        Assert.False(TextSimilarity.IsDuplicate("关心", "关小"));
    }
}
