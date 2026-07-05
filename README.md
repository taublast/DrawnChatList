# DRAWN CHAT LIST

## Why
* Same C# code for Android, iOS, MacCatalyst, Windows, Linux and Web
* Unlimited number of items in data source
* Smooth scrolling with uneven cell heights
* Single recycled cell for all message types
* Cell design switching on the fly upon context

## How
* Drawing on hardware-accelerated SkiaSharp canvas
* In-memory limited loaded data
* LoadMore mecanics for both top and bottom
* Double buffering, measuring and drawing cells in background

## AI-assistance
* Make AI use OpenTk platform to run probes, analyse output and fix issues.
* For visual design useful to use Blazor target to make AI use playwright to "see" screenshots to match your design goals.


## TODO

### DrawnUI

- when keyboard shows scroll offset should be adjusted to keep same last cell visible
 
- do NOT resize stack when scroll viewport became smaller (only if bigger), allow keyboard to redice viewport etc without remeasuring

- android entry:
1. de-focusing when keyboard already open
2. need tap 2 times to ficus it

### App

### SkiaCachedStack

Use SkiaCachedStack when: templated virtualized list inside a SkiaScroll where scrolling repaints the same cells every frame — chats, feeds, any long list you fling. Its win: the visible band (viewport ± 1 viewport overscan) is recorded ONCE into a plane and blitted on following frames; plain SkiaStack re-walks and re-paints every visible cell every scroll frame. Re-record only per half-viewport of drift or on invalidation — so ~1 paint per 460px of scroll instead of per frame.

Stay with plain SkiaStack when:
- Content mutates visually every frame (animations inside many cells, streaming into several bubbles at once) — plane invalidates each frame, you'd pay record cost per frame = worse than direct.
- Static/small layouts that rarely repaint — nothing to amortize, plane machinery is dead weight.
- You want a custom cache strategy on the stack itself (UseCache on it) — SkiaCachedStack owns DrawDirectInternal and sets UseCache=None.

Requirements/assumptions it carries: vertical Column, Split=1 (gates tile by cursor), cells ideally ImageDoubleBuffered (record blits their caches; cold cells gate the record), works with windowed sources (reanchor handles trims). Pairs best with UsePreparedViews + MeasureVisible — that combination is what we validated end-to-end.
