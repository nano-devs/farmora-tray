# Farmora Tray — Frontend Integration Spec

Audience: Farmora web frontend engineers.  
Companion: local print agent at `http://127.0.0.1:9123` (Windows cashier PCs).

Farmora Tray is a **print agent only**. It does not know about sale orders, taxes, or layouts. The frontend (with Farmora backend) must:

1. Configure printers once per PC  
2. After a successful transaction, obtain printable bytes (PDF or ESC/POS)  
3. Send those bytes to the tray  

---

## 1. Architecture

```text
┌─────────────────────┐     HTTPS      ┌──────────────────┐
│  Farmora Frontend   │ ─────────────► │ Farmora Backend  │
│  (browser)          │ ◄─ PDF/ESC-POS │ (generate print) │
└─────────┬───────────┘                └──────────────────┘
          │ http://127.0.0.1:9123
          │ X-Farmora-Tray-Key
          ▼
┌─────────────────────┐
│  Farmora Tray       │ ──► Windows printers (dot matrix / thermal)
└─────────────────────┘
```

| Responsibility | Owner |
|----------------|--------|
| Business data, invoice numbers, stock | Farmora backend |
| Printable PDF / ESC/POS bytes | Farmora backend |
| Detect tray, settings UI, trigger print | Frontend |
| Printer mapping + silent print | Farmora Tray |

---

## 2. Base URL & auth

| Item | Value |
|------|--------|
| Base URL | `http://127.0.0.1:9123` (default port; read from `GET /config` → `port` if needed) |
| API key header | `X-Farmora-Tray-Key: <key>` |
| Key storage | **Per PC**, `localStorage` (or similar). Never sync the key to the server as a global secret for all cashiers unless you intentionally want that. |
| CORS / Origin | Browser sends `Origin`. Tray must have matching `allowedOrigin` (set via `PUT /config`). |

**Public (no API key)**

- `GET /health`

**Requires API key**

- Everything else

---

## 3. Features the frontend must implement

### 3.1 Tray connection status

**When:** App shell load, printer settings page, before auto-print.

```http
GET /health
```

Success `200`:

```json
{ "status": "ok", "version": "1.0.0.0" }
```

| UI state | Condition |
|----------|-----------|
| Tray online | `/health` OK |
| Tray offline | network error / timeout (suggest 1–2s) |
| Key missing | online but no key in local storage → prompt paste key |
| Key invalid | `/printers` or `/config` returns `401` → re-paste key |

Do **not** block the whole POS if tray is offline; show a banner and skip auto-print (with optional “Retry print”).

### 3.2 One-time setup (per PC)

1. Install / start Farmora Tray on the PC.  
2. Copy API key from tray console log (first run) into FE settings.  
3. Open **Printer settings** in Farmora FE.  
4. Load printers + current config.  
5. User selects:
   - Dot matrix printer  
   - Thermal printer  
6. Save: `PUT /config` including `allowedOrigin` = `window.location.origin`.

Suggested local storage keys:

```text
farmoraTray.apiKey
farmoraTray.baseUrl          // optional; default http://127.0.0.1:9123
```

### 3.3 Printer settings page

**Load**

```http
GET /printers
X-Farmora-Tray-Key: <key>
```

```json
{ "printers": ["EPSON LX-310", "POS-80", "Microsoft Print to PDF"] }
```

```http
GET /config
X-Farmora-Tray-Key: <key>
```

```json
{
  "allowedOrigin": "https://app.farmora.example",
  "port": 9123,
  "printers": {
    "dotMatrix": "EPSON LX-310",
    "thermal": "POS-80"
  }
}
```

Note: `GET /config` **never** returns the API key.

**Save**

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

Partial updates are supported: omit fields you do not change. Sending `""` for a printer name clears that mapping.

**UI checklist**

- [ ] Input for API key (password-style + show/hide)  
- [ ] “Test connection” → `/health` then `/printers`  
- [ ] Dropdown: Dot matrix printer (from `/printers`)  
- [ ] Dropdown: Thermal printer  
- [ ] Save → `PUT /config` with `allowedOrigin: window.location.origin`  
- [ ] Optional: “Test print” (small PDF to `/dotmatrix`, tiny ESC/POS to `/thermal`)  
- [ ] Show clear errors for `401` / `403` / tray offline  

### 3.4 Auto-print after successful transactions

| Document | Print mode | Tray endpoint | Payload |
|----------|------------|---------------|---------|
| Sale order B2B (grosir) | Dot matrix | `POST /dotmatrix` | PDF |
| Sale order B2C (retail) | Thermal | `POST /thermal` | ESC/POS bytes |
| Sales return | Dot matrix | `POST /dotmatrix` | PDF |
| Purchase order | Dot matrix | `POST /dotmatrix` | PDF |
| Purchase return | Dot matrix | `POST /dotmatrix` | PDF |

**Recommended sequence**

```text
1. User submits transaction → Farmora backend succeeds
2. FE requests printable artifact from backend
   (routes locked in [BACKEND-PRINT.md](./BACKEND-PRINT.md)):
   GET /api/saleOrders/{id}/print.pdf
   GET /api/saleOrders/{id}/print.escpos
   etc.
3. FE POSTs bytes to tray
4. On tray failure: toast + "Reprint" action (keep document id)
```

**Dot matrix**

```http
POST /dotmatrix
X-Farmora-Tray-Key: <key>
Content-Type: application/pdf

<raw PDF bytes>
```

Alternative JSON body:

```json
{ "pdfBase64": "<base64>" }
```

**Thermal**

```http
POST /thermal
X-Farmora-Tray-Key: <key>
Content-Type: application/octet-stream

<raw ESC/POS bytes>
```

Alternative JSON body:

```json
{ "rawBase64": "<base64>" }
```

Success: **`204 No Content`**.

### 3.5 Manual reprint

From document detail / history:

1. Fetch the same printable artifact from backend (by id).  
2. `POST /dotmatrix` or `POST /thermal` as above.  

Do not require the cashier to re-submit the transaction.

### 3.6 Error handling (tray)

| HTTP | Meaning | FE action |
|------|---------|-----------|
| `204` | Printed (accepted by tray) | Silent success or brief toast |
| `400` | Bad payload | Bug / bad generator — log + toast |
| `401` | Bad/missing API key | Open settings, re-enter key |
| `403` | Origin not allowlisted | `PUT /config` with current `window.location.origin` |
| `404` | Printer not configured or not installed | Open printer settings |
| `503` | Print failed (spooler/driver/handler) | Toast + Reprint |
| Network error | Tray not running | Banner: “Farmora Tray offline” |

Example client helper:

```ts
const TRAY_DEFAULT = "http://127.0.0.1:9123";

function trayHeaders(): HeadersInit {
  const key = localStorage.getItem("farmoraTray.apiKey");
  if (!key) throw new Error("Farmora Tray API key not set");
  return { "X-Farmora-Tray-Key": key };
}

export async function isTrayOnline(timeoutMs = 1500): Promise<boolean> {
  const base = localStorage.getItem("farmoraTray.baseUrl") ?? TRAY_DEFAULT;
  const ctrl = new AbortController();
  const t = setTimeout(() => ctrl.abort(), timeoutMs);
  try {
    const res = await fetch(`${base}/health`, { signal: ctrl.signal });
    return res.ok;
  } catch {
    return false;
  } finally {
    clearTimeout(t);
  }
}

export async function printDotMatrix(pdf: ArrayBuffer): Promise<void> {
  const base = localStorage.getItem("farmoraTray.baseUrl") ?? TRAY_DEFAULT;
  const res = await fetch(`${base}/dotmatrix`, {
    method: "POST",
    headers: { ...trayHeaders(), "Content-Type": "application/pdf" },
    body: pdf,
  });
  if (!res.ok) throw new Error(await res.text());
}

export async function printThermal(raw: ArrayBuffer): Promise<void> {
  const base = localStorage.getItem("farmoraTray.baseUrl") ?? TRAY_DEFAULT;
  const res = await fetch(`${base}/thermal`, {
    method: "POST",
    headers: { ...trayHeaders(), "Content-Type": "application/octet-stream" },
    body: raw,
  });
  if (!res.ok) throw new Error(await res.text());
}

export async function saveTrayPrinterConfig(input: {
  dotMatrix: string | null;
  thermal: string | null;
}): Promise<void> {
  const base = localStorage.getItem("farmoraTray.baseUrl") ?? TRAY_DEFAULT;
  const res = await fetch(`${base}/config`, {
    method: "PUT",
    headers: { ...trayHeaders(), "Content-Type": "application/json" },
    body: JSON.stringify({
      allowedOrigin: window.location.origin,
      printers: {
        dotMatrix: input.dotMatrix ?? "",
        thermal: input.thermal ?? "",
      },
    }),
  });
  if (!res.ok) throw new Error(await res.text());
}
```

---

## 4. API reference (complete)

Base: `http://127.0.0.1:9123`

| Method | Path | Auth | Request | Success |
|--------|------|------|---------|---------|
| `GET` | `/health` | No | — | `{ status, version }` |
| `GET` | `/printers` | Key | — | `{ printers: string[] }` |
| `GET` | `/config` | Key | — | `{ allowedOrigin, port, printers }` |
| `PUT` | `/config` | Key | `{ allowedOrigin?, printers? }` | same as GET |
| `POST` | `/dotmatrix` | Key | PDF bytes or `{ pdfBase64 }` | `204` |
| `POST` | `/thermal` | Key | raw bytes or `{ rawBase64 }` | `204` |

CORS: tray allows the configured `allowedOrigin`. Preflight `OPTIONS` is permitted without API key.

---

## 5. Out of scope for frontend (tray contract)

Farmora Tray does **not**:

- Accept sale-order / return JSON to render invoices  
- Choose B2B vs B2C business rules  
- Store print history  
- Generate PDF or ESC/POS  

Those belong to Farmora backend (+ FE orchestration).

---

## 6. PDF / ESC-POS generation: frontend vs backend?

### Recommendation: **generate on the Farmora backend**

For Farmora (multi-branch POS, tax-ish forms, ~24 PCs, dot matrix + thermal), backend generation is the better default.

| | Backend generation | Frontend generation |
|--|--------------------|---------------------|
| Layout consistency | Same PDF on every PC / reprint | Easy to drift across browsers/versions |
| Dot matrix forms | Libraries (QuestPDF, etc.) control mm/points | Browser print/HTML→PDF is flaky for continuous forms |
| Thermal ESC/POS | Deterministic byte builder on server | Possible in JS, but easy to get wrong per printer model |
| Reprint / audit | `GET /…/print.pdf` anytime | FE may not have full data later |
| Security / tampering | Server signs off on what was printed | Client can alter HTML before print |
| Preview / download / email | Same artifact | Duplicate work |
| Offline tray | Still need network for backend anyway for submit | Only helps if printing without server |

**Frontend generation** is acceptable only for quick prototypes or purely visual drafts — not for production B2B rangkap invoices.

**Suggested split**

1. **Backend**  
   - `GET /api/sale-orders/{id}/print.pdf` (B2B / returns / PO)  
   - `GET /api/sale-orders/{id}/print.escpos` (B2C thermal)  
   - Same pattern for sales return / purchase order / purchase return  
2. **Frontend**  
   - After submit (or Reprint click): download bytes → `POST` tray `/dotmatrix` or `/thermal`  
3. **Optional later**  
   - Backend includes print URLs in the submit response to avoid an extra round-trip  

Thermal tip: prefer **server-built ESC/POS** (not “PDF to thermal”). Dot matrix tip: prefer **server PDF** sized for the continuous form, then tray `/dotmatrix`.

---

## 7. Implementation checklist (FE)

- [ ] `farmoraTray` client module (`health`, `printers`, `config`, `printDotMatrix`, `printThermal`)  
- [ ] Settings page: API key + two printer dropdowns + save `allowedOrigin`  
- [ ] Global tray status indicator  
- [ ] After B2B SO success → backend PDF → `POST /dotmatrix`  
- [ ] After B2C SO success → backend ESC/POS → `POST /thermal`  
- [ ] After sales return / PO / purchase return → PDF → `POST /dotmatrix`  
- [ ] Reprint actions on document detail  
- [ ] Graceful degradation when tray offline (transaction still saved)  
- [ ] i18n / copy for 401, 403, 404, 503, offline  

---

## 8. Backend print endpoints

Implement in **farmora-backend** per **[BACKEND-PRINT.md](./BACKEND-PRINT.md)**.

| Document | Backend GET | Tray POST |
|----------|-------------|-----------|
| SO B2B | `/api/saleOrders/{id}/print.pdf` | `/dotmatrix` |
| SO B2C | `/api/saleOrders/{id}/print.escpos` | `/thermal` |
| Sales return | `/api/salesReturn/{id}/print.pdf` | `/dotmatrix` |
| Purchase order | `/api/purchaseOrders/{id}/print.pdf` | `/dotmatrix` |
| Purchase return | `/api/purchaseReturns/{id}/print.pdf` | `/dotmatrix` |

Until those exist, FE can still ship the **settings + tray client** and test with a static sample PDF / sample ESC/POS file.
