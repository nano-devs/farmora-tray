# Farmora Backend — Print Artifact Endpoints (Agent Spec)

**Goal:** Implement authenticated `GET` endpoints on **farmora-backend** that return ready-to-print **PDF** (dot matrix) and **ESC/POS** (thermal) bytes for Farmora Tray + frontend.

**Consumer flow (do not change):**

```text
FE submits transaction → success
FE GET /api/.../{id}/print.pdf  or  .../print.escpos   (this spec)
FE POST bytes to Farmora Tray  http://127.0.0.1:9123/dotmatrix|thermal
```

**Related docs**

- Frontend tray integration: [FRONTEND.md](./FRONTEND.md)
- Tray agent (print only): [../README.md](../README.md)

**Implement in repo:** `g:\Work\Clients\farmora\farmora-backend`  
**Do not** put PDF/ESC-POS generation inside Farmora Tray.

---

## 0. Agent constraints

1. Follow existing Farmora patterns: static `Map*Api` classes, `TypedResults.*`, `Permissions.*`, tenant-scoped queries via existing `AppDbContext` / middleware.
2. Reuse **View** permissions of the parent resource (same as GET-by-id).
3. Match existing file downloads: `TypedResults.File(bytes, contentType, fileDownloadName)` (see tax XML / Excel APIs).
4. Do **not** invent a document-type field for sale orders — B2B vs B2C = `CustomerId != null` vs `CustomerId == null`.
5. Sales return API group is **`/salesReturn`** (singular). Do not rename.
6. Keep print generation in `Transaction/Services/` (or `Transaction/Printing/`). Keep endpoints thin.
7. Prefer adding packages via the project’s usual style (direct `PackageReference` in `Farmora.csproj`). Suggested: **QuestPDF** for PDF; ESC/POS can be hand-rolled bytes or a small helper (no requirement for a specific NuGet).
8. Out of scope: Farmora Tray changes, Windows printing, frontend UI (except response contract FE already expects).

---

## 1. Endpoints to add

All under existing `/api` group.

### 1.1 Sale orders — `Transaction/Apis/SaleOrderApi.cs` group `/saleOrders`

| Method | Route | Auth | When allowed | Body |
|--------|-------|------|--------------|------|
| `GET` | `/api/saleOrders/{id}/print.pdf` | `Permissions.SaleOrder.View` | Document exists; prefer printable statuses (see §3) | `application/pdf` |
| `GET` | `/api/saleOrders/{id}/print.escpos` | `Permissions.SaleOrder.View` | Document exists **and** B2C (`CustomerId == null`) | `application/octet-stream` |

**Routing rules for FE (backend enforces):**

| Sale order | PDF endpoint | ESC/POS endpoint |
|------------|--------------|------------------|
| B2B (`CustomerId != null`) | **200** PDF (dot-matrix invoice) | **400** — thermal not used for B2B |
| B2C (`CustomerId == null`) | **400** optional, or **200** simple PDF if you want preview — **prefer 400** so FE always uses escpos for retail | **200** ESC/POS receipt |

Minimum viable: B2B → PDF only; B2C → ESC/POS only; wrong mode → `400` with problem details.

### 1.2 Sales returns — `SalesReturnApi` group `/salesReturn`

| Method | Route | Auth | Body |
|--------|-------|------|------|
| `GET` | `/api/salesReturn/{id}/print.pdf` | `Permissions.SaleOrder.View` | PDF |

No ESC/POS for sales returns in v1.

### 1.3 Purchase orders — `PurchaseOrderApi` group `/purchaseOrders`

| Method | Route | Auth | Body |
|--------|-------|------|------|
| `GET` | `/api/purchaseOrders/{id}/print.pdf` | `Permissions.PurchaseOrder.View` | PDF |

### 1.4 Purchase returns — `PurchaseReturnApi` group `/purchaseReturns`

| Method | Route | Auth | Body |
|--------|-------|------|------|
| `GET` | `/api/purchaseReturns/{id}/print.pdf` | `Permissions.PurchaseOrder.View` | PDF |

### 1.5 Endpoint naming & OpenAPI

- `.WithName` examples: `GetSaleOrderPrintPdf`, `GetSaleOrderPrintEscPos`, `GetSalesReturnPrintPdf`, …
- `.WithSummary` short English summary.
- Return type style: `Results<FileContentHttpResult, NotFound, ProblemHttpResult>` (or project-equivalent `TypedResults` union used elsewhere).

---

## 2. HTTP response contract

### Success

```http
HTTP/1.1 200 OK
Content-Type: application/pdf
Content-Disposition: attachment; filename="SO-INV-20260809-xxxx.pdf"

<binary>
```

```http
HTTP/1.1 200 OK
Content-Type: application/octet-stream
Content-Disposition: attachment; filename="SO-RCP-20260809-xxxx.escpos"

<binary>
```

Filename helper (new, mirror `Inventory/ExcelDownloadFileNames.cs`):

```text
Transaction/PrintDownloadFileNames.cs
  Pdf(prefix, displayId)  → "{prefix}-{displayId}-{yyyyMMdd-HHmmss}.pdf"
  EscPos(prefix, displayId) → "{prefix}-{displayId}-{yyyyMMdd-HHmmss}.escpos"
```

Suggested prefixes: `SO-INV`, `SO-RCP`, `SR-INV`, `PO-INV`, `PR-INV`.

### Errors

| Status | When |
|--------|------|
| `401` / `403` | Existing auth pipeline |
| `404` | Unknown id / wrong tenant |
| `400` | Wrong print mode for document (e.g. ESC/POS on B2B); Draft not printable if you enforce §3 |
| `409` | Optional: cancelled / void — prefer `400` with clear `detail` unless project already uses 409 for state conflicts |

Use `TypedResults.Problem(...)` / existing problem-details style. Include a stable `title` + human `detail`.

Example detail strings:

- `"Thermal print is only available for walk-in (B2C) sale orders."`
- `"Dot-matrix PDF is only available for B2B sale orders (customer required)."`
- `"Sale order is Draft and cannot be printed."`

---

## 3. Printability rules (v1)

Apply consistently:

| Resource | Printable when |
|----------|----------------|
| SaleOrder | Status is **Completed** (and optionally also New/Processing/Shipped if product needs pre-completion print — **default: Completed only**; if create+fulfill returns Completed in one step, that covers POS). If cashiers must print before Completed, allow any non-`Draft`/`Cancelled`/`OnHold`. **Choose one rule and document it in code comments.** Recommended for POS: allow print when status ∈ {`Completed`, `Shipped`, `Processing`, `New`} and reject `Draft`, `Cancelled`, `OnHold`. |
| SalesReturn | Status **Approved** (reject Draft) |
| PurchaseOrder | Same spirit as SO: reject Draft/Cancelled/OnHold |
| PurchaseReturn | Status **Approved** |

Always scope by current tenant (same as GET-by-id).

---

## 4. Data loading

For each print GET:

1. Load aggregate with same includes as GET-by-id (header, lines, customer/vendor, payments if shown on form, author/branch if needed).
2. Map to a dedicated **print model** / DTO used only by renderers (do not pass `DbContext` entities deep into PDF engine if avoidable).
3. Render → `byte[]` → `TypedResults.File`.

Suggested services (new):

```text
Transaction/Services/Printing/SaleOrderPrintService.cs
Transaction/Services/Printing/SalesReturnPrintService.cs
Transaction/Services/Printing/PurchaseOrderPrintService.cs
Transaction/Services/Printing/PurchaseReturnPrintService.cs
Transaction/Services/Printing/EscPosEncoder.cs          // low-level helpers
Transaction/Services/Printing/Pdf/…                     // QuestPDF documents
```

Register as scoped in `Program.cs` (or existing Transaction DI extension if present).

---

## 5. PDF content requirements (dot matrix)

**Purpose:** Feed Farmora Tray `POST /dotmatrix`. Layout should resemble existing warehouse **Surat Jalan / invoice** continuous forms (“mirip SJ” per backend README). Exact mm/spacing may be tuned later with physical paper — v1 must be **complete and readable**, not pixel-perfect.

### 5.1 Shared header fields (all PDF types)

- Company / tenant display name (from tenant context if available)
- Document title (e.g. `FAKTUR PENJUALAN`, `RETUR PENJUALAN`, `PURCHASE ORDER`, `RETUR PEMBELIAN`)
- `DisplayId` / document number
- Date (`CreatedAt` or business date used elsewhere)
- Counterparty: customer name+address (SO/SR) or vendor (PO/PR)
- Optional: author / cashier name

### 5.2 Line table

| Column | Notes |
|--------|--------|
| No | 1-based index |
| Product code / SKU | from product |
| Product name | |
| Qty | with unit type name if available |
| Unit price | |
| Line discount | if model has it |
| Line total | |

### 5.3 Footer totals

- Subtotal, discount, tax base / PPN, grand total (use fields already on the aggregate: e.g. SO `SubTotal`, `TaxAmount`, `GrandTotal`, etc.)
- Payment summary if useful (paid / method) — optional v1
- Notes / terbilang — optional v1

### 5.4 Page setup (v1 defaults — adjust later for real form)

- QuestPDF page size: start with **A4** or a custom continuous width if known; document constants in one place (`PrintPageSettings`).
- Monospace-friendly or simple sans layout; avoid browser HTML.
- One logical “copy” in v1 (multi-copy rangkap can be N identical pages later).

### 5.5 Sale order PDF specificity

- **Only B2B** (`CustomerId != null`) for `/print.pdf` in the recommended MVP.
- Show customer + membership tier name if loaded.
- Show `DisplayId` prominently (barcode **optional** v1 — skip unless easy).

---

## 6. ESC/POS content requirements (thermal)

**Purpose:** Feed Farmora Tray `POST /thermal` as **raw** bytes (`application/octet-stream`).

### 6.1 Scope

- **Only** `GET /api/saleOrders/{id}/print.escpos`
- **Only** B2C (`CustomerId == null`)

### 6.2 Encoder behavior

Emit a byte sequence suitable for a generic 80mm ESC/POS printer (configurable width in chars, default **48** or **32** — pick one constant).

Minimum commands:

- Init: `ESC @`
- Align center for header; left for lines
- Text: store name, `DisplayId`, datetime
- Lines: `name` truncated + `qty` + `price` / line total
- Totals: subtotal, discount, tax, grand total
- Payment lines if present
- Feed + **cut** (`GS V` partial/full cut) at end
- Use code page that matches Indonesian text if needed (document assumption: UTF-8→printer code page; v1 may use ASCII-safe transliteration if encoding is hard — prefer CP437/CP850 helper with clear TODO)

### 6.3 Do not

- Return PDF for thermal
- Return base64 JSON from these GET endpoints (raw file bytes only)
- Call Farmora Tray from the backend

---

## 7. Suggested handler sketch (sale order PDF)

Pseudo-code aligned with project style:

```csharp
group.MapGet("/{id:guid}/print.pdf", GetSaleOrderPrintPdf)
    .WithName("GetSaleOrderPrintPdf")
    .WithSummary("Download sale order invoice PDF (B2B / dot matrix)")
    .RequireAuthorization(Permissions.SaleOrder.View);

static async Task<Results<FileContentHttpResult, NotFound, ProblemHttpResult>> GetSaleOrderPrintPdf(
    Guid id,
    AppDbContext db,
    SaleOrderPrintService printer,
    CancellationToken ct)
{
    var so = await /* tenant-filtered query with includes */;
    if (so is null) return TypedResults.NotFound();

    if (so.CustomerId is null)
        return TypedResults.Problem(
            detail: "Dot-matrix PDF is only available for B2B sale orders (customer required).",
            statusCode: StatusCodes.Status400BadRequest);

    if (/* not printable status */)
        return TypedResults.Problem(...);

    var bytes = printer.BuildInvoicePdf(so);
    var name = PrintDownloadFileNames.Pdf("SO-INV", so.DisplayId);
    return TypedResults.File(bytes, "application/pdf", name);
}
```

Mirror for ESC/POS with inverted B2B/B2C checks.

---

## 8. Implementation order (for the agent)

1. Add QuestPDF package; create `PrintDownloadFileNames` + `PrintPageSettings`.
2. Implement `SaleOrderPrintService.BuildInvoicePdf` + `BuildRetailEscPos` with real data from one Completed SO fixture/path.
3. Wire `GET .../print.pdf` and `GET .../print.escpos` on `SaleOrderApi`.
4. Implement SalesReturn / PurchaseOrder / PurchaseReturn PDF services + endpoints (can reuse shared PDF “invoice document” base with different titles).
5. Manual verify: download PDF opens; escpos file is non-empty; wrong mode returns 400; other tenant id 404.
6. Do not block on perfect SJ geometry — ship correct data + sane layout first.

---

## 9. Acceptance criteria

- [ ] All routes in §1 exist and require correct View permission  
- [ ] B2B SO: PDF 200; ESC/POS 400  
- [ ] B2C SO: ESC/POS 200; PDF 400 (MVP)  
- [ ] SR / PO / PR: PDF 200 when printable; Draft → 400  
- [ ] Responses are raw files via `TypedResults.File` with correct `Content-Type`  
- [ ] Filenames include document display id  
- [ ] Tenant isolation identical to GET-by-id  
- [ ] No tray / localhost print calls from backend  
- [ ] FE can: `fetch` → `arrayBuffer()` → `POST` tray `/dotmatrix` or `/thermal`  

---

## 10. Frontend URL mapping (keep in sync)

| Document | Backend GET | Tray POST |
|----------|-------------|-----------|
| SO B2B | `/api/saleOrders/{id}/print.pdf` | `/dotmatrix` |
| SO B2C | `/api/saleOrders/{id}/print.escpos` | `/thermal` |
| Sales return | `/api/salesReturn/{id}/print.pdf` | `/dotmatrix` |
| Purchase order | `/api/purchaseOrders/{id}/print.pdf` | `/dotmatrix` |
| Purchase return | `/api/purchaseReturns/{id}/print.pdf` | `/dotmatrix` |

Update [FRONTEND.md](./FRONTEND.md) examples if route names differ when implementing — **prefer the paths in this table**.

---

## 11. Explicit non-goals (v1)

- Multi-copy rangkap PDF pages  
- Exact Epson continuous-form coordinates  
- Per-branch logo upload  
- Storing generated PDF in DB/blob  
- Print job history  
- Changing Farmora Tray API  

---

## 12. Quick codebase anchors (farmora-backend)

| Item | Path |
|------|------|
| API registration | `Program.cs` → `MapGroup("/api")` |
| Sale orders API | `Transaction/Apis/SaleOrderApi.cs` |
| Sales returns API | `Transaction/Apis/SalesReturnApi.cs` (`/salesReturn`) |
| Purchase orders API | `Transaction/Apis/PurchaseOrderApi.cs` |
| Purchase returns API | `Transaction/Apis/PurchaseReturnApi.cs` |
| Permissions | `Services/Permissions.cs` → `SaleOrder.View`, `PurchaseOrder.View` |
| File download examples | `Transaction/Apis/TaxApi.cs`, `Inventory/Apis/ProductApi.cs` |
| B2B/B2C product note | `README.md` (Sale Order Grosir vs Retail) |
| Models | `Transaction/Models/SaleOrder.cs` etc. |

---

## 13. Definition of done message

When finished, the agent should report:

1. Routes added  
2. Packages added  
3. Printability rule chosen for SaleOrder  
4. How to curl one PDF and one ESC/POS locally  
5. Any layout TODOs left for physical form fitting  
