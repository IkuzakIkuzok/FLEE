using FLEE.Core;

var folder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../allocation-output"));
Directory.CreateDirectory(folder);
foreach (var count in new[] { 2048, DasWriter.MaxChannels })
{
    var time = Enumerable.Range(0, count).Select(i => (double)i).ToArray();
    var options = new DasWriteOptions { Name = "Allocation probe", Overwrite = true, Weighting = SignalWeighting.Poisson };
    var path = Path.Combine(folder, $"{count}.das");
    Action write = () => DasWriter.Write(path, time, time, options, time);
    for (var i = 0; i < 5; i++) write();
    var start = GC.GetAllocatedBytesForCurrentThread();
    const int iterations = 20;
    for (var i = 0; i < iterations; i++) write();
    var bytes = (GC.GetAllocatedBytesForCurrentThread() - start) / iterations;
    Console.WriteLine($"{count} channels: {bytes:N0} allocated bytes/write; {new FileInfo(path).Length:N0} output bytes");
}
