# FLEE.Core

API and implementation reference for developers. For desktop app usage, see the
[user guide](../README.md); for solution setup and testing, see the
[developer guide](../DEVELOPMENT.md).

.NET 10 conversion library ported from `../python/decay_to_das.py`,
`csv_to_das.py`, and `csv_folder_to_das.py`. The library has no external package,
Python, COM, or DAS-template dependency. Numeric inputs and column selectors use
`ReadOnlySpan<T>`; arrays, array slices, and stack-allocated numeric buffers can
be passed directly. Input buffers are never modified.

## Instrument CSV conversion

```csharp
using FLEE.Core;

// Skip one metadata row; read time (ns), decay, and measured prompt from columns 0–2.
DasInfo info = CsvToDasConverter.ConvertFile("sample.CSV", "sample.das",
    new CsvConversionOptions { StartTime = -2, EndTime = 20 });

FolderConversionResult batch = CsvToDasConverter.ConvertFolder("input", "output",
    new CsvConversionOptions { Overwrite = false });
foreach (var file in batch.Files)
    Console.WriteLine($"{file.InputPath}: {file.Status} {file.Error?.Message}");
```

Inclusive nanosecond bounds select existing samples before rebinning. Automatic
rebinning sums adjacent count bins to meet the 16,384-channel limit. Override with
`RebinFactor`; `1` disables rebinning. Incomplete trailing groups are removed only
if all decay and prompt counts in them are zero. Output times are group centres.
The input grid, selection, and rebinning are recorded in the decay comment.

Folder conversion considers only top-level `.csv` files, case-insensitively, in
filename order. It reports converted/skipped/failed results and continues after
individual failures. Invalid options and output filename collisions fail before
creating the output directory. Empty input folders create no output directory.

## Preview and exact trimming

The desktop app uses `DecayDataset` to retain the entire input for inspection and
export an exact selection without rebinning. Its numeric properties expose
`ReadOnlySpan<double>`; the public constructor copies its input spans to keep the
loaded snapshot stable.

```csharp
var dataset = DecayDataset.LoadCsv("sample.CSV");
SampleRange initial = dataset.InitialRange; // First min(Count, 16,384) samples.
SampleRange selected = dataset.SelectTimeRange(-2, 20); // Inclusive; snaps inward.

using var destination = new FileStream("selected.das", FileMode.CreateNew, FileAccess.Write);
DasWriteResult result = dataset.Export(destination, selected);
```

`SampleRange` uses zero-based, inclusive indices. Selections can be inspected at
any length, but export requires 2–16,384 samples. Both decay and measured prompt
use the same slice, and export preserves the original spacing and records trimming
provenance. Input time values must be increasing and uniform. Changing the source
file after loading does not affect the snapshot or its export.

The stream overload leaves ownership and file creation to the caller, with the
same stream semantics as `DasWriter.Write`. Callers must keep the destination
separate from the source. The path overload,
`dataset.Export("selected.das", selected, overwrite: false)`, additionally protects
the source path and validates before opening the destination. It uses the same
direct stream writer. The desktop app uses this protected path overload.

## Numeric data

```csharp
ReadOnlySpan<double> time = stackalloc double[] { 0, 0.5, 1, 1.5 };
ReadOnlySpan<double> decay = stackalloc double[] { 100, 61.2, 37.1, 22.4 };

DasInfo info = DasWriter.Write("decay.das", time, decay, new DasWriteOptions
{
    TimeUnit = TimeUnit.Nanoseconds,
    Weighting = SignalWeighting.Uniform,
    Name = "My sample",
    Comment = "Imported from my acquisition pipeline"
});

// Count rebinning is also available independently; results expose read-only spans.
RebinnedTrace rebinned = TraceProcessing.RebinCounts(time, decay, factor: 2);
DasWriter.Write("rebinned.das", rebinned.Time, rebinned.Signal,
    new() { TimeUnit = TimeUnit.Nanoseconds }, prompt: rebinned.Prompt);
```

You can also supply a `FileStream` or any writable `Stream` directly:

```csharp
using var destination = new FileStream("decay.das", FileMode.CreateNew, FileAccess.Write);
DasWriteResult result = DasWriter.Write(destination, time, decay,
    new() { TimeUnit = TimeUnit.Nanoseconds, Name = "My sample" });
```

The stream overload returns dataset statistics without a path. It writes at the
current position and leaves the stream open. It does not seek, truncate, or flush;
forward-only streams work as well. Supply an empty stream for a standalone DAS
file, and dispose or flush it when finished. `Overwrite` applies only to the path
overload; stream callers choose their own file mode. The default dataset name is
the `FileStream` filename, or `"Dataset"` for another stream type.

Both overloads plan and validate the container before writing. They then write
the header, payload blocks, and zero padding directly to the destination.
The writer retains serialized stream payloads and small layout buffers, but
does not allocate a complete output-file buffer or copy each regular sector.
The path overload uses the same implementation, so existing conversion helpers
also benefit from these allocation reductions.

In a local .NET 10 Release measurement (five warm-up writes, then the mean of
20 writes using `GC.GetAllocatedBytesForCurrentThread`), conversion with a
measured prompt and Poisson weighting allocated:

| Channels | Before | After | Reduction |
| --- | ---: | ---: | ---: |
| 2,048 | 367,179 bytes/write | 158,960 bytes/write | 57% |
| 16,384 | 1,872,770 bytes/write | 909,722 bytes/write | 51% |

These figures use the same path-based API and inputs before and after, including
opening/closing the output file. Inputs and options were created outside the
measurement. They measure managed allocations on the calling thread, not peak
memory or IO throughput, and will vary with runtime and data.

Supply `prompt` to `DasWriter.Write` for a measured instrument response and
optionally `promptTime` for its own time grid. Empty prompt spans mean absent.
Both axes use `TimeUnit`. `SortTime` sorts each trace while retaining pairing.
Duplicate times are rejected. By default, grids must be uniform and aligned.
`Resample = N` explicitly enables linear interpolation to N endpoint-inclusive
channels. The prompt must cover the entire decay interval; extrapolation is
rejected. Interpolation does **not** conserve photon counts.

`Uniform` weighting supports signed signals. `Poisson` uses `1 / max(decay, 1)`
and rejects negative decay values. Rebinning requires nonnegative values in both
traces. The default unit is seconds; milliseconds through femtoseconds are also
supported.

## Configurable text conversion

```csharp
TextToDasConverter.ConvertFile("report.txt", "report.das", new()
{
    TimeColumn = "Chan",
    SignalColumn = "Decay",
    PromptColumn = "Prompt",
    TimeScale = 0.028422212228178978,
    Writer = new() { TimeUnit = TimeUnit.Nanoseconds, Weighting = SignalWeighting.Poisson }
});

// Or read arbitrary columns independently and pass them to your own processing.
double[][] columns = DelimitedTextReader.LoadColumns("data.csv", ["Time", "Signal"],
    new() { Delimiter = "auto", SkipRows = 0 });
```

Selectors accept zero-based indices or exact header names. Decimal strings select
indices. Empty selectors default to columns 0 and 1. The reader supports CSV
quoting within a physical line, comma/tab/semicolon/pipe/whitespace detection,
blank lines, `#` comments, and Fortran `D` exponents. Named selectors search for
the header after a preamble; numeric selectors permit one textual header. Use
`SkipRows` for longer preambles. `Encoding` defaults to strict UTF-8 with BOM
support. Numeric parsing is independent of the current culture. Missing or
invalid data raises `FormatException` with the path and physical line number.

For a separate prompt, set `PromptFile = new PromptFileOptions("irf.txt")` with
its own `Reader`, column selectors, and optional `TimeUnit`/`TimeScale` overrides.
Unit and scale default to the decay settings. `PromptFile` and `PromptColumn`
are mutually exclusive.

## Format and error behavior

The generated file is a CFB v3 container with regular streams, mini streams, and
directory search trees, matching the Python writer's inferred DAS6 layout.
Metadata is UTF-16LE; data, weights, and ns/channel calibration are float32.
NaN, infinity, float32 overflow, and nonzero values rounding to zero are rejected.
The writer accepts 2–16,384 output channels. Its metadata/container limit is
approximately 7 MB. Dataset metadata defaults match the Python implementation;
no fitted curves or acquired metadata are invented.

`DasInfo.ChannelWidthSeconds` reflects stored float32 calibration.
`FirstTimeSeconds` is recorded in the comment: this format does not represent an
absolute time offset in native axis fields. Global analysis may have a lower
8,192-channel limit, as described by the reference scripts. Acceptance by the
DAS6 application, including datasets without a prompt, still requires checking
in that application.

All input validation, payload serialization, and container planning finish before
the destination is opened by the path overload, or before the stream overload's
first write. The writer cannot undo a file that the caller has already truncated
by opening a `FileStream` with `FileMode.Create`.
Existing output files require `Overwrite = true`. Conversion helpers reject
input/output path aliases, including symbolic links and Windows hard links.
Invalid numeric data/options raise `ArgumentException` (or a derived exception);
text parsing raises `FormatException`; filesystem errors retain .NET IO exception
types. Folder results retain each per-file exception for inspection. File writes
are synchronous and are not transactional if an IO failure occurs during writing.

## Verification

From the repository root:

```powershell
dotnet test FLEE.Core.Tests/FLEE.Core.Tests.csproj
python FLEE.Core.Tests/verify_python_parity.py
```

The .NET tests cover numeric validation, span slices, sorting, resampling,
rebinning, text parsing, time selection, filesystem protections, and batch results.
They also verify byte-identical FileStream output, stream ownership, forward-only
writing, current-position behavior, and validation before the first output write.
They independently read CFB output and compare the supplied report/film fixtures.
The optional parity script requires the reference scripts' Click dependency; it
compares nine generated fixtures against Python and, on Windows, reads every
stream through native `IStorage`/`IStream`, including the mini-stream boundary
and the maximum channel count. Python is only used for this verification.

The test suite also enforces a maximum cognitive complexity of 15 for library
and desktop app methods using Sonar's C# metric. See [COMPLEXITY.md](COMPLEXITY.md) for measured
before/after scores, the scope of the check, and how to print the full report.
