using Xunit;

namespace FLEE.Core.Tests;

public sealed class TrimmingTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "FLEE.TrimTests", Guid.NewGuid().ToString("N"));
    public TrimmingTests() => Directory.CreateDirectory(this.root);
    public void Dispose() => Directory.Delete(this.root, true);

    [Fact]
    public void LongInputRemainsAvailableWhileInitialSelectionFitsLimit()
    {
        var source = Path.Combine(ConversionTests.RepositoryRoot, "python", "Y6 film-1exp.CSV");
        var data = DecayDataset.LoadCsv(source);
        Assert.Equal(25001, data.Count);
        Assert.Equal(new SampleRange(0, 16383), data.InitialRange);
        Assert.Equal(new SampleRange(0, 25000), data.FullRange);
        Assert.False(data.FullRange.FitsDasLimit);
        Assert.Equal(45, data.TimeNanoseconds[^1]);
        Assert.Equal(new SampleRange(10000, 25000), data.LimitFrom(10000));
    }

    [Fact]
    public void TimeBoundsSnapInwardAndPreserveInclusiveEndpoints()
    {
        var data = CreateSmallDataset();
        Assert.Equal(new SampleRange(1, 3), data.SelectTimeRange(-1, 1));
        Assert.Equal(new SampleRange(2, 3), data.SelectTimeRange(-0.5, 1.5));
        Assert.Equal(data.FullRange, data.SelectTimeRange(-100, 100));
        Assert.Equal(new SampleRange(2, 2), data.SelectTimeRange(0, 0));
        Assert.False(data.SelectTimeRange(0, 0).FitsDasLimit);
        Assert.Equal(2, data.FindNearestSample(0.4));
        Assert.Equal(3, data.FindNearestSample(0.6));
        Assert.Equal(0, data.FindNearestSample(-100));
        Assert.Equal(4, data.FindNearestSample(100));
    }

    [Theory]
    [InlineData(2d, -2d)] [InlineData(3d, 5d)] [InlineData(-10d, -3d)]
    [InlineData(double.NaN, 2d)] [InlineData(0d, double.PositiveInfinity)]
    public void InvalidTimeRangesAreRejected(double start, double end) =>
        Assert.Throws<ArgumentException>(() => CreateSmallDataset().SelectTimeRange(start, end));

    [Fact]
    public void ExportUsesExactSnapshotAndWritesTrimmingProvenance()
    {
        var source = Path.Combine(this.root, "sample.csv");
        File.WriteAllText(source, ",900,900\n-2,1,11\n-1,2,12\n0,3,13\n1,4,14\n2,5,15\n");
        var data = DecayDataset.LoadCsv(source);
        File.WriteAllText(source, "Changed after loading");
        var path = Path.Combine(this.root, "trimmed.das");
        var info = data.Export(path, data.SelectTimeRange(-1, 1));
        var file = new CompoundFileReader(path);
        Assert.Equal(new double[] { 2, 3, 4 }, file.Array("Decays/1/Data"));
        Assert.Equal(new double[] { 12, 13, 14 }, file.Array("Prompts/1/Data"));
        Assert.Equal(1, file.Calibration);
        Assert.Equal(-1e-9, info.FirstTimeSeconds);
        Assert.False(info.Resampled);
        Assert.Contains("Trimmed 2 samples", file.Text("Decays/1/Comment"));
        Assert.Contains("No rebinning or interpolation", file.Text("Decays/1/Comment"));
    }

    [Fact]
    public void ExportRejectsTooFewTooManyAndInvalidIndicesBeforeTouchingDestination()
    {
        var time = Enumerable.Range(0, 20000).Select(i => (double)i).ToArray();
        var data = new DecayDataset(Path.Combine(this.root, "input.csv"), time, time, time);
        var output = Path.Combine(this.root, "existing.das");
        File.WriteAllText(output, "keep");
        foreach (var range in new[] { data.FullRange, new SampleRange(0, 0), new(-1, 10), new(10, 2), new(0, 20000) })
            Assert.ThrowsAny<ArgumentException>(() => data.Export(output, range, overwrite: true));
        Assert.Equal("keep", File.ReadAllText(output));
        Assert.Throws<ArgumentException>(() => data.Export(data.SourcePath, data.InitialRange, overwrite: true));
    }

    [Fact]
    public void StreamExportMatchesPathOutputAndLeavesDestinationOpen()
    {
        var data = CreateSmallDataset();
        var range = data.SelectTimeRange(-1, 1);
        var expected = Path.Combine(this.root, "expected.das");
        data.Export(expected, range);
        var actual = Path.Combine(this.root, "actual.das");
        using (var destination = new FileStream(actual, FileMode.CreateNew, FileAccess.Write))
        {
            var result = data.Export(destination, range);
            Assert.True(destination.CanWrite);
            Assert.Equal(3, result.Channels);
            Assert.False(result.Resampled);
        }
        Assert.Equal(File.ReadAllBytes(expected), File.ReadAllBytes(actual));
        using var untouched = new MemoryStream();
        Assert.Throws<ArgumentException>(() => data.Export(untouched, new SampleRange(0, 0)));
        Assert.Equal(0, untouched.Length);
    }

    [Fact]
    public void InputBuffersAreCopiedAndUnusableCountDataIsRejected()
    {
        double[] time = [0, 1], decay = [2, 3], prompt = [4, 5];
        var data = new DecayDataset("sample.csv", time, decay, prompt);
        time[0] = 100; decay[0] = 100; prompt[0] = 100;
        Assert.Equal(0, data.TimeNanoseconds[0]);
        Assert.Equal(2, data.Decay[0]);
        Assert.Equal(4, data.Prompt[0]);
        Assert.Throws<ArgumentException>(() => new DecayDataset("sample.csv", [0, 1, 3], [1, 2, 3], [1, 2, 3]));
        Assert.Throws<ArgumentException>(() => new DecayDataset("sample.csv", [0, 0], [1, 2], [1, 2]));
        Assert.Throws<ArgumentException>(() => new DecayDataset("sample.csv", [0, 1], [-1, 2], [1, 2]));
        Assert.Throws<ArgumentException>(() => new DecayDataset("sample.csv", [0, 1], [1, 2], [1]));
    }

    private static DecayDataset CreateSmallDataset() => new("sample.csv", [-2, -1, 0, 1, 2], [1, 2, 3, 4, 5], [11, 12, 13, 14, 15]);
}
