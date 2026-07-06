# DrawnChatList

DrawnUI multi-target chat list sample. Goal: one C# chat UI across Android, iOS, MacCatalyst, Windows, Linux, Web. Unlimited data source, smooth scroll with uneven cell heights, single recycled cell, on-the-fly cell design switching. Hardware-accelerated SkiaSharp canvas.

## Architecture

All UI + logic lives in **`src/Shared`** (shared project `DrawnChatList.Shared.shproj`). Each platform head is a thin shell that builds the same `ChatPage`.

- `Shared/ChatPage.Shared.cs` — core: `CreateCanvas()` / `CreateCanvasContent()` build the whole UI in fluent C#; navbar, LoadMore/trim/window logic, send/receive/reply/scroll, unread-messages tracking, Dev Tools picker.
- `Shared/ChatCell.cs` — single recycled cell, design switches by message type/context; owns the shared highlight visual (transient jump-flash + steady unread tint), long-press-to-reply on the bubble row.
- `Shared/ChatMessage.cs` (has `IsUnread`), `ChatMessagesStack.cs` (`SkiaLayout` subclass), `WindowedCollection.cs`/`LimitedSource.cs`, `MockChatApi.cs`, `ChatTheme.cs`, `ChatPage.Svg.cs`.
- Platform heads (each has own `ChatPage.<Plat>.cs` partial = ctor + build entry):
  - `src/Maui` — `BasePageReloadable`, HotReload via `Build()`. TFM `net10.0-{android,ios,maccatalyst,windows}`.
  - `src/OpenTk` — `Program.cs` runs a scripted "gesture robot" pan-flick driver, logs scroll/window state, dumps GL framebuffer PNGs, auto-closes. TFM `net10.0`. **Primary AI probe target.**
  - `src/Blazor` — Blazor WASM (`DrawnChatList.Blazor.csproj`). TFM `net10.0`. **Primary visual-design target (Playwright screenshots).** Published demo: https://chatproto.appomobi.com/, embeddable via iframe (dark bg matches `ChatTheme.Bg`, centered, size-capped via CSS in `wwwroot/css/app.css`, no extra page chrome).

`src/Shared` compiled into ALL heads — a Shared fix reaches every platform.

## Chat model (critical invariants)

- **Inverted scroll**: `SkiaScroll.Rotation=180` + `ReverseGestures=true`; cells `Rotation=180` upright themselves. `_items[0]` = newest = content start = visual BOTTOM.
- **Windowed ItemsSource**: `_all` = full dataset (stands in for SQLite paging); `_items` = resident window, NEWEST-FIRST. `_items[i] == _all[_windowEnd-1-i]`. Window = `_all[_windowStart.._windowEnd)`.
- **Bidirectional LoadMore**: scroll's plain bottom LoadMore = visually scrolling UP = `LoadOlder` (append at list tail). Top LoadMore = visually DOWN = `LoadNewer` (head-insert at 0, framework pins viewport).
- **Memory cap**: `LimitedSource` always trims (`WindowedSource` ctor'd with `true`), cap = `MaxItemsInMemory=200`, batch = `LoadBatch=50`. Trim BEFORE loading, OPPOSITE end of the load.
- New message: `InsertNewest` adds to `_all` tail + `_items.Insert(0,...)`. Returns false when window detached from present (deep in trimmed history) → rebase via `ReplaceRangeReset`.
- **Unread messages (Telegram-style)**: incoming message while scrolled away from newest (`!atNewest`) sets `ChatMessage.IsUnread=true`, tracked oldest-first in `_unreadMessages`, drives the scroll-to-end FAB badge count + a steady (non-animated) cell highlight (shares the visual with the transient jump-flash, gated by `_flashActive` so they don't fight). FAB (`ScrollToUnreadOrNewest`) jumps to the FIRST/oldest unread, not newest. Unread state clears ONLY when the user reaches true offset 0 (`UnreadClearEpsilon=0.5` in `OnChatScrolled`) — not on "near zero", not on tapping the FAB.
- `TotalItems` is smaller under `#if BROWSER || WEB` (20) vs native (322) — single-thread WASM budget, see comment at `ChatPage.Shared.cs` top.

## DrawnUI rules (from global CLAUDE.md — enforced)

- Load `drawnui` / `drawnui-fluent` / `drawnui-blazor` skills before DrawnUI work.
- Fluent C# only: construct controls inline in `Children`, chain `.Assign(out _field)`, wire events with `.OnTapped()` etc. NEVER `var x = new...` then reference in `Children`; NEVER `+=`.
- DrawnUI owns its own layout/render pipeline — don't reason via MAUI native pipeline.
- Don't switch a working control to a different virtualization mode/code path for an optimization; layer onto the path it already uses.
- This list = inverted Rotation=180 + windowed cap + bidirectional LoadMore + variable heights. Any perf/arch change must be validated against THESE defining conditions, not a simplified harness.
- **Focus-catch pattern**: navbar (and any control) can defocus the chat editor generically via `SkiaControl.CanBeFocused=true` (DrawnUi base property) — no app-level coupling to the editor needed. Canvas's own completed-Tapped logic transfers focus to whichever control returns `true` from `SetFrameworkFocus`.
- **Long-press** (`.OnLongPressing()` on the bubble row in `ChatCell.cs`) now works on ALL heads (Maui/Blazor/OpenTk) — was dead on Blazor/OpenTk until a DrawnUi framework fix (root cause: `LongPressing` event was declared but never invoked in the shared gesture pipeline; fixed with `SendLongPressing` mirroring `SendTapped`, plus a real timer added for OpenTk). Fix lives in the sibling `DrawnUi` repo, not here.

## Build / run

- Sibling repo `../DrawnUi` is the DrawnUI source, referenced by `DrawnChatList-refs.sln` (project refs, not NuGet). No `global.json` in this repo.
- OpenTk: `dotnet run --project src/OpenTk` — runs probe, prints console diagnostics, exits.
- Blazor: `dotnet run --project src/Blazor` (or `src/Blazor` dir directly) — validate via Playwright + browser console. Local dev port per `Properties/launchSettings.json` (currently 5105).
- Maui: standard `dotnet build -f net10.0-<plat>`.

## Validation (per global rules)

- OpenTk: build AND run, observe console/PNG output before reporting done. Never stop at compile.
- Blazor: Playwright MCP, read browser console FIRST on the target route, then screenshots only if needed. Synthetic `dispatchEvent` pointer events do NOT trigger `AppoMobi.Blazor.Gestures`' pointer-capture pipeline (isTrusted matters here) — use real input (`browser_run_code_unsafe` + `page.mouse.down/up` with an actual `waitForTimeout` hold) to test long-press or anything gesture-timing-sensitive.
- Long-lived Playwright tabs opened against the same URL can end up being the SAME tab a human is manually testing in concurrently — if console/DOM state looks inconsistent with what you just did, open a fresh tab (`browser_tabs` action `new`) before concluding anything.
