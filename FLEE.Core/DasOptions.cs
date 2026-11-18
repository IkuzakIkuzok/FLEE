namespace FLEE.Core;

/// <summary>Units used by the supplied time coordinates.</summary>
public enum TimeUnit { Seconds, Milliseconds, Microseconds, Nanoseconds, Picoseconds, Femtoseconds }

/// <summary>Uniform weights support signed signals; Poisson weights require nonnegative decay values.</summary>
public enum SignalWeighting { Uniform, Poisson }

/// <summary>Settings for creating a fresh DAS dataset.</summary>
public sealed record DasWriteOptions
{
    public TimeUnit TimeUnit { get; init; } = TimeUnit.Seconds;
    public SignalWeighting Weighting { get; init; } = SignalWeighting.Uniform;
    /// <summary>Explicitly interpolate onto this many endpoint-inclusive channels. Does not conserve counts.</summary>
    public int? Resample { get; init; }
    public bool SortTime { get; init; }
    public string? Name { get; init; }
    public string DecayName { get; init; } = "Decay";
    public string PromptName { get; init; } = "Prompt";
    public string Comment { get; init; } = "";
    /// <summary>Allows replacing an existing file in the path overload. Stream callers control their own file mode.</summary>
    public bool Overwrite { get; init; }
}

/// <summary>Dataset statistics for a caller-owned output stream. Channel width reflects stored float32 calibration.</summary>
public readonly record struct DasWriteResult(int Channels, double ChannelWidthSeconds,
    double FirstTimeSeconds, bool HasPrompt, bool Resampled);

/// <summary>The written dataset. Channel width reflects the stored float32 calibration.</summary>
public sealed record DasInfo(string Path, int Channels, double ChannelWidthSeconds,
    double FirstTimeSeconds, bool HasPrompt, bool Resampled);

/// <summary>Rebinned counts and bin centres, in the original time unit.</summary>
public sealed class RebinnedTrace
{
    private readonly double[] time, signal;
    private readonly double[]? prompt;

    internal RebinnedTrace(double[] time, double[] signal, double[]? prompt) =>
        (this.time, this.signal, this.prompt) = (time, signal, prompt);

    public ReadOnlySpan<double> Time => this.time;
    public ReadOnlySpan<double> Signal => this.signal;
    /// <summary>Empty when no measured prompt was supplied.</summary>
    public ReadOnlySpan<double> Prompt => this.prompt;
    public bool HasPrompt => this.prompt is not null;
}
