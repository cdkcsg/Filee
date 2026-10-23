// PDF → Markdown through MinerU (magic-pdf, magic-pdf[full]) — an optional on-device ML engine the user installs
// themselves. It is NOT downloaded by Filee: it is a Python + model package (~10 GB) and Filee stays lightweight.
// MinerU does the layout, OCR of scans, tables and formulas (LaTeX) and writes Markdown. Filee's built-in
// PdfTextConverter also writes PDF → Markdown, but only reads the text layer: no formulas, no tables, no OCR. MinerU
// is the preferred engine for PDF → Markdown when it is installed (its edge costs less); without it the built-in
// converter takes over, so the route always exists.
//
// magic-pdf is looked up like the other optional engines (the user's own copy from Settings → Engines, Filee's
// download folder, the PATH) and, because its usual install is a personal virtual environment, also at
// FILEE_MINERU_EXE, FILEE_MINERU_HOME\.venv\Scripts and D:\mineru\.venv\Scripts. Without magic-pdf the converter is
// simply absent: GetStatus is Unavailable and the PDF → Markdown route never appears. FILEE_MINERU=0 turns it off.
//
// The edge is DirectOnly: MinerU only runs when Markdown is what the user asked for, so installing it never changes
// any other PDF conversion (PDF → DOCX, HTML, …) — those keep using the built-in engines.

using System.Text;
using Filee.Core.Conversion;
using Filee.Engines.Infrastructure;

namespace Filee.Engines.Pdf;

/// <summary>PDF → Markdown through a local MinerU (magic-pdf) install.</summary>
public sealed class MineruConverter : IConverter
{
    /// <summary>Scanned books take minutes per page; generous so real documents finish.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(120);

    public string Id => "mineru";
    public string DisplayName => "MinerU";

    /// <summary>magic-pdf is a heavy Python + ML process; one at a time keeps the PC responsive.</summary>
    public int MaxParallelism => 1;

    /// <summary>
    /// PDF → Markdown with formulas, tables and scan OCR, where the built-in PdfTextConverter (cost 16) only reads
    /// the text layer. Cost 10 makes MinerU the preferred engine for this route while it is installed. DirectOnly
    /// (see the file header) so MinerU never becomes a hidden step in other routes.
    /// </summary>
    public IReadOnlyList<ConversionEdge> Edges { get; } = [new("pdf", "md", 10, DirectOnly: true)];

    private string? _executable;

    public EngineStatus GetStatus()
    {
        _executable = Locate();
        return _executable is null
            ? EngineStatus.Unavailable("engine.reason.not_installed")
            : EngineStatus.Available(_executable, "MinerU (magic-pdf)");
    }

    public async Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var exe = _executable ?? Locate() ?? throw new InvalidOperationException("MinerU (magic-pdf) is not installed.");
        var output = step.Output.Allocate("md");
        if (output is null)
            return [];

        var work = Path.Combine(step.WorkDirectory, "mineru-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);

        progress?.Report(0.05);
        // -m auto lets magic-pdf pick the text or OCR path per page (scans vs digital PDFs), which the built-in
        // reader cannot. Output lands under <work>/<name>/auto/ with a sibling images/ folder.
        var result = await ProcessRunner.RunAsync(exe,
            ["-p", Path.GetFullPath(step.InputPath), "-o", work, "-m", "auto"],
            Timeout, cancellationToken, workingDirectory: work, environment: Config());

        var markdown = FindMarkdown(work, Path.GetFileNameWithoutExtension(step.InputPath))
            ?? throw new InvalidOperationException($"MinerU produced no Markdown (exit {result.ExitCode}). {Tail(result)}");
        cancellationToken.ThrowIfCancellationRequested();

        var text = await File.ReadAllTextAsync(markdown, cancellationToken);
        progress?.Report(0.85);
        // Pictures are referenced relative to magic-pdf's images/ folder: copy them next to the output and repoint
        // the references, so the Markdown is portable. Born-digital PDFs have no images and are copied as they are.
        text = CopyImages(Path.Combine(Path.GetDirectoryName(markdown)!, "images"), output, text);
        await File.WriteAllTextAsync(output, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);
        progress?.Report(1);
        return [output];
    }

    /// <summary>magic-pdf executable, or null when MinerU is not installed / turned off.</summary>
    internal static string? Locate()
    {
        if (Environment.GetEnvironmentVariable("FILEE_MINERU") == "0")
            return null;
        return EngineEnvironment.OwnProgram("mineru", "magic-pdf.exe")
            ?? EngineEnvironment.BundledProgram("mineru", "magic-pdf.exe")
            ?? EngineEnvironment.SystemProgram("mineru", "magic-pdf.exe")
            ?? UnderVenv(Environment.GetEnvironmentVariable("FILEE_MINERU_EXE"))
            // The guessed install locations are a system search: tests turn SearchSystem off so a developer's
            // MinerU does not change results, exactly like the PATH scan in SystemProgram above.
            ?? (EngineEnvironment.SearchSystem
                ? UnderVenv(Environment.GetEnvironmentVariable("FILEE_MINERU_HOME")) ?? UnderVenv(@"D:\mineru")
                : null);
    }

    /// <summary>magic-pdf.exe directly at <paramref name="path"/>, or in its <c>.venv\Scripts</c> when path is a folder.</summary>
    private static string? UnderVenv(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        if (File.Exists(path))
            return path;
        var candidate = Path.Combine(path, ".venv", "Scripts", "magic-pdf.exe");
        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>Optional config (models dir, device) through FILEE_MINERU_CONFIG; magic-pdf reads its own json otherwise.</summary>
    private static IDictionary<string, string>? Config()
    {
        var config = Environment.GetEnvironmentVariable("FILEE_MINERU_CONFIG");
        return string.IsNullOrWhiteSpace(config) ? null : new Dictionary<string, string> { ["MINERU_TOOLS_CONFIG_JSON"] = config };
    }

    /// <summary>magic-pdf writes &lt;work&gt;/&lt;name&gt;/auto/&lt;name&gt;.md; searched for in case the name differs.</summary>
    private static string? FindMarkdown(string work, string name)
    {
        var direct = Path.Combine(work, name, "auto", name + ".md");
        if (File.Exists(direct))
            return direct;
        try
        {
            return Directory.EnumerateFiles(work, "*.md", SearchOption.AllDirectories).FirstOrDefault();
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Copies <paramref name="imagesFolder"/> next to the output as "&lt;stem&gt;_images" and rewrites the relative
    /// "images/…" references to it. Returns the Markdown unchanged when there are no pictures.
    /// </summary>
    private static string CopyImages(string imagesFolder, string outputPath, string markdown)
    {
        if (!Directory.Exists(imagesFolder))
            return markdown;
        string[] files;
        try
        {
            files = Directory.GetFiles(imagesFolder);
        }
        catch (IOException)
        {
            return markdown;
        }
        if (files.Length == 0)
            return markdown;

        var target = Path.Combine(Path.GetDirectoryName(outputPath)!, Path.GetFileNameWithoutExtension(outputPath) + "_images");
        Directory.CreateDirectory(target);
        foreach (var file in files)
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        var marker = Path.GetFileName(target) + "/";
        return markdown.Replace("](images/", "](" + marker)
            .Replace("src=\"images/", "src=\"" + marker);
    }

    /// <summary>The end of magic-pdf's messages: the actual error is at the bottom.</summary>
    private static string Tail(ProcessResult result)
    {
        var text = (result.StandardError + "\n" + result.StandardOutput).Trim();
        if (text.Length > 1200)
            text = text[^1200..];
        return text;
    }
}
