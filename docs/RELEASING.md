# Releasing Layer Form

A release is an installer on GitHub Releases plus an updated `site/update.json`. Installed copies of Layer Form read that feed (and the latest GitHub release) shortly after launch and offer the update to the user.

## How updates reach users

```text
Layer Form (installed)
   │  on launch, and from Help › Check for Updates…
   ├─► https://layerform.hastamev.com/update.json      ← site/update.json, uploaded to cPanel
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

   This publishes a self-contained Release build, compiles `artifacts\installer\LayerForm-Setup-x.y.z.exe` with [Inno Setup 6](https://jrsoftware.org/isdl.php), and rewrites `site\update.json` with the version, download URL, SHA-256 and release notes.
4. **Smoke-test** the installer on a clean account or VM: install, launch, open and save a project, uninstall.
5. **Commit and tag:**

   ```powershell
   git commit -am "Release x.y.z"
   git tag vx.y.z
   git push origin main vx.y.z
   ```

6. **Publish the GitHub release** for tag `vx.y.z`, paste the changelog section as the description, and attach `LayerForm-Setup-x.y.z.exe`. The file name must contain `Setup` and end in `.exe`.
7. **Update the website.** In cPanel › File Manager, open the `layerform.hastamev.com` folder and upload the new `site/update.json`, replacing the old one. (If the page itself changed, upload `artifacts/layerform-site.zip` and extract it instead.)

Do step 7 **after** step 6: `update.json` points at the release asset, so it must never go live before the installer exists.

## Website

`site/` is the download page for [layerform.hastamev.com](https://layerform.hastamev.com), hosted on the hastamev.com GoDaddy cPanel account. Its download button and "What's new" section read `update.json`, so they update with each release automatically. `site/.htaccess` forces HTTPS and stops `update.json` from being cached.

One-time setup in cPanel:

1. **Domains › Create a New Domain** (older cPanel: **Subdomains**): enter `layerform.hastamev.com`, keep the suggested document root (for example `public_html/layerform.hastamev.com`) and uncheck "Share document root".
2. **File Manager:** open that document root, **Upload** `artifacts/layerform-site.zip`, then right-click it › **Extract**, and delete the zip. Turn on *Settings › Show Hidden Files* to confirm `.htaccess` is there.
3. **SSL/TLS Status:** run **AutoSSL** so `https://layerform.hastamev.com` gets a certificate (can take a few minutes).

When hastamev.com's DNS is managed by GoDaddy on the same account, the subdomain's DNS record is created automatically. If `layerform.hastamev.com` doesn't resolve after an hour, add an `A` record named `layerform` pointing to the hosting IP shown in cPanel (currently the same address as `hastamev.com`).

## Code signing

The installer is not signed yet, so SmartScreen warns on first run until the file builds reputation. When a code-signing certificate is available, sign both the published `LayerForm.exe` and the installer (Inno Setup's `SignTool` directive), then keep signing every release with the same certificate.
