using System.Text;
using HangSloth.Collection;

namespace HangSloth.Tests;

/// A dump cut off by an interrupted copy or a full disk is an error that says so, whatever the format,
/// and a dump of an app with thousands of threads is not rejected by ClrMD's default caps.
[Collection("Cli")]
public class DumpCompletenessTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hangsloth-cut-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string WriteFile(string name, byte[] content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static long? Extent(byte[] dump) => DumpLoader.DumpExtent(new MemoryStream(dump));

    /// An ELF core: the 64-byte header, the program headers right after it, then the data of each segment.
    internal static byte[] Elf64Core(int programHeaders, params (ulong Offset, ulong Size)[] segments)
    {
        var count = Math.Max(programHeaders, segments.Length);
        var tableEnd = 64 + 56 * count;
        var end = segments.Length == 0 ? tableEnd : Math.Max(tableEnd, (int)segments.Max(s => s.Offset + s.Size));
        var elf = new byte[end];
        elf[0] = 0x7F; elf[1] = (byte)'E'; elf[2] = (byte)'L'; elf[3] = (byte)'F';
        elf[4] = 2; elf[5] = 1; elf[6] = 1;                     // 64-bit, little endian, version 1
        BitConverter.GetBytes((ushort)4).CopyTo(elf, 16);       // ET_CORE
        BitConverter.GetBytes((ushort)62).CopyTo(elf, 18);      // x86-64
        BitConverter.GetBytes(1u).CopyTo(elf, 20);
        BitConverter.GetBytes(64UL).CopyTo(elf, 32);            // e_phoff
        BitConverter.GetBytes((ushort)64).CopyTo(elf, 52);      // e_ehsize
        BitConverter.GetBytes((ushort)56).CopyTo(elf, 54);      // e_phentsize
        BitConverter.GetBytes((ushort)Math.Min(count, 0xFFFF)).CopyTo(elf, 56);
        for (var i = 0; i < segments.Length; i++)
        {
            var at = 64 + 56 * i;
            BitConverter.GetBytes(1u).CopyTo(elf, at);          // PT_LOAD
            BitConverter.GetBytes(segments[i].Offset).CopyTo(elf, at + 8);
            BitConverter.GetBytes(segments[i].Size).CopyTo(elf, at + 32);
        }
        return elf;
    }

    [Fact]
    public void A_complete_elf_core_is_as_long_as_its_furthest_segment()
    {
        var elf = Elf64Core(3, (4096, 8192), (12288, 4096), (0, 0));
        Assert.Equal(16384, elf.Length);
        Assert.Equal(16384, Extent(elf));
    }

    [Fact]
    public void An_elf_core_cut_in_its_last_segment_is_seen_as_cut()
    {
        var elf = Elf64Core(3, (4096, 8192), (12288, 4096), (0, 0));
        Assert.Equal(16384, Extent(elf[..16000]));
    }

    [Fact]
    public void An_elf_core_cut_in_its_program_headers_is_seen_as_cut()
    {
        var elf = Elf64Core(40, (4096, 4096));
        Assert.Equal(64 + 56 * 40, Extent(elf[..1000]));
    }

    [Fact]
    public void An_elf_core_with_more_than_65534_program_headers_takes_the_count_from_section_header_0()
    {
        // PN_XNUM: e_phnum is 0xFFFF and the real count is sh_info of section header 0.
        const int count = 70_000;
        var elf = Elf64Core(count, (4096, 8));
        var withSection = new byte[elf.Length + 64];
        elf.CopyTo(withSection, 0);
        BitConverter.GetBytes((ulong)elf.Length).CopyTo(withSection, 40);   // e_shoff
        BitConverter.GetBytes((ushort)64).CopyTo(withSection, 58);          // e_shentsize
        BitConverter.GetBytes((ushort)1).CopyTo(withSection, 60);           // e_shnum
        BitConverter.GetBytes((uint)count).CopyTo(withSection, elf.Length + 44);
        Assert.Equal(64 + 56 * count, elf.Length);
        Assert.Equal(64 + 56 * count, Extent(withSection));

        BitConverter.GetBytes((uint)count + 10).CopyTo(withSection, elf.Length + 44);
        Assert.Equal(64 + 56 * (count + 10), Extent(withSection));
    }

    [Fact]
    public void A_32_bit_elf_core_is_checked_too()
    {
        var elf = new byte[52 + 32 + 100];
        elf[0] = 0x7F; elf[1] = (byte)'E'; elf[2] = (byte)'L'; elf[3] = (byte)'F';
        elf[4] = 1; elf[5] = 1; elf[6] = 1;
        BitConverter.GetBytes((ushort)4).CopyTo(elf, 16);       // ET_CORE
        BitConverter.GetBytes((ushort)40).CopyTo(elf, 18);      // ARM
        BitConverter.GetBytes(52u).CopyTo(elf, 28);             // e_phoff
        BitConverter.GetBytes((ushort)32).CopyTo(elf, 42);      // e_phentsize
        BitConverter.GetBytes((ushort)1).CopyTo(elf, 44);       // e_phnum
        BitConverter.GetBytes(1u).CopyTo(elf, 52);              // PT_LOAD
        BitConverter.GetBytes(84u).CopyTo(elf, 56);             // p_offset
        BitConverter.GetBytes(100u).CopyTo(elf, 68);            // p_filesz
        Assert.Equal(184, Extent(elf));
        Assert.Equal(184, Extent(elf[..150]));
    }

    [Fact]
    public void An_elf_file_that_is_not_a_core_and_other_files_are_not_checked()
    {
        var elf = Elf64Core(1, (4096, 4096));
        BitConverter.GetBytes((ushort)3).CopyTo(elf, 16);       // ET_DYN, a shared library
        Assert.Null(Extent(elf[..200]));
        Assert.Null(Extent("this is not a memory dump\n"u8.ToArray()));
        Assert.Null(Extent([]));
    }

    /// A minidump with a stream directory, one small stream and a Memory64 list whose memory follows it.
    internal static byte[] Minidump(params ulong[] rangeSizes)
    {
        var output = new MemoryStream();
        var w = new BinaryWriter(output);
        const uint directory = 32, streams = 3;
        uint misc = directory + 12 * streams, unused = 0, memory64 = misc + 24;
        var baseRva = (ulong)(memory64 + 16 + 16 * rangeSizes.Length);
        w.Write(0x504D444Du); w.Write(0xA793u); w.Write(streams); w.Write(directory); w.Write(0u); w.Write(0x60000000u); w.Write(2UL);
        w.Write(15u); w.Write(24u); w.Write(misc);                          // MiscInfoStream
        w.Write(0u); w.Write(0u); w.Write(unused);                          // UnusedStream
        w.Write(9u); w.Write((uint)(16 + 16 * rangeSizes.Length)); w.Write(memory64);
        w.Write(new byte[24]);
        w.Write((ulong)rangeSizes.Length); w.Write(baseRva);
        var start = 0x10000UL;
        foreach (var size in rangeSizes)
        {
            w.Write(start); w.Write(size);
            start += 0x100000;
        }
        foreach (var size in rangeSizes) w.Write(new byte[size]);
        return output.ToArray();
    }

    [Fact]
    public void A_complete_minidump_ends_with_its_last_memory_range()
    {
        var dump = Minidump(4096, 8192, 100);
        Assert.Equal(dump.Length, Extent(dump));
    }

    [Fact]
    public void A_minidump_cut_in_its_memory_is_seen_as_cut()
    {
        var dump = Minidump(4096, 8192, 100);
        Assert.Equal(dump.Length, Extent(dump[..(dump.Length - 50)]));
        Assert.Equal(dump.Length, Extent(dump[..(dump.Length / 2)]));
    }

    [Fact]
    public void A_minidump_cut_in_its_stream_directory_is_seen_as_cut()
    {
        var dump = Minidump(4096);
        Assert.Equal(32 + 3 * 12, Extent(dump[..40]));
    }

    [Fact]
    public void A_minidump_memory_list_says_where_each_range_is()
    {
        // MemoryListStream (5): a count, then { start, size, rva } for each range.
        var output = new MemoryStream();
        var w = new BinaryWriter(output);
        w.Write(0x504D444Du); w.Write(0xA793u); w.Write(1u); w.Write(32u); w.Write(0u); w.Write(0u); w.Write(0UL);
        w.Write(5u); w.Write(4u + 16); w.Write(44u);
        w.Write(1u); w.Write(0x10000UL); w.Write(500u); w.Write(64u);
        w.Write(new byte[500]);
        var dump = output.ToArray();
        Assert.Equal(564, dump.Length);
        Assert.Equal(564, Extent(dump));
        Assert.Equal(564, Extent(dump[..300]));
    }

    [Fact]
    public void A_mach_o_core_is_as_long_as_its_furthest_segment()
    {
        var output = new MemoryStream();
        var w = new BinaryWriter(output);
        w.Write(0xFEEDFACFu); w.Write(0x01000007u); w.Write(3u); w.Write(4u);     // MH_CORE
        w.Write(2u); w.Write(72u * 2); w.Write(0u); w.Write(0u);
        foreach (var (offset, size) in new (ulong, ulong)[] { (4096, 4096), (8192, 1000) })
        {
            w.Write(0x19u); w.Write(72u); w.Write(new byte[16]);                 // LC_SEGMENT_64
            w.Write(0UL); w.Write(size); w.Write(offset); w.Write(size); w.Write(7u); w.Write(7u); w.Write(0u); w.Write(0u);
        }
        w.Write(new byte[9192 - output.Length]);
        var dump = output.ToArray();
        Assert.Equal(9192, Extent(dump));
        Assert.Equal(9192, Extent(dump[..9000]));
        Assert.Equal(32 + 144, Extent(dump[..100]));
    }

    [Fact]
    public void A_cut_dump_is_exit_1_with_one_line_that_says_so_and_does_not_suggest_dac()
    {
        var elf = Elf64Core(3, (4096, 8192), (12288, 4096));
        var path = WriteFile("orders.core", elf[..16000]);

        var (exit, stdout, err) = CliTests.Run([path, "--no-color"]);
        Assert.Equal(1, exit);
        Assert.Equal("", stdout);
        Assert.Equal(
            $"hangsloth: '{path}' looks cut off: the file has 16,000 bytes, but its own headers describe at least 16,384. " +
            "Was a copy or download interrupted, or the disk full while it was written? Copy or capture the dump again.",
            err.TrimEnd());
        Assert.DoesNotContain("--dac", err);
    }

    [Fact]
    public void A_dump_with_more_program_headers_than_clrmds_default_cap_is_not_rejected_for_it()
    {
        // About two program headers per thread: an app with 5,000 threads has more than ClrMD's default of 10,000.
        var path = WriteFile("many-threads.core", Elf64Core(10_001));
        var e = Assert.Throws<HangSlothException>(() => DumpLoader.LoadDump(path));
        Assert.DoesNotContain("exceeds the maximum", e.ToString());
        Assert.DoesNotContain("looks cut off", e.Message);
    }

    [Fact]
    public void The_cut_message_uses_the_same_digits_in_every_culture()
    {
        var old = Thread.CurrentThread.CurrentCulture;
        try
        {
            // A culture that groups digits with '.', built here so it works without culture data (invariant mode).
            var dotted = (System.Globalization.CultureInfo)System.Globalization.CultureInfo.InvariantCulture.Clone();
            dotted.NumberFormat.NumberGroupSeparator = ".";
            dotted.NumberFormat.NumberDecimalSeparator = ",";
            Thread.CurrentThread.CurrentCulture = dotted;
            var path = WriteFile("invoices.core", Elf64Core(2, (4096, 8192))[..5000]);
            var (exit, _, err) = CliTests.Run([path, "--no-color"]);
            Assert.Equal(1, exit);
            Assert.Contains("the file has 5,000 bytes, but its own headers describe at least 12,288.", err);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = old;
        }
    }

    /// A file that is <paramref name="length"/> bytes long without taking that much memory or disk:
    /// <paramref name="head"/> at the start and zeros after it.
    private sealed class HugeFile(byte[] head, long length) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get; set; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Clamp(length - Position, 0, count);
            Array.Clear(buffer, offset, n);
            for (var i = 0; i < n && Position + i < head.Length; i++) buffer[offset + i] = head[Position + i];
            Position += n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) =>
            Position = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => Position + offset, _ => length + offset };
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public void Nonsense_counts_in_a_huge_file_are_not_read_into_memory()
    {
        // A minidump header that claims 4 billion streams, and a Mach-O core whose commands claim 4 GB:
        // both fit in a 100 GB file, but are not a dump ClrMD could read, so they are left to ClrMD.
        var minidump = new byte[32];
        BitConverter.GetBytes(0x504D444Du).CopyTo(minidump, 0);
        BitConverter.GetBytes(0xFFFFFFFFu).CopyTo(minidump, 8);
        BitConverter.GetBytes(32u).CopyTo(minidump, 12);
        Assert.Null(DumpLoader.DumpExtent(new HugeFile(minidump, 100L << 30)));

        var macho = new byte[32];
        BitConverter.GetBytes(0xFEEDFACFu).CopyTo(macho, 0);
        BitConverter.GetBytes(4u).CopyTo(macho, 12);
        BitConverter.GetBytes(0xFFFFFFFFu).CopyTo(macho, 16);
        BitConverter.GetBytes(0xFFFFFFFFu).CopyTo(macho, 20);
        Assert.Null(DumpLoader.DumpExtent(new HugeFile(macho, 100L << 30)));
    }

    [Fact]
    public void The_dac_advice_points_to_a_dac_built_for_this_machine()
    {
        Assert.Equal(
            "Pass --dac <path to libmscordaccore.so or mscordaccore.dll> from that runtime version, built for this machine's OS, architecture and C library: " +
            "it is in the runtime's shared/Microsoft.NETCore.App/8.0.11 folder and in its Microsoft.NETCore.App.Runtime.<rid> package " +
            "(see \"Analyzing a dump from another machine\" in the README). HangSloth also finds it by itself when that runtime version is installed next to the one HangSloth runs on.",
            DumpLoader.DacAdvice("8.0.11"));
    }

    [Fact]
    public void A_text_file_named_like_a_dump_still_gets_the_not_a_dump_error()
    {
        var path = WriteFile("mail-queue.dmp", Encoding.UTF8.GetBytes("not a dump"));
        var (exit, _, err) = CliTests.Run([path, "--no-color"]);
        Assert.Equal(1, exit);
        Assert.StartsWith($"hangsloth: Could not open '{path}' as a memory dump", err);
    }
}
