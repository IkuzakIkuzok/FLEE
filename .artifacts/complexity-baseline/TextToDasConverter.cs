namespace FLEE.Core;

/// <summary>A separate measured prompt with independent columns, reader settings, and optional time calibration.</summary>
public sealed record PromptFileOptions(string Path)
{
    public ColumnSelector TimeColumn { get; init; } = 0;
    public ColumnSelector SignalColumn { get; init; } = 1;
    public DelimitedTextOptions Reader { get; init; } = new();
    /// <summary>Defaults to the decay time unit.</summary>
    public TimeUnit? TimeUnit { get; init; }
    /// <summary>Defaults to the decay time scale.</summary>
    public double? TimeScale { get; init; }
}

/// <summary>Configurable text conversion corresponding to decay_to_das.py.</summary>
public sealed record TextConversionOptions
{
    public ColumnSelector TimeColumn { get; init; } = 0;
    public ColumnSelector SignalColumn { get; init; } = 1;
    public ColumnSelector? PromptColumn { get; init; }
    public PromptFileOptions? PromptFile { get; init; }
    public DelimitedTextOptions Reader { get; init; } = new();
    public double TimeScale { get; init; } = 1;
    public DasWriteOptions Writer { get; init; } = new();
}

/// <summary>Combines the configurable text reader and DAS writer, including separate prompt tables.</summary>
public static class TextToDasConverter
{
    public static DasInfo ConvertFile(string inputPath, string outputPath, TextConversionOptions? options = null)
    {
        options ??= new();
        ArgumentNullException.ThrowIfNull(options.Reader);
        ArgumentNullException.ThrowIfNull(options.Writer);
        if (options.PromptColumn is not null && options.PromptFile is not null)
            throw new ArgumentException("PromptColumn and PromptFile are mutually exclusive.");
        InputFileGuard.EnsureDifferent(inputPath, outputPath);
        if (options.PromptFile is not null) InputFileGuard.EnsureDifferent(options.PromptFile.Path, outputPath);
        var promptScale = options.PromptFile?.TimeScale ?? options.TimeScale;
        if (!double.IsFinite(options.TimeScale) || options.TimeScale <= 0 || !double.IsFinite(promptScale) || promptScale <= 0)
            throw new ArgumentException("Time scales must be finite and positive.");
        ColumnSelector[] columns = options.PromptColumn is ColumnSelector promptColumn
            ? [options.TimeColumn, options.SignalColumn, promptColumn] : [options.TimeColumn, options.SignalColumn];
        var loaded = DelimitedTextReader.LoadColumns(inputPath, columns, options.Reader);
        var unit = TraceProcessing.UnitScale(options.Writer.TimeUnit);
        var time = loaded[0].Select(v => v * options.TimeScale * unit).ToArray();
        double[]? prompt = options.PromptColumn is null ? null : loaded[2], promptTime = null;
        if (options.PromptFile is PromptFileOptions file)
        {
            ArgumentNullException.ThrowIfNull(file.Reader);
            var promptData = DelimitedTextReader.LoadColumns(file.Path, [file.TimeColumn, file.SignalColumn], file.Reader);
            var promptUnit = TraceProcessing.UnitScale(file.TimeUnit ?? options.Writer.TimeUnit);
            promptTime = promptData[0].Select(v => v * promptScale * promptUnit).ToArray();
            prompt = promptData[1];
        }
        return DasWriter.Write(outputPath, time, loaded[1], options.Writer with { TimeUnit = TimeUnit.Seconds }, prompt, promptTime);
    }
}
