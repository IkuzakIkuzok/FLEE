using System.Drawing;
using FLEE.Core;

namespace FLEE.Controls;

internal readonly record struct PlotViewport(RectangleF Area, double FirstTime, double LastTime,
    double MaximumCoordinate, bool Logarithmic)
{
    internal static PlotViewport Create(DecayDataset data, SampleRange range, RectangleF area, bool logarithmic)
    {
        var timeAxis = CreateTimeAxis(data, range, area);
        var maximum = Math.Max(Maximum(data.Decay.Slice(range.StartIndex, range.Count)),
            Maximum(data.Prompt.Slice(range.StartIndex, range.Count)));
        var coordinate = logarithmic ? Math.Log10(1 + maximum) : maximum;
        return timeAxis with { MaximumCoordinate = Math.Max(coordinate * 1.08, 1), Logarithmic = logarithmic };
    }

    internal static PlotViewport CreateTimeAxis(DecayDataset data, SampleRange range, RectangleF area)
    {
        var first = Math.Min(0, data.TimeNanoseconds[range.StartIndex]);
        var last = Math.Max(0, data.TimeNanoseconds[range.EndIndex]);
        if (first == last) { first = -0.5; last = 0.5; }
        return new(area, first, last, 1, false);
    }

    internal float X(double time) => this.Area.Left + (float)((time - this.FirstTime) / (this.LastTime - this.FirstTime) * this.Area.Width);
    internal float Y(double signal)
    {
        var coordinate = this.Logarithmic ? Math.Log10(1 + signal) : signal;
        return this.Area.Bottom - (float)(coordinate / this.MaximumCoordinate * this.Area.Height);
    }
    internal PointF Point(double time, double signal) => new(X(time), Y(signal));
    internal double TimeAt(float x) => this.FirstTime + Math.Clamp((x - this.Area.Left) / this.Area.Width, 0, 1) * (this.LastTime - this.FirstTime);
    private static double Maximum(ReadOnlySpan<double> values)
    {
        var maximum = 0d;
        foreach (var value in values) maximum = Math.Max(maximum, value);
        return maximum;
    }
}
