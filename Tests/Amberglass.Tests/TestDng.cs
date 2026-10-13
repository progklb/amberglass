namespace Amberglass.Tests;

/// <summary>
/// Writes small DNG-shaped TIFFs for the readers that walk them: IFD0 plus SubIFDs, each image a
/// single strip. Little-endian, as Lightroom writes them.
/// </summary>
internal static class TestDng
{
    public const ushort Short = 3;
    public const ushort Long = 4;

    public sealed record Entry(ushort Tag, ushort Type, params uint[] Values);

    /// <summary>One image: its tags, and the bytes its single strip holds (tags 273/279 are added).</summary>
    public sealed record Image(byte[] Data, params Entry[] Entries);

    public static byte[] Build(Image ifd0, params Image[] subs)
    {
        const int EntrySize = 12;

        // Strip location tags are added to every image, and IFD0 points at the SubIFDs.
        var images = new[] { ifd0 }.Concat(subs).ToArray();
        var entries = images
            .Select((image, i) => image.Entries
                .Concat([new Entry(273, Long, 0), new Entry(279, Long, (uint)image.Data.Length)])
                .Concat(i == 0 && subs.Length > 0 ? [new Entry(330, Long, new uint[subs.Length])] : [])
                .OrderBy(e => e.Tag)
                .ToList())
            .ToList();

        static int Size(Entry e) => (e.Type == Short ? 2 : 4) * e.Values.Length;

        var ifdOffsets = new int[images.Length];
        var next = 8;
        for (var i = 0; i < images.Length; i++)
        {
            ifdOffsets[i] = next;
            next += 2 + (entries[i].Count * EntrySize) + 4;
        }

        var overflowStart = next;
        next += entries.SelectMany(list => list).Where(e => Size(e) > 4).Sum(Size);

        var dataOffsets = new int[images.Length];
        for (var i = 0; i < images.Length; i++)
        {
            dataOffsets[i] = next;
            next += images[i].Data.Length;
        }

        using var memory = new MemoryStream();
        using var writer = new BinaryWriter(memory);
        var overflow = new MemoryStream();

        writer.Write("II"u8);
        writer.Write((ushort)42);
        writer.Write(8u);

        for (var i = 0; i < images.Length; i++)
        {
            writer.Write((ushort)entries[i].Count);

            foreach (var entry in entries[i])
            {
                var values = entry.Tag switch
                {
                    273 => [(uint)dataOffsets[i]],
                    330 => ifdOffsets.Skip(1).Select(o => (uint)o).ToArray(),
                    _ => entry.Values
                };

                writer.Write(entry.Tag);
                writer.Write(entry.Type);
                writer.Write((uint)values.Length);

                var bytes = values.SelectMany(v => entry.Type == Short
                    ? BitConverter.GetBytes((ushort)v)
                    : BitConverter.GetBytes(v)).ToArray();

                if (bytes.Length <= 4)
                {
                    writer.Write(bytes);
                    writer.Write(new byte[4 - bytes.Length]);
                }
                else
                {
                    writer.Write((uint)(overflowStart + overflow.Length));
                    overflow.Write(bytes);
                }
            }

            writer.Write(0u);
        }

        writer.Write(overflow.ToArray());

        foreach (var image in images)
        {
            writer.Write(image.Data);
        }

        return memory.ToArray();
    }
}
