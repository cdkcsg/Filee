// Closing the click-mode donut: a click anywhere outside the ring (corners included) dismisses it, and the
// hole always dismisses. The ring's own backdrop folds an open group without closing — that folding is
// covered by DonutGroupTests; here we cover the window-level close.

using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Filee.App.Controls;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.Views;
using Filee.Core.Settings;
using Xunit;

namespace Filee.App.Tests;

[Collection("MainWindow")]
public class RadialCloseTests
{
    private static string TempImage()
    {
        var folder = Directory.CreateTempSubdirectory("filee-radial-close-").FullName;
        var file = Path.Combine(folder, "a.jpg");
        File.WriteAllText(file, "");
        return file;
    }

    private static Point CenterOf(DonutMenu donut) => new(donut.Bounds.Width / 2, donut.Bounds.Height / 2);

    [AvaloniaFact]
    public void A_click_outside_the_ring_closes_the_donut()
    {
        TestServices.EnsureInitialized();
        var file = TempImage();
        var radial = AppHost.Get<RadialController>();
        try
        {
            radial.ShowForFiles(450, 400, [file]);
            var window = radial.Window!;
            Assert.True(radial.IsVisible);

            // The window's corner is far outside the ring: the click lands on the window, not on a slice.
            Assert.True(window.Bounds.Width > 0);
            Assert.Equal(DonutHitKind.Outside, window.DonutControl.HitTest(new Point(2, 2)).Kind);

            window.MouseDown(new Point(2, 2), MouseButton.Left);
            window.MouseUp(new Point(2, 2), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.False(radial.IsVisible);
        }
        finally
        {
            radial.Close(animate: false);
            Directory.Delete(Path.GetDirectoryName(file)!, true);
        }
    }

    [AvaloniaFact]
    public void A_click_on_the_hole_closes_the_donut()
    {
        TestServices.EnsureInitialized();
        var file = TempImage();
        var radial = AppHost.Get<RadialController>();
        try
        {
            radial.ShowForFiles(450, 400, [file]);
            var window = radial.Window!;
            var center = CenterOf(window.DonutControl);

            window.MouseDown(center, MouseButton.Left);
            window.MouseUp(center, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.False(radial.IsVisible);
        }
        finally
        {
            radial.Close(animate: false);
            Directory.Delete(Path.GetDirectoryName(file)!, true);
        }
    }
}
