# Farmora Tray — On-site Install & Setup Guide

Audience: developers who visit the customer site to train staff and set up cashier / warehouse PCs.

Farmora Tray is a small Windows program that runs in the background on each client PC. The Farmora website talks to it on `http://127.0.0.1:9123` to print invoices (dot matrix PDF and thermal ESC/POS).

---

## 1. Before you go on site

### Bring / prepare

- [ ] Latest installers from [GitHub Releases](https://github.com/nano-devs/farmora-tray/releases/latest): the `win-x64` and `win-x86` `Setup.exe` files
- [ ] USB stick or access to a network share
- [ ] Farmora frontend URL for that site (production / staging)
- [ ] Admin login for Farmora (to open printer settings after FE is ready)
- [ ] This checklist (printed or on phone)
- [ ] Sample test PDF (optional) and knowing which PC has which printers
- [ ] **Adobe Acrobat Reader** offline installer (64-bit and 32-bit) on the USB stick, in case the PC has no PDF app or the shop internet is slow

### Get the installers (before travel)

Download both `Setup.exe` files from the latest release: <https://github.com/nano-devs/farmora-tray/releases/latest>. One is for `win-x64`, the other for `win-x86`. Each is self-contained; the PC does **not** need .NET installed.

Releases are built by GitHub Actions when a `v*` tag is pushed (see the README). Do not hand-build installers for customer PCs: a hand-built copy does not match the release history and may not update.

### PC and operating system requirements

| Requirement | Notes |
|-------------|--------|
| OS | Windows 10 or 11, **64-bit or 32-bit**. Not Windows 7. |
| Arch | x64 or x86. Match the installer to **System type**. This build does not support ARM. |
| .NET | Not required on the PC when you use the self-contained publish above. |
| Printers | Installed in Windows with working drivers. Print a Windows test page first. |
| PDF app | **Required for dot matrix printing.** Install **Adobe Acrobat Reader** (free) and set it as the default app for `.pdf`. Microsoft Edge alone is not enough. See [2.0](#20-install-a-pdf-app-that-can-print-silently). Thermal-only PCs do not need it. |
| User | The cashier Windows account that will use Farmora in the browser. The tray runs in that user session. |
| Browser | Chrome / Edge. Use the same browser staff use daily. |
| Network | The PC can open the Farmora web app. The tray serves only localhost, and checks `github.com` / `api.github.com` for updates every few hours. |
| Disk and memory | Light. About 200 MB free under `%LocalAppData%` for the app, one cached update and config. |
| Auto-start | Added by `Setup.exe` to that user's startup apps (section 3). Not a Windows Service. |

Typical printers per PC:

- **Dot matrix** — B2B invoice / returns / PO forms  
- **Thermal** — retail receipt (only on retail cashier PCs; warehouse may only need matrix)

---

## 2. Install Farmora Tray on the PC

Do this **once per client PC**.

### 2.0 Install a PDF app that can print silently

Farmora Tray does not render PDFs itself. For `/dotmatrix` it saves the PDF to a temp file and asks Windows to print it with the **printto** shell verb. Whatever app is the default for `.pdf` opens the file and does the printing. That app must support `printto`.

| PDF app | Works? |
|---------|--------|
| **Adobe Acrobat Reader** / Acrobat | Yes. **Recommended** (tested on site). |
| Microsoft Edge (Windows default) | Usually **no**. Edge does not register `printto` for PDFs, so the print fails with `503`. |
| Browsers (Chrome, Firefox) as default PDF app | No. |

Steps:

1. Install **Adobe Acrobat Reader** (from the USB stick or get.adobe.com/reader). Untick the optional extras (McAfee and similar) in the installer.
2. Open Acrobat Reader once. Accept the license and dismiss any first-run prompts, so it does not pop up a dialog during a print.
3. Make it the default: **Settings → Apps → Default apps** → search `.pdf` → choose **Adobe Acrobat**. Do this while signed in as the **cashier** Windows account (defaults are per user).
4. Check that `printto` is registered for that user:

   ```powershell
   $progId = (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.pdf\UserChoice').ProgId
   $progId
   Test-Path "Registry::HKEY_CLASSES_ROOT\$progId\shell\printto"
   ```

   Expect a ProgId such as `Acrobat.Document.DC` and `True`. `False` means dot matrix printing will fail. Fix the default app first.

Acrobat Reader may leave a small Reader window open after a print. That is normal. Cashiers can minimize it, but should not set Acrobat to block printing or ask for confirmation.

### 2.1 Run the installer

1. On the PC, open **Settings → System → About** and read **System type**.
2. Use the `win-x64` `Setup.exe` on 64-bit Windows and the `win-x86` one on 32-bit Windows. Do not install the x86 build on 64-bit Windows. Each build only updates to the same architecture.
3. Sign in as the **cashier** Windows account and double-click `Setup.exe`. No admin rights are needed. It installs for that user only, into:

   ```text
   %LocalAppData%\FarmoraTrayApp\current\FarmoraTray.exe
   ```

   and adds **Farmora Tray** to the Start menu. Farmora Tray starts as soon as setup finishes.

4. If SmartScreen says "Windows protected your PC", choose **More info → Run anyway**. The installer is not code-signed yet.
5. Do **not** move or copy files out of the install folder. Updates replace `current\`. Config lives elsewhere (`%LocalAppData%\FarmoraTray\config.json`), so reinstalling or uninstalling keeps the API key and printer map.

**PC that already has the old zip install** (`C:\Program Files\FarmoraTray` or `C:\FarmoraTray`): right-click the tray icon → **Exit**, delete the old scheduled task or Startup-folder shortcut and the old folder, then run `Setup.exe` as above. Setup adds the new startup entry itself (section 3). The API key and printers carry over.

### 2.2 First run (get API key)

1. Setup starts Farmora Tray for you. Later, start it from the Start menu (**Farmora Tray**).
2. No console window opens. Look for the Farmora Tray icon in the system tray. Windows may hide it under **Show hidden icons**.
3. On **first run**, a balloon tip says that an API key was created.
4. Right-click the icon and choose **Copy API key**.
5. Paste the key into a note for this PC.
6. Leave Farmora Tray running. Do not choose **Exit**.

If you missed the balloon:

1. Right-click the tray icon and choose **Copy API key**, or
2. Right-click the tray icon and choose **Open config folder**, then copy the `"apiKey"` value from `config.json`.

Config path is per Windows user. Prefer setting up under the **same Windows account** the cashier uses.

### 2.3 Verify tray is listening

On that PC, open PowerShell:

```powershell
Invoke-RestMethod http://127.0.0.1:9123/health
```

Expected:

```json
{ "status": "ok", "version": "..." }
```

If this fails: tray not running, or wrong port in `config.json`.

---

## 3. Start Farmora Tray at Windows logon

Cashiers must not have to start it manually every morning.

`Setup.exe` does this for you. It adds **Farmora Tray** to the cashier's startup apps (per-user `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` entry pointing at `%LocalAppData%\FarmoraTrayApp\current\FarmoraTray.exe`). That path stays the same after updates. Uninstalling removes the entry.

Do **not** install this tray as a Windows Service. A service runs in session 0 and cannot show a tray icon.

### Check it

1. Right-click the tray icon: **Start with Windows** is ticked.
2. Task Manager → **Startup apps**: **FarmoraTray** shows **Enabled**.
3. Sign out and back in, then:

   ```powershell
   Invoke-RestMethod http://127.0.0.1:9123/health
   ```

### Turning it off or on

- Tray menu → **Start with Windows** toggles it. Turning it off there is remembered across updates.
- Disabling it in Task Manager → Startup apps also works. Ticking **Start with Windows** in the tray menu turns it back on.

### PCs set up before auto-start existed

The first update to this version adds the startup entry. Then delete the old **FarmoraTray** task in Task Scheduler and any Farmora Tray shortcut in `shell:startup`. Leftovers do no harm (a second copy started at logon exits quietly), but one startup method is easier to support.

---

## 4. Configure printers in Windows

Before Farmora mapping:

1. **Settings → Bluetooth & devices → Printers & scanners**
2. Confirm the correct printers appear (exact names matter).
3. Print a Windows **test page** on each physical printer.
4. Note the exact names, e.g.:
   - `EPSON LX-310 ESC/P`
   - `POS-80C`

If the name is wrong or the driver is “offline,” Farmora Tray cannot fix that — fix Windows first.

---

## 5. Configure Farmora web app (this PC)

Tray has no settings UI. Configuration is done from the **Farmora frontend** on this same PC (or via PowerShell below).

### 5.1 When frontend printer settings page exists

1. Open Farmora in Chrome/Edge **on this PC** (same origin staff will use).
2. Go to **Printer / Farmora Tray settings** (name may vary).
3. Paste **API key**.
4. Test connection (should show tray online + printer list).
5. Select:
   - **Dot matrix** printer → matrix device  
   - **Thermal** printer → thermal device (skip / leave empty if this PC has none)
6. Save (this also sets `allowedOrigin` to the current site URL).

### 5.2 If frontend settings page is not ready yet (manual PowerShell)

Run on the client PC (tray must be running). Replace the key and printer names.

```powershell
$key = "<paste-api-key>"
$headers = @{ "X-Farmora-Tray-Key" = $key }

# List Windows printers as seen by tray
Invoke-RestMethod http://127.0.0.1:9123/printers -Headers $headers

# Set origin + printer map (use the real Farmora URL origin)
$body = @{
  allowedOrigin = "https://app.farmora.example"   # e.g. https://farmora.customer.com
  printers = @{
    dotMatrix = "EPSON LX-310 ESC/P"              # exact name from list
    thermal   = "POS-80C"                         # or "" if none
  }
} | ConvertTo-Json

Invoke-RestMethod http://127.0.0.1:9123/config -Method Put -Headers $headers `
  -ContentType "application/json" -Body $body

# Confirm
Invoke-RestMethod http://127.0.0.1:9123/config -Headers $headers
```

`allowedOrigin` must match the browser origin exactly (scheme + host + port), e.g. `http://localhost:3000` vs `https://app.example.com`.

### 5.3 Desktop icon to open Farmora

Give the cashier one icon on the desktop that opens Farmora, so they never type the URL or open the wrong browser.

The shortcut must open the **same browser and profile** where you saved the API key in 5.1. The API key lives in that browser profile's local storage. A plain website shortcut (`.url`) opens the *default* browser, which may be a different browser or profile. If it is, Farmora shows the tray as not configured.

**Option A (recommended): shortcut to the browser with the Farmora URL**

Run in PowerShell as the cashier user. Set `$url` to the Farmora URL for this site, and set `$browser` to the browser used in 5.1.

```powershell
$url     = "https://app.farmora.example"   # same origin as allowedOrigin
# Edge:
$browser = "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe"
# Chrome (use instead if staff use Chrome):
# $browser = "$env:ProgramFiles\Google\Chrome\Application\chrome.exe"

$lnkPath  = Join-Path ([Environment]::GetFolderPath("Desktop")) "Farmora.lnk"
$shell    = New-Object -ComObject WScript.Shell
$lnk      = $shell.CreateShortcut($lnkPath)
$lnk.TargetPath   = $browser
$lnk.Arguments    = "--profile-directory=Default $url"
$lnk.IconLocation = "$env:LocalAppData\FarmoraTrayApp\current\FarmoraTray.exe,0"   # Farmora icon
$lnk.Description  = "Open Farmora"
$lnk.Save()
```

Notes:

- Check the browser path exists: `Test-Path $browser`. Chrome may be under `${env:ProgramFiles(x86)}` or `%LocalAppData%\Google\Chrome\Application` instead.
- `--profile-directory=Default` is the first browser profile. If staff use another profile, find its folder name at `edge://version` or `chrome://version` (**Profile path**, last part, e.g. `Profile 1`).
- To open Farmora in its own window without tabs or an address bar, add `--app=` before the URL: `"--profile-directory=Default --app=$url"`.

**Option B: install as an app from the browser**

- Edge: open Farmora → **… → Apps → Install this site as an app**, then pin or create a desktop shortcut when asked.
- Chrome: open Farmora → **⋮ → Cast, save, and share → Install page as app** (or **Create shortcut…** with **Open as window** ticked).

This uses the current profile automatically and adds a desktop icon.

After creating the icon: close the browser, double-click the icon, and confirm Farmora opens and shows the tray **online** (the API key is still there).

---

## 6. Smoke test (do not skip)

### 6.1 Tray health

```powershell
Invoke-RestMethod http://127.0.0.1:9123/health
```

### 6.2 Dot matrix path

From Farmora FE (preferred): create/reprint a B2B document and confirm paper prints.

Or manual (tray configured):

```powershell
# Example: send any small PDF file
$pdf = [System.IO.File]::ReadAllBytes("C:\Temp\test.pdf")
Invoke-WebRequest http://127.0.0.1:9123/dotmatrix -Method Post -Headers $headers `
  -ContentType "application/pdf" -Body $pdf
```

Expect HTTP **204** and output on the matrix printer. **204** means the spooler finished sending the job, not merely that it accepted the queue. An offline, paused, or unreachable printer returns **503** and the queued job is removed. Microsoft Print to PDF only returns **204** if the Save dialog is completed and the job finishes; leaving the dialog open fails the request.

### 6.3 Thermal path (retail PCs only)

From FE: complete a walk-in / B2C sale and confirm receipt.

Or send tiny raw bytes (may print garbage — only proves the path works):

```powershell
$bytes = [System.Text.Encoding]::ASCII.GetBytes("Farmora Tray OK`n`n`n")
Invoke-WebRequest http://127.0.0.1:9123/thermal -Method Post -Headers $headers `
  -ContentType "application/octet-stream" -Body $bytes
```

### 6.4 After reboot test

1. Restart the PC.
2. Log in as the cashier.
3. Wait ~15–30 seconds.
4. `Invoke-RestMethod http://127.0.0.1:9123/health`
5. Open Farmora → confirm tray still connected (key still in browser local storage for that profile).

---

## 7. Train the cashier (short script)

Explain only what they need:

1. Farmora Tray sits in the system tray. It starts at login. Do not choose **Exit** unless support asks you to. It updates itself; the icon may disappear for a few seconds while it restarts.
2. Open Farmora by double-clicking the **Farmora** icon on the desktop. Do not open it from another browser or by typing the URL in a different profile.
3. If an Adobe Acrobat Reader window stays open after printing, minimize it. Do not uninstall Acrobat or change the default PDF app.
4. If print fails:
   - Check printer power / paper  
   - Check Farmora shows tray online  
   - Call support / on-site contact — do not uninstall Farmora Tray
5. Reprint from the document screen if a print jammed (when FE supports it).

---

## 8. Per-PC handover checklist

Copy one row per PC:

| # | Item | PC name / location | Done |
|---|------|--------------------|------|
| 1 | Windows printers installed + test page OK | | [ ] |
| 2 | Acrobat Reader installed, default for `.pdf`, `printto` check = `True` (dot matrix PCs) | | [ ] |
| 3 | Farmora Tray installed with the matching `Setup.exe` (x64/x86); tray menu shows the latest version | | [ ] |
| 4 | API key saved into Farmora FE on this PC | | [ ] |
| 5 | `dotMatrix` / `thermal` mapped correctly | | [ ] |
| 6 | `allowedOrigin` = production Farmora URL | | [ ] |
| 7 | Tray menu shows **Start with Windows** ticked; FarmoraTray is Enabled in Task Manager → Startup apps | | [ ] |
| 8 | Farmora desktop icon opens the right browser profile, tray shows online | | [ ] |
| 9 | `/health` OK after reboot | | [ ] |
| 10 | Real document smoke print OK | | [ ] |
| 11 | Cashier shown the desktop icon and what not to close/delete | | [ ] |

---

## 9. Updating Farmora Tray later

Farmora Tray updates itself from GitHub Releases. Nobody needs to visit the PC.

1. About 2 minutes after start, then every 4 hours, it checks for a newer release on its channel (`win-x64` or `win-x86`).
2. A newer release is downloaded in the background (a small delta when possible).
3. It waits until no print is running **and** no print has finished in the last 5 minutes, so it does not restart in the middle of a sale.
4. It exits and the updater swaps in the new version and starts it again. The tray is offline for a few seconds.
5. `config.json` (API key, printers, origin) is untouched.

To update right away (e.g. on site), right-click the tray icon → **Check for updates**. It still waits for a running print to finish. The top line of the tray menu shows the installed version; `GET /health` returns it too.

If the PC cannot reach GitHub, the tray keeps working on its current version and tries again later. An update that was downloaded but not applied is applied on the next start.

The old zip install (copy of `FarmoraTray.exe` in a folder) does **not** update itself, and neither does `dotnet run`. **Check for updates** is greyed out there. Move those PCs to `Setup.exe` (2.1).

## 10. Troubleshooting

| Symptom | Likely cause | Fix |
|---------|--------------|-----|
| Tray running but no icon | Task set to run whether user is logged on (session 0) | Recreate the task with **Run only when user is logged on** |
| No tray icon | Hidden icons, or tray not running | Click **Show hidden icons**, or start `FarmoraTray.exe` / Run the scheduled task |
| Balloon about port / "already running" | Second instance, or port in use | Use the existing tray icon. If `/health` fails, End Task `FarmoraTray.exe` and start once |
| `/health` fails | Tray not running | Start exe or Run scheduled task |
| FE says tray offline | Tray down, or FE opened on another PC | Tray must run on **same** PC as the browser |
| `401` from tray | Wrong/missing API key in FE | Re-paste key from `config.json` |
| `403 Origin not allowed` | `allowedOrigin` mismatch | Save settings again from the real Farmora URL, or `PUT /config` with correct origin |
| `404` printer not configured | Mapping empty | Set `dotMatrix` / `thermal` in FE or PowerShell |
| `404` printer not found | Windows renamed/removed printer | Fix Windows printers, remap exact name |
| `503` print failed | Driver / offline / no PDF handler | Windows test page. For dot matrix, make sure Acrobat Reader is the default `.pdf` app and the `printto` check in 2.0 returns `True` |
| Dot matrix fails, thermal works | Default PDF app is Edge / a browser, or Windows reset the default after an update | Set Adobe Acrobat as default for `.pdf` again (2.0) |
| Desktop icon opens Farmora but tray "not configured" | Icon opens a different browser or profile than where the key was saved | Recreate the icon (5.3) with the right browser and `--profile-directory`, or paste the key again in that profile |
| Works then stops after reboot | Startup entry off, or installed under another Windows user | Tray menu → tick **Start with Windows**; check Task Manager → Startup apps. Install with `Setup.exe` while signed in as the cashier |
| Tray stays on an old version | No access to `github.com`, or old zip install | Right-click → **Check for updates**; a balloon says if the check failed. Allow `github.com`, `api.github.com` and `objects.githubusercontent.com` through the shop firewall. Zip installs: reinstall with `Setup.exe` (2.1) |
| Old startup task fails after moving to `Setup.exe` | Task still points to the old folder | Delete the task; `Setup.exe` adds its own startup entry (section 3) |
| Key lost after new Windows user | Config is per-user | Set up under the cashier account, or copy config carefully |

### Useful paths

| What | Path |
|------|------|
| Install | `%LocalAppData%\FarmoraTrayApp\` (per Windows user) |
| Config + API key | `%LocalAppData%\FarmoraTray\config.json` |
| Health | `http://127.0.0.1:9123/health` |

### Read config quickly

```powershell
Get-Content $env:LOCALAPPDATA\FarmoraTray\config.json
```

---

## 11. Security notes (tell yourself, not the cashier)

- Tray listens on **localhost only** — not reachable from other PCs on the LAN.
- API key is a **per-PC** secret stored in local config + browser local storage.
- Do not commit API keys to git or share one key across all shops unless intentional.
- Anyone with local access to the PC can read the key from `config.json`.

---

## 12. Related docs

- [FRONTEND.md](./FRONTEND.md) — how the web app should integrate  
- [BACKEND-PRINT.md](./BACKEND-PRINT.md) — PDF / ESC-POS APIs on farmora-backend  
- [../README.md](../README.md) — API reference  
