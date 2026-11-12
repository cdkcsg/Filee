// The engine page's required tier: a required package arrives ticked and locked (its checkbox is disabled, the
// choice survives the user unticking the suggested ones), and the flag travels through the payload manifest from
// make-setup.cs, which reads it from EngineDownloads.

using Avalonia.Headless.XUnit;
using Filee.Setup.Installation;
using Filee.Setup.Payload;
using Filee.Setup.ViewModels;

namespace Filee.Setup.Tests;

public class RequiredEngineTests
{
    [AvaloniaFact]
    public void A_required_engine_is_ticked_and_locked()
    {
        var model = Model(
        [
            new EngineOffer("exiftool", ["exiftool"], 11_000_000, 30_000_000, Suggested: false, Required: true),
            new EngineOffer("pandoc", ["pandoc"], 42_000_000, 236_000_000, Suggested: true),
        ]);
        var exiftool = model.Engines.Single(e => e.Id == "exiftool");
        var pandoc = model.Engines.Single(e => e.Id == "pandoc");

        // Required: ticked and locked; suggested: ticked, free to untick.
        Assert.True(exiftool.Selected);
        Assert.False(exiftool.CanSelect);
        Assert.True(pandoc.Selected);
        Assert.True(pandoc.CanSelect);

        // Unticking the suggested one leaves the required one — the installer installs it regardless.
        pandoc.Selected = false;
        Assert.Equal(["exiftool"], model.Choices().Engines);
    }

    [AvaloniaFact]
    public void An_installed_required_engine_is_neither_offered_nor_reinstalled()
    {
        var model = Model([new EngineOffer("exiftool", ["exiftool"], 11_000_000, 30_000_000, Suggested: false, Required: true)],
            installed: "exiftool");
        var exiftool = model.Engines.Single(e => e.Id == "exiftool");
        Assert.True(exiftool.Installed);
        Assert.False(exiftool.CanSelect);
        Assert.Equal([], model.Choices().Engines);
    }

    [Fact]
    public void The_manifest_round_trips_the_required_flag()
    {
        var manifest = new PayloadManifest("2.4.0", [], "sha", [new EngineOffer("exiftool", ["exiftool"], 11, 30, false, true)], ".marker", [1], "AAAAAAA=");
        var parsed = PayloadManifest.FromJson(manifest.ToJson());
        Assert.True(parsed.Engines.Single().Required);
    }

    [Fact]
    public void A_manifest_without_the_flag_is_backward_compatible()
    {
        // An installer built before the flag existed: the engine parses as not required.
        const string json = """
            {
              "format": 1,
              "version": "2.4.0",
              "compression": "lzma",
              "lzmaProperties": "AAAAAAA=",
              "compressedSha256": "sha",
              "engineMarker": ".marker",
              "segments": [1],
              "engines": [
                { "id": "exiftool", "markerFolders": ["exiftool"], "downloadSize": 11, "installedSize": 30, "suggested": false }
              ],
              "files": []
            }
            """;
        var parsed = PayloadManifest.FromJson(System.Text.Encoding.UTF8.GetBytes(json));
        Assert.False(parsed.Engines.Single().Required);
    }

    private static SetupViewModel Model(IReadOnlyList<EngineOffer> offers, string? installed = null)
    {
        var texts = new SetupTexts("en");
        return new SetupViewModel(texts, new NullRunner(), "2.4.0",
            new InstallChoices(@"C:\Program Files\Filee", InstallScope.Machine, SetupTasks.Defaults, [], "en"),
            previous: null, offers.Select(o => (o, o.Id == installed)).ToList(), modernMenuSupported: true, neededSpace: 1);
    }

    private sealed class NullRunner : ISetupRunner
    {
        public InstallResult Install(InstallChoices choices, IProgress<SetupProgress> progress) =>
            new(new LaunchRequest(@"C:\Program Files\Filee\Filee.exe", ""), []);

        public UninstallResult Uninstall(bool deleteSettings, IProgress<SetupProgress> progress) => new();
    }
}
