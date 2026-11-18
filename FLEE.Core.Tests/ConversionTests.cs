using System.Globalization;
using System.Runtime.InteropServices;
using Xunit;

namespace FLEE.Core.Tests;

public sealed class ConversionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "FLEE.Tests", Guid.NewGuid().ToString("N"));
    private string Output => Path.Combine(this.root, "result.das");
    public ConversionTests() => Directory.CreateDirectory(this.root);
    public void Dispose() => Directory.Delete(this.root, true);
    private string Table(string contents, string name = "input.csv")
    {
        var path = Path.Combine(this.root, name);
        File.WriteAllText(path, contents);
        return path;
    }
    internal static string RepositoryRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FLEE.slnx"))) directory = directory.Parent;
            return directory?.FullName ?? throw new InvalidOperationException("Cannot find repository fixtures.");
        }
    }
    private const string Csv = ",900,900\n-2,1,11,x,y\n-1,2,12,x,y\n0,3,13,x,y\n1,4,14,x,y\n2,5,15,x,y\n3,6,16,x,y\n";
    private static readonly string[] expected = ["A.csv", "b.CSV", "bad.csv"];

    [Fact]
    public void SpanInputsSupportSignedSignalsUnicodeAndSlicing()
    {
        var times = (stackalloc double[] { -1, 4, 4.5, 5, 99 });
        var signal = (stackalloc double[] { -2.5, 0.25, 1.75 });
        var info = DasWriter.Write(this.Output, times[1..4], signal, new() { TimeUnit = TimeUnit.Nanoseconds, Name = "試料 🌟" });
        var file = new CompoundFileReader(this.Output);
        Assert.Equal(new double[] { -2.5, 0.25, 1.75 }, file.Array("Decays/1/Data"));
        Assert.Equal(new double[] { 1, 1, 1 }, file.Array("Decays/1/Weights"));
        Assert.Equal("試料 🌟", file.Text("Information/Name"));
        Assert.Equal(0.5f, file.Calibration);
        Assert.Equal(4e-9, info.FirstTimeSeconds);
        Assert.False(info.HasPrompt);
        Assert.Contains("Prompts", file.Storages);
        Assert.DoesNotContain("Prompts/1", file.Storages);
        Assert.Contains("Fits", file.Storages);
        Assert.DoesNotContain(file.Streams.Keys, p => p.StartsWith("Fits/", StringComparison.Ordinal));
    }

    [Fact]
    public void SortingKeepsDecayAndPromptPairedWithoutMutatingInputs()
    {
        double[] time = [2, 0, 1], decay = [20, 0, 10], prompt = [200, 0, 100];
        DasWriter.Write(this.Output, time, decay, new() { SortTime = true }, prompt);
        var file = new CompoundFileReader(this.Output);
        Assert.Equal(new double[] { 0, 10, 20 }, file.Array("Decays/1/Data"));
        Assert.Equal(new double[] { 0, 100, 200 }, file.Array("Prompts/1/Data"));
        Assert.Equal(new double[] { 2, 0, 1 }, time);
        Assert.Equal(new double[] { 20, 0, 10 }, decay);
        Assert.Equal(new double[] { 200, 0, 100 }, prompt);
    }

    [Fact]
    public void ExplicitResamplingAlignsSeparateMeasuredPrompt()
    {
        var info = DasWriter.Write(this.Output, [0, 1, 3], [0, 10, 0], new() { Resample = 4 }, [0, 6, 0], [0, 1.5, 3]);
        var file = new CompoundFileReader(this.Output);
        Assert.True(info.Resampled);
        Assert.Equal(new double[] { 0, 10, 5, 0 }, file.Array("Decays/1/Data"));
        Assert.Equal(new double[] { 0, 4, 4, 0 }, file.Array("Prompts/1/Data"));
        Assert.Contains("interpolation does not conserve photon counts", file.Text("Decays/1/Comment"));
    }

    [Theory]
    [InlineData(TimeUnit.Seconds, 1e9)]
    [InlineData(TimeUnit.Milliseconds, 1e6)]
    [InlineData(TimeUnit.Microseconds, 1e3)]
    [InlineData(TimeUnit.Nanoseconds, 1)]
    [InlineData(TimeUnit.Picoseconds, 1e-3)]
    [InlineData(TimeUnit.Femtoseconds, 1e-6)]
    public void AllTimeUnitsStoreNanoseconds(TimeUnit unit, double expected)
    {
        var info = DasWriter.Write(this.Output, [0, 1], [1, 2], new() { TimeUnit = unit });
        Assert.Equal((float)expected, new CompoundFileReader(this.Output).Calibration);
        Assert.Equal((float)expected * 1e-9, info.ChannelWidthSeconds);
    }

    public static IEnumerable<object[]> InvalidTraces()
    {
        yield return [new double[] { 0 }, new double[] { 1 }, new DasWriteOptions()];
        yield return [new double[] { 0, 1 }, new double[] { 1 }, new DasWriteOptions()];
        yield return [new double[] { 0, 0 }, new double[] { 1, 2 }, new DasWriteOptions()];
        yield return [new double[] { 1, 0 }, new double[] { 1, 2 }, new DasWriteOptions()];
        yield return [new double[] { 0, 1, 3 }, new double[] { 1, 2, 3 }, new DasWriteOptions()];
        yield return [new double[] { 0, double.NaN }, new double[] { 1, 2 }, new DasWriteOptions()];
        yield return [new double[] { 0, 1 }, new double[] { 1, double.PositiveInfinity }, new DasWriteOptions()];
        yield return [new double[] { 0, 1 }, new double[] { -1, 2 }, new DasWriteOptions { Weighting = SignalWeighting.Poisson }];
        yield return [new double[] { 0, 1 }, new double[] { 1e40, 2 }, new DasWriteOptions()];
        yield return [new double[] { 0, 1 }, new double[] { 1e-50, 2 }, new DasWriteOptions()];
        yield return [new double[] { 0, 1e-60 }, new double[] { 1, 2 }, new DasWriteOptions()];
        yield return [new double[] { 0, 1 }, new double[] { 1, 2 }, new DasWriteOptions { Resample = 1 }];
        yield return [new double[] { 0, 1 }, new double[] { 1, 2 }, new DasWriteOptions { Resample = 16385 }];
        yield return [new double[] { 0, 1 }, new double[] { 1, 2 }, new DasWriteOptions { TimeUnit = (TimeUnit)999 }];
        yield return [new double[] { 0, 1 }, new double[] { 1, 2 }, new DasWriteOptions { Weighting = (SignalWeighting)999 }];
        yield return [new double[] { 0, 0 }, new double[] { 1, 2 }, new DasWriteOptions { SortTime = true, Resample = 2 }];
    }

    [Theory]
    [MemberData(nameof(InvalidTraces))]
    public void InvalidDataNeverCreatesOrOverwritesOutput(double[] time, double[] signal, DasWriteOptions options)
    {
        Assert.ThrowsAny<ArgumentException>(() => DasWriter.Write(this.Output, time, signal, options));
        Assert.False(File.Exists(this.Output));
        File.WriteAllText(this.Output, "keep");
        Assert.ThrowsAny<ArgumentException>(() => DasWriter.Write(this.Output, time, signal, options with { Overwrite = true }));
        Assert.Equal("keep", File.ReadAllText(this.Output));
    }

    [Fact]
    public void InvalidPromptGridsAndExtrapolationAreRejected()
    {
        Assert.Throws<ArgumentException>(() => DasWriter.Write(this.Output, [0, 1], [1, 2], promptTime: [0, 1]));
        Assert.Throws<ArgumentException>(() => DasWriter.Write(this.Output, [0, 1], [1, 2], prompt: [1]));
        Assert.Throws<ArgumentException>(() => DasWriter.Write(this.Output, [0, 1], [1, 2], prompt: [1, 2], promptTime: [0, 2]));
        Assert.Throws<ArgumentException>(() => DasWriter.Write(this.Output, [0, 2], [1, 2], new() { Resample = 3 }, [1, 2], [0, 1]));
        Assert.False(File.Exists(this.Output));
    }

    [Fact]
    public void OverwriteIsExplicitAndLargeMetadataIsValidatedFirst()
    {
        File.WriteAllText(this.Output, "keep");
        Assert.Throws<IOException>(() => DasWriter.Write(this.Output, [0, 1], [1, 2]));
        Assert.Throws<ArgumentException>(() => DasWriter.Write(this.Output, [0, 1], [1, 2], new() { Overwrite = true, Comment = new string('x', 4_000_000) }));
        Assert.Equal("keep", File.ReadAllText(this.Output));
        DasWriter.Write(this.Output, [0, 1], [1, 2], new() { Overwrite = true });
        Assert.Equal(new double[] { 1, 2 }, new CompoundFileReader(this.Output).Array("Decays/1/Data"));
    }

    [Theory]
    [InlineData(2)] [InlineData(1021)] [InlineData(1022)] [InlineData(2048)] [InlineData(16384)]
    public void MiniAndRegularStreamsRoundTripAtBoundaries(int count)
    {
        var values = Enumerable.Range(0, count).Select(i => (double)i).ToArray();
        DasWriter.Write(this.Output, values, values, new() { Weighting = SignalWeighting.Poisson }, values);
        var file = new CompoundFileReader(this.Output);
        Assert.Equal(values, file.Array("Decays/1/Data"));
        Assert.Equal(values, file.Array("Prompts/1/Data"));
        Assert.Equal(values.Select(v => (double)(float)(1 / Math.Max(v, 1))), file.Array("Decays/1/Weights"));
        Assert.Equal(count, BitConverter.ToInt16(file.Streams["Decays/1/IDM"], 22));
        Assert.Equal(file.Streams["Information/TCal"], file.Streams["Prompts/1/RDM"][12..16]);
    }

    [Theory]
    [InlineData(16385)] [InlineData(25001)]
    public void UnreducedOversizedTracesAreRejected(int count)
    {
        var values = Enumerable.Range(0, count).Select(i => (double)i).ToArray();
        Assert.Throws<ArgumentException>(() => DasWriter.Write(this.Output, values, values));
        Assert.False(File.Exists(this.Output));
    }

    [Fact]
    public void RebinningSumsCountsAtCentresAndNeverDropsNonzeroTail()
    {
        var time = (stackalloc double[] { -2, -1, 0, 1, 2 });
        var result = TraceProcessing.RebinCounts(time, [1, 2, 3, 4, 0], [5, 6, 7, 8, 0]);
        Assert.Equal(new double[] { -1.5, 0.5 }, result.Time.ToArray());
        Assert.Equal(new double[] { 3, 7 }, result.Signal.ToArray());
        Assert.Equal(new double[] { 11, 15 }, result.Prompt.ToArray());
        Assert.True(result.HasPrompt);
        Assert.False(TraceProcessing.RebinCounts([0, 1, 2, 3], [1, 2, 3, 4]).HasPrompt);
        Assert.Throws<ArgumentException>(() => TraceProcessing.RebinCounts([0, 1, 2, 3, 4], [1, 2, 3, 4, 1]));
        Assert.Throws<ArgumentException>(() => TraceProcessing.RebinCounts([0, 1, 2, 3, 4], [1, 2, 3, 4, 0], [0, 0, 0, 0, 1]));
        Assert.Throws<ArgumentException>(() => TraceProcessing.RebinCounts([0, 1, 2, 4], [1, 2, 3, 4]));
        Assert.Throws<ArgumentException>(() => TraceProcessing.RebinCounts([0, 1, 2, 3], [1, -2, 3, 4]));
        Assert.Throws<ArgumentException>(() => TraceProcessing.RebinCounts([0, 1, 2, 3], [1, 2, 3, 4], [1, 2]));
        Assert.Throws<ArgumentOutOfRangeException>(() => TraceProcessing.RebinCounts([0, 1, 2, 3], [1, 2, 3, 4], factor: 0));
        Assert.Throws<ArgumentException>(() => TraceProcessing.RebinCounts([0, 1, 2, 3], [1, 2, 3, 4], factor: 3));
        Assert.Throws<ArgumentException>(() => TraceProcessing.RebinCounts([0, 1, 2, 3], [double.MaxValue, double.MaxValue, 1, 1]));
    }

    [Theory]
    [InlineData(",")] [InlineData("\t")] [InlineData(";")] [InlineData("|")] [InlineData("   ")]
    public void ReaderDetectsDelimitersHeadersAndFortranNumbers(string separator)
    {
        var path = Table($"# comment\n\ntime{separator}signal\n0{separator}2.5D+0\n1{separator}-3\n");
        foreach (var columns in new ColumnSelector[][] { [0, 1], ["time", "signal"] })
        {
            var data = DelimitedTextReader.LoadColumns(path, columns);
            Assert.Equal(new double[] { 0, 1 }, data[0]);
            Assert.Equal(new double[] { 2.5, -3 }, data[1]);
        }
    }

    [Fact]
    public void ReaderSupportsQuotedNamesPreamblesAndInvariantCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var path = Table("preamble\nversion=99\n\"signal, a\",\"time (ns)\",unused\n2.5,0,x\n3,1,y\n");
            var data = DelimitedTextReader.LoadColumns(path, ["time (ns)", "signal, a"]);
            Assert.Equal(new double[] { 0, 1 }, data[0]);
            Assert.Equal(new double[] { 2.5, 3 }, data[1]);
            Assert.Equal(data[1], DelimitedTextReader.LoadColumns(path, [1, 0], new() { SkipRows = 2 })[1]);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData("0,bad\n1,2\n2,3\n", 1)]
    [InlineData("t,y\n0,1\n1,bad\n2,3\n", 3)]
    [InlineData("0,1\n1,nan\n", 2)]
    [InlineData("0,1\n1,inf\n", 2)]
    [InlineData("0,1\n1\n", 2)]
    [InlineData("0,1\n1,\"bad\n", 2)]
    public void BadRowsAreNotSilentlySkipped(string text, int line)
    {
        var path = Table(text);
        var error = Assert.Throws<FormatException>(() => DelimitedTextReader.LoadColumns(path));
        Assert.Contains($"{path}:{line}:", error.Message);
    }

    [Fact]
    public void ReaderRejectsAmbiguousOrMissingHeaders()
    {
        Assert.Throws<FormatException>(() => DelimitedTextReader.LoadColumns(Table("time,time,y\n0,0,1\n1,1,2"), ["time", "y"]));
        Assert.Throws<FormatException>(() => DelimitedTextReader.LoadColumns(Table("time,y\n0,1\n1,2"), ["missing", "y"]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ColumnSelector(-1));
    }

    [Fact]
    public void TextConversionSupportsSeparatePromptUnitsAndProtectsBothInputs()
    {
        var decay = Table("time,signal\n0,2\n1,3\n2,4\n");
        var prompt = Table("preamble\n0;0\n1000;8\n2000;0\n", "prompt.txt");
        var options = new TextConversionOptions
        {
            Writer = new() { TimeUnit = TimeUnit.Nanoseconds, Overwrite = true },
            PromptFile = new(prompt) { TimeUnit = TimeUnit.Picoseconds, Reader = new() { SkipRows = 1 } }
        };
        TextToDasConverter.ConvertFile(decay, this.Output, options);
        Assert.Equal(new double[] { 0, 8, 0 }, new CompoundFileReader(this.Output).Array("Prompts/1/Data"));
        Assert.Throws<ArgumentException>(() => TextToDasConverter.ConvertFile(decay, decay, options));
        Assert.Throws<ArgumentException>(() => TextToDasConverter.ConvertFile(decay, prompt, options));
        Assert.Throws<ArgumentException>(() => TextToDasConverter.ConvertFile(decay, this.Output, options with { PromptColumn = 2 }));
        Assert.Throws<ArgumentException>(() => TextToDasConverter.ConvertFile(decay, this.Output, options with { TimeScale = double.NaN }));
    }

    [Fact]
    public void CsvSelectionPrecedesRebinAndRecordsProvenance()
    {
        var info = CsvToDasConverter.ConvertFile(Table(Csv), this.Output, new() { StartTime = -1, EndTime = 2, RebinFactor = 2 });
        var file = new CompoundFileReader(this.Output);
        Assert.Equal(new double[] { 5, 9 }, file.Array("Decays/1/Data"));
        Assert.Equal(new double[] { 25, 29 }, file.Array("Prompts/1/Data"));
        Assert.Equal(2, file.Calibration);
        Assert.Equal(-0.5e-9, info.FirstTimeSeconds);
        Assert.Contains("Selected grid: 4 samples, -1 to 2 ns", file.Text("Decays/1/Comment"));
        Assert.Contains("Removed 0 trailing samples", file.Text("Decays/1/Comment"));
    }

    [Theory]
    [InlineData(-1d, 1d, 3, -1d)] [InlineData(0d, null, 4, 0d)] [InlineData(null, 0d, 3, -2d)]
    [InlineData(-0.5, 1.5, 2, 0d)] [InlineData(-10d, 10d, 6, -2d)]
    public void CsvWindowsAreInclusiveWithOptionalBounds(double? start, double? end, int count, double first)
    {
        var info = CsvToDasConverter.ConvertFile(Table(Csv), this.Output, new() { StartTime = start, EndTime = end });
        Assert.Equal(count, info.Channels);
        Assert.Equal(first * 1e-9, info.FirstTimeSeconds);
    }

    [Theory]
    [InlineData(1d, -1d)] [InlineData(double.NaN, null)] [InlineData(null, double.PositiveInfinity)]
    [InlineData(4d, null)] [InlineData(null, -3d)] [InlineData(0d, 0d)]
    public void InvalidWindowsDoNotOverwrite(double? start, double? end)
    {
        var source = Table(Csv);
        File.WriteAllText(this.Output, "keep");
        Assert.Throws<ArgumentException>(() => CsvToDasConverter.ConvertFile(source, this.Output, new() { StartTime = start, EndTime = end, Overwrite = true }));
        Assert.Equal("keep", File.ReadAllText(this.Output));
    }

    [Fact]
    public void FilmConversionPreservesAllCountsAndSelectsWindowBeforeAutomaticRebin()
    {
        var source = Path.Combine(RepositoryRoot, "python", "Y6 film-1exp.CSV");
        var data = DelimitedTextReader.LoadColumns(source, [0, 1, 2], new() { Delimiter = ",", SkipRows = 1 });
        var info = CsvToDasConverter.ConvertFile(source, this.Output);
        var file = new CompoundFileReader(this.Output);
        Assert.Equal(12500, info.Channels);
        Assert.Equal(data[1].Sum(), file.Array("Decays/1/Data").Sum());
        Assert.Equal(data[2].Sum(), file.Array("Prompts/1/Data").Sum());
        Assert.Equal(13_828_222, file.Array("Decays/1/Data").Sum());
        Assert.Equal(5_566_488, file.Array("Prompts/1/Data").Sum());
        Assert.Equal(0.004f, file.Calibration);
        Assert.InRange(Math.Abs(-4.999e-9 - info.FirstTimeSeconds), 0, 1e-20);
        Assert.Equal(Enumerable.Range(0, 12500).Select(i => data[1][2 * i] + data[1][2 * i + 1]), file.Array("Decays/1/Data"));
        info = CsvToDasConverter.ConvertFile(source, this.Output, new() { StartTime = -2, EndTime = 20, Overwrite = true });
        Assert.Equal(11001, info.Channels);
        Assert.Equal(0.002f, new CompoundFileReader(this.Output).Calibration);
    }

    [Fact]
    public void SuppliedReportReproducesReferenceStreams()
    {
        var folder = Path.Combine(RepositoryRoot, "python");
        var source = new CompoundFileReader(Path.Combine(folder, "Y6 CB sol.das"));
        TextToDasConverter.ConvertFile(Path.Combine(folder, "Y6 CB sol-1exp.txt"), this.Output, new()
        {
            TimeColumn = "Chan", SignalColumn = "Decay", PromptColumn = "Prompt", TimeScale = source.Calibration,
            Writer = new() { TimeUnit = TimeUnit.Nanoseconds, Weighting = SignalWeighting.Poisson }
        });
        var actual = new CompoundFileReader(this.Output);
        foreach (var key in new[] { "Decays/1/Data", "Prompts/1/Data", "Decays/1/Weights", "Decays/1/IDM", "Prompts/1/IDM", "Information/TCal" })
            Assert.Equal(source.Streams[key], actual.Streams[key]);
    }

    [Fact]
    public void FolderConversionOrdersSkipsContinuesAfterFailureAndIgnoresSubfolders()
    {
        Table(Csv, "b.CSV"); Table(Csv, "A.csv"); Table("broken", "bad.csv"); Table(Csv, "ignore.txt");
        Directory.CreateDirectory(Path.Combine(this.root, "sub"));
        File.WriteAllText(Path.Combine(this.root, "sub", "nested.csv"), Csv);
        var output = Path.Combine(this.root, "output");
        var result = CsvToDasConverter.ConvertFolder(this.root, output, new() { StartTime = -1, EndTime = 2, RebinFactor = 2 });
        Assert.Equal((2, 0, 1), (result.Converted, result.Skipped, result.Failed));
        Assert.Equal(expected, result.Files.ToArray().Select(f => Path.GetFileName(f.InputPath)));
        Assert.Equal(new double[] { 5, 9 }, new CompoundFileReader(Path.Combine(output, "A.das")).Array("Decays/1/Data"));
        Assert.Equal(2, Directory.GetFiles(output).Length);
        Assert.Equal(Csv, File.ReadAllText(Path.Combine(this.root, "A.csv")));
        result = CsvToDasConverter.ConvertFolder(this.root, output);
        Assert.Equal((0, 2, 1), (result.Converted, result.Skipped, result.Failed));
        result = CsvToDasConverter.ConvertFolder(this.root, output, new() { Overwrite = true });
        Assert.Equal((2, 0, 1), (result.Converted, result.Skipped, result.Failed));
    }

    [Fact]
    public void EmptyFolderAndInvalidOptionsDoNotCreateOutputDirectory()
    {
        var output = Path.Combine(this.root, "output");
        Assert.Empty(CsvToDasConverter.ConvertFolder(this.root, output).Files.ToArray());
        Assert.False(Directory.Exists(output));
        Table(Csv);
        Assert.Throws<ArgumentException>(() => CsvToDasConverter.ConvertFolder(this.root, output, new() { RebinFactor = 0 }));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void CsvProtectsInputIncludingWindowsHardLinks()
    {
        var source = Table(Csv);
        Assert.Throws<ArgumentException>(() => CsvToDasConverter.ConvertFile(source, source, new() { Overwrite = true }));
        if (OperatingSystem.IsWindows())
        {
            Assert.True(CreateHardLink(this.Output, source, IntPtr.Zero));
            Assert.Throws<ArgumentException>(() => CsvToDasConverter.ConvertFile(source, this.Output, new() { Overwrite = true }));
        }
        Assert.Equal(Csv, File.ReadAllText(source));
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string filename, string existingFilename, IntPtr securityAttributes);

    // Kept under .artifacts for the independent Python/native-Windows verification script.
    [Fact]
    public void GeneratePythonParityFixtures()
    {
        var folder = Path.Combine(RepositoryRoot, ".artifacts", "python-parity");
        Directory.CreateDirectory(folder);
        foreach (var count in new[] { 2, 1021, 1022, 2048, 16384 })
        {
            var values = Enumerable.Range(0, count).Select(i => (double)i).ToArray();
            DasWriter.Write(Path.Combine(folder, $"boundary-{count}.das"), values, values,
                new() { Overwrite = true, Weighting = SignalWeighting.Poisson }, values);
        }
        DasWriter.Write(Path.Combine(folder, "signed.das"), [4, 4.5, 5], [-2.5, 0.25, 1.75],
            new() { Overwrite = true, TimeUnit = TimeUnit.Nanoseconds, Name = "試料 🌟" });
        DasWriter.Write(Path.Combine(folder, "resample.das"), [0, 1, 3], [0, 10, 0],
            new() { Overwrite = true, Resample = 4 }, [0, 6, 0], [0, 1.5, 3]);
        CsvToDasConverter.ConvertFile(Path.Combine(RepositoryRoot, "python", "Y6 film-1exp.CSV"), Path.Combine(folder, "film.das"), new() { Overwrite = true });
        CsvToDasConverter.ConvertFile(Path.Combine(RepositoryRoot, "python", "Y6 film-1exp.CSV"), Path.Combine(folder, "window.das"), new() { Overwrite = true, StartTime = -2, EndTime = 20 });
    }
}
