using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace FLEE.Core;

// Minimal CFB v3 writer for the fixed DAS hierarchy. No COM or template file is required.
internal static class CompoundFileWriter
{
    private const uint Free = 0xFFFFFFFF, End = 0xFFFFFFFE, FatSector = 0xFFFFFFFD;
    private sealed class Entry(string name, byte kind, byte[]? data = null)
    {
        public string Name = name;
        public byte Kind = kind, Color = 1;
        public byte[] Data = data ?? [];
        public List<int> Children = [];
        public uint Left = Free, Right = Free, Child = Free, Start = End;
        public ulong Size = (ulong)(data?.Length ?? 0);
    }

    internal static byte[] Write(IReadOnlyList<KeyValuePair<string, byte[]>> streams, IEnumerable<string> storages)
    {
        // Reject pathological metadata before allocating the compound container.
        if (streams.Sum(p => (long)p.Value.Length) > 109L * 128 * 512)
            throw new ArgumentException("DAS file exceeds this writer's size limit (about 7 MB); shorten metadata.");
        List<Entry> entries = [new("Root Entry", 5)];
        Dictionary<string, int> paths = new(StringComparer.Ordinal) { [""] = 0 };
        int Storage(string path)
        {
            if (paths.TryGetValue(path, out var existing)) return existing;
            var slash = path.LastIndexOf('/');
            var parent = Storage(slash < 0 ? "" : path[..slash]);
            var id = entries.Count;
            paths.Add(path, id);
            entries[parent].Children.Add(id);
            entries.Add(new(path[(slash + 1)..], 1));
            return id;
        }
        foreach (var storage in storages) Storage(storage);
        foreach (var (path, data) in streams)
        {
            var slash = path.LastIndexOf('/');
            var parent = Storage(slash < 0 ? "" : path[..slash]);
            entries[parent].Children.Add(entries.Count);
            entries.Add(new(path[(slash + 1)..], 2, data));
        }
        uint Tree(int[] ordered, int redLevel, int lo, int hi, int level = 0)
        {
            if (lo >= hi) return Free;
            int mid = (lo + hi) / 2, index = ordered[mid];
            var node = entries[index];
            node.Color = (byte)(level == redLevel ? 0 : 1);
            node.Left = Tree(ordered, redLevel, lo, mid, level + 1);
            node.Right = Tree(ordered, redLevel, mid + 1, hi, level + 1);
            return (uint)index;
        }
        foreach (var entry in entries)
        {
            var ordered = entry.Children.OrderBy(i => entries[i].Name.Length)
                .ThenBy(i => entries[i].Name.ToUpperInvariant(), StringComparer.Ordinal).ToArray();
            entry.Child = Tree(ordered, BitOperations.Log2((uint)ordered.Length + 1), 0, ordered.Length);
        }

        List<byte[]> sectors = [];
        List<uint> fat = [], miniFat = [];
        using var miniData = new MemoryStream();
        uint Allocate(byte[] data)
        {
            if (data.Length == 0) return End;
            var start = (uint)sectors.Count;
            var count = (data.Length + 511) / 512;
            for (var i = 0; i < count; i++)
            {
                var sector = new byte[512];
                data.AsSpan(i * 512, Math.Min(512, data.Length - i * 512)).CopyTo(sector);
                sectors.Add(sector);
                fat.Add(i + 1 < count ? start + (uint)i + 1 : End);
            }
            return start;
        }
        foreach (var entry in entries.Skip(1).Where(e => e.Kind == 2 && e.Size > 0))
        {
            if (entry.Size >= 4096) { entry.Start = Allocate(entry.Data); continue; }
            entry.Start = (uint)miniFat.Count;
            var count = (entry.Data.Length + 63) / 64;
            miniData.Write(entry.Data);
            miniData.Write(new byte[count * 64 - entry.Data.Length]);
            for (var i = 0; i < count; i++)
                miniFat.Add(i + 1 < count ? entry.Start + (uint)i + 1 : End);
        }
        entries[0].Start = Allocate(miniData.ToArray());
        entries[0].Size = (ulong)miniData.Length;
        var miniFatSectors = (miniFat.Count + 127) / 128;
        miniFat.AddRange(Enumerable.Repeat(Free, miniFatSectors * 128 - miniFat.Count));
        var miniStart = Allocate(UIntArray(miniFat));

        var directory = new byte[((entries.Count + 3) / 4) * 512];
        for (var i = 0; i < directory.Length / 128; i++)
        {
            var record = directory.AsSpan(i * 128, 128);
            if (i >= entries.Count)
            {
                for (var offset = 68; offset <= 76; offset += 4) Put32(record, offset, Free);
                continue;
            }
            var entry = entries[i];
            var name = Encoding.Unicode.GetBytes(entry.Name + '\0');
            if (name.Length > 64) throw new ArgumentException("Internal CFB directory name is too long.");
            name.CopyTo(record);
            BinaryPrimitives.WriteUInt16LittleEndian(record[64..], (ushort)name.Length);
            record[66] = entry.Kind;
            record[67] = entry.Color;
            Put32(record, 68, entry.Left); Put32(record, 72, entry.Right); Put32(record, 76, entry.Child);
            Put32(record, 116, entry.Kind == 1 ? 0 : entry.Start);
            BinaryPrimitives.WriteUInt64LittleEndian(record[120..], entry.Size);
        }
        var directoryStart = Allocate(directory);
        var fatCount = 1;
        while (fatCount * 128 < sectors.Count + fatCount) fatCount++;
        if (fatCount > 109)
            throw new ArgumentException("DAS file exceeds this writer's size limit (about 7 MB); shorten metadata.");
        var fatStart = (uint)sectors.Count;
        fat.AddRange(Enumerable.Repeat(FatSector, fatCount));
        fat.AddRange(Enumerable.Repeat(Free, 128 * fatCount - fat.Count));
        var fatData = UIntArray(fat);
        for (var offset = 0; offset < fatData.Length; offset += 512) sectors.Add(fatData[offset..(offset + 512)]);

        var header = new byte[512];
        Convert.FromHexString("D0CF11E0A1B11AE1").CopyTo(header, 0);
        ushort[] fields = [0x3E, 3, 0xFFFE, 9, 6];
        for (var i = 0; i < fields.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(24 + i * 2), fields[i]);
        uint[] values = [0, (uint)fatCount, directoryStart, 0, 4096, miniStart, (uint)miniFatSectors, End, 0];
        for (var i = 0; i < values.Length; i++) Put32(header, 40 + i * 4, values[i]);
        for (uint i = 0; i < 109; i++) Put32(header, 76 + (int)i * 4, i < fatCount ? fatStart + i : Free);
        using var result = new MemoryStream();
        result.Write(header);
        foreach (var sector in sectors) result.Write(sector);
        return result.ToArray();
    }

    private static void Put32(Span<byte> buffer, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[offset..], value);

    private static byte[] UIntArray(List<uint> values)
    {
        var data = new byte[values.Count * 4];
        for (var i = 0; i < values.Count; i++) Put32(data, i * 4, values[i]);
        return data;
    }
}
