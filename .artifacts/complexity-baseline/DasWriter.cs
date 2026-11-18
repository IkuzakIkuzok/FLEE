using System.Text;

namespace FLEE.Core;

/// <summary>Creates binary HORIBA DAS6-style datasets from numeric time and signal sequences.</summary>
public static class DasWriter
{
    /// <summary>Maximum output channels accepted by the reference script's DAS6 exponential-analysis limit.</summary>
    public const int MaxChannels = 16_384;

    /// <summary>
    /// Writes a fresh dataset with optional measured prompt. Both time axes use options.TimeUnit.
    /// Empty prompt spans mean no prompt. Without promptTime, the prompt is paired with the original decay samples before sorting.
    /// All data is validated and serialized before the destination is opened.
    /// </summary>
    public static DasInfo Write(string path, ReadOnlySpan<double> time, ReadOnlySpan<double> signal,
        DasWriteOptions? options = null, ReadOnlySpan<double> prompt = default, ReadOnlySpan<double> promptTime = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        options ??= new();
        ArgumentNullException.ThrowIfNull(options.DecayName);
        ArgumentNullException.ThrowIfNull(options.PromptName);
        ArgumentNullException.ThrowIfNull(options.Comment);
        if (!Enum.IsDefined(options.Weighting)) throw new ArgumentOutOfRangeException(nameof(options), "Unknown weighting.");
        if (options.Resample is int requested && (requested < 2 || requested > MaxChannels))
            throw new ArgumentOutOfRangeException(nameof(options), $"Resample must be between 2 and {MaxChannels}.");
        var scale = TraceProcessing.UnitScale(options.TimeUnit);
        var original = TraceProcessing.Numbers(time, "Time in seconds", scale);
        var (times, decay) = TraceProcessing.Trace(original, TraceProcessing.Numbers(signal, "Decay"), options.SortTime, "Decay");
        double[]? promptTimes = null, promptValues = null;
        if (!prompt.IsEmpty)
        {
            var suppliedTimes = promptTime.IsEmpty ? original : TraceProcessing.Numbers(promptTime, "Prompt time in seconds", scale);
            (promptTimes, promptValues) = TraceProcessing.Trace(suppliedTimes, TraceProcessing.Numbers(prompt, "Prompt"), options.SortTime, "Prompt");
        }
        else if (!promptTime.IsEmpty) throw new ArgumentException("Prompt time was supplied without prompt values.");

        var count = options.Resample ?? times.Length;
        if (count is < 2 or > MaxChannels)
            throw new ArgumentException($"DAS6 exponential analysis supports 2..{MaxChannels} output channels; received {count}. Rebin photon counts, select a shorter interval, or explicitly resample non-count data.");
        var spacing = (times[^1] - times[0]) / (count - 1);
        if (spacing <= 0 || !double.IsFinite(spacing)) throw new ArgumentException("Channel spacing must be finite and positive.");
        var grid = Enumerable.Range(0, count).Select(i => times[0] + i * spacing).ToArray();
        grid[^1] = times[^1];
        for (var i = 1; i < count; i++)
            if (grid[i] <= grid[i - 1]) throw new ArgumentException("Time origin is too large to resolve the channel spacing; subtract a reference time first.");
        if (options.Resample is null)
        {
            if (!TraceProcessing.SameGrid(times, grid, spacing))
                throw new ArgumentException("DAS requires evenly spaced times; set Resample to interpolate explicitly.");
        }
        else decay = TraceProcessing.Interpolate(times, decay, grid);
        if (promptValues is not null && promptTimes is not null)
        {
            if (options.Resample is not null) promptValues = TraceProcessing.Interpolate(promptTimes, promptValues, grid);
            else if (!TraceProcessing.SameGrid(promptTimes, grid, spacing))
                throw new ArgumentException("Prompt and decay times must match; set Resample to interpolate explicitly.");
        }
        var calibration = Float32(spacing * 1e9, "Time calibration (ns/channel)");
        if (options.Weighting == SignalWeighting.Poisson && decay.Any(v => v < 0))
            throw new ArgumentException("Poisson weighting requires nonnegative decay values; use uniform for signed signals.");
        var weights = decay.Select(v => options.Weighting == SignalWeighting.Poisson ? 1 / Math.Max(v, 1) : 1).ToArray();
        var provenance = $"Source first-channel time: {TraceProcessing.Format(times[0])} s. Weighting: {options.Weighting.ToString().ToLowerInvariant()}.";
        if (options.Resample is not null) provenance += $" Linearly resampled to {count} channels; interpolation does not conserve photon counts.";
        if (promptValues is null) provenance += " No measured Prompt supplied.";
        if (options.Comment.Length > 0) provenance += "\n" + options.Comment;
        List<KeyValuePair<string, byte[]>> streams =
        [
            new("Version", StringData("2.0")),
            new("Information/Name", StringData(options.Name ?? Path.GetFileName(path))),
            new("Information/Measurement", StringData("General TCSPC")),
            new("Information/HubType", StringData("TAC")),
            new("Information/TCal", Binary(w => w.Write(calibration)))
        ];
        AddTrace(streams, "Decays/1", decay, options.DecayName, provenance, calibration);
        streams.Add(new("Decays/1/Weights", ArrayData(weights)));
        if (promptValues is not null)
            AddTrace(streams, "Prompts/1", promptValues, options.PromptName, "Measured Prompt supplied by the caller.", calibration);
        var data = CompoundFileWriter.Write(streams, ["Information", "Decays", "Prompts", "Fits"]);
        using (var output = new FileStream(path, options.Overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, FileShare.None))
            output.Write(data);
        return new(path, count, calibration * 1e-9, times[0], promptValues is not null, options.Resample is not null);
    }

    private static float Float32(double value, string label)
    {
        var result = (float)value;
        if (!float.IsFinite(result) || (value != 0 && result == 0))
            throw new ArgumentException($"{label} is outside the nonzero DAS float32 range.");
        return result;
    }

    private static byte[] Binary(Action<BinaryWriter> write)
    {
        using var data = new MemoryStream();
        using var writer = new BinaryWriter(data, Encoding.Unicode, leaveOpen: true);
        write(writer);
        writer.Flush();
        return data.ToArray();
    }

    private static byte[] StringData(string value) => Binary(w =>
    {
        // Length is the number of UTF-16 code units, including surrogate pairs.
        var encoded = new UnicodeEncoding(false, false, true).GetBytes(value);
        w.Write((uint)(encoded.Length / 2));
        w.Write(encoded);
    });

    private static byte[] ArrayData(double[] values) => Binary(w =>
    {
        w.Write(1); w.Write(values.Length);
        foreach (var value in values) w.Write(Float32(value, "Signal/weight"));
    });

    private static void AddTrace(List<KeyValuePair<string, byte[]>> streams, string prefix,
        double[] signal, string name, string comment, float calibration)
    {
        streams.Add(new(prefix + "/Data", ArrayData(signal)));
        streams.Add(new(prefix + "/IDM", Binary(w =>
        {
            w.Write(1); w.Write(8);
            foreach (var value in new short[] { 0, 0, 0, 0, 0, 0, 1, (short)signal.Length }) w.Write(value);
        })));
        streams.Add(new(prefix + "/RDM", Binary(w =>
        {
            w.Write(1); w.Write(8);
            foreach (var value in new float[] { 0, calibration, 0, 0, 0, 0, 0, 0 }) w.Write(value);
        })));
        streams.Add(new(prefix + "/Name", StringData(name)));
        streams.Add(new(prefix + "/Comment", StringData(comment)));
        for (var i = 1; i <= 3; i++)
        {
            streams.Add(new($"{prefix}/MonoPos{i}", Binary(w => w.Write((short)-1))));
            streams.Add(new($"{prefix}/MopolPos{i}", Binary(w => w.Write(-1f))));
        }
    }
}
