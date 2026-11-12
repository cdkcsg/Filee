// The "required" tier: one package Filee cannot work without (exiftool — the Metadata tool), ticked and locked in
// the installer and the first-run choice, downloaded at once on first launch.

using Filee.Engines.Infrastructure;

namespace Filee.Engines.Tests;

public class EngineDownloadsTests
{
    [Fact]
    public void Exiftool_is_the_only_required_package()
    {
        var required = EngineDownloads.Packages.Where(EngineDownloads.IsRequired).Select(p => p.Id).ToList();
        Assert.Equal(["exiftool"], required);
    }

    [Fact]
    public void The_required_package_is_not_just_the_suggested_one()
    {
        // The suggested tier is about size; the required one is about Filee breaking without it. They differ, and a
        // required package is offered with a download where Filee can download one.
        var exiftool = EngineDownloads.Packages.Single(p => p.Id == "exiftool");
        Assert.True(EngineDownloads.IsRequired(exiftool));
        Assert.NotEqual(EngineDownloads.Packages.Where(EngineDownloads.IsSuggested).Select(p => p.Id), [exiftool.Id]);
    }

    [Fact]
    public void The_manifest_names_the_required_packages_for_the_installer()
    {
        // make-setup.cs offers exactly the downloadable packages; whatever it offers as required is required here too.
        var offered = EngineDownloads.Packages.Where(EngineDownloads.CanDownload).ToList();
        Assert.Contains(offered.Where(EngineDownloads.IsRequired), p => p.Id == "exiftool");
    }
}
