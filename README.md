<div align="center">
  <img src="src/Compositor.App/Assets/app-icon-128.png" width="96" height="96" alt="Layer Form icon">
  <h1>Layer Form</h1>
  <p><strong>A native, open-source image editor for Windows.</strong></p>
  <p>Layers, masks, non-destructive transforms, selections, painting, retouching and compositing—without a subscription.</p>

  [![Windows](https://img.shields.io/badge/Windows-10%201809%2B-0078D4?logo=windows)](https://github.com/binodray/Layerform)
  [![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
  [![CI](https://github.com/binodray/Layerform/actions/workflows/ci.yml/badge.svg)](https://github.com/binodray/Layerform/actions/workflows/ci.yml)
  [![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
</div>

## Download

**[Download Layer Form for Windows](https://layerform.hastamev.com)** — or grab `LayerForm-Setup-<version>.exe` from [GitHub Releases](https://github.com/binodray/Layerform/releases/latest).

The installer sets Layer Form up for your user account, with no administrator prompt. Layer Form then keeps itself current: when a new version is released it shows what's new and offers to download and install it, skip that version, or remind you later.

> [!NOTE]
> The installer isn't code-signed yet, so Windows SmartScreen may show "Windows protected your PC" the first time. Choose **More info › Run anyway**.

## Why Layer Form?

Layer Form brings the editing model of [Compositor](https://github.com/robbietilton/Compositor) to Windows with a native Windows interface and a new platform implementation. It is designed for familiar, direct image-editing work: arrange layers, isolate subjects, retouch pixels, make color adjustments and export a finished composite.

This is not a wrapped Mac application. The Windows port rebuilds the application shell, canvas, input system, file dialogs, clipboard integration, rendering bridge and background-removal runtime in C# on .NET 8, WinUI 3, Win2D, SkiaSharp and DirectML. The editing behavior and project format retain their Compositor lineage, while the Windows experience has continued to evolve independently.

## Highlights

### Layers and compositing

- Layers and folders with opacity and blend modes
- Layer, folder and clipping masks
- Adjustment layers for Hue/Saturation, Levels, Curves, Exposure, Gradient Map and Grain
- Reorder, duplicate, rename and group layers
- Change Color, Gradient Overlay and Shadow layer effects
- Non-destructive move, resize, rotate, flip and free-distort transforms

### Alignment, color and swatches

- **Align** left, right, top, bottom or center edges of one or more layers, and **distribute** them evenly — from the Layer menu or the dockable Align panel
- **Swatches** panel with a ready-made palette (grays, hues and skin tones) plus your own saved swatches, kept between sessions
- **Color** panel with RGB sliders and a hex field for the foreground and background colors

### Shapes

- Rectangle, Ellipse, Line, Triangle, Polygon and Star tools, cycled with `U` / `Shift+U`

### Selection, painting and retouching

- Rectangle and ellipse marquees, freehand and polygonal lasso, and Magic Wand
- Add, subtract, invert, expand and contract selection workflows
- Brush and eraser with opacity, hardness and straight-line support
- Spot Healing, Clone Stamp, Blur, Smudge/Liquify, Gradient and Shape tools
- Content-Aware Fill and background removal

### Adjustments and filters

- Levels, Curves, Hue/Saturation, Exposure, Gradient Map, Grain and Invert
- Gaussian Blur, Motion Blur, Add Noise and Lens Correction
- Downloadable BiRefNet background-removal models
- DirectML GPU selection with automatic CPU fallback

### Windows workflow

- Native WinUI 3 window, menus, controls and Windows file pickers
- Dockable Color, Swatches, Align, History and Actions panels (Window menu)
- **Report a Bug** and **Request a Feature** in the Help menu open a pre-filled GitHub issue, so feedback reaches the project in one click
- Record a sequence of menu commands as an Action and replay it with one click
- Right-click canvas menu for selection and layer commands
- Multiple documents in tabs with overflow navigation
- Photoshop-style shortcuts, zoom cursor and transform controls
- High-DPI canvas, smooth zooming, fit/actual-pixel controls and pixel grid
- Open each image in its own tab or import images as layers
- PNG, JPEG, TIFF, BMP, GIF, ICO, WebP, AVIF, JPEG XR, HEIC/HEIF where Windows codecs are available, plus SVG rasterization
- `.comp` project compatibility, autosafe replacement checks and undo/redo history
- PNG and JPEG export and Windows clipboard integration

For the detailed engineering and behavior changes from the macOS project, see [The Windows port](docs/WINDOWS_PORT.md). For a feature-by-feature comparison with Compositor, see the [changelog](CHANGELOG.md).

## Feedback and support

Everything is handled through GitHub, and the app links straight to it:

- **Help › Report a Bug…** opens a [bug report](https://github.com/binodray/Layerform/issues/new?template=bug_report.yml) with your Layer Form version and Windows build already filled in.
- **Help › Request a Feature…** opens a [feature request](https://github.com/binodray/Layerform/issues/new?template=feature_request.yml).

Security issues should be reported privately; see [SECURITY.md](SECURITY.md).

## Requirements

- Windows 10 version 1809 or newer; Windows 11 is recommended
- .NET 8 SDK (`8.0.4xx` or newer in the .NET 8 feature band)
- Visual Studio Code with the recommended C# extension, or Visual Studio 2022
- x64 for the current development workflow; the project also declares ARM64 support

Some formats such as HEIC/HEIF depend on codecs installed in Windows. Remove Background downloads an optional BiRefNet model on first use (approximately 115 MB or 490 MB).

## Build and run

Clone the repository:

```powershell
git clone https://github.com/binodray/Layerform.git
cd Layerform
dotnet restore LayerForm.sln
```

### Visual Studio Code

Open the folder and press `Ctrl+Shift+B`. The default task builds the current source and launches the unpackaged application. Press `F5` to run with the debugger.

### Terminal

```powershell
dotnet build src/Compositor.App/Compositor.App.csproj -c Debug -p:Platform=x64
& ".\src\Compositor.App\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\LayerForm.exe"
```

You do not need to build an installer to test the application. To build one, see [Releasing](docs/RELEASING.md).

## Tests

```powershell
dotnet test tests/Compositor.Core.Tests/Compositor.Core.Tests.csproj -c Debug
```

The test suite covers project compatibility, rendering and editor-session behavior. Windows builds and tests also run in GitHub Actions.

## Project layout

```text
src/Compositor.App          WinUI 3 application and Windows integrations
src/Compositor.Core         Platform-neutral document, editing and rendering core
tests/Compositor.Core.Tests Compatibility and behavior tests
tools/Compositor.Cli        Project inspection/export command-line utility
docs                        Porting and development notes
```

## Contributing

Bug reports, feature proposals, documentation improvements and code contributions are welcome. Please read [CONTRIBUTING.md](CONTRIBUTING.md) before opening a pull request. Use the structured GitHub issue forms so reports include enough information to reproduce and evaluate them.

## Lineage and attribution

Layer Form is based on **Compositor** by Wonder Assembly LLC / Robbie Tilton and retains substantial translated concepts and algorithms from that project. Compositor is distributed under the MIT License. Layer Form preserves the original copyright notice, clearly documents the port, and adds its own Windows-specific implementation and changes.

Layer Form is an independent community project and is not an official Wonder Assembly product. See [LICENSE](LICENSE) and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for the complete notices.

## License

MIT. See [LICENSE](LICENSE).

