# DRAWN CHAT LIST

## Why
* Can have 1_000_000_000 items in your data source
* Smooth scrolling with any number of items
* Single cell for all message types, no need for templates
* Any cell design switching on the fly upon message content
* Recycling cells of different heights
* Same code for Android, iOS, MacCatalyst, Windows, Linux and Web

## How
* Drawing on hardware-accelerated SkiaSharp canvas
* In-memory limited loaded data source chunk
* LoadMore mecanics for both top and bottom
* Only visible and upcoming cells are measured and drawn
* Double buffering, measuring and drawing cells in background

## AI-assistance
* Make AI use OpenTk platform to run probes, analyse output and fix issues.
* For visual design useful to use Blazor target to make AI use playwright to "see" screenshots to match your design goals.