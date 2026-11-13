# Farmora Tray

Localhost print agent for Farmora cashier PCs. The Farmora frontend talks to this app over `http://127.0.0.1` to configure printers and silently print invoices. Cashiers run `FarmoraTray.exe` with no console window. A system tray icon shows that the agent is running.

**On-site install & setup (for field developers):** [docs/ON-SITE-SETUP.md](./docs/ON-SITE-SETUP.md)  
**Frontend integration spec:** [docs/FRONTEND.md](./docs/FRONTEND.md)  
**Backend print PDF / ESC-POS spec (for farmora-backend agents):** [docs/BACKEND-PRINT.md](./docs/BACKEND-PRINT.md)

- **Stack:** ASP.NET Core / .NET 10, Windows only
- **Default URL:** `http://127.0.0.1:9123`
- **Tray:** right-click the icon for status, **Copy API key**, **Open config folder**, and **Exit**. Printer mapping stays in the Farmora frontend (or `curl`) via config APIs.

## Run

```powershell
cd src/FarmoraTray
dotnet run
```

On first start, Farmora Tray writes `%LocalAppData%\FarmoraTray\config.json` and shows a tray balloon that an API key was created. Right-click the tray icon and choose **Copy API key**. Paste that key into the Farmora frontend printer settings for this PC.

Keep Farmora Tray running while cashiers use Farmora. On production PCs install it with the release `Setup.exe` and start it at user logon with Task Scheduler (see [docs/ON-SITE-SETUP.md](./docs/ON-SITE-SETUP.md)). Do not install it as a Windows Service: session 0 has no tray icon.

## Releases and self-update

Farmora Tray installs and updates itself with [Velopack](https://velopack.io) from this repo's [GitHub Releases](https://github.com/nano-devs/farmora-tray/releases).

**Publish a release:** push a version tag.

```powershell
git tag v1.1.0
git push origin v1.1.0
```

`.github/workflows/release.yml` then runs the tests, publishes self-contained `win-x64` and `win-x86` builds with that version, packs them with `vpk` (one channel per architecture, plus delta packages against the previous release) and uploads them to a GitHub release named after the tag. Each channel gets a `Setup.exe` for first installs, a portable zip, `.nupkg` update packages and a `releases.<channel>.json` feed.

**On the PC:**

- `Setup.exe` installs per user into `%LocalAppData%\FarmoraTrayApp\current\FarmoraTray.exe` without admin rights. Config stays in `%LocalAppData%\FarmoraTray`, so reinstall and uninstall keep the API key.
- About 2 minutes after start, then every 4 hours, the tray checks the latest non-prerelease GitHub release on its own channel. It downloads updates in the background.
- It restarts into the new version only when no print is running and none finished in the last 5 minutes. The tray is offline for a few seconds.
- Tray menu → **Check for updates** checks immediately and only waits for a running print. The menu's top line shows the installed version.
- Copies run with `dotnet run` or from a plain exe/zip are not Velopack installs and do not update.

Notes:

- Don't change the Velopack app id `FarmoraTrayApp` (`PACK_ID` in the workflow), or installed PCs stop receiving updates.
- Keep `VPK_VERSION` in the workflow equal to the `Velopack` package version in the csproj.
- Release tags must be increasing semver (`vMAJOR.MINOR.PATCH`). Pre-releases are ignored by installed trays.
- GitHub allows 60 unauthenticated API calls per hour per public IP. Even dozens of PCs behind one shop IP stay well under that at one check per 4 hours.
- The installer and exe are not code-signed, so SmartScreen warns on first install. Signing can be added with `vpk pack --signParams` or `--azureTrustedSignFile`.

### Local publish (testing only)

```powershell
cd src/FarmoraTray
dotnet publish -c Release -r win-x64 --self-contained true -o .\publish\win-x64
```

Release publish is a self-contained single `FarmoraTray.exe` per RID. A copy run this way works but does not self-update. To publish a folder of assemblies instead, use Debug or pass `/p:PublishSingleFile=false`.

## Auth & CORS

| Rule | Behavior |
|------|----------|
| Bind address | `127.0.0.1` only |
| API key | Required header `X-Farmora-Tray-Key` on all routes except `GET /health` |
| Origin | If `Origin` is sent and `allowedOrigin` is set, it must match. If `allowedOrigin` is empty, any origin is allowed (bootstrap) |

## Config file

`%LocalAppData%\FarmoraTray\config.json`

```json
{
  "apiKey": "...",
  "allowedOrigin": "https://app.farmora.example",
  "port": 9123,
  "printers": {
    "dotMatrix": "EPSON LX-310",
    "thermal": "POS-80"
  }
}
```

Changing `port` requires restarting Farmora Tray.

Legacy per-document printer keys (`saleOrderDotMatrix`, etc.) are migrated into `dotMatrix` / `thermal` on load.

## HTTP API

Base: `http://127.0.0.1:9123`

### Health

```http
GET /health
```

No API key. Response:

```json
{ "status": "ok", "version": "1.0.0.0" }
```

### List printers

```http
GET /printers
X-Farmora-Tray-Key: <key>
```

```json
{ "printers": ["EPSON LX-310", "Microsoft Print to PDF", "POS-80"] }
```

### Get / update config

```http
GET /config
X-Farmora-Tray-Key: <key>
```

```http
PUT /config
X-Farmora-Tray-Key: <key>
Content-Type: application/json
```

```json
{
  "allowedOrigin": "https://app.farmora.example",
  "printers": {
    "dotMatrix": "EPSON LX-310",
    "thermal": "POS-80"
  }
}
```

`GET /config` never returns the API key.

### Print

| Method | Path | Body | Config key |
|--------|------|------|------------|
| `POST` | `/dotmatrix` | PDF | `printers.dotMatrix` |
| `POST` | `/thermal` | ESC/POS raw | `printers.thermal` |

Use `/dotmatrix` for B2B SO, sales return, PO, purchase return (any form PDF).  
Use `/thermal` for retail/thermal receipts.

PDF endpoint accepts either:

- `Content-Type: application/pdf` with raw PDF bytes, or
- `Content-Type: application/json` with `{ "pdfBase64": "..." }`

Thermal accepts either:

- `Content-Type: application/octet-stream` with raw ESC/POS bytes, or
- `Content-Type: application/json` with `{ "rawBase64": "..." }`

Success: `204 No Content` only after the spooler finishes sending the job to the printer. A job that is only queued is not success.

Errors:

| Status | When |
|--------|------|
| `400` | Missing / invalid payload |
| `401` | Bad or missing API key |
| `403` | `Origin` not allowlisted |
| `404` | Printer not configured or not installed |
| `503` | Printer not ready (`title`: `Printer not ready`), including a USB printer that is unplugged or switched off, or the job stayed queued / failed and was removed from the spooler. Other spooler, driver, or PDF-handler failures use `title`: `Print failed`. |

## Example: configure from PowerShell

```powershell
$key = "<paste-from-first-run-log>"
$headers = @{ "X-Farmora-Tray-Key" = $key }

Invoke-RestMethod http://127.0.0.1:9123/printers -Headers $headers

$body = @{
  allowedOrigin = "http://localhost:3000"
  printers = @{
    dotMatrix = "Microsoft Print to PDF"
    thermal   = "POS-80"
  }
} | ConvertTo-Json

Invoke-RestMethod http://127.0.0.1:9123/config -Method Put -Headers $headers -ContentType "application/json" -Body $body
```

## Example: print thermal (raw)

```powershell
$bytes = [System.Text.Encoding]::ASCII.GetBytes("Hello from Farmora Tray`n")
Invoke-WebRequest http://127.0.0.1:9123/thermal `
  -Method Post `
  -Headers $headers `
  -ContentType "application/octet-stream" `
  -Body $bytes
```

## Example: frontend fetch

```js
const TRAY = "http://127.0.0.1:9123";
const key = localStorage.getItem("farmoraTrayKey");

async function printThermal(rawBytes) {
  const res = await fetch(`${TRAY}/thermal`, {
    method: "POST",
    headers: {
      "X-Farmora-Tray-Key": key,
      "Content-Type": "application/octet-stream",
    },
    body: rawBytes,
  });
  if (!res.ok) throw new Error(await res.text());
}

async function printDotMatrixPdf(pdfBytes) {
  const res = await fetch(`${TRAY}/dotmatrix`, {
    method: "POST",
    headers: {
      "X-Farmora-Tray-Key": key,
      "Content-Type": "application/pdf",
    },
    body: pdfBytes,
  });
  if (!res.ok) throw new Error(await res.text());
}
```

## Frontend integration notes

1. Detect tray: `GET /health` (no key).
2. Store API key per PC in `localStorage` (paste once from **Copy API key** / support sheet).
3. Settings page: `GET /printers` → two dropdowns (dot matrix / thermal) → `PUT /config` (include `allowedOrigin`).
4. After sale/purchase success: generate PDF or ESC/POS in Farmora backend/frontend, then `POST /dotmatrix` or `POST /thermal`.

Farmora Tray does **not** generate invoice layouts; it only routes ready-to-print payloads to the configured Windows printers.

PDF jobs use the Windows **printto** shell verb, so the default `.pdf` app on the PC must support it. Install **Adobe Acrobat Reader** and make it the default PDF app. Microsoft Edge usually does not register `printto` and dot matrix printing fails with `503`. Thermal jobs send raw ESC/POS bytes through the Windows spooler.

## Smoke test

1. `dotnet run` in `src/FarmoraTray` (or start `FarmoraTray.exe`)
2. Copy the API key from the tray menu (**Copy API key**)
3. `GET /printers` and `PUT /config` mapping `dotMatrix` / `thermal`
4. `POST /dotmatrix` with a small PDF — e.g. **Microsoft Print to PDF**
5. When a thermal printer is available, `POST /thermal` with ESC/POS bytes
