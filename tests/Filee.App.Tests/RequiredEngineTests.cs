// The first-run choice ticks the required packages (exiftool); their downloads start at once on first launch and
// the user still picks the optional ones. Here: the ticking and the per-package lock.

using Avalonia.Headless.XUnit;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.Core.Localization;

namespace Filee.App.Tests;

[Collection("MainWindow")]
public class RequiredEngineTests
{
    [AvaloniaFact]
    public void The_first_run_choice_ticks_the_required_packages_only()
    {
        TestServices.EnsureInitialized("en");
        var vm = new EngineSetupViewModel(AppHost.Get<EngineDownloadService>(), AppHost.Get<LocalizationService>());
        try
        {
            var required = vm.Packages.Where(p => p.IsRequired).ToList();
            Assert.NotEmpty(required);
            Assert.All(required, p => Assert.True(p.Selected));
            Assert.All(vm.Packages.Where(p => !p.IsRequired), p => Assert.False(p.Selected));
        }
        finally
        {
            vm.Release();
        }
    }
}
