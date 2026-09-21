# Changelog

All notable changes to Layer Form are documented in this file.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and Layer Form uses [Semantic Versioning](https://semver.org/) for public Windows releases.

Layer Form started as a Windows port of [Compositor](https://github.com/robbietilton/Compositor) for macOS. The 1.0.0 section records what the Windows edition changes and adds compared with the macOS app it is based on; later sections track releases since then.

## [Unreleased]

## [1.2.0] - 2026-09-21

### Added

- **Editable text layers**, ported from Compositor 1.1: choose the Type tool (`T`), click the canvas, and enter multiline text with an installed font, size, color, alignment, tracking, leading and optional fixed paragraph bounds. Text remains editable after saving; the PNG fallback keeps projects readable in older versions.
- **Soft Light** blend mode, matching Compositor 1.1.2.
- **Folder duplication** now copies the complete nested branch and preserves internal clipping-mask relationships, matching Compositor 1.1.5.
- **Folder opacity**, which multiplies through every layer inside the folder in the editor and exported images, matching Compositor 1.1.6.
- **Keyboard Shortcuts** window with a complete command list, custom bindings, conflict detection and reset, matching and extending Compositor 1.1.6 for Windows.
- Reusable **Custom Layers**, persistent custom shortcuts and per-layer locking.
- **Single-instance launch:** opening Layer Form again activates the existing window instead of starting another copy.
- **Rulers, configurable layout grids and a Slice tool** with manual slices, row/column grid division and numbered PNG slice export.
- **Photoshop-style Character panel:** Type tool text is created and edited live from the right panel instead of through a modal dialog.
- Text entry now happens directly on the canvas; the Character panel contains formatting controls only. Text editing activates only with the Type tool selected, leaving the Move tool free to select, drag, resize and rotate text layers.
- **Customizable toolbar:** File > Toolbar Settings lets each user show or hide individual left-rail tools and restore them all.

### Changed

- Clicking a swatch recolors the selected editable layer immediately; editable text keeps its text metadata when recolored.

## [1.1.0] — 2026-09-20

The first public release, with a Windows installer and automatic updates.

### Added

- **Windows installer** (`LayerForm-Setup-1.1.0.exe`). Installs for the current user without administrator rights, adds a Start menu shortcut and an optional desktop shortcut, and registers a standard uninstaller in Settings › Apps.
- **Automatic updates.** Shortly after launch, Layer Form checks [hastamev.com/layerform](https://hastamev.com/layerform) and GitHub Releases for a newer version. When one is available it shows what's new and offers **Download and Install**, **Skip This Version** or **Later**. The download is verified against its SHA-256 checksum, Layer Form asks to save open projects, installs the update and reopens by itself.
- **Help › Check for Updates…** to check on demand.
- **Help › Report a Bug…** opens a GitHub bug-report form with the Layer Form version and Windows build already filled in.
- **Help › Request a Feature…** opens the GitHub feature-request form, so ideas land in the issue tracker with consistent labels.
- A proper **About Layer Form** dialog with the app icon, version, Windows build, a short introduction, project lineage and links to the website, repository, changelog, license and the original Compositor project. It replaces the plain message box used previously.
- GitHub issue forms, pull-request template, security policy and a Windows CI workflow.

### Changed

- The Shape tool's hint now says `Shift+U` cycles through all six shapes.

## [1.0.0] — Windows edition, compared with Compositor for macOS

This is the baseline of Layer Form: everything the Windows edition does differently from, or in addition to, Compositor. It was built from source only and not published as a download.

### Platform and architecture

- Rewrote the application in **C# 12 on .NET 8** — the macOS app is Swift with C pixel kernels.
- Replaced the SwiftUI/AppKit interface with a **WinUI 3 / Windows App SDK** interface.
- Split the code into `Compositor.Core`, a platform-neutral library for documents, history, editing, masks, filters, rendering and the `.comp` format, and `Compositor.App`, the Windows UI. Core logic can be tested without opening a window.
- Replaced Core Graphics and Metal rendering with a **SkiaSharp** raster core and a **Win2D** canvas.
- Moved Remove Background from Apple's frameworks to **ONNX Runtime with DirectML**.
- Ships as an unpackaged, self-contained x64/ARM64 executable. The macOS project instead produces a signed and notarized DMG.
- Minimum OS is Windows 10 1809 (macOS 26 for the original).

### Added — features not in Compositor

- **Align and Distribute** for layers (left, horizontal center, right, top, vertical center, bottom; distribute horizontal/vertical centers), from the Layer menu and a dockable Align panel.
- **Layer effects**: *Change Color* (recolor a layer and keep its transparency), *Gradient Overlay* and *Add Shadow*.
- **More shapes**: Line, Triangle, Polygon and Star join Rectangle and Ellipse; `Shift+U` cycles through all six.
- **Swatches panel** with a preset palette of grays, hues and skin tones, plus custom swatches saved from the foreground color and kept between sessions.
- **Color panel** with RGB sliders and a hex field for the foreground and background colors.
- **Dockable panels** in a new *Window* menu: Color, Swatches, Align, History and Actions, plus Layers and *Reset Panels*. Visibility is saved between sessions.
- **Actions panel** that records a sequence of menu commands, saves it by name and replays it on the active document with one click.
- **History panel** listing the document's undo steps.
- **Canvas context menu**: right-click with a selection for cut/copy/fill/delete/hue-saturation, or without one for layer commands.
- **Broader file support** via Windows Imaging Component: WebP, AVIF, GIF, BMP, ICO and JPEG XR, alongside JPEG, PNG, TIFF and HEIC/HEIF (where the Windows codec is installed).
- **SVG import**, rasterized at its declared size or `viewBox` with transparency preserved.
- **Open Image** opens each selected file in its own tab. *Import Images* still places files as layers in the current project.
- **Background-removal model manager**: download, update, select and remove BiRefNet Lite (~115 MB) or full BiRefNet (~490 MB) inside the app.
- **GPU selection for Remove Background**: DirectML runs on the adapter with the most dedicated memory, tries the other adapters, then falls back to the CPU. Models load on first use and unload when idle to free memory.
- **Zoom controls in the options bar** (Fit, 100%, Zoom In, Zoom Out) and a zoom cursor that shows whether a click zooms in or out.
- **Rotate from a corner handle** during Transform, kept in sync with the numeric Rotate field.
- New Windows app icon set and a matching title-bar icon.
- Command-line tool (`tools/Compositor.Cli`) to inspect `.comp` projects, export them to PNG/JPEG and verify a save round trip.
- xUnit test suite for project compatibility, rendering and editor-session behavior.

### Changed — adapted for Windows

- Keyboard shortcuts use Windows conventions: `Ctrl` instead of `⌘`, `Alt` instead of `⌥` (for example, Alt-click sets the Clone Stamp source), `Ctrl+Y` as an alias for Redo, and numpad aliases for zoom.
- Menus follow Windows order (File, Edit, Select, Image, Filter, Layer, View, Window, Help), with Exit (`Alt+F4`) on the File menu.
- Custom title bar with native caption buttons, the menu bar and document tabs sharing one row.
- Tab close buttons moved to the right side of each tab. When tabs no longer fit, an overflow menu lists the hidden ones.
- Untitled documents are numbered per session and no longer inherit counters from earlier sessions.
- Windows file pickers, drag-and-drop, clipboard formats, per-monitor DPI awareness and long-path support replace their macOS counterparts.
- Tool options live in a contextual options bar. Long transform controls scroll horizontally so the zoom controls stay visible.
- Magic Wand selections have smoother edges after mask post-processing.
- The Change Color dialog was redesigned so its preview and mode controls don't clip at common window sizes.
- A dark visual system built from WinUI theme resources and Lucide/Fluent icons.

### Fixed during the port

- The Zoom tool can be selected reliably with `Z`.
- Transform rotation gives the same result whether it's typed in or dragged.
- Tab controls stay usable when the title bar runs out of horizontal space.
- Transparent pixels are preserved when recoloring layers and importing SVGs.

### Kept from Compositor

The editing model and file format are unchanged, so projects open in both apps: layers and folders, blend modes, layer/folder/clipping masks, adjustment layers, non-destructive transforms and free distort, marquee/lasso/Magic Wand selections, Brush/Eraser, Spot Healing, Clone Stamp, Smear, Gradient and Shape tools, Levels, Curves, Hue/Saturation, Exposure, Gradient Map, Grain, Gaussian and Motion Blur, Add Noise, Lens Correction, Content-Aware Fill, Crop, Canvas Size, Image Size, JPEG export with preview, and `.comp` projects.

[Unreleased]: https://github.com/binodray/Layerform/compare/v1.2.0...HEAD
[1.2.0]: https://github.com/binodray/Layerform/compare/v1.1.0...v1.2.0
[1.1.0]: https://github.com/binodray/Layerform/releases/tag/v1.1.0
