// MinerU is the optional PDF → Markdown engine (magic-pdf). It is a machine-local install (~10 GB), so these
// tests never touch a real one: they use FILEE_MINERU=0, a fake "Use my copy…" folder and the env-var paths.
// What must hold on every computer: MinerU registers with one direct PDF → Markdown edge, it is simply absent
// when it is not installed (the built-in PDF text engine takes the route over), and a copy the user chose is
// found and preferred.

using Filee.Core.Conversion;
using Filee.Core.Settings;
using Filee.Engines;
using Filee.Engines.Infrastructure;
using Filee.Engines.Pdf;

namespace Filee.Engines.Tests;

public class MineruEngineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "filee-mineru-tests", Guid.NewGuid().ToString("N")[..8]);
    private readonly IReadOnlyDictionary<string, string> _ownCopies;

    public MineruEngineTests()
    {
        Directory.CreateDirectory(_root);
        _ownCopies = EngineEnvironment.OwnCopies;
    }

    public void Dispose()
    {
        EngineEnvironment.OwnCopies = _ownCopies;
        Environment.SetEnvironmentVariable("FILEE_MINERU", null);
        Environment.SetEnvironmentVariable("FILEE_MINERU_EXE", null);
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Fact]
    public void Registered_with_one_direct_pdf_to_markdown_edge()
    {
        var mineru = Assert.Single(EngineRegistry.CreateAll(new EngineEnvironment(_root)).Where(c => c.Id == "mineru"));

        Assert.Equal("MinerU", mineru.DisplayName);
        // magic-pdf is a heavy Python + ML process: one at a time keeps the PC responsive.
        Assert.Equal(1, mineru.MaxParallelism);

        var edge = Assert.Single(mineru.Edges);
        Assert.Equal("pdf", edge.From);
        Assert.Equal("md", edge.To);
        // Cheaper than the built-in pdf-text edge (10 + PdfPenalty), so MinerU wins the route while installed.
        Assert.Equal(10, edge.Cost);
        // DirectOnly: installing MinerU never changes any other PDF conversion.
        Assert.True(edge.DirectOnly);
    }

    [Fact]
    public void Turned_off_it_is_absent_and_the_built_in_engine_takes_the_route()
    {
        Environment.SetEnvironmentVariable("FILEE_MINERU", "0");

        var mineru = Assert.Single(EngineRegistry.CreateAll(new EngineEnvironment(_root)).Where(c => c.Id == "mineru"));
        Assert.False(mineru.GetStatus().IsAvailable);

        var planner = new ConverterCatalog(EngineRegistry.CreateAll(new EngineEnvironment(_root))).CreatePlanner();
        var step = Assert.Single(planner.Plan("pdf", "md")!.Steps);
        Assert.Equal("pdf-text", step.Converter.Id);
    }

    [Fact]
    public void A_copy_the_user_chose_is_found_and_preferred_over_the_built_in_engine()
    {
        var scripts = Path.Combine(_root, "mineru", ".venv", "Scripts");
        Directory.CreateDirectory(scripts);
        // Only the file's existence is checked; no real magic-pdf runs in these tests.
        File.WriteAllText(Path.Combine(scripts, "magic-pdf.exe"), "");

        // "Use my copy…" may point at the install folder or straight at the program.
        EngineEnvironment.OwnCopies = new Dictionary<string, string> { ["mineru"] = Path.Combine(_root, "mineru") };

        var mineru = Assert.Single(EngineRegistry.CreateAll(new EngineEnvironment(_root)).Where(c => c.Id == "mineru"));
        var status = mineru.GetStatus();
        Assert.True(status.IsAvailable);
        Assert.Equal(Path.Combine(scripts, "magic-pdf.exe"), status.Detail);

        var planner = new ConverterCatalog(EngineRegistry.CreateAll(new EngineEnvironment(_root))).CreatePlanner();
        var step = Assert.Single(planner.Plan("pdf", "md")!.Steps);
        Assert.Equal("mineru", step.Converter.Id);
    }

    [Fact]
    public void The_program_path_variable_points_at_the_install()
    {
        var scripts = Path.Combine(_root, "venv", "Scripts");
        Directory.CreateDirectory(scripts);
        var exe = Path.Combine(scripts, "magic-pdf.exe");
        File.WriteAllText(exe, "");

        Environment.SetEnvironmentVariable("FILEE_MINERU_EXE", exe);

        Assert.Equal(exe, MineruConverter.Locate());
    }
}
