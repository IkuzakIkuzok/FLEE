# Developer guide

For desktop usage, see [README.md](README.md). This document covers building,
implementation, and verification.

## Build and run

The solution targets .NET 10. Install the .NET 10 SDK on Windows to build the
WinForms app and run the complete test suite. `FLEE.Core` itself targets `net10.0`
without a Windows dependency.

Run these commands from the repository root:

```powershell
dotnet restore FLEE.slnx
dotnet build FLEE.slnx
dotnet run --project FLEE/FLEE.csproj
```

Python is not required to build or run FLEE. It is used only for the optional
reference compatibility check and for rebuilding icon assets.

## Publish with Native AOT

The app project enables `PublishAot`. For Windows x64 publishing, install the
Visual Studio **Desktop development with C++** workload, including its default
Windows SDK and compiler components, then run:

```powershell
dotnet publish FLEE/FLEE.csproj -c Release -r win-x64
```

The published application is written to
`FLEE/bin/Release/net10.0-windows/win-x64/publish/`. Native AOT output includes the
runtime code it needs, so users do not need a separate .NET Desktop Runtime.
Normal builds and test runs still use managed code; native compilation happens
during publishing. See Microsoft's
[Native AOT deployment guide](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/?tabs=windows)
for the publishing prerequisites and property behavior.

When `PublishAot` is enabled, the project also sets
`_SuppressWinFormsTrimError=true` to use the WinForms workaround already tested
for this app. This bypasses the SDK's WinForms trimming error. Trim and AOT
warnings remain visible; no warning-code or blanket warning suppressions are
added.

To publish a managed build instead, pass `-p:PublishAot=false` explicitly.

If an existing published executable is running, close it before replacing that
file or publish to a separate folder with `-o <output-directory>`.

## Project layout

| Project or directory | Responsibility |
| --- | --- |
| `FLEE` | WinForms app, file dialogs, range controls, and linked plots. |
| `FLEE.Core` | Parsing, validation, trimming, conversion, and DAS serialization. |
| `FLEE.Core.Tests` | Library tests, plot geometry checks, and complexity measurements. |
| `FLEE.App.Tests` | Windows form state, layout, and asynchronous loading tests. |
| `python` | Original conversion scripts and reference fixtures. |
| `FLEE/Assets` | Icon artwork, embedded resources, and the ICO packaging script. |

See the [library API and format reference](FLEE.Core/README.md) for conversion
examples, stream ownership, validation rules, binary format details, and allocation
measurements. The [Python guide](python/README.md) documents the reference tools.

## Loading and selection state

`MainForm.LoadPathAsync` clears the current dataset and both plots before reading
the next file. It also resets the filename, time fields, selection, and pending
edits. Parsing runs through `Task.Run`; successful completion displays the new
dataset, while failure leaves an empty view with an error and export disabled.
Canceling the file picker never calls the loader.

`DecayDataset` owns a validated, immutable copy of the complete input. Its numeric
properties expose `ReadOnlySpan<double>`. `SampleRange` stores zero-based,
inclusive indices. `DecayDataset.InitialRange` selects the first
`min(Count, 16,384)` samples. Time bounds snap inward to existing samples.

Both plots share the same dataset and selection. Text edits remain pending until
Apply or Enter; pending edits disable export to prevent a mismatch between the
displayed bounds and exported samples. Ctrl+O and Ctrl+S invoke the corresponding
buttons and respect their enabled state.

Export uses slices of the loaded snapshot without rereading the CSV, rebinning,
or interpolation. The app uses the protected path overload, which validates and
checks input/output aliases before opening the destination. Both path and
caller-owned stream overloads use the direct stream writer.

The desktop trimming workflow uses `DecayDataset.Export`. The separate
`CsvToDasConverter` API retains the reference script's automatic count rebinning;
its behavior is documented in the library reference.

## Plot rendering

- `PlotViewport` defines the coordinate transforms. Time bounds include zero,
  and mouse selection uses the same time-axis geometry as the displayed overview.
- `AxisTicks` generates linear ticks anchored at zero in multiples of 1, 2, or 5
  times a power of ten. Logarithmic ticks use count decades and leave room for
  labels. `TracePlotControl.ExpFormatter` converts invariant `E2` notation to
  `×10` with Unicode superscript exponents and removes the redundant `1.00×`
  prefix for decade ticks, producing labels such as `10²`. Zero remains `0`.
- The default vertical transform is `log10(1 + counts)`, preserving zero counts.
  Changing scale affects presentation only.
- `PlotEnvelope` retains the first, minimum, maximum, and last samples of each
  horizontal drawing bucket in temporal order. This keeps narrow peaks visible
  when many samples map to one pixel. Export always uses the original samples.

## Verification

```powershell
dotnet test FLEE.slnx
```

The core tests cover numeric validation, parsing, trimming, output correctness,
filesystem protections, span slices, and stream ownership. Plot checks cover
extrema preservation, tick spacing, zero visibility, and coordinate mapping.

The Windows app tests instantiate forms without opening a desktop window. They
check selection and export availability, default scale, layout bounds, load
failures, and recovery. Async tests pump UI continuations. Offscreen rendering
writes previews to `.artifacts/ui-preview`; native text boxes may not paint in
these images, so their contents are checked directly.

The same suite enforces cognitive complexity of at most **15 per method** for
both production projects using Sonar's C# metric. See the
[complexity report](FLEE.Core/COMPLEXITY.md) for measurement details and reporting
commands.

### Python compatibility

After running the .NET tests, install the reference scripts' Click dependency
in your Python environment and run:

```powershell
python FLEE.Core.Tests/verify_python_parity.py
```

The check compares nine generated fixtures in `.artifacts/python-parity` with
the Python implementation, covering 241 streams. On Windows it also reads every
stream through native `IStorage`/`IStream`, including the mini-stream boundary
and maximum channel count.

## Application icon

The ICO is embedded in the executable and as a managed resource for the main
window. The header uses its 256 px image. Icon and bitmap resources are disposed
with the form; the app does not need loose image files at runtime.

See the [asset guide](FLEE/Assets/README.md) for the source artwork, generation
prompt, supported icon sizes, and rebuild commands. Repackaging the ICO requires
Python and Pillow; normal .NET builds use the checked-in icon.
