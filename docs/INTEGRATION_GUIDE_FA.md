# راهنمای نصب، یکپارچه‌سازی و انتشار نسخه ۳.۰.۰ شمسا (Shamsa Pro)

این بسته ارتقا به‌طور اختصاصی برای هسته فعلی مخزن **[seyedebrahim0211-oss/Shamsa](https://github.com/seyedebrahim0211-oss/Shamsa)** (زبان C# / .NET 8.0 / WPF) طراحی شده است.

## ۱. نحوه اعمال در مخزن گیت‌هاب و ساخت خودکار نصاب (`.exe`)

1. محتویات این فایل ZIP را دقیقاً در ریشه مخزن محلی `Shamsa` کپی کنید (فایل‌های جدید در پوشه‌های `Services/`، `Services/Desktop/`، `Views/`، `Views/Desktop/`، `sdk/`، `installer/` و `.github/workflows/` قرار می‌گیرند).
2. در فایل `App.xaml.cs` موجود در مخزن، فقط **دو خط** زیر را اضافه کنید:
   - در انتهای متد `Application_Startup`:
     ```csharp
     Shamsa.Services.ShamsaProBootstrap.Initialize(this);
     ```
   - در ابتدای متد `Application_Exit`:
     ```csharp
     Shamsa.Services.ShamsaProBootstrap.Shutdown();
     ```
   - (اختیاری) در منوی کلیک‌راست System Tray (`SetupTrayIcon`) می‌توانید میانبرهای زیر را اضافه کنید:
     - `ShamsaProBootstrap.OpenProHub()` — مرکز تنظیمات و بروزرسانی
     - `ShamsaProBootstrap.OpenScreenRecorderHud()` — ضبط صفحه هوشمند
     - `ShamsaProBootstrap.OpenFolderCustomizer()` — استودیو آیکون و قفل پوشه
     - `ShamsaProBootstrap.OpenSpotlight()` — جستجوی سریع و کلیپ‌بورد
3. تغییرات را روی شاخه `main` گیت‌هاب `commit` و `push` کنید:
   ```bash
   git add .
   git commit -m "feat: upgrade Shamsa core to v3.0.0 Pro Desktop Suite"
   git push origin main
   ```
4. گردش‌کار `.github/workflows/release.yml` به‌صورت خودکار روی سرور ویندوز گیت‌هاب اجرا می‌شود، پروژه را با `.NET 8` بیلد می‌کند، نصاب **`Shamsa-Setup-v3.0.0-win-x64.exe`** و فایل **`SHA256SUMS.txt`** را می‌سازد و در بخش **Releases** منتشر می‌کند.

## ۲. راهنمای بازیابی اضطراری (Recovery & Safe Mode)
- اگر زمانی نوار وظیفه اصلی ویندوز مخفی شده بود یا مشکلی در چیدمان دسکتاپ رخ داد:
  1. در پنجره **مرکز مدیریت شمسا** روی دکمه قرمز **«بازیابی دسکتاپ ویندوز»** کلیک کنید.
  2. در صورت بسته شدن ناگهانی (Crash)، شمسا در اجرای بعدی به‌صورت خودکار وارد **Safe Mode** می‌شود و نوار وظیفه ویندوز را بازمی‌گرداند.
  3. تنظیمات کاربر در مسیر `%LocalAppData%\Shamsa\pro-settings.json` ذخیره می‌شود و قبل از هر بروزرسانی یک بکاپ در `pro-settings.backup.json` گرفته می‌شود.

## ۳. فهرست قابلیت‌های پیاده‌سازی‌شده و محدودیت‌های شفاف
- **بروزرسانی درون‌برنامه‌ای (`UpdateServicePro`)**: بررسی آخرین نسخه از GitHub Releases، نمایش تغییرات نسخه، دانلود با نوار پیشرفت، بررسی SHA-256، بکاپ تنظیمات و اجرای نصاب.
- **پیش‌نمایش صفحه اول آفیس و PDF (`DocumentPreviewService`)**: استفاده از Windows Shell Preview + خواندن مستقیم متن/اسلاید/شیت صفحه اول برای `.docx`، `.xlsx`، `.pptx` و شمارش صفحه `.pdf`.
- **پیش‌نمایش شیشه‌ای و بزرگ پوشه (`GlassFolderPreviewWindow`)**: پنجره شیشه‌ای قابل تغییر اندازه و شفافیت با جستجو و نمایش پیش‌نمایش فایل‌های داخل پوشه.
- **استودیو آیکون و قفل پوشه (`FolderIconService` و `FolderVaultService`)**: ساخت آیکون گرادیانی/متنی و تبدیل PNG/JPG/ICO، اعمال گروهی روی چند پوشه، منوی کلیک‌راست بدون نیاز به ادمین، و رمزگذاری واقعی با AES-256-GCM.
- **ضبط صفحه و فرمان صوتی (`ScreenRecorderService` و `VoiceCommandService`)**: ضبط تمام‌صفحه/ناحیه/پنجره با `FfmpegManager`، اسکرین‌شات فوری، و فرمان صوتی (وابسته به نصب بودن بسته تشخیص گفتار ویندوز در سیستم کاربر؛ دارای درگاه آماده برای اتصال به Whisper/Vosk محلی).
- **محیط دسکتاپ شبیه Seelen UI**: نوار بالا و مرکز کنترل، داک هوشمند با پیش‌نمایش زنده پنجره‌ها (`DwmRegisterThumbnail`)، مدیر پنجره‌ها (`SetWindowPos`) با قوانین اختصاصی هر برنامه و پروفایل‌های کاری، گروه‌بندی آیکون‌ها به سبک Fences، ویجت‌های تقویم شمسی/مناسبت‌ها/اوقات شرعی/پایش سیستم/یادداشت، جستجوی سریع با تاریخچه کلیپ‌بورد، تم خودکار روز و شب، و میزبان افزونه نوار بالا.
- **محدودیت‌های شناخته‌شده**:
  - نمایش دمای دقیق GPU نیازمند درایور اختصاصی NVML/ADL است و در صورت نبود دسترسی، مقدار «ناموجود» نمایش داده می‌شود.
  - تشخیص گفتار مستقیم فارسی در `System.Speech` نیازمند نصب بسته زبان ویندوز یا اتصال یک پل STT محلی (مثل Whisper) به متد `DispatchExternalTranscript` است.
  - کلیدهای کنترل وای‌فای/بلوتوث/نور شب/مزاحم نشوید در نوار بالا به صفحات رسمی تنظیمات ویندوز (`ms-settings:`) متصل شده‌اند تا بدون تغییر فایل‌های سیستمی و بدون نیاز به دسترسی Administrator عمل کنند.
