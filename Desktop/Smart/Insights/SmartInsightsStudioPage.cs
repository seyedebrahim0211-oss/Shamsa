using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Shamsa.FolderStudio;
using Shamsa.Services;
using static Shamsa.Desktop.ControlCenter.StudioUi;

namespace Shamsa.Desktop.Smart.Insights;

/// <summary>«هوشمندسازی»: suggestions, tidy desktop + archive, and folder icon suggestions on one simple page.</summary>
public static class SmartInsightsStudioPage
{
    /// <summary>Test hook: when set, the folder-icon card starts with this folder (render harness).</summary>
    public static string? PreviewFolder { get; set; }

    /// <summary>Page without a working copy: desktop settings are saved directly when the groups change.</summary>
    public static UIElement Build(Action changed) => Build(null, changed);

    /// <summary>
    /// Studio page. With <paramref name="working"/> the desktop groups (fences) are added to that working copy and
    /// <paramref name="changed"/> is called so the studio saves it; otherwise they are saved here.
    /// </summary>
    public static UIElement Build(DesktopSettings? working, Action changed)
    {
        var page = Page();
        var st = InsightsStore.Settings;
        void Save() => InsightsStore.Save();

        // ---------------------------------------------------------- suggestions
        var sugg = Body(Card(page, "پیشنهادهای هوشمند",
            "نگارخانه پوشه دانلود، فضای دیسک و برنامه‌های صبحگاهی را سبک بررسی می‌کند و پیشنهاد می‌دهد. هیچ کاری بدون کلیک و تأیید تو انجام نمی‌شود و چیزی از این رایانه بیرون نمی‌رود."));
        sugg.Children.Add(Toggle("نمایش پیشنهادها", null, st.SuggestionsEnabled, v => st.SuggestionsEnabled = v, () => { Save(); SmartSuggestions.Refresh(force: true); }));
        var view = SmartSuggestionsView.Build(maxCards: 4, showTitle: false);
        if (view is FrameworkElement fe) fe.Margin = new Thickness(0, 8, 0, 0);
        sugg.Children.Add(view);

        var more = new StackPanel();
        more.Children.Add(Toggle("پوشه دانلود شلوغ", "پیشنهاد مرتب‌سازی بر اساس نوع، با امکان برگشت", st.SuggestDownloads, v => st.SuggestDownloads = v, Save));
        more.Children.Add(Toggle("فایل‌های تکراری", "فقط فایل‌های بالای ۱ مگابایت در دانلود و دسکتاپ، بدون زیرپوشه‌ها", st.SuggestDuplicates, v => st.SuggestDuplicates = v, Save));
        more.Children.Add(Toggle("فضای دیسک، فایل‌های موقت و سطل بازیافت", null, st.SuggestDisk, v => st.SuggestDisk = v, Save));
        more.Children.Add(Toggle("برنامه‌های صبحگاهی", "پیشنهاد «باز کردن یکجا» برای برنامه‌هایی که بیشتر صبح‌ها باز می‌کنی", st.SuggestRoutine, v => st.SuggestRoutine = v, Save));
        more.Children.Add(Toggle("یادگیری عادت صبح", "هر دقیقه فقط نام برنامه پنجره فعال (قبل از ظهر) روی همین رایانه ثبت می‌شود", st.LearnRoutine, v => st.LearnRoutine = v, Save));
        var actions = new WrapPanel();
        actions.Children.Add(Button("فراموش کردن عادت‌ها", () =>
        {
            InsightsStore.Update(s => { s.Routine = new RoutineData(); });
            Notifier.Show("عادت‌های صبح پاک شد.");
        }));
        actions.Children.Add(Button("نمایش دوباره پیشنهادهای ردشده", () =>
        {
            InsightsStore.Update(s => s.Dismissed.Clear());
            SmartSuggestions.Refresh(force: true);
        }));
        more.Children.Add(actions);
        sugg.Children.Add(MoreExpander(more));

        // ---------------------------------------------------------- tidy desktop
        var tidy = Body(Card(page, "دسکتاپ مرتب",
            "فایل‌های دسکتاپ خودکار در پنج گروه دیده می‌شوند: عکس‌ها، اسناد، نصبی‌ها، فشرده و ویدیو. فایل‌ها جابه‌جا نمی‌شوند؛ فقط مرتب نمایش داده می‌شوند."));
        tidy.Children.Add(Toggle("گروه‌بندی خودکار دسکتاپ", "از «گروه‌بندی دسکتاپ» استفاده می‌کند و خاموش کردنش گروه‌ها را برمی‌دارد",
            st.TidyEnabled, v =>
            {
                if (DesktopTidy.SetEnabled(v, working) && working != null) changed();
            }, () => { }));

        var archiveMore = new StackPanel();
        var archiveStatus = Muted("");
        void UpdateArchiveStatus()
        {
            int archived = DesktopTidy.ArchivedCount;
            archiveStatus.Text = archived > 0
                ? $"{SmartSuggestions.Fa(archived)} فایل در «{DesktopTidy.ArchiveFolderName}» است و قابل برگشت است."
                : "هنوز فایلی بایگانی نشده.";
        }
        UpdateArchiveStatus();

        CheckBox? archiveToggle = null;
        archiveToggle = Toggle("بایگانی خودکار فایل‌های قدیمی", $"فایل‌هایی که مدتی دست نخورده‌اند به پوشه «{DesktopTidy.ArchiveFolderName}» روی دسکتاپ می‌روند (میانبرها می‌مانند)",
            st.ArchiveEnabled, v =>
            {
                if (v && !st.ArchiveEnabled)
                {
                    int n = DesktopTidy.ArchiveCandidates(DesktopTidy.DesktopFolder(), st.ArchiveDays, DateTime.Now).Count;
                    var answer = MessageBox.Show(
                        $"از الان هر ۶ ساعت، فایل‌های دسکتاپ که {SmartSuggestions.Fa(st.ArchiveDays)} روز دست نخورده‌اند به «{DesktopTidy.ArchiveFolderName}» منتقل می‌شوند (الان {SmartSuggestions.Fa(n)} فایل). همیشه با یک کلیک برمی‌گردند. روشن شود؟",
                        "نگارخانه — بایگانی دسکتاپ", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No,
                        MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
                    if (answer != MessageBoxResult.Yes)
                    {
                        archiveToggle!.Dispatcher.BeginInvoke(() => archiveToggle.IsChecked = false);
                        return;
                    }
                }
                st.ArchiveEnabled = v;
            }, Save);
        archiveMore.Children.Add(archiveToggle);
        archiveMore.Children.Add(Labeled("بعد از چند روز",
            Combo(new[] { ("7", "۷ روز"), ("14", "۱۴ روز"), ("30", "۳۰ روز"), ("60", "۶۰ روز"), ("90", "۹۰ روز") },
                st.ArchiveDays.ToString(), v => st.ArchiveDays = int.Parse(v), Save)));
        archiveMore.Children.Add(archiveStatus);
        var archiveButtons = new WrapPanel();
        archiveButtons.Children.Add(Button("بایگانی الان", () =>
        {
            int n = DesktopTidy.ArchiveCandidates(DesktopTidy.DesktopFolder(), st.ArchiveDays, DateTime.Now).Count;
            if (n == 0) { Notifier.Show("فایل قدیمی‌ای روی دسکتاپ نیست."); return; }
            if (MessageBox.Show($"{SmartSuggestions.Fa(n)} فایل به «{DesktopTidy.ArchiveFolderName}» منتقل شود؟", "نگارخانه — بایگانی دسکتاپ",
                    MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign) != MessageBoxResult.Yes)
                return;
            int moved = DesktopTidy.RunArchive(DateTime.Now);
            Notifier.Show($"{SmartSuggestions.Fa(moved)} فایل بایگانی شد.");
            UpdateArchiveStatus();
        }));
        archiveButtons.Children.Add(Button("برگرداندن همه به دسکتاپ", () =>
        {
            int back = DesktopTidy.UndoArchive();
            Notifier.Show($"{SmartSuggestions.Fa(back)} فایل به دسکتاپ برگشت.");
            UpdateArchiveStatus();
        }));
        archiveMore.Children.Add(archiveButtons);
        tidy.Children.Add(MoreExpander(archiveMore));

        // ---------------------------------------------------------- folder icon suggestions
        var icons = Body(Card(page, "پیشنهاد آیکون پوشه",
            "یک پوشه انتخاب کن؛ نگارخانه از روی نامش (مثل «دانشگاه»، «عکس‌ها»، «مالی») یا عکسی که داخلش هست آیکون پیشنهاد می‌دهد. با یک کلیک اعمال می‌شود."));
        var folderText = Muted("پوشه‌ای انتخاب نشده.");
        var wrap = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        var iconStatus = Muted("");
        string? currentFolder = null;

        void ShowFor(string folder)
        {
            currentFolder = folder;
            folderText.Text = "پوشه: " + FileKinds.Ltr(Path.GetFileName(folder.TrimEnd('\\')) is { Length: > 0 } n ? n : folder);
            wrap.Children.Clear();
            iconStatus.Text = "";
            foreach (var design in FolderIconSuggester.Suggest(folder))
                wrap.Children.Add(IconTile(design, () => ApplyIcon(currentFolder!, design, iconStatus)));
            if (wrap.Children.Count == 0) iconStatus.Text = "پیشنهادی پیدا نشد.";
        }

        var pick = Button("انتخاب پوشه…", () =>
        {
            var folder = ShellActions.PickFolder(currentFolder, Window.GetWindow(page));
            if (folder != null) ShowFor(folder);
        }, "AccentButton");
        icons.Children.Add(pick);
        icons.Children.Add(folderText);
        icons.Children.Add(wrap);
        icons.Children.Add(iconStatus);
        if (PreviewFolder != null) ShowFor(PreviewFolder);

        return page;
    }

    private static Expander MoreExpander(UIElement content)
    {
        var header = new TextBlock { Text = "بیشتر" };
        header.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        var exp = new Expander { Header = header, Content = content, Margin = new Thickness(0, 8, 0, 0), IsExpanded = false };
        exp.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        return exp;
    }

    private static UIElement IconTile(IconDesign design, Action apply)
    {
        BitmapSource? img = null;
        try { img = FolderIconRenderer.Render(design, 64); } catch (Exception ex) { AppLog.Warn("Icon suggestion render: " + ex.Message); }
        var stack = new StackPanel { Width = 92 };
        stack.Children.Add(new Image { Source = img, Width = 64, Height = 64, HorizontalAlignment = HorizontalAlignment.Center });
        var label = new TextBlock { Text = design.Name, FontSize = 11.5, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        label.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        stack.Children.Add(label);
        var b = new Button
        {
            Content = stack,
            Padding = new Thickness(6),
            Margin = new Thickness(0, 0, 8, 8),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            ToolTip = "اعمال روی پوشه",
            Cursor = System.Windows.Input.Cursors.Hand
        };
        b.SetResourceReference(Control.BorderBrushProperty, "BorderBrush");
        b.Click += (_, _) => apply();
        return b;
    }

    private static void ApplyIcon(string folder, IconDesign design, TextBlock status)
    {
        try
        {
            string? problem = FolderStudio.FolderIconService.ValidateFolder(folder);
            if (problem != null) { status.Text = problem; return; }
            string ico = FolderIconRenderer.EnsureIcoFile(design);
            FolderStudio.FolderIconService.ApplyIcon(folder, ico);
            try { FolderStudioStore.AddHistory(folder, design); } catch { }
            status.Text = "آیکون «" + design.Name + "» روی پوشه اعمال شد. (بازگردانی از استودیو پوشه)";
        }
        catch (Exception ex)
        {
            AppLog.Warn("Suggested icon apply failed: " + ex.Message);
            status.Text = "اعمال آیکون ناموفق بود: " + ex.Message;
        }
    }
}
