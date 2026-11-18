using System.Drawing;

namespace FLEE.Controls;

// Drawing-only reduction keeps the extrema of every horizontal bucket. Export always uses the original samples.
internal static class PlotEnvelope
{
    internal static PointF[] Create(ReadOnlySpan<double> times, ReadOnlySpan<double> signal, PlotViewport viewport)
    {
        var buckets = Math.Min(times.Length, Math.Max(1, (int)viewport.Area.Width));
        List<PointF> points = new(Math.Min(times.Length, buckets * 4));
        for (var bucket = 0; bucket < buckets; bucket++)
        {
            var start = (int)((long)bucket * times.Length / buckets);
            var end = (int)((long)(bucket + 1) * times.Length / buckets);
            AppendBucket(points, times[start..end], signal[start..end], viewport);
        }
        return [.. points];
    }

    private static void AppendBucket(List<PointF> points, ReadOnlySpan<double> times,
        ReadOnlySpan<double> signal, PlotViewport viewport)
    {
        var (minimum, maximum) = FindExtrema(signal);
        var first = Math.Min(minimum, maximum);
        var second = Math.Max(minimum, maximum);
        points.Add(viewport.Point(times[0], signal[0]));
        if (first > 0) points.Add(viewport.Point(times[first], signal[first]));
        if (second > first) points.Add(viewport.Point(times[second], signal[second]));
        if (second < times.Length - 1) points.Add(viewport.Point(times[^1], signal[^1]));
    }

    private static (int Minimum, int Maximum) FindExtrema(ReadOnlySpan<double> values)
    {
        int minimum = 0, maximum = 0;
        for (var i = 1; i < values.Length; i++)
        {
            if (values[i] < values[minimum]) minimum = i;
            if (values[i] > values[maximum]) maximum = i;
        }
        return (minimum, maximum);
    }
}
