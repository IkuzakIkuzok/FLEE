# Cognitive complexity

The library and desktop app use a maximum cognitive complexity of **15 per method**, matching
the default method threshold in Sonar's C# S3776 rule. Scores account for nested
control flow and logical expressions, rather than simply counting lines or
possible execution paths. They are a maintenance signal, not a proof of readability.

Measurements use `CSharpCognitiveComplexityMetric.GetComplexity` from the pinned
`SonarAnalyzer.CSharp` **10.35.0.4138** package, with Roslyn **5.3.0** parsing the
actual library and app sources. The metric is called directly; these are measured scores,
not manual estimates. Analyzer dependencies are confined to the test project.

Sources: [Sonar's metric implementation](https://github.com/SonarSource/sonar-dotnet/blob/10.35.0.4138/analyzers/src/SonarAnalyzer.CSharp/Metrics/CSharpCognitiveComplexityMetric.cs)
and [S3776 thresholds](https://github.com/SonarSource/sonar-dotnet/blob/10.35.0.4138/analyzers/src/SonarAnalyzer.Core/Rules/CognitiveComplexityBase.cs).

## Refactoring results

| Method | Before | After |
| --- | ---: | ---: |
| `DelimitedTextReader.LoadColumns` | 75 | 1 |
| `CompoundFileWriter.Prepare` (formerly `Write`) | 47 | 2 |
| `DasWriter.Write(string, ...)` | 33 | 1 |
| `DelimitedTextReader.Split` | 33 | 4 |
| `TraceProcessing.RebinCounts` | 23 | 3 |
| `CsvToDasConverter.ConvertFile` | 16 | 2 |

The maximum across **all library methods, including the extracted helpers**, is
now **12** (`CsvToDasConverter.ConvertFolder`). The most complex new helper is
`TableParser.FindNamedHeader` at **10**. No method exceeds 15.

The trimming GUI is covered by the same check. Its maximum is **8**
(`MainForm.SaveClicked`); range handling, plot geometry, envelope rendering, and
layout are separate methods. The new `DecayDataset` methods reach at most **6**
(`SelectTimeRange`). These scores include all helpers and do not exempt event handlers.

The refactoring separates responsibilities:

- `TableParser` owns per-table state; named-header discovery, delimiter detection,
  numeric-row validation, and quoted-field parsing have separate methods.
- `DirectoryBuilder` owns CFB directory entries and search trees; `SectorAllocator`
  plans regular-sector ranges and their allocation table without copying payloads
  into individual sector buffers. `PreparedFile.WriteTo` streams the validated
  layout to the destination. Mini-stream allocation and binary header serialization
  remain explicit steps.
- `DasWriter` separates options, measured-prompt preparation, grid alignment,
  weights, provenance, and stream serialization.
- Count rebinning separates trace validation, zero-tail validation, bin summation,
  and compensated arithmetic.
- CSV conversion separates inclusive time selection and provenance generation.

Existing public signatures, `ReadOnlySpan<T>` inputs, output ordering, validation
behavior, and the binary format are preserved. A new `Stream` overload accepts
caller-owned destinations. Parsing and serialization helpers use spans where they
can operate on slices; the layout retains `ReadOnlyMemory<byte>` to keep payloads
alive until they are written.

## Regression check

```powershell
dotnet test FLEE.Core.Tests/FLEE.Core.Tests.csproj
```

`CognitiveComplexityTests.MethodsStayWithinComplexityBudget` measures the library
and app sources copied into the test output at build time. It fails if a method,
constructor, operator, accessor, expression-bodied property/indexer, or local
function exceeds 15. Nested lambdas are included by the Sonar metric. The check
does not exclude individual methods or suppress complexity findings. Generated
`bin`/`obj` sources are not included.

To print the measurements:

```powershell
dotnet test FLEE.Core.Tests/FLEE.Core.Tests.csproj --filter FullyQualifiedName~CognitiveComplexityTests --logger "console;verbosity=detailed"
```

Behavior was verified with all **127 .NET tests** (119 core/geometry checks and
8 Windows form checks) and the independent parity check:

```powershell
dotnet test FLEE.slnx
python FLEE.Core.Tests/verify_python_parity.py
```

The parity check compares nine files and 241 streams against the Python reference
implementation and reads every stream using native Windows `IStorage`/`IStream`.
