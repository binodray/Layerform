# Third-party notices

Layer Form is a Windows port of **Compositor** by Wonder Assembly LLC,
released under the MIT License (see `LICENSE`, which preserves the original
copyright notice, and the original repository at
https://github.com/robbietilton/Compositor).
Algorithms from the original Swift and C sources (brush, spot healing,
content-aware fill, levels, noise, lens correction, magic wand, grain, gradient
map, guided matte and others) were translated to C# for this port and remain
covered by the same license.

The Layer Form application icon is original artwork for this Windows project
and is distributed under the repository's MIT License.

The Windows build redistributes the following components:

| Component | License | Notes |
|---|---|---|
| SkiaSharp and native Skia (Mono/.NET Foundation, Google) | MIT (SkiaSharp), BSD-3-Clause (Skia) | CPU rendering, PNG/JPEG codecs |
| Windows App SDK / WinUI 3 (Microsoft) | Microsoft Software License Terms (redistributable) | Application framework, self-contained |
| Win2D (Microsoft.Graphics.Win2D) | MIT | Canvas presentation |
| .NET runtime (Microsoft) | MIT | Self-contained runtime |

Full license texts for these packages are available from their NuGet package
pages and repositories:
- https://github.com/mono/SkiaSharp/blob/main/LICENSE.md
- https://skia.org/about/ (BSD-3-Clause)
- https://github.com/microsoft/Win2D/blob/main/LICENSE.txt
- https://github.com/dotnet/runtime/blob/main/LICENSE.TXT
- https://www.nuget.org/packages/Microsoft.WindowsAppSDK (license terms)

## Fluent UI System Icons

Tool, panel and menu icons are from Microsoft's Fluent UI System Icons
(https://github.com/microsoft/fluentui-system-icons, @fluentui/svg-icons 1.1.341),
Copyright (c) 2020 Microsoft Corporation, used under the MIT License.

## Lucide Icons

Additional interface icons are derived from Lucide (`lucide-static` 1.47.0,
https://lucide.dev), Copyright Lucide Contributors, used under the ISC License.
