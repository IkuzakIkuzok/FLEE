namespace FLEE.Core;

/// <summary>Settings for instrument CSV exports containing a metadata row, time (ns), decay, and prompt.</summary>
public sealed record CsvConversionOptions
{
    public double? StartTime { get; init; }
    public double? EndTime { get; init; }
    /// <summary>Defaults to the smallest factor meeting the channel limit after time selection.</summary>
    public int? RebinFactor { get; init; }
    public bool Overwrite { get; init; }

    internal void Validate()
    {
        if (StartTime is double start && !double.IsFinite(start)) throw new ArgumentException("StartTime must be finite (nanoseconds).");
        if (EndTime is double end && !double.IsFinite(end)) throw new ArgumentException("EndTime must be finite (nanoseconds).");
        if (StartTime > EndTime) throw new ArgumentException("StartTime must be less than or equal to EndTime.");
        if (RebinFactor is < 1) throw new ArgumentException("RebinFactor must be a positive integer.");
    }
}

public enum ConversionStatus { Converted, Skipped, Failed }

/// <summary>One folder conversion outcome. Info is set on success; Error is set on a failure or skip.</summary>
public sealed record FileConversionResult(string InputPath, string OutputPath, ConversionStatus Status,
    DasInfo? Info = null, Exception? Error = null);

/// <summary>Folder conversion results in filename order.</summary>
public sealed class FolderConversionResult
{
    private readonly FileConversionResult[] files;
    internal FolderConversionResult(FileConversionResult[] files) => this.files = files;
    public ReadOnlySpan<FileConversionResult> Files => files;
    public int Converted => files.Count(f => f.Status == ConversionStatus.Converted);
    public int Skipped => files.Count(f => f.Status == ConversionStatus.Skipped);
    public int Failed => files.Count(f => f.Status == ConversionStatus.Failed);
}

/// <summary>Ports csv_to_das.py and csv_folder_to_das.py without a Python runtime dependency.</summary>
public static class CsvToDasConverter
{
    /// <summary>Converts inclusive time bounds (ns) before rebinning; additional CSV columns are ignored.</summary>
    public static DasInfo ConvertFile(string inputPath, string? outputPath = null, CsvConversionOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        options ??= new();
        options.Validate();
        outputPath ??= Path.ChangeExtension(inputPath, ".das");
        InputFileGuard.EnsureDifferent(inputPath, outputPath);
        var data = DelimitedTextReader.LoadColumns(inputPath, [0, 1, 2], new() { Delimiter = ",", SkipRows = 1 });
        double[] times = data[0], decay = data[1], prompt = data[2];
        var sourceCount = times.Length;
        double sourceStart = times[0], sourceEnd = times[^1];
        if (options.StartTime is not null || options.EndTime is not null)
        {
            var selected = Enumerable.Range(0, times.Length).Where(i =>
                (options.StartTime is null || times[i] >= options.StartTime) &&
                (options.EndTime is null || times[i] <= options.EndTime)).ToArray();
            times = selected.Select(i => times[i]).ToArray();
            decay = selected.Select(i => decay[i]).ToArray();
            prompt = selected.Select(i => prompt[i]).ToArray();
        }
        var selectedCount = times.Length;
        if (selectedCount < 2) throw new ArgumentException($"Time range must contain at least two samples; selected {selectedCount}.");
        var factor = options.RebinFactor ?? (int)(((long)selectedCount + DasWriter.MaxChannels - 1) / DasWriter.MaxChannels);
        var comment = $"Source: {Path.GetFileName(inputPath)}. CSV columns: time (ns), Decay, Prompt; remaining columns ignored. " +
            $"Original grid: {sourceCount} samples, {TraceProcessing.Format(sourceStart)} to {TraceProcessing.Format(sourceEnd)} ns.";
        if (options.StartTime is not null || options.EndTime is not null)
        {
            var lower = options.StartTime is double start ? TraceProcessing.Format(start) : "unbounded";
            var upper = options.EndTime is double end ? TraceProcessing.Format(end) : "unbounded";
            comment += $" Requested time window (inclusive): {lower} to {upper} ns. Selected grid: {selectedCount} samples, " +
                $"{TraceProcessing.Format(times[0])} to {TraceProcessing.Format(times[^1])} ns, before rebinning.";
        }
        RebinnedTrace? rebinned = null;
        if (factor > 1)
        {
            rebinned = TraceProcessing.RebinCounts(times, decay, prompt, factor);
            comment += $" Summed adjacent groups of {factor} bins in both traces; times are group centres. " +
                $"Removed {selectedCount % factor} trailing samples containing zero counts in both traces. " +
                "Decay and Prompt total counts within the selected interval are preserved.";
        }
        return DasWriter.Write(outputPath, rebinned is null ? times : rebinned.Time,
            rebinned is null ? decay : rebinned.Signal, new()
            {
                TimeUnit = TimeUnit.Nanoseconds, Weighting = SignalWeighting.Poisson,
                Name = Path.GetFileNameWithoutExtension(inputPath), Comment = comment, Overwrite = options.Overwrite
            }, rebinned is null ? prompt : rebinned.Prompt);
    }

    /// <summary>
    /// Converts top-level CSV files in deterministic, case-insensitive filename order.
    /// Existing outputs are skipped unless Overwrite is set; a failed file does not stop the batch.
    /// Invalid options and colliding output names are rejected before creating the output directory.
    /// </summary>
    public static FolderConversionResult ConvertFolder(string folder, string? outputDirectory = null,
        CsvConversionOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        options ??= new();
        options.Validate();
        var sources = Directory.EnumerateFiles(folder).Where(p => Path.GetExtension(p).Equals(".csv", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => Path.GetFileName(p), StringComparer.Ordinal).ToArray();
        if (sources.Length == 0) return new([]);
        HashSet<string> targets = new(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
            if (!targets.Add(Path.ChangeExtension(Path.GetFileName(source), ".das")))
                throw new ArgumentException("Multiple CSV files would share an output filename; rename one CSV.");
        outputDirectory ??= folder;
        Directory.CreateDirectory(outputDirectory);
        List<FileConversionResult> results = [];
        foreach (var source in sources)
        {
            var destination = Path.Combine(outputDirectory, Path.ChangeExtension(Path.GetFileName(source), ".das"));
            try
            {
                var info = ConvertFile(source, destination, options);
                results.Add(new(source, destination, ConversionStatus.Converted, info));
            }
            catch (IOException ex) when (!options.Overwrite && (ex.HResult & 0xFFFF) is 80 or 183 or 17)
            {
                results.Add(new(source, destination, ConversionStatus.Skipped, Error: ex));
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or IOException or UnauthorizedAccessException)
            {
                results.Add(new(source, destination, ConversionStatus.Failed, Error: ex));
            }
        }
        return new(results.ToArray());
    }
}
