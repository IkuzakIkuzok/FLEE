using System.Globalization;

namespace FLEE.Core;

/// <summary>Count-preserving processing independent of file parsing and DAS serialization.</summary>
public static class TraceProcessing
{
    /// <summary>
    /// Sums adjacent, equal-width nonnegative bins. An incomplete final group may be
    /// removed only when every decay and prompt value in it is zero. At least two bins must remain.
    /// </summary>
    public static RebinnedTrace RebinCounts(ReadOnlySpan<double> time, ReadOnlySpan<double> signal,
        ReadOnlySpan<double> prompt = default, int factor = 2)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(factor, 1);
        var (times, decay) = Trace(Numbers(time, "Time"), Numbers(signal, "Decay"), false, "Decay");
        var promptValues = prompt.IsEmpty ? null : Numbers(prompt, "Prompt");
        ValidateCountTraces(times.Length, decay, promptValues);
        var spacing = RebinSpacing(times);
        var count = times.Length / factor;
        if (count < 2)
            throw new ArgumentException("Rebinning must leave at least two complete bins.");
        ValidateZeroTail(decay, promptValues, count * factor);
        var centres = Enumerable.Range(0, count)
            .Select(i => times[0] + (i * factor + (factor - 1) / 2.0) * spacing).ToArray();
        return new(centres, SumBins(decay, factor), promptValues is null ? null : SumBins(promptValues, factor));
    }

    private static void ValidateCountTraces(int sampleCount, ReadOnlySpan<double> decay, ReadOnlySpan<double> prompt)
    {
        if (!prompt.IsEmpty && prompt.Length != sampleCount)
            throw new ArgumentException("Prompt and decay must have the same number of samples for rebinning.");
        if (HasNegativeValue(decay) || HasNegativeValue(prompt))
            throw new ArgumentException("Count rebinning requires nonnegative decay and prompt values.");
    }

    private static bool HasNegativeValue(ReadOnlySpan<double> values)
    {
        foreach (var value in values)
            if (value < 0) return true;
        return false;
    }

    private static double RebinSpacing(double[] times)
    {
        var spacing = (times[^1] - times[0]) / (times.Length - 1);
        var grid = Enumerable.Range(0, times.Length).Select(i => times[0] + i * spacing).ToArray();
        if (spacing <= 0 || !SameGrid(times, grid, spacing))
            throw new ArgumentException("Count rebinning requires evenly spaced, increasing times.");
        return spacing;
    }

    private static void ValidateZeroTail(ReadOnlySpan<double> decay, ReadOnlySpan<double> prompt, int retained)
    {
        if (decay[retained..].ContainsAnyExcept(0d) || (!prompt.IsEmpty && prompt[retained..].ContainsAnyExcept(0d)))
            throw new ArgumentException("The final incomplete rebin group contains nonzero counts. Choose a factor that divides the sample count, or select a suitable input interval; no counts were discarded.");
    }

    private static double[] SumBins(ReadOnlySpan<double> values, int factor)
    {
        var result = new double[values.Length / factor];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = SumCounts(values.Slice(i * factor, factor));
            if (!double.IsFinite(result[i]))
                throw new ArgumentException("Rebinned counts exceed the finite numeric range.");
        }
        return result;
    }

    private static double SumCounts(ReadOnlySpan<double> values)
    {
        // Neumaier compensation retains small contributions next to large counts.
        double sum = 0, correction = 0;
        foreach (var value in values)
        {
            var next = sum + value;
            correction += sum >= value ? (sum - next) + value : (value - next) + sum;
            sum = next;
        }
        return sum + correction;
    }

    internal static double UnitScale(TimeUnit unit) => unit switch
    {
        TimeUnit.Seconds => 1, TimeUnit.Milliseconds => 1e-3, TimeUnit.Microseconds => 1e-6,
        TimeUnit.Nanoseconds => 1e-9, TimeUnit.Picoseconds => 1e-12, TimeUnit.Femtoseconds => 1e-15,
        _ => throw new ArgumentOutOfRangeException(nameof(unit))
    };

    internal static string Format(double value) => value.ToString("G17", CultureInfo.InvariantCulture);

    internal static double[] Numbers(ReadOnlySpan<double> values, string label, double scale = 1)
    {
        var result = values.ToArray();
        for (var i = 0; i < result.Length; i++)
        {
            result[i] *= scale;
            if (!double.IsFinite(result[i]))
                throw new ArgumentException($"{label} contains NaN or infinity.");
        }
        return result;
    }

    internal static (double[] Time, double[] Signal) Trace(double[] time, double[] signal, bool sort, string label)
    {
        if (time.Length != signal.Length || time.Length < 2)
            throw new ArgumentException($"{label} needs at least two time/signal pairs of equal length.");
        if (sort)
        {
            // Do not mutate a time array also used to align the measured prompt.
            time = (double[])time.Clone();
            signal = (double[])signal.Clone();
            Array.Sort(time, signal);
        }
        for (var i = 1; i < time.Length; i++)
            if (time[i] <= time[i - 1])
                throw new ArgumentException($"{label} times must be strictly increasing; enable SortTime for unordered data. Duplicates are not supported.");
        if (!double.IsFinite(time[^1] - time[0]))
            throw new ArgumentException($"{label} time span is too large.");
        return (time, signal);
    }

    internal static double Ulp(double value)
    {
        value = Math.Abs(value);
        var next = Math.BitIncrement(value);
        return double.IsInfinity(next) ? value - Math.BitDecrement(value) : next - value;
    }

    internal static bool SameGrid(ReadOnlySpan<double> left, ReadOnlySpan<double> right, double spacing)
    {
        if (left.Length != right.Length) return false;
        for (var i = 0; i < left.Length; i++)
            if (Math.Abs(left[i] - right[i]) > Math.Max(spacing * 1e-6, 8 * Math.Max(Ulp(left[i]), Ulp(right[i]))))
                return false;
        return true;
    }

    internal static double[] Interpolate(ReadOnlySpan<double> time, ReadOnlySpan<double> signal, ReadOnlySpan<double> grid)
    {
        var tolerance = new[] { time[0], time[^1], grid[0], grid[^1] }.Max(Ulp) * 8;
        if (grid[0] < time[0] - tolerance || grid[^1] > time[^1] + tolerance)
            throw new ArgumentException("Prompt must cover the complete decay time range; extrapolation is not supported.");
        var result = new double[grid.Length];
        for (var i = 0; i < grid.Length; i++)
        {
            var value = grid[i];
            if (value <= time[0]) { result[i] = signal[0]; continue; }
            if (value >= time[^1]) { result[i] = signal[^1]; continue; }
            var index = time.BinarySearch(value);
            if (index < 0) index = ~index - 1;
            var fraction = (value - time[index]) / (time[index + 1] - time[index]);
            result[i] = (1 - fraction) * signal[index] + fraction * signal[index + 1];
        }
        return result;
    }
}
