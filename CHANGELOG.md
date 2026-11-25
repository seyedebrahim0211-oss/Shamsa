# Changelog — Shamsa Pro

## [3.0.0] - 2026-11-25
### Added
- **In-App Auto-Updater (`UpdateServicePro`)**: GitHub Releases integration with changelog preview, streaming progress bar, SHA-256 verification, pre-update settings backup, and silent installer launch.
- **Office & PDF First-Page Preview (`DocumentPreviewService`)**: High-DPI Windows Shell thumbnails plus offline OpenXML first-page preview for Word, Excel, PowerPoint, and PDF files.
- **Glassmorphic Customizable Folder Preview (`GlassFolderPreviewWindow`)**: Resizable glass window with live opacity slider, search, and integrated document/media preview pane.
- **Fold11-Style Folder Icon Studio & AES-256 Locker (`FolderIconService`, `FolderVaultService`, `FolderCustomizerWindow`)**: Custom gradient/text/image `.ico` builder, batch folder apply via `desktop.ini`, HKCU Explorer context menu, and AES-256-GCM folder vault encryption.
- **Bandicam-Style Screen Recorder & Voice Commands (`ScreenRecorderService`, `VoiceCommandService`, `ScreenRecorderHudWindow`)**: FFmpeg-backed recording, instant screenshots, and Persian/English voice triggers.
- **Seelen UI-Inspired Desktop Environment Suite**:
  - Customizable Top Bar & Control Center (`TopBarWindow`) with Jalali clock, Workspace switcher, Windows Settings quick controls, and Plugin Host slot.
  - Floating Smart Dock (`SmartDockWindow`) with hover magnification and live DWM window previews (`DwmRegisterThumbnail`).
  - Real Win32 Tiling Window Manager (`WindowManagerService`) with 2/3/4-zone/stacked layouts, per-app rules, and saved Workspace profiles.
  - Desktop Widgets (`WidgetHostWindow`) with Jalali/Gregorian/Hijri calendar, Iranian holidays, Prayer Times, real CPU/RAM/Disk/Battery/Network telemetry, and Sticky Notes.
  - Non-destructive Fences-style Desktop Grouping (`DesktopFencesWindow`).
  - Spotlight Quick Search & Local Clipboard History (`SpotlightLauncherWindow`, `ClipboardHistoryService`).
  - Extended Theme Engine (`ThemeEngineProService`) with 8 themes, auto day/night clock schedule, RTL, and `Vazirmatn` font support.
  - Safe Mode & One-Click Restore Windows Desktop (`RecoveryService`).
