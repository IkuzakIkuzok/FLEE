using System.Drawing;
using FLEE.Controls;
using Xunit;

namespace FLEE.Core.Tests;

public sealed class PlotEnvelopeTests
{
    [Fact]
    public void OverviewRetainsNarrowPeaksAndTemporalOrder()
    {
        var times = Enumerable.Range(0, 25001).Select(i => (double)i).ToArray();
        var values = new double[times.Length];
        values[103] = 100000;
        values[21037] = 70000;
        var viewport = new PlotViewport(new(0, 0, 200, 100), 0, 25000, 100000, false);
        var points = PlotEnvelope.Create(times, values, viewport);
        Assert.InRange(points.Length, 2, 800);
        Assert.Contains(viewport.Point(103, 100000), points);
        Assert.Contains(viewport.Point(21037, 70000), points);
        Assert.Equal(viewport.Point(0, 0), points[0]);
        Assert.Equal(viewport.Point(25000, 0), points[^1]);
        Assert.True(points.Zip(points.Skip(1)).All(pair => pair.First.X <= pair.Second.X));
    }

    [Fact]
    public void PreviewUsesOnlyTheSelectedSamplesAndBothTracesForVerticalScale()
    {
        var data = new DecayDataset("sample.csv", [0, 1, 2, 3, 4], [10000, 1, 4, 2, 20000], [0, 10, 0, 0, 0]);
        var range = new SampleRange(1, 3);
        var viewport = PlotViewport.Create(data, range, new RectangleF(0, 0, 200, 100), false);
        var points = PlotEnvelope.Create(data.TimeNanoseconds.Slice(1, 3), data.Decay.Slice(1, 3), viewport);
        Assert.Equal(0, viewport.FirstTime);
        Assert.Equal(3, viewport.LastTime);
        Assert.Equal(10.8, viewport.MaximumCoordinate);
        Assert.Equal(new[] { viewport.Point(1, 1), viewport.Point(2, 4), viewport.Point(3, 2) }, points);
    }
}
