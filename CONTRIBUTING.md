# Contributing to Layer Form

Thank you for helping improve Layer Form. Contributions are welcome whether they are code, tests, documentation, design feedback or careful bug reports.

## Before you start

- Search existing issues before opening a new one.
- Use the bug or feature issue form and include concrete reproduction details.
- For a large change, open an issue first so the design can be discussed before substantial work begins.
- Keep pull requests focused. Unrelated cleanup makes review and regression testing harder.

## Development setup

You need Windows 10 1809 or newer and the .NET 8 SDK.

```powershell
git clone https://github.com/binodray/Layerform.git
cd Layerform
dotnet restore LayerForm.sln
dotnet build src/Compositor.App/Compositor.App.csproj -c Debug -p:Platform=x64
dotnet test tests/Compositor.Core.Tests/Compositor.Core.Tests.csproj -c Debug
```

In Visual Studio Code, `Ctrl+Shift+B` builds and launches the current source. `F5` runs the debugger configuration.

## Code organization

- Keep platform-independent document, history, editing and rendering behavior in `Compositor.Core`.
- Keep WinUI controls, Windows dialogs, clipboard, file codecs and DirectML integration in `Compositor.App`.
- Add or update tests for editor behavior, project serialization and rendering changes.
- Preserve `.comp` compatibility unless a format change has been discussed and versioned.

## Style

- Follow the repository `.editorconfig` and existing C# conventions.
- Prefer small methods with explicit names over clever abstractions.
- Keep UI text concise and user-facing; diagnostics should contain enough context to investigate a failure.
- Comments should explain constraints or intent, not restate the code.
- Do not commit generated `bin`, `obj`, SDK, model, installer or local application-state files.

## Attribution and translated work

Layer Form is a derivative Windows port of Compositor. If a change translates or closely follows upstream code, name the upstream file or behavior in the pull-request description. Preserve copyright and third-party notices, and add a notice when introducing a new dependency or icon set.

## Pull requests

A ready pull request should:

1. Explain the problem and the chosen approach.
2. Describe visible behavior changes and include screenshots when useful.
3. Pass `dotnet test` and an x64 Debug build.
4. Update the README, changelog or porting notes when behavior or support changes.
5. Avoid drive-by formatting of unrelated files.

By contributing, you agree that your contribution is licensed under the repository's MIT License.

