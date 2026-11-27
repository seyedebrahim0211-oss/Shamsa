using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using Shamsa.Desktop;
using Shamsa.Desktop.Fences;
using Shamsa.FolderStudio;
using Shamsa.Services;
using Xunit;

namespace Shamsa.Tests;

public class GalleryRegressionTests
{
    [Theory]
    [InlineData(1920, 946)]
    [InlineData(1280, 640)]
    [InlineData(1024, 605)]
    public void UpgradingOverlappingGroupsKeepsEveryWindowInsideTheScreen(int width, int height)
    {
        var area = new Rect(0, 34, width, height);
        var s = new DesktopSettings();
        s.Widgets.Enabled = s.Fences.Enabled = true;
        s.Widgets.Items.Add(new WidgetPlacement { Kind = "weather", X = width - 288, Y = 46, Width = 265, Height = 218 });
        s.Widgets.Items.Add(new WidgetPlacement { Kind = "prayer", X = width - 280, Y = 278, Width = 250, Height = 290 });
        s.Fences.Fences = FencesModule.SuggestedFences();
        foreach (var f in s.Fences.Fences) { f.X = width - 370; f.Y = 46; }

        var weather = DesktopPlacement.Bounds(s.Widgets.Items[0]);
        Assert.True(DesktopPlacement.Arrange(s, area));
        var bounds = s.Fences.Fences.Select(DesktopPlacement.Bounds).Concat(s.Widgets.Items.Select(DesktopPlacement.Bounds)).ToArray();
        Assert.Equal(weather, DesktopPlacement.Bounds(s.Widgets.Items[0]));
        Assert.All(bounds, b => Assert.True(area.Contains(b)));
        for (int i = 0; i < bounds.Length; i++)
            for (int j = 0; j < i; j++) Assert.False(bounds[i].IntersectsWith(bounds[j]));
        Assert.True(DesktopPlacement.Arrange(s, area));
        Assert.Equal(bounds, s.Fences.Fences.Select(DesktopPlacement.Bounds).Concat(s.Widgets.Items.Select(DesktopPlacement.Bounds)).ToArray());
    }

    [Fact]
    public void AddingAnItemAvoidsAGroupAndCollapsedGroupsRetainExpandedHeight()
    {
        var s = new DesktopSettings();
        s.Fences.Enabled = true;
        var f = new FenceDefinition { X = 778, Y = 12, Width = 410, Height = 360, Collapsed = true };
        s.Fences.Fences.Add(f);
        var area = new Rect(0, 0, 1200, 800);
        var added = DesktopPlacement.ForNew(s, area, new Size(280, 230));
        Assert.False(added.IntersectsWith(DesktopPlacement.Bounds(f)));
        Assert.True(DesktopPlacement.Arrange(s, area, true));
        Assert.Equal(360d, f.Height);
        Assert.Equal(DesktopPlacement.CollapsedHeight, DesktopPlacement.Bounds(f).Height);
    }

    [Fact]
    public void DuplicateIdsInOldSettingsKeepBothGroupsAndTheirFiles()
    {
        var s = new DesktopSettings();
        s.Fences.Fences.Add(new FenceDefinition { Id = "same", Title = "a" });
        s.Fences.Fences.Add(new FenceDefinition { Id = "same", Title = "b", Members = new() { ["x"] = "y" } });
        s.Normalize();
        Assert.Equal(2, s.Fences.Fences.Select(f => f.Id).Distinct().Count());
        Assert.Equal("y", s.Fences.Fences[1].Members["x"]);
    }

    [Fact]
    public void PortalIndexesFilesBeyondTheOldThreeHundredItemLimit()
    {
        string folder = Directory.CreateTempSubdirectory().FullName;
        try
        {
            for (int i = 0; i < 305; i++) File.WriteAllText(Path.Combine(folder, $"تصویر-{i:D3}.txt"), "");
            var items = FenceWindow.LoadItems(new FenceDefinition { Mode = "folder", FolderPath = folder }, new DesktopSettings());
            Assert.Equal(305, items.Count);
            Assert.Equal("تصویر-304.txt", items[^1].Name);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void EveryWallpaperIsBundledAndEveryFinishRendersDistinctly()
    {
        DesktopThemeAndClockTests.RunSta(() =>
        {
            var wallpapers = DesktopThemePresets.All.Where(p => p.Wallpaper != null).ToList();
            Assert.Equal(15, wallpapers.Count);
            Assert.Equal(wallpapers.Count, wallpapers.Select(p => p.Wallpaper).Distinct().Count());
            foreach (var preset in wallpapers)
            {
                Assert.Contains(FenceIconArt.Styles, s => s.Id == preset.FenceIconStyle);
                using var stream = File.OpenRead(preset.WallpaperPath!);
                var frame = BitmapFrame.Create(stream);
                Assert.True(frame.PixelWidth >= 1280 && frame.PixelWidth > frame.PixelHeight * 1.6);
            }
            var identities = new HashSet<string>();
            var pixels = new HashSet<string>();
            foreach (var finish in Enum.GetValues<FolderFinish>())
            {
                var design = new IconDesign { Finish = finish };
                Assert.True(identities.Add(design.Signature));
                var bitmap = FolderIconRenderer.Render(design, 48);
                var bytes = new byte[48 * 48 * 4];
                bitmap.CopyPixels(bytes, 48 * 4, 0);
                Assert.True(pixels.Add(Convert.ToHexString(SHA256.HashData(bytes))));
            }
            Assert.Equal(FolderFinish.Classic, JsonSerializer.Deserialize<IconDesign>("{}")!.Finish);
        });
    }

    [Fact]
    public void OfficialInstallerAndChecksumAreRecognisedByAnOlderInstallation()
    {
        string tag = "v3.6.0", setup = $"Shamsa-Setup-{tag}-win-x64.exe";
        var release = JsonSerializer.SerializeToElement(new {
            tag_name = tag, draft = false, prerelease = false,
            assets = new[] { setup, "SHA256SUMS.txt" }.Select(name => new {
                name, size = 1000, browser_download_url = $"https://github.com/seyedebrahim0211-oss/Shamsa/releases/download/{tag}/{name}"
            })
        });
        Assert.Equal(setup, UpdateService.ReadRelease(release, new Version(3, 4, 0, 0))!.FileName);
        Assert.Null(UpdateService.ReadRelease(release, new Version(3, 5, 0, 0)));
    }

    [Fact]
    public async Task QueuedShellRequestsFinishWhenSupersededOrClosed()
    {
        using var worker = new StaWorker();
        using var started = new ManualResetEventSlim();
        using var unblock = new ManualResetEventSlim();
        var running = worker.RunAsync(() => { started.Set(); unblock.Wait(TimeSpan.FromSeconds(8)); return 7; });
        Assert.True(started.Wait(TimeSpan.FromSeconds(4)));
        var queued = Enumerable.Range(1, 12).Select(i => worker.RunAsync(() => i)).ToArray();
        worker.Dispose();
        unblock.Set();
        Assert.Equal(7, await running);
        await Task.WhenAll(queued).WaitAsync(TimeSpan.FromSeconds(4));
    }

    [Fact]
    public async Task OrganizerUndoPreservesExistingFoldersAndRetryableConflicts()
    {
        string folder = Directory.CreateTempSubdirectory().FullName;
        try
        {
            string source = Path.Combine(folder, "طرح.txt");
            Directory.CreateDirectory(Path.Combine(folder, "Documents"));
            File.WriteAllText(source, "نگارخانه");
            var service = new SmartOrganizerService();
            var moved = Assert.Single(service.Execute(service.BuildPlan(folder)).Done);
            Assert.Equal("نگارخانه", File.ReadAllText(moved.To));
            File.WriteAllText(source, "جایگزین");
            var remaining = new List<OrganizeMove>();
            Assert.Equal(0, service.Undo(new[] { moved }, remaining));
            Assert.Single(remaining);
            File.Move(source, Path.Combine(folder, "دیگر.txt"));
            Assert.Equal(1, service.Undo(remaining));
            Assert.True(Directory.Exists(Path.Combine(folder, "Documents")));
            Assert.Equal("نگارخانه", File.ReadAllText(source));
            File.Copy(source, Path.Combine(folder, "کپی.txt"));
            var duplicate = Assert.Single(await new DuplicateFinderService().FindDuplicatesAsync(folder));
            Assert.Equal(2, duplicate.FilePaths.Count);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))), duplicate.Hash);
            Assert.NotNull(BatchRenameService.ValidateName("CON.sample.txt"));
        }
        finally { Directory.Delete(folder, true); }
    }
}
