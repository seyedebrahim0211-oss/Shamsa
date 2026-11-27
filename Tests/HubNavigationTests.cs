using System.IO;
using System.Windows;
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
}
