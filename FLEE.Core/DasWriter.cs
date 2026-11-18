using System.Buffers.Binary;
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
    /// Data and the container layout are validated before the destination is opened.
    /// </summary>
    public static DasInfo Write(string path, ReadOnlySpan<double> time, ReadOnlySpan<double> signal,
        DasWriteOptions? options = null, ReadOnlySpan<double> prompt = default, ReadOnlySpan<double> promptTime = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        options ??= new();
        var prepared = Prepare(time, signal, prompt, promptTime, options, Path.GetFileName(path));
        using (var output = new FileStream(path, options.Overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, FileShare.None))
            prepared.File.WriteTo(output);
        var info = prepared.Info;
        return new(path, info.Channels, info.ChannelWidthSeconds, info.FirstTimeSeconds, info.HasPrompt, info.Resampled);
    }

    /// <summary>
    /// Writes to a caller-owned writable stream at its current position, without seeking, truncating,
    /// flushing, or closing it. FileStream and non-seekable streams are supported. All input and layout
    /// validation finishes before the first write. For a standalone DAS file, supply an empty stream.
    /// Overwrite applies only to the path overload; the caller controls the stream's file mode.
    /// The default dataset name is the FileStream filename, or "Dataset" for other streams.
    /// </summary>
    public static DasWriteResult Write(Stream destination, ReadOnlySpan<double> time, ReadOnlySpan<double> signal,
        DasWriteOptions? options = null, ReadOnlySpan<double> prompt = default, ReadOnlySpan<double> promptTime = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite) throw new ArgumentException("The destination stream must be writable.", nameof(destination));
        options ??= new();
        var defaultName = destination is FileStream file ? Path.GetFileName(file.Name) : "Dataset";
        var prepared = Prepare(time, signal, prompt, promptTime, options, defaultName);
        prepared.File.WriteTo(destination);
        return prepared.Info;
    }

    private sealed record PreparedDataset(CompoundFileWriter.PreparedFile File, DasWriteResult Info);

    private static PreparedDataset Prepare(ReadOnlySpan<double> time, ReadOnlySpan<double> signal,
        ReadOnlySpan<double> prompt, ReadOnlySpan<double> promptTime, DasWriteOptions options, string defaultName)
    {
        ValidateOptions(options);
        var scale = TraceProcessing.UnitScale(options.TimeUnit);
        var original = TraceProcessing.Numbers(time, "Time in seconds", scale);
        var (times, decay) = TraceProcessing.Trace(original, TraceProcessing.Numbers(signal, "Decay"), options.SortTime, "Decay");
        var measuredPrompt = PreparePrompt(original, prompt, promptTime, options);
        var (grid, spacing) = CreateOutputGrid(times, options.Resample ?? times.Length);
        var resample = options.Resample is not null;
        decay = AlignTrace(times, decay, grid, spacing, resample,
            "DAS requires evenly spaced times; set Resample to interpolate explicitly.");
        double[]? promptValues = null;
        if (measuredPrompt is not null)
            promptValues = AlignTrace(measuredPrompt.Time, measuredPrompt.Values, grid, spacing, resample,
                "Prompt and decay times must match; set Resample to interpolate explicitly.");
        var calibration = Float32(spacing * 1e9, "Time calibration (ns/channel)");
        var streams = CreateStreams(options.Name ?? defaultName, decay, promptValues, calibration, times[0], options);
        var container = CompoundFileWriter.Prepare(streams, ["Information", "Decays", "Prompts", "Fits"]);
        return new(container, new(grid.Length, calibration * 1e-9, times[0], promptValues is not null, resample));
    }

    private static void ValidateOptions(DasWriteOptions options)
    {
        ArgumentNullException.ThrowIfNull(options.DecayName);
        ArgumentNullException.ThrowIfNull(options.PromptName);
        ArgumentNullException.ThrowIfNull(options.Comment);
        if (!Enum.IsDefined(options.Weighting)) throw new ArgumentOutOfRangeException(nameof(options), "Unknown weighting.");
        if (options.Resample is int requested && (requested < 2 || requested > MaxChannels))
            throw new ArgumentOutOfRangeException(nameof(options), $"Resample must be between 2 and {MaxChannels}.");
    }

    private sealed record MeasuredTrace(double[] Time, double[] Values);

    private static MeasuredTrace? PreparePrompt(double[] originalTime, ReadOnlySpan<double> prompt,
        ReadOnlySpan<double> promptTime, DasWriteOptions options)
    {
        if (prompt.IsEmpty)
        {
            if (!promptTime.IsEmpty) throw new ArgumentException("Prompt time was supplied without prompt values.");
            return null;
        }
        var suppliedTimes = promptTime.IsEmpty ? originalTime
            : TraceProcessing.Numbers(promptTime, "Prompt time in seconds", TraceProcessing.UnitScale(options.TimeUnit));
        var (times, values) = TraceProcessing.Trace(suppliedTimes, TraceProcessing.Numbers(prompt, "Prompt"), options.SortTime, "Prompt");
        return new(times, values);
    }

    private static (double[] Grid, double Spacing) CreateOutputGrid(ReadOnlySpan<double> times, int count)
    {
        if (count is < 2 or > MaxChannels)
            throw new ArgumentException($"DAS6 exponential analysis supports 2..{MaxChannels} output channels; received {count}. Rebin photon counts, select a shorter interval, or explicitly resample non-count data.");
        var spacing = (times[^1] - times[0]) / (count - 1);
        if (spacing <= 0 || !double.IsFinite(spacing)) throw new ArgumentException("Channel spacing must be finite and positive.");
        var grid = new double[count];
        for (var i = 0; i < count; i++) grid[i] = times[0] + i * spacing;
        grid[^1] = times[^1];
        for (var i = 1; i < count; i++)
            if (grid[i] <= grid[i - 1]) throw new ArgumentException("Time origin is too large to resolve the channel spacing; subtract a reference time first.");
        return (grid, spacing);
    }

    private static double[] AlignTrace(ReadOnlySpan<double> times, double[] values, ReadOnlySpan<double> grid,
        double spacing, bool resample, string mismatchMessage)
    {
        if (resample) return TraceProcessing.Interpolate(times, values, grid);
        if (!TraceProcessing.SameGrid(times, grid, spacing)) throw new ArgumentException(mismatchMessage);
        return values;
    }

    private static double[] CreateWeights(ReadOnlySpan<double> decay, SignalWeighting weighting)
    {
        var weights = new double[decay.Length];
        if (weighting == SignalWeighting.Uniform)
        {
            Array.Fill(weights, 1d);
            return weights;
        }
        for (var i = 0; i < decay.Length; i++)
        {
            if (decay[i] < 0)
                throw new ArgumentException("Poisson weighting requires nonnegative decay values; use uniform for signed signals.");
            weights[i] = 1 / Math.Max(decay[i], 1);
        }
        return weights;
    }

    private static string CreateProvenance(double firstTime, int count, bool hasPrompt, DasWriteOptions options)
    {
        var provenance = $"Source first-channel time: {TraceProcessing.Format(firstTime)} s. Weighting: {options.Weighting.ToString().ToLowerInvariant()}.";
        if (options.Resample is not null) provenance += $" Linearly resampled to {count} channels; interpolation does not conserve photon counts.";
        if (!hasPrompt) provenance += " No measured Prompt supplied.";
        if (options.Comment.Length > 0) provenance += "\n" + options.Comment;
        return provenance;
    }

    private static List<KeyValuePair<string, byte[]>> CreateStreams(string name, ReadOnlySpan<double> decay,
        ReadOnlySpan<double> prompt, float calibration, double firstTime, DasWriteOptions options)
    {
        var weights = CreateWeights(decay, options.Weighting);
        var provenance = CreateProvenance(firstTime, decay.Length, !prompt.IsEmpty, options);
        List<KeyValuePair<string, byte[]>> streams =
        [
            new("Version", StringData("2.0")),
            new("Information/Name", StringData(name)),
            new("Information/Measurement", StringData("General TCSPC")),
            new("Information/HubType", StringData("TAC")),
            new("Information/TCal", Binary(w => w.Write(calibration)))
        ];
        AddTrace(streams, "Decays/1", decay, options.DecayName, provenance, calibration);
        streams.Add(new("Decays/1/Weights", ArrayData(weights)));
        if (!prompt.IsEmpty)
            AddTrace(streams, "Prompts/1", prompt, options.PromptName, "Measured Prompt supplied by the caller.", calibration);
        return streams;
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

    private static byte[] StringData(string value)
    {
        // Length is the number of UTF-16 code units, including surrogate pairs.
        var encoding = new UnicodeEncoding(false, false, true);
        var data = new byte[4 + encoding.GetByteCount(value)];
        BinaryPrimitives.WriteUInt32LittleEndian(data, (uint)value.Length);
        encoding.GetBytes(value, data.AsSpan(4));
        return data;
    }

    private static byte[] ArrayData(ReadOnlySpan<double> values)
    {
        var data = new byte[8 + values.Length * 4];
        BinaryPrimitives.WriteInt32LittleEndian(data, 1);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), values.Length);
        for (var i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(8 + i * 4), Float32(values[i], "Signal/weight"));
        return data;
    }

    private static void AddTrace(List<KeyValuePair<string, byte[]>> streams, string prefix,
        ReadOnlySpan<double> signal, string name, string comment, float calibration)
    {
        var count = (short)signal.Length;
        streams.Add(new(prefix + "/Data", ArrayData(signal)));
        streams.Add(new(prefix + "/IDM", Binary(w =>
        {
            w.Write(1); w.Write(8);
            foreach (var value in new short[] { 0, 0, 0, 0, 0, 0, 1, count }) w.Write(value);
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
