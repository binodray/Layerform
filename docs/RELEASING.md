# Releasing Layer Form

A release is an installer on GitHub Releases plus an updated `update.json` on the website. Installed copies of Layer Form read that feed (and the latest GitHub release) shortly after launch and offer the update to the user.

## How updates reach users

```text
Layer Form (installed)
   │  on launch, and from Help › Check for Updates…
   ├─► https://hastamev.com/layerform/update.json      ← HastamevWebsite public/layerform/update.json
   └─► api.github.com/repos/binodray/Layerform/releases/latest
          │
          ▼  newest version wins; ignored if the user skipped it
   "Update available"  →  Download and Install / Skip This Version / Later
          │
          ▼  installer downloaded to %TEMP%, SHA-256 verified
   Layer Form asks to save open projects, closes, runs
   LayerForm-Setup-x.y.z.exe /SILENT /UPDATE=1, then reopens
```

The installer is per-user (`%LOCALAPPDATA%\Programs\Layer Form`), so updates never need administrator rights. Its `AppId` in `installer/LayerForm.iss` must never change, or updates will install a second copy instead of replacing the first.

## Checklist

1. **Bump the version** in `Directory.Build.props` (`<Version>`), following [Semantic Versioning](https://semver.org/).
2. **Write the changelog.** Move the `[Unreleased]` entries in `CHANGELOG.md` into a new `## [x.y.z] — YYYY-MM-DD` section and add the link at the bottom. These notes are what users see in the update dialog and on the website.
3. **Build the installer:**

   ```powershell
   .\scripts\build-installer.ps1
   ```

   This publishes a self-contained Release build, compiles `artifacts\installer\LayerForm-Setup-x.y.z.exe` with [Inno Setup 6](https://jrsoftware.org/isdl.php), and writes `update.json` (to `artifacts\` and the Hastamev website project) with the version, download URL, SHA-256 and release notes.
4. **Smoke-test** the installer on a clean account or VM: install, launch, open and save a project, uninstall.
5. **Commit and tag:**

   ```powershell
   git commit -am "Release x.y.z"
   git tag vx.y.z
   git push origin main vx.y.z
   ```

6. **Publish the GitHub release** for tag `vx.y.z`, paste the changelog section as the description, and attach `LayerForm-Setup-x.y.z.exe`. The file name must contain `Setup` and end in `.exe`.
7. **Update the website.** The build script has already written the new feed to `HastamevWebsite/public/layerform/update.json`. In the website project run `npm run build` and upload `dist/` to hastamev.com as usual.

Do step 7 **after** step 6: `update.json` points at the release asset, so it must never go live before the installer exists.

## Website

The download page lives at **<https://hastamev.com/layerform>**, in the [Hastamev website](https://github.com/binodray/HastamevWebsite) project (`src/pages/LayerForm.jsx`, assets and `update.json` in `public/layerform/`). The Tools page links to it.

Its download button and "What's new" section read `/layerform/update.json` — the same file the app checks — so they follow each release automatically. The site's `.htaccess` marks that file as not cacheable.

A `layerform.hastamev.com` subdomain was tried first and dropped: GoDaddy only issues certificates for the main domain on this plan, and the app requires HTTPS. Anything that moves the page needs `Updater.WebsiteFeed` and `ProjectLinks.Website` updated in the app, which means a new release.

## Code signing

The installer is not signed yet, so SmartScreen warns on first run until the file builds reputation. When a code-signing certificate is available, sign both the published `LayerForm.exe` and the installer (Inno Setup's `SignTool` directive), then keep signing every release with the same certificate.
