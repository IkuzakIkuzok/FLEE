using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace FLEE.Core;

// Minimal CFB v3 writer for the fixed DAS hierarchy. No COM or template file is required.
internal static class CompoundFileWriter
{
    private const uint Free = 0xFFFFFFFF, End = 0xFFFFFFFE, FatSector = 0xFFFFFFFD;
    private const int SectorSize = 512, MiniSectorSize = 64, MiniStreamCutoff = 4096, MaxFatSectors = 109;
    private const string SizeLimitMessage = "DAS file exceeds this writer's size limit (about 7 MB); shorten metadata.";

    internal static PreparedFile Prepare(IReadOnlyList<KeyValuePair<string, byte[]>> streams, IEnumerable<string> storages)
    {
        // Reject pathological metadata before allocating the compound container.
        if (streams.Sum(p => (long)p.Value.Length) > (long)MaxFatSectors * 128 * SectorSize)
            throw new ArgumentException(SizeLimitMessage);
        var directory = new DirectoryBuilder();
        foreach (var storage in storages) directory.AddStorage(storage);
        foreach (var (path, data) in streams) directory.AddStream(path, data);
        directory.BuildSearchTrees();

        var allocator = new SectorAllocator();
        var (miniStart, miniFatSectors) = AllocateStreams(directory.Entries, allocator);
        var directoryStart = allocator.Allocate(directory.Serialize());
        var (fatStart, fatCount) = allocator.AllocateFat();
        var header = CreateHeader(fatStart, fatCount, directoryStart, miniStart, miniFatSectors);
        return allocator.PrepareFile(header);
    }

    // Retains the already serialized stream payloads, without copying them into sectors or a file-sized buffer.
    internal sealed class PreparedFile(byte[] header, ReadOnlyMemory<byte>[] blocks)
    {
        internal void WriteTo(Stream destination)
        {
            Span<byte> padding = stackalloc byte[SectorSize];
            padding.Clear();
            destination.Write(header);
            foreach (var block in blocks)
            {
                destination.Write(block.Span);
                var remaining = (SectorSize - block.Length % SectorSize) % SectorSize;
                if (remaining > 0) destination.Write(padding[..remaining]);
            }
        }
    }

    private sealed class Entry(string name, byte kind, byte[]? data = null)
    {
        internal string Name = name;
        internal byte Kind = kind, Color = 1;
        internal byte[] Data = data ?? [];
        internal List<int> Children = [];
        internal uint Left = Free, Right = Free, Child = Free, Start = End;
        internal ulong Size = (ulong)(data?.Length ?? 0);
    }

    private sealed class DirectoryBuilder
    {
        internal List<Entry> Entries { get; } = [new("Root Entry", 5)];
        private readonly Dictionary<string, int> paths = new(StringComparer.Ordinal) { [""] = 0 };

        internal int AddStorage(string path)
        {
            if (this.paths.TryGetValue(path, out var existing)) return existing;
            var (parentPath, name) = SplitPath(path);
            var parent = AddStorage(parentPath);
            var id = this.Entries.Count;
            this.paths.Add(path, id);
            this.Entries[parent].Children.Add(id);
            this.Entries.Add(new(name, 1));
            return id;
        }

        internal void AddStream(string path, byte[] data)
        {
            var (parentPath, name) = SplitPath(path);
            var parent = AddStorage(parentPath);
            this.Entries[parent].Children.Add(this.Entries.Count);
            this.Entries.Add(new(name, 2, data));
        }

        private static (string Parent, string Name) SplitPath(string path)
        {
            var slash = path.LastIndexOf('/');
            return (slash < 0 ? "" : path[..slash], path[(slash + 1)..]);
        }

        internal void BuildSearchTrees()
        {
            foreach (var entry in this.Entries)
            {
                var ordered = entry.Children.OrderBy(i => this.Entries[i].Name.Length)
                    .ThenBy(i => this.Entries[i].Name.ToUpperInvariant(), StringComparer.Ordinal).ToArray();
                entry.Child = BuildTree(ordered, BitOperations.Log2((uint)ordered.Length + 1), 0);
            }
        }

        private uint BuildTree(ReadOnlySpan<int> ordered, int redLevel, int level)
        {
            if (ordered.IsEmpty) return Free;
            var mid = ordered.Length / 2;
            var index = ordered[mid];
            var node = this.Entries[index];
            node.Color = (byte)(level == redLevel ? 0 : 1);
            node.Left = BuildTree(ordered[..mid], redLevel, level + 1);
            node.Right = BuildTree(ordered[(mid + 1)..], redLevel, level + 1);
            return (uint)index;
        }

        internal byte[] Serialize()
        {
            var directory = new byte[((this.Entries.Count + 3) / 4) * SectorSize];
            for (var i = 0; i < directory.Length / 128; i++)
            {
                var record = directory.AsSpan(i * 128, 128);
                if (i < this.Entries.Count) WriteEntry(record, this.Entries[i]);
                else WriteEmptyEntry(record);
            }
            return directory;
        }

        private static void WriteEntry(Span<byte> record, Entry entry)
        {
            var name = Encoding.Unicode.GetBytes(entry.Name + '\0');
            if (name.Length > 64) throw new ArgumentException("Internal CFB directory name is too long.");
            name.CopyTo(record);
            BinaryPrimitives.WriteUInt16LittleEndian(record[64..], (ushort)name.Length);
            record[66] = entry.Kind;
            record[67] = entry.Color;
            Put32(record, 68, entry.Left);
            Put32(record, 72, entry.Right);
            Put32(record, 76, entry.Child);
            Put32(record, 116, entry.Kind == 1 ? 0 : entry.Start);
            BinaryPrimitives.WriteUInt64LittleEndian(record[120..], entry.Size);
        }

        private static void WriteEmptyEntry(Span<byte> record)
        {
            Put32(record, 68, Free);
            Put32(record, 72, Free);
            Put32(record, 76, Free);
        }
    }

    // Plans regular-sector ranges without materializing or copying each sector's bytes.
    private sealed class SectorAllocator
    {
        private readonly List<ReadOnlyMemory<byte>> blocks = [];
        private readonly List<uint> fat = [];
        private int sectorCount;

        internal uint Allocate(ReadOnlyMemory<byte> data)
        {
            if (data.IsEmpty) return End;
            var start = (uint)this.sectorCount;
            var count = (data.Length + SectorSize - 1) / SectorSize;
            this.blocks.Add(data);
            this.sectorCount += count;
            for (var i = 0; i < count; i++)
                this.fat.Add(i + 1 < count ? start + (uint)i + 1 : End);
            return start;
        }

        internal (uint Start, int Count) AllocateFat()
        {
            var count = 1;
            while (count * 128 < this.sectorCount + count) count++;
            if (count > MaxFatSectors) throw new ArgumentException(SizeLimitMessage);
            var start = (uint)this.sectorCount;
            this.fat.AddRange(Enumerable.Repeat(FatSector, count));
            this.fat.AddRange(Enumerable.Repeat(Free, 128 * count - this.fat.Count));
            this.blocks.Add(UIntArray(this.fat));
            this.sectorCount += count;
            return (start, count);
        }

        internal PreparedFile PrepareFile(byte[] header) => new(header, [.. this.blocks]);
    }

    private static (uint Start, int SectorCount) AllocateStreams(List<Entry> entries, SectorAllocator allocator)
    {
        List<uint> miniFat = [];
        var miniLength = entries.Where(e => e.Kind == 2 && e.Size > 0 && e.Size < MiniStreamCutoff)
            .Sum(e => ((e.Data.Length + MiniSectorSize - 1) / MiniSectorSize) * MiniSectorSize);
        var miniData = new byte[miniLength];
        foreach (var entry in entries.Skip(1).Where(e => e.Kind == 2 && e.Size > 0))
        {
            if (entry.Size >= MiniStreamCutoff) entry.Start = allocator.Allocate(entry.Data);
            else AllocateMiniStream(entry, miniFat, miniData);
        }
        entries[0].Start = allocator.Allocate(miniData);
        entries[0].Size = (ulong)miniData.Length;
        var miniFatSectors = (miniFat.Count + 127) / 128;
        miniFat.AddRange(Enumerable.Repeat(Free, miniFatSectors * 128 - miniFat.Count));
        return (allocator.Allocate(UIntArray(miniFat)), miniFatSectors);
    }

    private static void AllocateMiniStream(Entry entry, List<uint> miniFat, Span<byte> miniData)
    {
        entry.Start = (uint)miniFat.Count;
        var count = (entry.Data.Length + MiniSectorSize - 1) / MiniSectorSize;
        entry.Data.CopyTo(miniData[(miniFat.Count * MiniSectorSize)..]);
        for (var i = 0; i < count; i++)
            miniFat.Add(i + 1 < count ? entry.Start + (uint)i + 1 : End);
    }

    private static byte[] CreateHeader(uint fatStart, int fatCount, uint directoryStart, uint miniStart, int miniFatSectors)
    {
        var header = new byte[SectorSize];
        Convert.FromHexString("D0CF11E0A1B11AE1").CopyTo(header, 0);
        ReadOnlySpan<ushort> fields = [0x3E, 3, 0xFFFE, 9, 6];
        for (var i = 0; i < fields.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(24 + i * 2), fields[i]);
        ReadOnlySpan<uint> values = [0, (uint)fatCount, directoryStart, 0, MiniStreamCutoff, miniStart, (uint)miniFatSectors, End, 0];
        for (var i = 0; i < values.Length; i++) Put32(header, 40 + i * 4, values[i]);
        for (uint i = 0; i < MaxFatSectors; i++) Put32(header, 76 + (int)i * 4, i < fatCount ? fatStart + i : Free);
        return header;
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
