# DrawnChatList

DrawnUI multi-target chat list sample. Goal: one C# chat UI across Android, iOS, MacCatalyst, Windows, Linux, Web. Unlimited data source, smooth scroll with uneven cell heights, single recycled cell, on-the-fly cell design switching. Hardware-accelerated SkiaSharp canvas.

## Architecture

All UI + logic lives in **`src/Shared`** (shared project `DrawnChatList.Shared.shproj`). Each platform head is a thin shell that builds the same `ChatPage`.

- `Shared/ChatPage.Shared.cs` — core: `CreateCanvas()` / `CreateCanvasContent()` build the whole UI in fluent C#; LoadMore/trim/window logic; send/receive/reply/scroll.
- `Shared/ChatCell.cs` — single recycled cell, design switches by message type/context.
- `Shared/ChatMessage.cs`, `ChatMessagesStack.cs` (`SkiaLayout` subclass), `WindowedCollection.cs`, `MockChatApi.cs`, `ChatTheme.cs`, `ChatPage.Svg.cs`.
- Platform heads (each has own `ChatPage.<Plat>.cs` partial = ctor + build entry):
  - `src/Maui` — `BasePageReloadable`, HotReload via `Build()`. TFM `net10.0-{android,ios,maccatalyst,windows}`.
  - `src/OpenTk` — `Program.cs` runs a scripted "gesture robot" pan-flick driver, logs scroll/window state, dumps GL framebuffer PNGs, auto-closes. TFM `net10.0`. **Primary AI probe target.**
  - `src/Web` — Blazor WASM. TFM `net10.0`. **Primary visual-design target (Playwright screenshots).**

`src/Shared` compiled into ALL heads — a Shared fix reaches every platform.

## Chat model (critical invariants)

- **Inverted scroll**: `SkiaScroll.Rotation=180` + `ReverseGestures=true`; cells `Rotation=180` upright themselves. `_items[0]` = newest = content start = visual BOTTOM.
- **Windowed ItemsSource**: `_all` = full dataset (stands in for SQLite paging); `_items` = resident window, NEWEST-FIRST. `_items[i] == _all[_windowEnd-1-i]`. Window = `_all[_windowStart.._windowEnd)`.
- **Bidirectional LoadMore**: scroll's plain bottom LoadMore = visually scrolling UP = `LoadOlder` (append at list tail). Top LoadMore = visually DOWN = `LoadNewer` (head-insert at 0, framework pins viewport).
- **Memory cap**: `LimitMemoryWindow`, `MaxItemsInMemory=250`. Trim BEFORE loading, OPPOSITE end of the load.
- New message: `InsertNewest` adds to `_all` tail + `_items.Insert(0,...)`. Returns false when window detached from present (deep in trimmed history) → rebase via `ReplaceRangeReset`.

## DrawnUI rules (from global CLAUDE.md — enforced)

- Load `drawnui` / `drawnui-fluent` / `drawnui-blazor` skills before DrawnUI work.
- Fluent C# only: construct controls inline in `Children`, chain `.Assign(out _field)`, wire events with `.OnTapped()` etc. NEVER `var x = new...` then reference in `Children`; NEVER `+=`.
- DrawnUI owns its own layout/render pipeline — don't reason via MAUI native pipeline.
- Don't switch a working control to a different virtualization mode/code path for an optimization; layer onto the path it already uses.
- This list = inverted Rotation=180 + windowed cap + bidirectional LoadMore + variable heights. Any perf/arch change must be validated against THESE defining conditions, not a simplified harness.

## Build / run

- Sibling repo `../DrawnUi` is the DrawnUI source, referenced by `DrawnChatList-refs.sln` (project refs, not NuGet). No `global.json` in this repo.
- OpenTk: `dotnet run --project src/OpenTk` — runs probe, prints console diagnostics, exits.
- Web: `dotnet run --project src/Web` — validate via Playwright + browser console.
- Maui: standard `dotnet build -f net10.0-<plat>`.

## Validation (per global rules)

- OpenTk: build AND run, observe console/PNG output before reporting done. Never stop at compile.
- Blazor: Playwright MCP, read browser console FIRST on the target route, then screenshots only if needed.

## Open issues (README TODO)

- Structure messed up by background measurement.
- Android entry de-focusing when keyboard already open.
- App: add semaphore so only one background cache draw runs; CANCEL prior draw when a new one starts.
