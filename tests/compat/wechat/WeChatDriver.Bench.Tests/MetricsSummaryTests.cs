using WeChatDriver.Bench;
using Xunit;

namespace WeChatDriver.Bench.Tests;

public class MetricsSummaryTests
{
    [Fact]
    public void Percentile_Median()
    {
        var samples = new double[] { 1, 2, 3, 4, 5 };
        Assert.Equal(3, MetricsSummary.Percentile(samples, 0.5));
    }

    [Fact]
    public void Percentile_Empty_ReturnsZero()
    {
        Assert.Equal(0, MetricsSummary.Percentile(Array.Empty<double>(), 0.5));
    }

    [Fact]
    public void Summary_ComputesMinMax()
    {
        var s = MetricsSummary.From("x", new double[] { 5, 1, 3 });
        Assert.Equal(1, s.Min);
        Assert.Equal(5, s.Max);
        Assert.Equal(3, s.Count);
    }
}
