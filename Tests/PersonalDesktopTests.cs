using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Shamsa.Desktop;
using Shamsa.Desktop.Fences;
using Shamsa.Desktop.Personalize;
using Xunit;

namespace Shamsa.Tests;

public class PersonalDesktopTests
{
    [Fact]
    public void RestoringOnAnotherScreenMatchesSurvivingItemsAndUndoSurvivesReload()
    {
        var s = new DesktopSettings { Enabled = true };
        s.Widgets.Enabled = s.Fences.Enabled = true; s.Fences.AvoidOverlap = false;
        var w = new WidgetPlacement { Id = "note", Kind = "note", X = 800, Y = 600, Width = 220, Height = 200, Text = "اول" };
        var f = new FenceDefinition { Id = "folder", X = 20, Y = 790, Width = 260, Height = 390, Collapsed = true };
        s.Widgets.Items.Add(w); s.Fences.Fences.Add(f); s.Fences.Fences.Add(new() { Id = "removed" });
        var large = new Rect(20, 30, 1000, 800);
        Assert.True(PersonalLayouts.SaveCurrent(s, large, "  طراحی  ").Save);
        string id = Assert.Single(s.PersonalLayouts.Saved).Id;
        Assert.False(PersonalLayouts.SaveCurrent(s, large, "طراحی").Save);
        s.Fences.Fences.RemoveAt(1);
        var fresh = new FenceDefinition { Id = "added", X = 100, Y = 180 };
        s.Fences.Fences.Add(fresh);
        w.X = 120; w.Y = 130; w.Text = "ویرایش بعدی"; f.X = 260; f.Y = 100; f.Collapsed = false;
        f.Members["doc"] = "گزارش";
        var small = new Rect(40, 50, 600, 430);
        var applied = PersonalLayouts.Switch(s, small, id);
        Assert.True(applied.Save); Assert.Contains("۱", applied.Message);
        Assert.Equal(small.Right, w.X + w.Width); Assert.InRange(w.Y, small.Top, small.Bottom - w.Height);
        Assert.True(f.Collapsed); Assert.Equal(small.Bottom, f.Y + DesktopPlacement.CollapsedHeight);
        Assert.Equal(390, f.Height); Assert.Equal(100, fresh.X); Assert.Equal("گزارش", f.Members["doc"]);
        s = s.Clone();
        Assert.True(PersonalLayouts.Switch(s, small, null, undo: true).Save);
        Assert.Equal(120, s.Widgets.Items[0].X); Assert.Equal("ویرایش بعدی", s.Widgets.Items[0].Text);
        Assert.Equal(260, s.Fences.Fences[0].X); Assert.False(s.Fences.Fences[0].Collapsed);
        Assert.Null(s.PersonalLayouts.BeforeSwitch);
    }

    [Fact]
    public void MissingItemsDoNotApplySwitchAndDisplayBindingDoesNotLoop()
    {
        var s = new DesktopSettings(); s.Widgets.Items.Add(new() { Id = "old" });
        var area = new Rect(0, 0, 1200, 700);
        PersonalLayouts.SaveCurrent(s, area, "کار");
        var id = s.PersonalLayouts.Saved[0].Id;
        Assert.True(PersonalLayouts.BindDisplay(s, id, "display1").Save);
        var tracker = new LayoutDisplayTracker();
        Assert.Equal(id, tracker.Enter(s.PersonalLayouts, "DISPLAY1", 1920, 1080, true));
        Assert.Null(tracker.Enter(s.PersonalLayouts, "display1", 1920, 1080, true));
        Assert.Null(tracker.Enter(s.PersonalLayouts, "display2", 1920, 1080, true));
        Assert.Equal(id, tracker.Enter(s.PersonalLayouts, "display1", 1280, 720, true));
        s.Widgets.Items.Clear();
        Assert.False(PersonalLayouts.Switch(s, area, id).Save);
        Assert.Null(s.PersonalLayouts.BeforeSwitch);
        Assert.True(PersonalLayouts.Remove(s, id).Save); Assert.Empty(s.PersonalLayouts.Displays);
    }

    [Fact]
    public void AppFoldersKeepOrderAndDeletingAFolderReturnsOnlyItsShortcuts()
    {
        var group = new FenceDefinition { Mode = "manual", Column = new(), Members = new() { ["b.exe"] = "B", ["c.exe"] = "C", ["a.exe"] = "A" } };
        var col = group.Column!; col.Normalize(group.Members);
        col.Folders.Add("طراحی"); col.Move("b.exe", "طراحی"); col.Move("a.exe", "طراحی");
        Assert.True(col.Step("a.exe", -1)); Assert.False(col.Step("a.exe", -1));
        Assert.Equal(new[] { "a.exe", "c.exe", "b.exe" }, FenceWindow.LoadItems(group, new()).Select(i => i.Path));
        col.Folders.Remove("طراحی"); col.Normalize(group.Members);
        Assert.Empty(col.Locations); Assert.Equal(3, group.Members.Count);
        group.Members.Remove("b.exe"); col.Normalize(group.Members);
        Assert.DoesNotContain("b.exe", col.Order);
    }

    [Fact]
    public void IndependentStudioEditsPreserveMovedGroupsEditedNotesAndSavedProfiles()
    {
        var initial = new DesktopSettings();
        var g = new FenceDefinition { Id = "g" }; initial.Fences.Fences.Add(g); initial.Widgets.Items.Add(new() { Id = "n" });
        var edit = initial.Clone(); var live = initial.Clone();
        edit.Fences.Fences[0].Title = "کار"; live.Fences.Fences[0].X = 480; live.Widgets.Items[0].Text = "تازه";
        live.Fences.Fences.Add(new() { Id = "created" });
        PersonalLayouts.SaveCurrent(live, new Rect(0, 0, 1200, 700), "روزانه");
        var result = DesktopStudioEdits.Merge(initial, edit, live);
        Assert.Equal("کار", result.Fences.Fences[0].Title); Assert.Equal(480, result.Fences.Fences[0].X);
        Assert.Equal(2, result.Fences.Fences.Count); Assert.Equal("تازه", result.Widgets.Items[0].Text);
        Assert.Single(result.PersonalLayouts.Saved);
    }

    [Theory]
    [InlineData("Top", "Top", "Bottom", false, false)]
    [InlineData("Bottom", "Bottom", "Top", true, false)]
    [InlineData("Top", "Bottom", "Bottom", true, true)]
    [InlineData("Bottom", "Top", "Top", false, true)]
    [InlineData("Top", "Left", "Left", false, true)]
    [InlineData("Bottom", "Right", "Right", false, false)]
    public void StatusBarAndDockStayApartOnOffsetMonitors(string bar, string requested, string dock, bool reserve, bool autoHide)
    {
        var s = new DesktopSettings(); s.TopBar.Enabled = true; s.TopBar.Position = bar; s.TopBar.ReserveSpace = reserve;
        s.Dock.Position = requested; s.Dock.AutoHide = autoHide;
        Assert.Equal(autoHide ? 3 : 108, DesktopBarLayout.DockReservation(s.Dock.AutoHide, 72, 1.5));
        Assert.Equal(dock, DesktopBarLayout.DockEdge(s)); Assert.Equal(requested, s.Dock.Position);
        var monitor = new Rect(-1920, -480, 1920, 1080);
        var stripe = DesktopBarLayout.Strip(monitor, bar, s.TopBar.Height * 1.5);
        var work = reserve ? new Rect(monitor.Left, bar == "Top" ? stripe.Bottom : monitor.Top, monitor.Width, monitor.Height - stripe.Height) : monitor;
        var strip = DesktopBarLayout.Strip(DesktopBarLayout.SideSpan(work, s, 1.5), dock, 118);
        var overlap = Rect.Intersect(stripe, strip);
        Assert.True(overlap.IsEmpty || overlap.Width * overlap.Height == 0);
        Assert.True(monitor.Contains(strip));
        s.TopBar.Enabled = false; Assert.Equal(requested, DesktopBarLayout.DockEdge(s));
    }

    [Fact]
    public void IconModelsRenderDifferentlyAndExplicitOverridesSurviveThemeChanges()
    {
        DesktopThemeAndClockTests.RunSta(() =>
        {
            var s = new DesktopSettings { ThemePresetId = "mostofi" };
            var hashes = new HashSet<string>();
            foreach (var model in AppIconArt.Models.Where(m => m.Id != "plain"))
            {
                var plate = FenceIconArt.Plate(model.Id, Colors.Turquoise)!;
                Assert.True(plate.IsFrozen);
                var visual = new DrawingVisual();
                using (var draw = visual.RenderOpen()) draw.DrawImage(plate, new Rect(0, 0, 64, 64));
                var frame = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32); frame.Render(visual);
                byte[] bytes = new byte[64 * 64 * 4]; frame.CopyPixels(bytes, 256, 0);
                Assert.True(hashes.Add(Convert.ToHexString(SHA256.HashData(bytes))));
            }
            var auto = AppIconArt.Resolve(s, "app.exe"); Assert.Equal("enamel", auto.Style);
            s.AppIcons.WallpaperPresetId = "boshrouyeh-night"; s.AppIcons.WallpaperAccentHex = "#99BBFF";
            Assert.Equal("prismatic", AppIconArt.Resolve(s, "app.exe").Style);
            s.AppIcons.PerApp["App.exe"] = new("lens", "#FEAA55"); s = s.Clone();
            Assert.Equal(("lens", Color.FromRgb(254, 170, 85)), AppIconArt.Resolve(s, "app.exe"));
            s.AppIcons.MatchWallpaper = false; Assert.Equal("enamel", AppIconArt.Resolve(s, null).Style);
            var mostofi = DesktopThemePresets.All.Single(p => p.Id == "mostofi");
            Assert.Matches("^#[0-9A-F]{6}$", WallpaperService.SampleAccent(mostofi.WallpaperPath!)!);
        });
    }
}
