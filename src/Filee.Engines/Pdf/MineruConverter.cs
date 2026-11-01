// PDF → Markdown through MinerU — an optional on-device ML engine the user installs themselves. It is NOT downloaded
// by Filee: it is a Python + model package (~4 GB for 4.x, ~10 GB for 1.x) and Filee stays lightweight. MinerU does
// the layout, OCR of scans, tables and formulas (LaTeX) and writes Markdown. Filee's built-in PdfTextConverter also
// writes PDF → Markdown, but only reads the text layer: no formulas, no tables, no OCR. MinerU is the preferred
// engine for PDF → Markdown when it is installed (its edge costs less); without it the built-in converter takes over,
// so the route always exists.
//
// Two command lines are understood, because MinerU renamed it:
//   * 1.x — "magic-pdf -p <file> -o <work> -m auto": writes <work>/<name>/auto/<name>.md and an images/ folder.
//   * 4.x — "mineru-kit parse <file> -o <work> --tier standard": writes <work>/<name>.md with the pictures inlined
//     as base64 data URIs. Measured on a 15-page paper: 4.x 73 s against 1.x 201 s, and its tables come out as
//     Markdown tables where 1.x writes HTML <table> soup with OCR slips (O(1) read as 0(1)).
// The install is looked up like the other optional engines (the user's own copy from Settings → Engines, Filee's
// download folder, the PATH) and, because its usual install is a personal virtual environment, also at
// FILEE_MINERU_EXE, FILEE_MINERU_HOME\.venv\Scripts and D:\mineru\.venv\Scripts. Without either program the converter
// is simply absent: GetStatus is Unavailable and the PDF → Markdown route never appears. FILEE_MINERU=0 turns it off.
//
// The edge is DirectOnly: MinerU only runs when Markdown is what the user asked for, so installing it never changes
// any other PDF conversion (PDF → DOCX, HTML, …) — those keep using the built-in engines.

using System.Text;
using System.Text.RegularExpressions;
using Filee.Core.Conversion;
using Filee.Engines.Infrastructure;

namespace Filee.Engines.Pdf;

/// <summary>Which command line the MinerU copy on this PC speaks.</summary>
internal enum MineruFlavor
{
    /// <summary>1.x: <c>magic-pdf -p … -o … -m auto</c>.</summary>
    MagicPdf,

    /// <summary>4.x: <c>mineru-kit parse … -o … --tier standard</c>.</summary>
    Kit,
}

/// <summary>PDF → Markdown through a local MinerU install (magic-pdf 1.x or mineru-kit 4.x).</summary>
public sealed partial class MineruConverter : IConverter
{
    /// <summary>Scanned books take minutes per page; generous so real documents finish.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(120);

    public string Id => "mineru";
    public string DisplayName => "MinerU";

    /// <summary>MinerU is a heavy Python + ML process; one at a time keeps the PC responsive.</summary>
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
        _executable = Locate(out var flavor);
        return _executable is null
            ? EngineStatus.Unavailable("engine.reason.not_installed")
            : EngineStatus.Available(_executable, flavor == MineruFlavor.Kit ? "MinerU 4 (mineru-kit)" : "MinerU (magic-pdf)");
    }

    public async Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var exe = _executable ?? Locate(out _) ?? throw new InvalidOperationException("MinerU is not installed.");
        var output = step.Output.Allocate("md");
        if (output is null)
            return [];

        var work = Path.Combine(step.WorkDirectory, "mineru-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);

        progress?.Report(0.05);
        var stem = Path.GetFileNameWithoutExtension(step.InputPath);
        var kit = FlavorOf(exe) == MineruFlavor.Kit;
        var result = kit
            // 4.x picks the text or OCR path per page itself; --tier is the quality/speed dial (FILEE_MINERU_TIER).
            ? await ProcessRunner.RunAsync(exe,
                ["parse", Path.GetFullPath(step.InputPath), "-o", work, "--tier", Tier()],
                Timeout, cancellationToken, workingDirectory: work, environment: Config())
            // -m auto lets magic-pdf pick the text or OCR path per page (scans vs digital PDFs), which the built-in
            // reader cannot. Output lands under <work>/<name>/auto/ with a sibling images/ folder.
            : await ProcessRunner.RunAsync(exe,
                ["-p", Path.GetFullPath(step.InputPath), "-o", work, "-m", "auto"],
                Timeout, cancellationToken, workingDirectory: work, environment: Config());

        var markdown = FindMarkdown(work, stem, kit)
            ?? throw new InvalidOperationException($"MinerU produced no Markdown (exit {result.ExitCode}). {Tail(result)}");
        cancellationToken.ThrowIfCancellationRequested();

        var text = await File.ReadAllTextAsync(markdown, cancellationToken);
        progress?.Report(0.85);
        // Pictures are referenced relative to magic-pdf's images/ folder: copy them next to the output and repoint
        // the references, so the Markdown is portable. Born-digital PDFs have no images and are copied as they are.
        // 4.x inlines the pictures as base64 data URIs instead: those are written out as files for the same reason
        // (a 15-page paper becomes 1.1 MB of Markdown otherwise, unreadable and slow to open).
        text = kit
            ? ExtractInlineImages(text, output)
            : CopyImages(Path.Combine(Path.GetDirectoryName(markdown)!, "images"), output, text);
        await File.WriteAllTextAsync(output, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);
        progress?.Report(1);
        return [output];
    }

    /// <summary>magic-pdf executable, or null when MinerU is not installed / turned off.</summary>
    internal static string? Locate() => Locate(out _);

    /// <summary>The MinerU program to run (4.x <c>mineru-kit</c> preferred) and the command line it speaks.</summary>
    internal static string? Locate(out MineruFlavor flavor)
    {
        flavor = MineruFlavor.MagicPdf;
        if (Environment.GetEnvironmentVariable("FILEE_MINERU") == "0")
            return null;
        var found = In(EngineEnvironment.OwnCopyFolder("mineru"))
            ?? In(EngineEnvironment.FindBundled("mineru"))
            ?? In(EngineEnvironment.SystemCopyFolder("mineru"))
            ?? UnderVenv(Environment.GetEnvironmentVariable("FILEE_MINERU_EXE"))
            // The guessed install locations are a system search: tests turn SearchSystem off so a developer's
            // MinerU does not change results, exactly like the PATH scan in SystemCopyFolder above.
            ?? (EngineEnvironment.SearchSystem
                ? UnderVenv(Environment.GetEnvironmentVariable("FILEE_MINERU_HOME")) ?? UnderVenv(@"D:\mineru")
                : null);
        if (found is not null)
            flavor = FlavorOf(found);
        return found;
    }

    /// <summary>4.x named its program mineru-kit; 1.x magic-pdf.</summary>
    internal static MineruFlavor FlavorOf(string executable) =>
        Path.GetFileNameWithoutExtension(executable).StartsWith("mineru-kit", StringComparison.OrdinalIgnoreCase)
            ? MineruFlavor.Kit
            : MineruFlavor.MagicPdf;

    /// <summary>The program inside <paramref name="folder"/>, 4.x first, or null.</summary>
    private static string? In(string? folder)
    {
        if (folder is null)
            return null;
        foreach (var name in ProgramNames)
            if (File.Exists(Path.Combine(folder, name)))
                return Path.Combine(folder, name);
        return null;
    }

    private static string[] ProgramNames => OperatingSystem.IsWindows()
        ? ["mineru-kit.exe", "magic-pdf.exe"]
        : ["mineru-kit", "magic-pdf"];

    /// <summary>The program at <paramref name="path"/>, or in its <c>.venv\Scripts</c> when path is a folder.</summary>
    private static string? UnderVenv(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        if (File.Exists(path))
            return path;
        return In(Path.Combine(path, ".venv", OperatingSystem.IsWindows() ? "Scripts" : "bin"));
    }

    /// <summary>Optional config (models dir, device) through FILEE_MINERU_CONFIG; MinerU reads its own file otherwise.</summary>
    private static IDictionary<string, string>? Config()
    {
        var config = Environment.GetEnvironmentVariable("FILEE_MINERU_CONFIG");
        return string.IsNullOrWhiteSpace(config) ? null : new Dictionary<string, string> { ["MINERU_TOOLS_CONFIG_JSON"] = config };
    }

    /// <summary>
    /// The 4.x quality/speed dial from FILEE_MINERU_TIER: flash (preview), basic (small models only), standard
    /// (default, adds the VLM) or advanced (slower, hardest documents). Unknown values fall back to standard.
    /// </summary>
    internal static string Tier()
    {
        var tier = Environment.GetEnvironmentVariable("FILEE_MINERU_TIER")?.Trim().ToLowerInvariant();
        return tier is "flash" or "basic" or "standard" or "advanced" ? tier : "standard";
    }

    /// <summary>
    /// magic-pdf writes &lt;work&gt;/&lt;name&gt;/auto/&lt;name&gt;.md, mineru-kit &lt;work&gt;/&lt;name&gt;.md; searched
    /// for in case the name differs.
    /// </summary>
    private static string? FindMarkdown(string work, string name, bool kit)
    {
        var direct = kit ? Path.Combine(work, name + ".md") : Path.Combine(work, name, "auto", name + ".md");
        if (File.Exists(direct))
            return direct;
        try
        {
            return Directory.EnumerateFiles(work, "*.md", SearchOption.AllDirectories)
                .OrderByDescending(f => Path.GetFileName(f).StartsWith(name, StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault();
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

    /// <summary>
    /// 4.x writes pictures into the Markdown as "![](data:image/png;base64,…)". Writes them next to the output as
    /// "&lt;stem&gt;_images/image-1.png" and points the Markdown at those files, so the result stays small and can be
    /// opened, diffed and copied like any other Markdown. Markdown without inlined pictures is returned unchanged.
    /// </summary>
    internal static string ExtractInlineImages(string markdown, string outputPath)
    {
        if (!markdown.Contains("data:image/", StringComparison.Ordinal))
            return markdown;

        var folder = Path.Combine(Path.GetDirectoryName(outputPath)!, Path.GetFileNameWithoutExtension(outputPath) + "_images");
        var count = 0;
        var text = InlineImage().Replace(markdown, match =>
        {
            var extension = match.Groups["type"].Value.ToLowerInvariant() switch
            {
                "jpeg" or "jpg" => "jpg",
                "webp" => "webp",
                "gif" => "gif",
                var other => other,
            };
            var data = match.Groups["data"].Value;
            try
            {
                var bytes = Convert.FromBase64String(data);
                Directory.CreateDirectory(folder);
                var name = $"image-{++count}.{extension}";
                File.WriteAllBytes(Path.Combine(folder, name), bytes);
                var alt = match.Groups["alt"].Value;
                return $"![{alt}]({Path.GetFileName(folder)}/{name})";
            }
            catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
            {
                // Leave this picture inline rather than losing it.
                return match.Value;
            }
        });
        return text;
    }

    /// <summary>A Markdown picture whose source is a base64 data URI: the alt text, the picture type and the data.</summary>
    [GeneratedRegex(@"!\[(?<alt>[^\]]*)\]\(data:image/(?<type>[a-zA-Z0-9.+-]+);base64,(?<data>[A-Za-z0-9+/=\s]+)\)",
        RegexOptions.CultureInvariant)]
    private static partial Regex InlineImage();

    /// <summary>The end of MinerU's messages: the actual error is at the bottom.</summary>
    private static string Tail(ProcessResult result)
    {
        var text = (result.StandardError + "\n" + result.StandardOutput).Trim();
        if (text.Length > 1200)
            text = text[^1200..];
        return text;
    }
}
