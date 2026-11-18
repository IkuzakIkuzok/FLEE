using System.Text;

namespace FLEE.Core.Tests;

// Test-only reader independent of the production writer. Validates chains and directory reachability.
internal sealed class CompoundFileReader
{
    internal Dictionary<string, byte[]> Streams { get; } = new(StringComparer.Ordinal);
    internal HashSet<string> Storages { get; } = new(StringComparer.Ordinal);

    internal CompoundFileReader(string path)
    {
        var raw = File.ReadAllBytes(path);
        Xunit.Assert.Equal(Convert.FromHexString("D0CF11E0A1B11AE1"), raw[..8]);
        var sectorSize = 1 << BitConverter.ToUInt16(raw, 30);
        byte[] Sector(uint index) => raw[((int)(index + 1) * sectorSize)..((int)(index + 2) * sectorSize)];
        List<uint> fat = [];
        for (var i = 0; i < 109; i++)
        {
            var index = BitConverter.ToUInt32(raw, 76 + i * 4);
            if (index >= 0xFFFFFFFA) continue;
            var sector = Sector(index);
            for (var j = 0; j < sectorSize; j += 4) fat.Add(BitConverter.ToUInt32(sector, j));
        }
        uint[] Chain(uint start, IReadOnlyList<uint> table)
        {
            HashSet<uint> seen = [];
            List<uint> chain = [];
            while (start < 0xFFFFFFFA)
            {
                Xunit.Assert.True(seen.Add(start), "Cyclic sector chain");
                chain.Add(start);
                start = table[(int)start];
            }
            Xunit.Assert.Equal(0xFFFFFFFEu, start);
            return [.. chain];
        }
        byte[] Regular(uint start) => [.. Chain(start, fat).SelectMany(Sector)];
        var directory = Regular(BitConverter.ToUInt32(raw, 48));
        var miniStream = Regular(BitConverter.ToUInt32(directory, 116));
        var miniFatData = Regular(BitConverter.ToUInt32(raw, 60));
        var miniFat = Enumerable.Range(0, miniFatData.Length / 4).Select(i => BitConverter.ToUInt32(miniFatData, i * 4)).ToArray();
        HashSet<uint> visited = [];
        void Walk(uint index, string parent)
        {
            if (index == uint.MaxValue) return;
            Xunit.Assert.True(visited.Add(index), "Cyclic directory tree");
            var offset = (int)index * 128;
            var nameLength = BitConverter.ToUInt16(directory, offset + 64) - 2;
            var name = Encoding.Unicode.GetString(directory, offset, nameLength);
            uint left = BitConverter.ToUInt32(directory, offset + 68), right = BitConverter.ToUInt32(directory, offset + 72);
            var child = BitConverter.ToUInt32(directory, offset + 76);
            Walk(left, parent);
            var streamPath = parent + name;
            if (directory[offset + 66] == 1)
            {
                this.Storages.Add(streamPath);
                Walk(child, streamPath + "/");
            }
            else
            {
                var start = BitConverter.ToUInt32(directory, offset + 116);
                var size = checked((int)BitConverter.ToUInt64(directory, offset + 120));
                var contents = size >= 4096 ? Regular(start) : [.. Chain(start, miniFat).SelectMany(i => miniStream[((int)i * 64)..((int)(i + 1) * 64)])];
                Xunit.Assert.True(contents.Length >= size);
                this.Streams.Add(streamPath, contents[..size]);
            }
            Walk(right, parent);
        }
        Walk(BitConverter.ToUInt32(directory, 76), "");
    }

    internal double[] Array(string name)
    {
        var data = this.Streams[name];
        Xunit.Assert.Equal(1, BitConverter.ToInt32(data, 0));
        var count = BitConverter.ToInt32(data, 4);
        Xunit.Assert.Equal(8 + 4 * count, data.Length);
        return [.. Enumerable.Range(0, count).Select(i => (double)BitConverter.ToSingle(data, 8 + i * 4))];
    }

    internal string Text(string name)
    {
        var data = this.Streams[name];
        var count = checked((int)BitConverter.ToUInt32(data, 0));
        Xunit.Assert.Equal(4 + count * 2, data.Length);
        return Encoding.Unicode.GetString(data, 4, count * 2);
    }

    internal float Calibration => BitConverter.ToSingle(this.Streams["Information/TCal"]);
}
