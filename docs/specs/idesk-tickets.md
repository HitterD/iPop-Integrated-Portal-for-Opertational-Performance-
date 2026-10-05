# ICT Tickets via iDesk — implementation spec

iPop gets a `/tickets` module that files ICT tickets into iDesk and shows their progress.
HC ticketing comes later through the same page; for now there is one provider (iDesk, "ICT").

- iDesk API contract: `C:\Users\IT12\orca\workspaces\iDesk-main\zoom-page\docs\integrations\ipop-api.md`
- Approved mockup (layout + every micro-interaction): `docs/mockups/ipop-apple/tickets.html` (tab 7 = motion spec)
- Shared DTOs (frozen contract between API and Web): `src/IPop.Application/Tickets/TicketDtos.cs`

## Decisions (all approved by the user)

- One page `/tickets`, thin `ITicketProvider`-style seam; HC tab hidden until a second provider exists.
- Requester is always the signed-in user (their NIK). Everyone sees only their own tickets, admins too.
- Read-only detail after creation: no cancel/edit/comment (iDesk API has none). "Open in iDesk" for the rest.
- Queues come live from iDesk `GET /modules` (cache ~10 min). Defaults: `it-support`, `MEDIUM`; category =
  `defaultCategory` or required. Category codes are humanised (`HARDWARE_INSTALLATION` → "Hardware installation").
- Local mirror: tickets + events in the iPop DB. Webhook events deduped by `id`.
- Sync: when the user opens /tickets (`GET /tickets?nik=&state=all`) + a background job every ~15 min for open
  tickets (`GET /tickets/{ticketNumber}`). Changes discovered by sync update the mirror but do **not** push notifications.
- `externalRef` = `IPOP-ICT-{yyyy}-{NNNNNN}`, generated and stored **before** POST (status `SENDING`).
  Timeout / 5xx / network → keep `SENDING`, background retry with the same ref (iDesk is idempotent on it) up to ~24 h,
  then `FAILED` ("Try again" / "Discard"). 400 → error back to the form, nothing stored. 422 → `not_registered`.
- Attachments: up to 5 × 10 MB, iDesk's extension list (see `TicketAttachmentRules`). Kept server-side only until the
  ticket is accepted, then dropped (we keep file name + size for the detail view).
- 422 (NIK not in iDesk) → "Your iDesk account isn't set up yet. Contact ICT to create your iDesk account so you can
  submit tickets and follow their progress." Remember per user; banner on /tickets until a sync or submit succeeds.
  ICT contact/extension is still a placeholder ("Contact ICT").
- Ticket deleted in iDesk (404 on detail, or missing from the `?nik=` sync while we still had it) → soft delete (`RemovedAt`), hidden.
- Notifications (in-app + push via `INotificationGatewayService.PublishInternalAsync`, clientSlug `tickets`,
  targetUrl `/tickets/{id}`) for webhook events `ticket.assigned`, `ticket.status_changed`, `ticket.resolved`,
  `ticket.closed`. Not for `ticket.created` of an iPop-filed ticket. RESOLVED reads "Ticket resolved — please confirm".
  Own sound slot "Tickets" in notification sound settings.
- Nav: "Tickets" (lucide `life-buoy`) in the Workspace group, badge key `tickets` = number of RESOLVED tickets.
- Dashboard widget "My tickets": open count, please-confirm count, top 3 open.
- Webhook for an unknown NIK → `200` + debug log.
- English UI. Permissions `tickets.read`, `tickets.create` granted to Employee + Admin (revocable via RBAC override).

## Backend

### Config

```
IDesk:BaseUrl      default https://idesk.santos.co.id/api/v1/integrations/ipop/
IDesk:ClientId     default ipop
IDesk:ClientSecret required, >= 32 chars; missing/short → feature reports "not configured" (fail closed), webhook → 503
```

### iDesk client (Infrastructure/Tickets)

Modelled on `Infrastructure/Sja/HttpSjaGateway.cs`: serialise the body once, sign
`HMAC-SHA256(secret, "{timestampMs}.{rawBody}")` lowercase hex, headers `x-ipop-client-id`, `x-ipop-timestamp`,
`x-ipop-signature`; GET signs the empty string. Polly retry only for idempotent calls (GETs, and POST /tickets is
idempotent by externalRef). Timeout ~20 s per call (attachments can be large). Typed result, never throws to callers.

### API (`/api/tickets`, RequireAuthorization + RequirePermission("tickets.read"))

| Method | Path | Notes |
|---|---|---|
| GET | `/api/tickets/modules` | `TicketModuleDto[]` (cached) |
| GET | `/api/tickets?state=open\|all` | `TicketListDto` from the mirror (fast, cached-first) |
| POST | `/api/tickets/sync` | pulls `?nik=&state=all` from iDesk into the mirror, returns `TicketListDto` |
| GET | `/api/tickets/{id:guid}` | `TicketDetailDto`; for non-pending tickets may refresh from iDesk in the background |
| POST | `/api/tickets` | `CreateTicketRequest` → 201/202 `CreateTicketResultDto`, 400 `{code,message}`, 422 `{code:"not_registered"}`; permission `tickets.create`; request size limit ~80 MB |
| POST | `/api/tickets/{id:guid}/retry` | FAILED → try now; 200 detail (sent) or 503 `{code:"unreachable"}` |
| DELETE | `/api/tickets/{id:guid}` | only SENDING/FAILED (discard) |
| POST | `/api/idesk/webhook` | AllowAnonymous; verify timestamp ±5 min + HMAC over raw body (constant-time); dedupe `id`; rate limited; answer 2xx fast |

Every change to a user's tickets (webhook, sync, retry worker) also sends realtime `IRealtime.ToUserAsync(nik,
"tickets.changed", { id })` so an open page / badge updates live.

Validation mirrors iDesk: title 5–200, description 3–5000, criticalReason 3–500 when CRITICAL, device/software ≤ 100,
category must belong to module (required for multi-category modules), hardware fields required for
HARDWARE_INSTALLATION (date `yyyy-MM-dd` not in the past, weekday, slot in `TicketAttachmentRules.HardwareSlots`),
attachments ≤ 5, extension allow-list, ≤ 10 MB decoded.

### Workers

- Outbox: SENDING tickets retried with backoff (1, 2, 5, 10, 30 min, then every 30 min) until 24 h after creation → FAILED + notification "Couldn't send your ticket".
  When one lands: notify "Ticket sent to ICT" only if it was queued (the user already saw the sheet otherwise).
- Reconcile: every 15 min, open tickets with a ticket number → `GET /tickets/{number}`; update mirror silently.

### Tests (xunit, `tests/IPop.Tests`, SQLite `TestDb`)

Signing (known vector), webhook signature/timestamp rejection, duplicate event ignored, IDESK-origin webhook creates
the mirror row matched by NIK, unknown NIK → 200 no-op, create: 201 / 400 / 422 / timeout → SENDING, idempotent retry
with the same externalRef, outbox gives up after 24 h, sync soft-deletes missing tickets, badge counts RESOLVED,
users never see another user's ticket.

## Frontend (Blazor WASM, `src/IPop.Web`)

Follow the mockup exactly. Pages `/tickets`, `/tickets/{id:guid}`, `/tickets/new`. Reuse the app-wide motion layer
(`wwwroot/css/core/micro.css`, `wwwroot/js/micro.js`, tokens in `core/tokens.css`; `css/ipop.css` is NOT loaded,
never edit it). Add the Tickets segmented controls to `micro.js` SLIDERS. prefers-reduced-motion → static.

- Nav item + badge key `tickets`, NotificationModules entry `tickets` (icon `life-buoy`) so toasts/bell/sounds get it.
- Cached-first list: render the mirror immediately, then `POST /api/tickets/sync`; live updates on `tickets.changed`.
- New ticket draft autosaved in localStorage per account (existing DraftStore pattern), attachments validated client-side
  with the same rules before upload (base64).
- Dashboard "My tickets" widget.
