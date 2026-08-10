# Farmora Tray — On-site Install & Setup Guide

Audience: developers who visit the customer site to train staff and set up cashier / warehouse PCs.

Farmora Tray is a small Windows program that runs in the background on each client PC. The Farmora website talks to it on `http://127.0.0.1:9123` to print invoices (dot matrix PDF and thermal ESC/POS).

---

## 1. Before you go on site

### Bring / prepare

- [ ] Latest **self-contained** build zip, e.g. `FarmoraTray-x.y.z-win-x64.zip`
- [ ] USB stick or access to a network share
- [ ] Farmora frontend URL for that site (production / staging)
- [ ] Admin login for Farmora (to open printer settings after FE is ready)
- [ ] This checklist (printed or on phone)
- [ ] Sample test PDF (optional) and knowing which PC has which printers

### Build the zip (on your laptop, before travel)

```powershell
cd <repo>\src\FarmoraTray
dotnet publish -c Release -r win-x64 --self-contained true -o .\publish\win-x64
Compress-Archive -Path .\publish\win-x64\* -DestinationPath .\publish\FarmoraTray-win-x64.zip -Force
```

Self-contained = PC does **not** need .NET installed.

### Confirm PC requirements

| Requirement | Notes |
|-------------|--------|
| OS | Windows 10/11 **64-bit** |
| Printers | Installed in Windows with working drivers (test a Windows test page) |
| User | Cashier Windows account that will use Farmora in the browser |
| Browser | Chrome / Edge (same one staff will use daily) |
| Network | PC can open Farmora web app; tray itself only uses localhost |

Typical printers per PC:

- **Dot matrix** — B2B invoice / returns / PO forms  
- **Thermal** — retail receipt (only on retail cashier PCs; warehouse may only need matrix)

---

## 2. Install Farmora Tray on the PC

Do this **once per client PC**.

### 2.1 Copy files

1. Create folder:

   ```text
   C:\Program Files\FarmoraTray
   ```

   If UAC blocks you, use:

   ```text
   C:\FarmoraTray
   ```

2. Unzip the publish build into that folder. You should see `FarmoraTray.exe` among other files.

3. Do **not** delete or hand-edit files inside the install folder later when updating — replace the whole folder contents with a new zip (config lives elsewhere; see below).

### 2.2 First run (get API key)

1. Double-click `FarmoraTray.exe`.
2. A console window opens. On **first run** it prints something like:

   ```text
   Farmora Tray API key generated. Copy this key into the Farmora frontend printer settings...
   <long hex key>
   Config file: C:\Users\<user>\AppData\Local\FarmoraTray\config.json
   ```

3. **Copy the API key** into a note for this PC (or leave the window open).
4. Leave the console running for now.

If you closed the window before copying the key:

1. Open:

   ```text
   %LocalAppData%\FarmoraTray\config.json
   ```

   (`Win+R` → paste that → Enter)

2. Copy the `"apiKey"` value from the JSON file.

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

### Recommended: Task Scheduler

1. Open **Task Scheduler** → **Create Task…** (not Basic Task).
2. **General**
   - Name: `FarmoraTray`
   - Select **Run only when user is logged on**
   - Configure for: Windows 10/11
3. **Triggers** → **New…**
   - Begin the task: **At log on**
   - User: the cashier Windows account
4. **Actions** → **New…**
   - Action: **Start a program**
   - Program/script: `C:\Program Files\FarmoraTray\FarmoraTray.exe`  
     (or `C:\FarmoraTray\FarmoraTray.exe`)
   - Start in (optional): same folder as the exe
5. **Conditions**
   - Uncheck **Start the task only if the computer is on AC power** (laptops)
6. **Settings**
   - Allow task to be run on demand
   - **If the task fails, restart every** `1 minute`, attempt `3` times
   - Uncheck **Stop the task if it runs longer than**
7. OK → enter Windows password if prompted.

### Quick check after creating the task

1. Sign out and sign back in (or run the task manually: Task Scheduler → FarmoraTray → Run).
2. Confirm:

   ```powershell
   Invoke-RestMethod http://127.0.0.1:9123/health
   ```

### Alternative (faster, less robust): Startup folder

1. `Win+R` → `shell:startup`
2. Create a shortcut to `FarmoraTray.exe`

Prefer Task Scheduler for production PCs.

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

Expect HTTP **204** and output on the matrix printer (or Save dialog if mapped to Microsoft Print to PDF).

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

1. **Do not close** the Farmora Tray black window if they see it (or explain it starts at login).
2. Always open Farmora in the **same browser profile** where you saved the API key.
3. If print fails:
   - Check printer power / paper  
   - Check Farmora shows tray online  
   - Call support / on-site contact — do not delete `C:\Program Files\FarmoraTray`
4. Reprint from the document screen if a print jammed (when FE supports it).

---

## 8. Per-PC handover checklist

Copy one row per PC:

| # | Item | PC name / location | Done |
|---|------|--------------------|------|
| 1 | Windows printers installed + test page OK | | [ ] |
| 2 | Farmora Tray files in fixed folder | | [ ] |
| 3 | API key saved into Farmora FE on this PC | | [ ] |
| 4 | `dotMatrix` / `thermal` mapped correctly | | [ ] |
| 5 | `allowedOrigin` = production Farmora URL | | [ ] |
| 6 | Task Scheduler logon task created | | [ ] |
| 7 | `/health` OK after reboot | | [ ] |
| 8 | Real document smoke print OK | | [ ] |
| 9 | Cashier shown what not to close/delete | | [ ] |

---

## 9. Updating Farmora Tray later

1. Ask cashier to finish current sale.
2. Stop tray: close console, or End Task `FarmoraTray.exe`, or stop the scheduled task.
3. Replace files under `C:\Program Files\FarmoraTray` with the new zip contents.
4. Start tray again (Run scheduled task or reboot).
5. **Do not** delete `%LocalAppData%\FarmoraTray\config.json` — API key and printer map stay there.
6. Re-check `/health` and one test print.

---

## 10. Troubleshooting

| Symptom | Likely cause | Fix |
|---------|--------------|-----|
| `/health` fails | Tray not running | Start exe or Run scheduled task |
| FE says tray offline | Tray down, or FE opened on another PC | Tray must run on **same** PC as the browser |
| `401` from tray | Wrong/missing API key in FE | Re-paste key from `config.json` |
| `403 Origin not allowed` | `allowedOrigin` mismatch | Save settings again from the real Farmora URL, or `PUT /config` with correct origin |
| `404` printer not configured | Mapping empty | Set `dotMatrix` / `thermal` in FE or PowerShell |
| `404` printer not found | Windows renamed/removed printer | Fix Windows printers, remap exact name |
| `503` print failed | Driver / offline / no PDF handler | Windows test page; for PDF ensure Edge/Acrobat can open PDFs |
| Works then stops after reboot | No startup task / task under wrong user | Recreate Task Scheduler trigger for cashier user |
| Key lost after new Windows user | Config is per-user | Set up under the cashier account, or copy config carefully |

### Useful paths

| What | Path |
|------|------|
| Install | `C:\Program Files\FarmoraTray\` |
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
