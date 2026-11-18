using System.Drawing;
using FLEE.Controls;
using Xunit;

namespace FLEE.Core.Tests;

public sealed class AxisTicksTests
{
    [Theory]
    [InlineData(-5, 45, 8, 10)]
    [InlineData(-5, 27.766, 8, 5)]
    [InlineData(-0.003, 0.007, 8, 0.002)]
    [InlineData(-0.3, 0.3, 6, 0.1)]
    [InlineData(0, 1e-12, 5, 2e-13)]
    [InlineData(-1e12, 0, 5, 2e11)]
    public void LinearTicksIncludeZeroAndUseConvenientMultiples(double first, double last, int intervals, double step)
    {
        Span<double> ticks = stackalloc double[64];
        var count = AxisTicks.Linear(ticks, first, last, intervals);
        Assert.InRange(count, 2, intervals + 2);
        var values = ticks[..count].ToArray();
        Assert.Contains(0d, values);
        Assert.Equal(0, BitConverter.DoubleToInt64Bits(values.Single(value => value == 0)));
        foreach (var value in values)
        {
            Assert.InRange(value, first - step * 1e-10, last + step * 1e-10);
            Assert.InRange(Math.Abs(value / step - Math.Round(value / step)), 0, 1e-10);
        }
        foreach (var pair in values.Zip(values.Skip(1)))
            Assert.InRange(Math.Abs((pair.Second - pair.First) / step - 1), 0, 1e-10);
    }

    [Fact]
    public void LogarithmicTicksUseCountDecadesAndAllowSpaceForLabels()
    {
        Span<double> ticks = stackalloc double[64];
        var count = AxisTicks.Logarithmic(ticks, 4.4, 0.8);
        Assert.Equal(new double[] { 0, 10, 100, 1000, 10000 }, ticks[..count].ToArray());
        count = AxisTicks.Logarithmic(ticks, 4.4, 2);
        Assert.Equal(new double[] { 0, 100 }, ticks[..count].ToArray());
        count = AxisTicks.Logarithmic(ticks, 1, 0.1);
        Assert.Equal(new double[] { 0, 1 }, ticks[..count].ToArray());
    }

    [Theory]
    [InlineData(10, 20)]
    [InlineData(-20, -10)]
    [InlineData(-10, 10)]
    public void ZeroIsVisibleAndNavigationMatchesTheDisplayedTimeAxis(double first, double last)
    {
        var data = new DecayDataset("sample.csv", [first, last], [1, 2], [1, 2]);
        var area = new RectangleF(74, 28, 800, 150);
        var plot = PlotViewport.Create(data, data.FullRange, area, true);
        var navigation = PlotViewport.CreateTimeAxis(data, data.FullRange, area);
        Assert.InRange(plot.X(0), area.Left, area.Right);
        Assert.Equal(plot.FirstTime, navigation.FirstTime);
        Assert.Equal(plot.LastTime, navigation.LastTime);
        Assert.Equal(first, navigation.TimeAt(plot.X(first)), 8);
        Assert.Equal(last, navigation.TimeAt(plot.X(last)), 8);
    }

    [Fact]
    public void SingleZeroSampleAndZeroCountsHaveUsableAxes()
    {
        var data = new DecayDataset("sample.csv", [-1, 0, 1], [0, 0, 0], [0, 0, 0]);
        var area = new RectangleF(0, 0, 400, 100);
        var plot = PlotViewport.Create(data, new SampleRange(1, 1), area, true);
        Assert.Equal(200, plot.X(0));
        Assert.Equal(100, plot.Y(0));
        Assert.True(plot.LastTime > plot.FirstTime);
        Assert.Equal(1, plot.MaximumCoordinate);
    }
}
