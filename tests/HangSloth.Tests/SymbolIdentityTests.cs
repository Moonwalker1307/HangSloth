using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using HangSloth.Analysis;
using HangSloth.Collection;

namespace HangSloth.Tests;

/// Line numbers must come from the build that was dumped. The tests write two real builds of a small
/// OrderApp.dll (with a portable PDB each) and resolve the dumped Orders.Load against them.
public sealed class SymbolIdentityTests : IDisposable
{
    private const string Note = "OrderApp.dll on this machine is not the build that was dumped; file and line numbers for it may be off. Pass --symbols <folder with the dumped build>.";

    private static readonly Guid Dumped = new("6f0d5a1e-52c8-4f0b-9c57-0f6b8f3a2d11");
    private static readonly Guid Rebuilt = new("b83c2f77-91a4-4d3e-8a60-5d1e7c9b4e22");

    /// Orders.Load in the dumped build: the first method of the module.
    private const int LoadToken = 0x06000001;

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"hangsloth-symbols-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void The_dumped_build_resolves_without_a_note()
    {
        var module = WriteDumpedBuild("app");
        using var resolver = new SourceResolver();

        Assert.Equal(("Orders.cs", 9), Line(resolver.Resolve(module, LoadToken, 0, () => Dumped)));
        var call = resolver.ResolveBlockingCall(module, LoadToken, 0, () => Dumped);
        Assert.Equal(".Result", call?.Call);
        Assert.Equal(("Orders.cs", 9), Line(call?.Location));
        Assert.Empty(resolver.Warnings);
    }

    [Fact]
    public void Another_build_still_resolves_and_says_so_once()
    {
        var module = WriteRebuild("app");
        using var resolver = new SourceResolver();

        // The rebuild's lines (another method's, since the tokens shifted): kept, but no longer without a word.
        Assert.Equal(("Orders.cs", 8), Line(resolver.Resolve(module, LoadToken, 0, () => Dumped)));
        Assert.Equal(("Orders.cs", 8), Line(resolver.Resolve(module, LoadToken, 0, () => Dumped)));
        Assert.Equal(".Wait()", resolver.ResolveBlockingCall(module, LoadToken, 0, () => Dumped)?.Call);
        resolver.ResolveAwait(module, LoadToken, 0, () => Dumped);
        Assert.Equal([Note], resolver.Warnings);
    }

    [Fact]
    public void Each_module_gets_its_own_note()
    {
        var orders = WriteRebuild("app");
        var invoices = WriteRebuild("app", "InvoiceApp");
        using var resolver = new SourceResolver();

        resolver.Resolve(orders, LoadToken, 0, () => Dumped);
        resolver.ResolveBlockingCall(invoices, LoadToken, 0, () => Dumped);
        resolver.Resolve(invoices, LoadToken, 0, () => Dumped);
        Assert.Equal([Note, Note.Replace("OrderApp.dll", "InvoiceApp.dll")], resolver.Warnings);
    }

    [Fact]
    public void The_dumped_build_in_a_symbols_folder_wins_over_a_rebuild_at_the_module_path()
    {
        var module = WriteRebuild("app");
        WriteDumpedBuild("symbols");
        using var resolver = new SourceResolver([Folder("symbols")]);

        Assert.Equal(("Orders.cs", 9), Line(resolver.Resolve(module, LoadToken, 0, () => Dumped)));
        var call = resolver.ResolveBlockingCall(module, LoadToken, 0, () => Dumped);
        Assert.Equal(".Result", call?.Call);
        Assert.Equal(("Orders.cs", 9), Line(call?.Location));
        Assert.Empty(resolver.Warnings);
    }

    [Fact]
    public void The_dumped_build_in_a_later_symbols_folder_wins_over_a_rebuild_in_an_earlier_one()
    {
        WriteRebuild("newer");
        WriteDumpedBuild("older");
        using var resolver = new SourceResolver([Folder("newer"), Folder("older")]);

        Assert.Equal(("Orders.cs", 9), Line(resolver.Resolve(ElsewhereModule, LoadToken, 0, () => Dumped)));
        Assert.Equal(".Result", resolver.ResolveBlockingCall(ElsewhereModule, LoadToken, 0, () => Dumped)?.Call);
        Assert.Empty(resolver.Warnings);
    }

    [Fact]
    public void The_dumped_build_with_an_embedded_pdb_wins_too()
    {
        var module = WriteRebuild("app");
        WriteBuild("symbols", "OrderApp", Dumped, rebuilt: false, pdb: false, embedPdb: true);
        using var resolver = new SourceResolver([Folder("symbols")]);

        Assert.Equal(("Orders.cs", 9), Line(resolver.Resolve(module, LoadToken, 0, () => Dumped)));
        Assert.Empty(resolver.Warnings);
    }

    [Fact]
    public void A_pdb_without_its_binary_is_checked_by_its_own_id()
    {
        WriteBuild("newer", "OrderApp", Rebuilt, rebuilt: true, dll: false);
        WriteBuild("older", "OrderApp", Dumped, rebuilt: false, dll: false);

        using (var resolver = new SourceResolver([Folder("newer"), Folder("older")]))
        {
            Assert.Equal(("Orders.cs", 9), Line(resolver.Resolve(ElsewhereModule, LoadToken, 0, () => Dumped)));
            Assert.Empty(resolver.Warnings);
        }

        using (var resolver = new SourceResolver([Folder("newer")]))
        {
            Assert.Equal(("Orders.cs", 8), Line(resolver.Resolve(ElsewhereModule, LoadToken, 0, () => Dumped)));
            Assert.Equal([Note], resolver.Warnings);
        }
    }

    [Fact]
    public void A_pdb_that_does_not_belong_to_the_dumped_binary_is_not_used_without_a_note()
    {
        // The right binary, and in another folder a PDB of a rebuild: the binary's own check turns that PDB down.
        WriteBuild("binaries", "OrderApp", Dumped, rebuilt: false, pdb: false);
        WriteBuild("pdbs", "OrderApp", Rebuilt, rebuilt: true, dll: false);
        using var resolver = new SourceResolver([Folder("binaries"), Folder("pdbs")]);

        Assert.Equal(("Orders.cs", 8), Line(resolver.Resolve(ElsewhereModule, LoadToken, 0, () => Dumped)));
        Assert.Equal([Note], resolver.Warnings);
    }

    [Fact]
    public void A_rebuilt_binary_is_not_read_next_to_the_dumped_builds_pdb()
    {
        // IL offsets of one build looked up in the PDB of another would give a wrong line with nothing to warn about.
        var module = WriteRebuild("app");
        WriteBuild("symbols", "OrderApp", Dumped, rebuilt: false, dll: false);
        using var resolver = new SourceResolver([Folder("symbols")]);

        Assert.Null(resolver.ResolveBlockingCall(module, LoadToken, 0, () => Dumped));
        Assert.Equal(("Orders.cs", 9), Line(resolver.Resolve(module, LoadToken, 0, () => Dumped)));
        Assert.Empty(resolver.Warnings);
    }

    [Fact]
    public void Without_an_id_from_the_dump_the_first_build_found_is_used_as_before()
    {
        var module = WriteRebuild("app");
        WriteDumpedBuild("symbols");

        using (var resolver = new SourceResolver([Folder("symbols")]))
        {
            Assert.Equal(("Orders.cs", 8), Line(resolver.Resolve(module, LoadToken, 0)));
            Assert.Equal(".Wait()", resolver.ResolveBlockingCall(module, LoadToken, 0)?.Call);
            Assert.Empty(resolver.Warnings);
        }

        using (var resolver = new SourceResolver([Folder("symbols")]))
        {
            Assert.Equal(("Orders.cs", 8), Line(resolver.Resolve(module, LoadToken, 0, () => null)));
            Assert.Empty(resolver.Warnings);
        }

        // A dump whose module can't be read is a dump without an id, not a failure.
        using (var resolver = new SourceResolver([Folder("symbols")]))
        {
            Assert.Equal(("Orders.cs", 8), Line(resolver.Resolve(module, LoadToken, 0, () => throw new InvalidOperationException("unreadable"))));
            Assert.Empty(resolver.Warnings);
        }
    }

    // ---------------------------------------------------------------- the two builds

    /// The module path recorded in a dump from a machine whose folders don't exist here.
    private string ElsewhereModule => Path.Combine(_root, "not-on-this-machine", "OrderApp.dll");

    private string Folder(string name) => Path.Combine(_root, name);

    private string WriteDumpedBuild(string folder) => WriteBuild(folder, "OrderApp", Dumped, rebuilt: false);

    private string WriteRebuild(string folder, string name = "OrderApp") => WriteBuild(folder, name, Rebuilt, rebuilt: true);

    private static (string? File, int? Line) Line(Model.SourceLocation? location) => (location?.FileName, location?.Line);

    /// Writes <paramref name="name"/>.dll and .pdb with the given PDB id. The dumped build has Orders.Load
    /// first, blocking in .Result on line 9. The rebuild has a new Orders.Flush above it, blocking in .Wait()
    /// on line 8, so Load moved down to line 14 and its old method token now names Flush.
    private string WriteBuild(string folder, string name, Guid id, bool rebuilt, bool dll = true, bool pdb = true, bool embedPdb = false)
    {
        var assembly = new PersistedAssemblyBuilder(new AssemblyName(name), typeof(object).Assembly);
        var module = assembly.DefineDynamicModule(name);
        var document = module.DefineDocument("Orders.cs", System.Diagnostics.SymbolStore.SymLanguageType.CSharp);
        var type = module.DefineType("Orders", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        if (rebuilt)
        {
            var flush = type.DefineMethod("Flush", MethodAttributes.Public | MethodAttributes.Static, typeof(void), [typeof(Task)]).GetILGenerator();
            flush.MarkSequencePoint(document, 8, 9, 8, 24);
            flush.Emit(OpCodes.Ldarg_0);
            flush.Emit(OpCodes.Callvirt, typeof(Task).GetMethod(nameof(Task.Wait), Type.EmptyTypes)!);
            flush.Emit(OpCodes.Ret);
        }
        var loadLine = rebuilt ? 14 : 9;
        var load = type.DefineMethod("Load", MethodAttributes.Public | MethodAttributes.Static, typeof(int), [typeof(Task<int>)]).GetILGenerator();
        load.MarkSequencePoint(document, loadLine, 9, loadLine, 31);
        load.Emit(OpCodes.Ldarg_0);
        load.Emit(OpCodes.Callvirt, typeof(Task<int>).GetProperty(nameof(Task<int>.Result))!.GetMethod!);
        load.Emit(OpCodes.Ret);
        type.CreateType();

        var metadata = assembly.GenerateMetadata(out var il, out _, out var pdbMetadata);
        var pdbBuilder = new PortablePdbBuilder(pdbMetadata, metadata.GetRowCounts(), default, _ => new BlobContentId(id, 1));
        var pdbBlob = new BlobBuilder();
        var pdbId = pdbBuilder.Serialize(pdbBlob);

        var debug = new DebugDirectoryBuilder();
        debug.AddCodeViewEntry($"{name}.pdb", pdbId, pdbBuilder.FormatVersion);
        if (embedPdb) debug.AddEmbeddedPortablePdbEntry(pdbBlob, pdbBuilder.FormatVersion);
        var peBlob = new BlobBuilder();
        new ManagedPEBuilder(PEHeaderBuilder.CreateLibraryHeader(), new MetadataRootBuilder(metadata), il, debugDirectoryBuilder: debug).Serialize(peBlob);

        var directory = Directory.CreateDirectory(Folder(folder)).FullName;
        var path = Path.Combine(directory, $"{name}.dll");
        if (dll) File.WriteAllBytes(path, peBlob.ToArray());
        if (pdb) File.WriteAllBytes(Path.ChangeExtension(path, ".pdb"), pdbBlob.ToArray());
        return path;
    }
}

/// The identity comes out of a real dump: HangApp is dumped, then a different build of it is put in its place.
[Trait("Category", "EndToEnd")]
[Collection("EndToEnd")]
public class SymbolIdentityEndToEndTests
{
    private const string Note = "HangApp.dll on this machine is not the build that was dumped; file and line numbers for it may be off. Pass --symbols <folder with the dumped build>.";

    [Fact]
    public void A_dump_next_to_another_build_gets_a_note_until_symbols_point_at_the_dumped_build()
    {
        var release = Path.GetDirectoryName(SampleApp.Build("HangApp"))!;
        var debug = Path.GetDirectoryName(SampleApp.Build("HangApp", "Debug"))!;
        var root = Path.Combine(Path.GetTempPath(), $"hangsloth-symbols-{Guid.NewGuid():N}");
        var app = Directory.CreateDirectory(Path.Combine(root, "app")).FullName;
        var dumped = Directory.CreateDirectory(Path.Combine(root, "dumped-build")).FullName;
        var dump = Path.Combine(Directory.CreateDirectory(Path.Combine(root, "dumps")).FullName, "hangapp.dmp");
        try
        {
            foreach (var file in Directory.GetFiles(release))
            {
                File.Copy(file, Path.Combine(app, Path.GetFileName(file)));
                File.Copy(file, Path.Combine(dumped, Path.GetFileName(file)));
            }

            DumpHangApp(Path.Combine(app, "HangApp.dll"), "sync-over-async", dump);

            // The files the process ran from: nothing to say.
            var asDumped = DumpLoader.LoadDump(dump);
            Assert.DoesNotContain(asDumped.Warnings, w => w.Contains("is not the build that was dumped"));
            Assert.Equal("OrderController.cs", new HangAnalyzer().Analyze(asDumped).Findings[0].Location?.FileName);

            // The app is rebuilt in place (here: its Debug build) and the old dump is looked at again.
            File.Copy(Path.Combine(debug, "HangApp.dll"), Path.Combine(app, "HangApp.dll"), overwrite: true);
            File.Copy(Path.Combine(debug, "HangApp.pdb"), Path.Combine(app, "HangApp.pdb"), overwrite: true);
            Assert.Contains(Note, DumpLoader.LoadDump(dump).Warnings);

            // A folder with the dumped build wins over the rebuild at the module's own path.
            var withSymbols = DumpLoader.LoadDump(dump, new LoadOptions { SymbolDirectories = [dumped] });
            Assert.DoesNotContain(withSymbols.Warnings, w => w.Contains("is not the build that was dumped"));
            Assert.Equal("OrderController.cs", new HangAnalyzer().Analyze(withSymbols).Findings[0].Location?.FileName);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    /// Runs the sample from where it is, dumps it once it hangs, and stops it so that its files can be replaced.
    private static void DumpHangApp(string appDll, string mode, string dump)
    {
        using var process = Process.Start(new ProcessStartInfo("dotnet", [appDll, mode])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        try
        {
            process.ErrorDataReceived += (_, _) => { };
            process.BeginErrorReadLine();
            var ready = Task.Run(() =>
            {
                while (process.StandardOutput.ReadLine() is { } line)
                    if (line == "READY") return true;
                return false;
            });
            Assert.True(ready.Wait(TimeSpan.FromSeconds(30)) && ready.Result, $"Sample '{mode}' never became ready.");
            DumpLoader.CollectDump(process.Id, dump);
        }
        finally
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            process.WaitForExit(10_000);
        }
    }
}
