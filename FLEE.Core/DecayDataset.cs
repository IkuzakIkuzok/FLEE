namespace FLEE.Core;

/// <summary>An inclusive, zero-based selection of existing samples.</summary>
public readonly record struct SampleRange(int StartIndex, int EndIndex)
{
    public int Count => this.EndIndex - this.StartIndex + 1;
    public bool FitsDasLimit => this.Count is >= 2 and <= DasWriter.MaxChannels;
}

/// <summary>A loaded, immutable instrument CSV snapshot used for previewing and exporting exact sample ranges.</summary>
public sealed class DecayDataset
{
    private readonly double[] time, decay, prompt;
    public string SourcePath { get; }
    public int Count => this.time.Length;
    public ReadOnlySpan<double> TimeNanoseconds => this.time;
    public ReadOnlySpan<double> Decay => this.decay;
    public ReadOnlySpan<double> Prompt => this.prompt;
    public SampleRange FullRange => new(0, this.Count - 1);
    public SampleRange InitialRange => new(0, Math.Min(this.Count, DasWriter.MaxChannels) - 1);

    /// <summary>Copies and validates time (ns), decay counts, and measured prompt counts.</summary>
    public DecayDataset(string sourcePath, ReadOnlySpan<double> timeNanoseconds,
        ReadOnlySpan<double> decay, ReadOnlySpan<double> prompt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        this.SourcePath = Path.GetFullPath(sourcePath);
        this.time = TraceProcessing.Numbers(timeNanoseconds, "Time");
        this.decay = TraceProcessing.Numbers(decay, "Decay");
        this.prompt = TraceProcessing.Numbers(prompt, "Prompt");
        TraceProcessing.Trace(this.time, this.decay, false, "Decay");
        if (this.prompt.Length != this.Count) throw new ArgumentException("Prompt and decay must have the same number of samples.");
        ValidateCounts(this.decay);
        ValidateCounts(this.prompt);
        ValidateTimeGrid(this.time);
    }

    /// <summary>Loads the instrument export: one metadata row, then time (ns), decay, and prompt in columns 0–2.</summary>
    public static DecayDataset LoadCsv(string path)
    {
        var columns = DelimitedTextReader.LoadColumns(path, [0, 1, 2], new() { Delimiter = ",", SkipRows = 1 });
        return new(path, columns[0], columns[1], columns[2]);
    }

    /// <summary>Selects existing samples inside inclusive time bounds. Bounds between samples do not interpolate.</summary>
    public SampleRange SelectTimeRange(double startNanoseconds, double endNanoseconds)
    {
        if (!double.IsFinite(startNanoseconds) || !double.IsFinite(endNanoseconds))
            throw new ArgumentException("Start and end times must be finite numbers.");
        if (startNanoseconds > endNanoseconds) throw new ArgumentException("Start time must be at or before end time.");
        var start = Array.BinarySearch(this.time, startNanoseconds);
        var end = Array.BinarySearch(this.time, endNanoseconds);
        if (start < 0) start = ~start;
        if (end < 0) end = ~end - 1;
        if (start > end) throw new ArgumentException("No samples fall inside this time range.");
        return new(start, end);
    }

    public int FindNearestSample(double timeNanoseconds)
    {
        if (!double.IsFinite(timeNanoseconds)) throw new ArgumentException("Time must be finite.");
        var index = Array.BinarySearch(this.time, timeNanoseconds);
        if (index >= 0) return index;
        index = ~index;
        if (index == 0) return 0;
        if (index == this.Count) return this.Count - 1;
        return timeNanoseconds - this.time[index - 1] <= this.time[index] - timeNanoseconds ? index - 1 : index;
    }

    /// <summary>Returns a range of at most 16,384 samples beginning at the supplied sample.</summary>
    public SampleRange LimitFrom(int startIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(startIndex, this.Count);
        var count = Math.Min(this.Count - startIndex, DasWriter.MaxChannels);
        return new(startIndex, startIndex + count - 1);
    }

    public void ValidateRange(SampleRange range)
    {
        if (range.StartIndex < 0 || range.EndIndex < range.StartIndex || range.EndIndex >= this.Count)
            throw new ArgumentOutOfRangeException(nameof(range), "The selection must lie inside the loaded dataset.");
    }

    /// <summary>Exports exactly the displayed samples, with no rebinning, interpolation, or rereading of the input file.</summary>
    public DasInfo Export(string path, SampleRange range, bool overwrite = false)
    {
        ValidateExportRange(range);
        InputFileGuard.EnsureDifferent(this.SourcePath, path);
        return DasWriter.Write(path, this.time.AsSpan(range.StartIndex, range.Count),
            this.decay.AsSpan(range.StartIndex, range.Count), CreateWriteOptions(range, overwrite),
            this.prompt.AsSpan(range.StartIndex, range.Count));
    }

    /// <summary>
    /// Writes the selected samples directly to a caller-owned stream, leaving it open.
    /// The caller owns file creation, input-path protection, and flushing; see DasWriter.Write(Stream, ...).
    /// </summary>
    public DasWriteResult Export(Stream destination, SampleRange range)
    {
        ValidateExportRange(range);
        return DasWriter.Write(destination, this.time.AsSpan(range.StartIndex, range.Count),
            this.decay.AsSpan(range.StartIndex, range.Count), CreateWriteOptions(range, false),
            this.prompt.AsSpan(range.StartIndex, range.Count));
    }

    private void ValidateExportRange(SampleRange range)
    {
        ValidateRange(range);
        if (!range.FitsDasLimit) throw new ArgumentException($"Select between 2 and {DasWriter.MaxChannels:N0} samples before exporting.");
    }

    private DasWriteOptions CreateWriteOptions(SampleRange range, bool overwrite) => new()
    {
        TimeUnit = TimeUnit.Nanoseconds, Weighting = SignalWeighting.Poisson,
        Name = Path.GetFileNameWithoutExtension(this.SourcePath), Overwrite = overwrite,
        Comment = CreateComment(range)
    };

    private string CreateComment(SampleRange range) =>
        $"Source: {Path.GetFileName(this.SourcePath)}. Original grid: {this.Count} samples, " +
        $"{TraceProcessing.Format(this.time[0])} to {TraceProcessing.Format(this.time[^1])} ns. " +
        $"Selected samples (1-based, inclusive): {range.StartIndex + 1} to {range.EndIndex + 1}. " +
        $"Selected grid: {range.Count} samples, {TraceProcessing.Format(this.time[range.StartIndex])} to " +
        $"{TraceProcessing.Format(this.time[range.EndIndex])} ns. " +
        $"Trimmed {this.Count - range.Count} samples outside the selection. No rebinning or interpolation applied.";

    private static void ValidateCounts(ReadOnlySpan<double> values)
    {
        foreach (var value in values)
        {
            var stored = (float)value;
            if (value < 0 || !float.IsFinite(stored) || (value != 0 && stored == 0))
                throw new ArgumentException("Instrument CSV counts must be nonnegative and representable as DAS float32 values.");
        }
    }

    private static void ValidateTimeGrid(ReadOnlySpan<double> time)
    {
        var spacing = (time[^1] - time[0]) / (time.Length - 1);
        if (!float.IsFinite((float)spacing) || (float)spacing <= 0)
            throw new ArgumentException("The channel spacing is outside the DAS calibration range.");
        for (var i = 0; i < time.Length; i++)
        {
            var expected = time[0] + i * spacing;
            var tolerance = Math.Max(spacing * 1e-6, 8 * Math.Max(TraceProcessing.Ulp(time[i]), TraceProcessing.Ulp(expected)));
            if (Math.Abs(time[i] - expected) > tolerance)
                throw new ArgumentException("The CSV time values must be evenly spaced. Trimming does not interpolate irregular data.");
        }
    }
}
