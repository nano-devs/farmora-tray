# Farmora Tray

Headless localhost print agent for Farmora cashier PCs. The Farmora frontend talks to this app over `http://127.0.0.1` to configure printers and silently print invoices.

**On-site install & setup (for field developers):** [docs/ON-SITE-SETUP.md](./docs/ON-SITE-SETUP.md)  
**Frontend integration spec:** [docs/FRONTEND.md](./docs/FRONTEND.md)  
**Backend print PDF / ESC-POS spec (for farmora-backend agents):** [docs/BACKEND-PRINT.md](./docs/BACKEND-PRINT.md)

- **Stack:** ASP.NET Core / .NET 10, Windows only
- **Default URL:** `http://127.0.0.1:9123`
- **No UI:** setup is done from the Farmora frontend (or `curl`) via config APIs

## Run

```powershell
cd src/FarmoraTray
dotnet run
```

On first start, Farmora Tray writes `%LocalAppData%\FarmoraTray\config.json` and logs a new API key. Copy that key into the Farmora frontend printer settings for this PC.

Example log line:

```text
Farmora Tray API key generated. Copy this key into the Farmora frontend printer settings...
```

Keep the process running while cashiers use Farmora. For production PCs, start it at login (Task Scheduler / Startup folder). A Windows Service installer can come later.

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

Success: `204 No Content`

Errors:

| Status | When |
|--------|------|
| `400` | Missing / invalid payload |
| `401` | Bad or missing API key |
| `403` | `Origin` not allowlisted |
| `404` | Printer not configured or not installed |
| `503` | Spooler / driver print failure |

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
2. Store API key per PC in `localStorage` (paste once from first-run log / support sheet).
3. Settings page: `GET /printers` → two dropdowns (dot matrix / thermal) → `PUT /config` (include `allowedOrigin`).
4. After sale/purchase success: generate PDF or ESC/POS in Farmora backend/frontend, then `POST /dotmatrix` or `POST /thermal`.

Farmora Tray does **not** generate invoice layouts; it only routes ready-to-print payloads to the configured Windows printers.

PDF jobs use the Windows **printto** shell verb (Edge / Acrobat / whatever is registered for `.pdf`). Thermal jobs send raw ESC/POS bytes through the Windows spooler.

## Smoke test

1. `dotnet run` in `src/FarmoraTray`
2. Copy API key from the console
3. `GET /printers` and `PUT /config` mapping `dotMatrix` / `thermal`
4. `POST /dotmatrix` with a small PDF — e.g. **Microsoft Print to PDF**
5. When a thermal printer is available, `POST /thermal` with ESC/POS bytes
