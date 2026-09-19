# The Windows port

Layer Form began with a practical question: could Compositor's focused, Photoshop-familiar editing workflow feel at home on Windows rather than merely run there? The result is a source-level port with a shared lineage, not a binary wrapper or an automated one-to-one translation.

The macOS project remains the foundation for the document model, editing vocabulary and many image-processing algorithms. The Windows application rebuilds the surrounding platform and adapts interactions where Windows users expect different behavior.

## Architecture

| Area | Compositor lineage | Layer Form implementation |
|---|---|---|
| Language/runtime | Swift and Apple frameworks | C# 12 and .NET 8 |
| Desktop UI | Native macOS application | WinUI 3 / Windows App SDK |
| Pixel core | Core Graphics-oriented image model | SkiaSharp RGBA-premultiplied raster core |
| Canvas presentation | Apple graphics stack | Win2D canvas with high-DPI input mapping |
| ML background removal | macOS model path | ONNX Runtime with DirectML adapters and CPU fallback |
| Files and clipboard | macOS pickers/pasteboard | Windows dialogs, WIC codecs, drag-and-drop and clipboard APIs |
| Distribution | Signed, notarized DMG | Self-contained app in a per-user Inno Setup installer, with in-app updates |

The solution is intentionally split into `Compositor.Core` and `Compositor.App`. Core owns documents, history, project serialization, editing operations, masks, filters and rendering. The app project owns WinUI controls and Windows-only services. This keeps core behavior testable without constructing a desktop window.

## Editing behavior carried forward

- Layer and folder hierarchy, masks, clipping masks, blend modes and opacity
- Non-destructive layer transforms and multi-layer operations
- Marquee, lasso and Magic Wand selections
- Brush, healing, clone, blur/smudge, gradient, shape and eyedropper tools
- Adjustment layers and live filter previews
- Content-Aware Fill and subject/background separation
- Canvas and image sizing, cropping, high-quality zoom rendering and pixel grid
- `.comp` manifest and image-package compatibility
- Photoshop-style shortcuts and familiar compositing workflow

## Windows-specific work

### Application shell

- Custom WinUI title bar with native caption buttons, menus, app icon and document tabs
- Tab overflow menu, correct per-session untitled numbering and Windows-style close-button placement
- Native file dialogs, recent Windows paths, drag-and-drop and clipboard support
- Per-monitor DPI awareness and long-path support
- Contextual tool options, dockable/floating panels and a Windows-native dark visual system

### Features that go beyond Compositor

- Align and Distribute for one or more layers, from the Layer menu or a dockable Align panel
- Change Color, Gradient Overlay and Add Shadow layer effects
- Line, Triangle, Polygon and Star shapes alongside Rectangle and Ellipse
- A Swatches panel with a preset palette and saved custom swatches
- A Window menu of dockable panels — Color, Swatches, Align, History and Actions — whose visibility is remembered
- Actions: record menu commands, save them by name and replay them on the active document
- A right-click canvas menu with selection or layer commands depending on context
- Help menu links to pre-filled GitHub bug and feature forms, and an About dialog

### Canvas and input

- Pointer, wheel and keyboard routing for WinUI and Win2D
- `Z` zoom-tool selection, visible zoom cursor, click zoom and horizontal scrub zoom
- Space-to-pan behavior and high-DPI document/view coordinate conversion
- Transform handles for resize, free distort and rotation, linked to exact numeric fields
- Zoom controls moved into the options bar to preserve title-bar space
- Smoothed Magic Wand selection boundaries and pixel-grid rendering at high zoom

### Files and projects

- Open Image creates a new document tab for each selected file
- Import Images places assets into the active project
- Windows Imaging Component formats: JPEG, PNG, TIFF, BMP, GIF, ICO, WebP, AVIF, JPEG XR and HEIC/HEIF when the corresponding Windows codec is installed
- SVG import rendered at declared size or `viewBox`, with transparency preserved
- PNG/JPEG export and safe project replacement/close confirmation
- Project path validation and compatibility tests for existing `.comp` packages

### Background removal

- Optional BiRefNet Lite and full-quality BiRefNet ONNX models downloaded from their published Hugging Face repositories
- In-app install, update, selection and removal of models
- DirectML execution on the hardware adapter with the most dedicated memory
- Automatic fallback through other adapters and finally the CPU
- Lazy loading and idle unloading to return GPU/CPU memory

### Quality and maintainability

- Platform-neutral xUnit suite covering project compatibility, editor sessions and workspace behavior
- Deterministic SkiaSharp rendering and image-codec helpers
- GitHub Actions build/test workflow for Windows
- Portable VS Code build, launch and test tasks

## Intentional differences and current limitations

- Layer Form follows Windows keyboard, dialog and window-management conventions where they differ from macOS.
- Codec availability can vary with installed Windows extensions.
- Remove Background is optional because its models are large and downloaded on demand.
- The installer is not code-signed yet, so SmartScreen may warn on first run.
- The Windows UI will continue to diverge where a native Windows interaction is clearer, while preserving project compatibility and the recognizable Compositor workflow.

## Attribution

Compositor is Copyright (c) 2026 Wonder Assembly LLC and is licensed under the MIT License. Layer Form preserves that notice and documents translated algorithms and project lineage in `THIRD-PARTY-NOTICES.md`. Windows-specific code and artwork are maintained by Layer Form contributors under the same repository license.

