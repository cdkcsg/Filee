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
        Environment.SetEnvironmentVariable("FILEE_MINERU_TIER", null);
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

    [Fact]
    public void MinerU_4_is_found_by_its_own_program_name_and_says_so()
    {
        var scripts = Path.Combine(_root, "mineru4", ".venv", "Scripts");
        Directory.CreateDirectory(scripts);
        File.WriteAllText(Path.Combine(scripts, "mineru-kit.exe"), "");

        EngineEnvironment.OwnCopies = new Dictionary<string, string> { ["mineru"] = Path.Combine(_root, "mineru4") };

        Assert.Equal(Path.Combine(scripts, "mineru-kit.exe"), MineruConverter.Locate(out var flavor));
        Assert.Equal(MineruFlavor.Kit, flavor);

        var mineru = Assert.Single(EngineRegistry.CreateAll(new EngineEnvironment(_root)), c => c.Id == "mineru");
        var status = mineru.GetStatus();
        Assert.True(status.IsAvailable);
        // The card says which command line the found copy speaks, so a user can tell 1.x from 4.x.
        Assert.Equal("MinerU 4 (mineru-kit)", status.Version);
    }

    [Fact]
    public void With_both_installed_MinerU_4_is_the_one_that_runs()
    {
        var scripts = Path.Combine(_root, "both", ".venv", "Scripts");
        Directory.CreateDirectory(scripts);
        File.WriteAllText(Path.Combine(scripts, "magic-pdf.exe"), "");
        File.WriteAllText(Path.Combine(scripts, "mineru-kit.exe"), "");

        EngineEnvironment.OwnCopies = new Dictionary<string, string> { ["mineru"] = Path.Combine(_root, "both") };

        Assert.Equal(Path.Combine(scripts, "mineru-kit.exe"), MineruConverter.Locate());
    }

    [Theory]
    [InlineData("flash")]
    [InlineData("basic")]
    [InlineData("STANDARD")]
    [InlineData("advanced")]
    public void The_quality_dial_is_taken_from_the_environment(string tier)
    {
        Environment.SetEnvironmentVariable("FILEE_MINERU_TIER", tier);

        Assert.Equal(tier.ToLowerInvariant(), MineruConverter.Tier());
    }

    [Fact]
    public void An_unknown_quality_dial_falls_back_to_standard()
    {
        Environment.SetEnvironmentVariable("FILEE_MINERU_TIER", "ultra");

        Assert.Equal("standard", MineruConverter.Tier());
    }

    [Fact]
    public void Inlined_pictures_become_files_next_to_the_markdown()
    {
        var output = Path.Combine(_root, "out", "paper.md");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var picture = Convert.ToBase64String([0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10]);
        var markdown = $"# Title\n\n![](data:image/jpeg;base64,{picture})\n\ntext\n";

        var text = MineruConverter.ExtractInlineImages(markdown, output);

        Assert.Equal("# Title\n\n![](paper_images/image-1.jpg)\n\ntext\n", text);
        var written = Path.Combine(Path.GetDirectoryName(output)!, "paper_images", "image-1.jpg");
        Assert.True(File.Exists(written));
        Assert.Equal([0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10], File.ReadAllBytes(written));
    }

    [Fact]
    public void Markdown_without_inlined_pictures_is_left_alone()
    {
        var output = Path.Combine(_root, "plain.md");
        const string markdown = "# Title\n\n![](images/1.jpg)\n";

        Assert.Equal(markdown, MineruConverter.ExtractInlineImages(markdown, output));
        Assert.False(Directory.Exists(Path.Combine(_root, "plain_images")));
    }
}
