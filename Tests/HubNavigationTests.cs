using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Media;
using Shamsa.Desktop;
using Shamsa.Desktop.Personalize;
using Shamsa.Services;
using Shamsa.Views;
using Xunit;

namespace Shamsa.Tests;

[CollectionDefinition("Hub UI", DisableParallelization = true)]
public sealed class HubUiCollection { }

[Collection("Hub UI")]
public class HubNavigationTests
{
    [Fact]
    public void AllHubPagesOpenAndLayOutThroughNormalNavigation()
    {
        DesktopThemeAndClockTests.RunSta(() =>
        {
            string temp = Directory.CreateTempSubdirectory().FullName;
            string original = AppPaths.DataDir;
            AppPaths.DataDir = temp;
            PersonalizeStudioPage.SafePreviewMode = true;
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(application.Dispatcher));
            try
            {
                application.Resources.MergedDictionaries.Add(new ResourceDictionary {
                    Source = new Uri("/Shamsa;component/Themes/Dark.xaml", UriKind.Relative)
                });
                ThemeService.Apply(SettingsService.Current);
                var hub = new MainHubWindow(new HubActions { CreateDashboard = () => new ToolsDashboardWindow(new ConverterService()) });
                try
                {
                    string[] pages = { "home", "files.preview", "files.donut", "files.organize", "files.rename", "files.duplicates",
                        "files.convert", "capture.recorder", "folders.studio", "personalize", "settings", "help" };
                    foreach (string id in pages.Concat(DesktopStudioView.Pages.Select(p => "desktop." + p.Id)))
                    {
                        hub.Navigate(id);
                        Assert.Equal(id, hub.CurrentPageId);
                        var root = (FrameworkElement)hub.Content;
                        root.Measure(new Size(1280, 760));
                        root.Arrange(new Rect(0, 0, 1280, 760));
                        root.UpdateLayout();
                    }
                    ExerciseDesktopControls(hub);
                    if (File.Exists(AppLog.LogFile))
                    {
                        var errors = File.ReadLines(AppLog.LogFile).Where(x =>
                            x.Contains("[ERROR] Hub page") || x.Contains("[ERROR] Studio page"));
                        Assert.True(!errors.Any(), string.Join(Environment.NewLine, errors));
                    }
                }
                finally { hub.Close(); }
            }
            finally
            {
                application.Shutdown();
                PersonalizeStudioPage.SafePreviewMode = false;
                AppPaths.DataDir = original;
                Directory.Delete(temp, true);
            }
        });
    }

    private static void ExerciseDesktopControls(MainHubWindow hub)
    {
        var original = DesktopSettingsStore.Current.Clone();
        try
        {
            // Use the same navigation and routed button events as an interactive WPF session.
            var root = (FrameworkElement)hub.Content;
            void Layout()
            {
                root.Measure(new Size(1280, 760)); root.Arrange(new Rect(0, 0, 1280, 760)); root.UpdateLayout();
            }
            void Click(string label)
            {
                Layout();
                var button = Tree(root).OfType<Button>().First(b => Equals(b.Content, label));
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
            }
            hub.Navigate("desktop.apps");
            Click("ساخت و نمایش ستون برنامه‌ها");
            hub.Navigate("desktop.layouts");
            Layout();
            var box = Tree(root).OfType<TextBox>().First(t => AutomationProperties.GetName(t) == "نام چیدمان جدید");
            box.Text = "طراحی آزمایشی";
            Click("ذخیرهٔ جای فعلی");
            Assert.Contains(DesktopSettingsStore.Load().PersonalLayouts.Saved, p => p.Name == box.Text);
            var current = DesktopSettingsStore.Current.Clone();
            var group = current.Fences.Fences.Single(g => g.Column != null);
            double old = group.X;
            current.Fences.AvoidOverlap = false;
            group.X = old > 180 ? 70 : 320;
            double moved = group.X;
            DesktopSettingsStore.Save(current);
            Click("اعمال چیدمان");
            Assert.NotEqual(moved, DesktopSettingsStore.Current.Fences.Fences.Single(g => g.Column != null).X);
            Click("برگرداندن آخرین جابه‌جایی");
            Assert.Equal(moved, DesktopSettingsStore.Current.Fences.Fences.Single(g => g.Column != null).X);
        }
        finally { DesktopSettingsStore.Save(original); }
    }

    private static IEnumerable<DependencyObject> Tree(DependencyObject item)
    {
        yield return item;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(item); i++)
            foreach (var child in Tree(VisualTreeHelper.GetChild(item, i))) yield return child;
    }
}
