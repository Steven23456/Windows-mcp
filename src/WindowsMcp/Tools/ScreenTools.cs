using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using WindowsMcp.Services.UiTree;

namespace WindowsMcp.Tools;

[McpServerToolType]
public sealed class ScreenTools
{
    private readonly IScreenshotService _screenshot;
    private readonly IOcrService _ocr;
    private readonly IWindowService _windows;
    private readonly IInputService _input;
    private readonly IUIAutomationService _uia;
    private readonly IFlashOverlay _flash;
    private readonly ScreenshotOptions _options;
    private readonly ILogger _log;

    /// <param name="windows">
    /// Source of the monitor inventory (A-8) — the same order <c>multi_monitor</c> reports, which
    /// is what the <c>display</c> indices refer to.
    /// </param>
    /// <param name="options">
    /// The process-level <c>--screenshot-scale</c> (A-9); null means no process-level scaling,
    /// so tests and other hosts can construct the tool without it.
    /// </param>
    /// <param name="input">Where the cursor is (A-11): reported on every screenshot in the same virtual-desktop pixels.</param>
    /// <param name="uia">
    /// A-6: the element list <c>annotate</c> draws and lists, from the same snapshot the text
    /// block renders — so label N in the picture is row N in the text of the same call.
    /// </param>
    /// <param name="flash">The post-capture glow (A-14): hidden before every capture, shown after when <c>--flash</c> is on.</param>
    public ScreenTools(IScreenshotService screenshot, IOcrService ocr, IWindowService windows, IInputService input, IUIAutomationService uia, IFlashOverlay flash, ScreenshotOptions? options = null, ILogger<ScreenTools>? log = null)
    {
        _flash = flash;
        _log = log ?? (ILogger)NullLogger<ScreenTools>.Instance;
        _screenshot = screenshot;
        _ocr = ocr;
        _windows = windows;
        _input = input;
        _uia = uia;
        _options = options ?? ScreenshotOptions.Default;
    }

    /// <summary>More divisions than this draws a line every few pixels and a caption per line: unreadable, and slow.</summary>
    private const int MaxGridDivisions = 64;

    private const string RegionDescription =
        "Region as 'x,y,w,h' in virtual-desktop pixels (the same space click/drag/scroll use; a monitor left of or above the primary has negative coordinates). Must lie inside the virtual screen — it is rejected, not clipped. Wins over 'display'. Default: none";
    private const string DisplayDescription =
        "Which monitor(s) to capture: 'all', or comma-separated zero-based indices in multi_monitor order (e.g. '1' or '0,2'); the union of several is captured. Default: the primary display. 'region' wins over this, but an invalid value still errors";

    private const string WindowDescription =
        "Capture the screen rectangle of this window instead of a region or display: its title, matched exact, then substring, then fuzzy (as the window tool does). A minimized window is refused; one partly off the virtual screen is clipped. Cannot be combined with region, display or element_id. Default: none";
    private const string HwndDescription =
        "Capture the screen rectangle of the window with this handle, as reported by window(action:\"list\"); with 'window' too, hwnd wins. Cannot be combined with region, display or element_id. Default: none";
    private const string ElementIdDescription =
        "Capture the screen rectangle of this element: an el_N id from snapshot, or an id from find_element or get_state. An element with no rectangle on screen is refused. Cannot be combined with region, display, window or hwnd. Default: none";

    /// <summary>Appended to both tools' descriptions: what a window or element capture is, and is not.</summary>
    private const string TargetNote =
        " Instead of region/display, 'window' (a title), 'hwnd' (a handle) or 'element_id' picks the rect: one kind of target per call (a rect, a window or an element), two kinds are refused. " +
        "A target capture is the screen rectangle the target occupies, not the target's own surface, so a window lying over it is in the picture: bring it to the front first (switch_to_window) when that matters. " +
        "A maximized window normally reports targetClipped:true, because its invisible frame overhangs its monitor and is left out. " +
        "Nothing is focused, raised, restored or scrolled by this call.";

    /// <summary>screenshot only: annotate takes a snapshot, and a snapshot replaces snapshot ids.</summary>
    private const string ElementIdWithAnnotateDescription =
        "With annotate:true an el_N id from an earlier snapshot is replaced by the new ids in the returned element list (an id from find_element or get_state stays valid). " +
        ElementIdDescription;

    /// <summary>E-7: the window or element that picked the rect, as the caller is told about it.</summary>
    /// <param name="Json">The <c>target</c> object of the metadata.</param>
    /// <param name="Clipped">Clipping to the virtual screen removed part of the target's own rect.</param>
    private sealed record CaptureTarget(object Json, bool Clipped);

    /// <summary>
    /// A-8: resolves the rect to capture from <paramref name="region"/> / <paramref name="display"/>
    /// against the live monitor inventory. <c>region</c> wins (validated against the virtual
    /// screen); else the union of the selected displays; neither means the primary display
    /// (roadmap C3). One resolver for both tools, so <c>screenshot</c> and <c>ocr</c> cannot drift
    /// in what they accept. <c>display</c> is parsed even when <c>region</c> wins: a bad value is
    /// a bad call, not something to ignore quietly.
    /// </summary>
    /// <returns>The rect, the inventory it was resolved against, and the selected indices (null
    /// unless <paramref name="display"/> picked the rect).</returns>
    private async Task<(ScreenRegion Region, MonitorInfo[] Monitors, int[]? Selected, CaptureTarget? Target)> ResolveRegionAsync(
        string? region, string? display, string? window, long? hwnd, string? elementId, CancellationToken ct = default)
    {
        // E-7: one kind of target per call, refused before anything is looked up. Blank counts as
        // not given, as it always has for region and display.
        if (string.IsNullOrWhiteSpace(window)) window = null;
        if (string.IsNullOrWhiteSpace(elementId)) elementId = null;
        var rectGiven = new[] { string.IsNullOrWhiteSpace(region) ? null : "region", string.IsNullOrWhiteSpace(display) ? null : "display" };
        var windowGiven = new[] { window is null ? null : "window", hwnd is null ? null : "hwnd" };
        var kinds = new[]
        {
            string.Join(" and ", rectGiven.Where(n => n is not null)),
            string.Join(" and ", windowGiven.Where(n => n is not null)),
            elementId is null ? "" : "element_id",
        }.Where(k => k.Length > 0).ToArray();
        if (kinds.Length > 1)
            throw new ArgumentException(
                $"{string.Join(" and ", kinds)} name {kinds.Length} different kinds of target; pass only one kind: a rect (region and/or display), a window (window and/or hwnd) or an element (element_id).");

        var monitors = await _windows.EnumerateMonitorsAsync(ct);

        if (window is not null || hwnd is not null)
        {
            // The inventory and the matcher the window tool uses, so a miss reads the same.
            var match = WindowMatcher.Match(await _windows.ListAsync(true, false, ct), window, hwnd);
            var w = match.Window;
            if (w.State == WindowState.Minimized)
                throw new InvalidOperationException(
                    $"Window '{UiRefusals.Clip(w.Title)}' is minimized, so it has no picture on screen; restore it first (window action:restore, or switch_to_window) and call again.");
            var (rect, clipped) = ClipTarget($"Window '{UiRefusals.Clip(w.Title)}'", w.Bounds, monitors,
                "If it has just closed, list the windows again (window action:list); otherwise move it onto a monitor (window action:move).");
            return (rect, monitors, null, new CaptureTarget(
                new { kind = "window", title = w.Title, hwnd = w.Hwnd, matchStrategy = match.Strategy, score = match.Score }, clipped));
        }

        if (elementId is not null)
        {
            var el = await _uia.GetElementAsync(elementId, ct);
            if (el.Bounds is not { Width: > 0, Height: > 0 } bounds)
                throw new InvalidOperationException(
                    $"Element {elementId} has no rectangle on screen: it is scrolled out of view, collapsed or virtualised, or its window has closed. " +
                    "interact_element with action scroll_into_view may bring it into view; if its window closed, take a new snapshot.");
            var (rect, clipped) = ClipTarget($"Element {elementId}", bounds, monitors,
                "Its window may be minimized or moved off the screen.");
            return (rect, monitors, null, new CaptureTarget(
                new { kind = "element", elementId = el.ElementId, name = el.Name, controlType = el.ControlType }, clipped));
        }

        var selected = RegionMath.ParseDisplays(display, monitors.Length);
        var parsed = RegionMath.ParseRegion(region);

        if (parsed is not null)
        {
            RegionMath.Validate(parsed, RegionMath.VirtualScreen(monitors));
            return (parsed, monitors, null, null);
        }
        if (selected is not null)
            return (RegionMath.Union(selected.Select(i => monitors[i]).ToArray()), monitors, selected, null);

        var primary = RegionMath.Primary(monitors);
        return (new ScreenRegion(primary.X, primary.Y, primary.Width, primary.Height), monitors, null, null);
    }

    /// <summary>
    /// E-7: a target's own rect clipped to the virtual screen (a maximised window overhangs its
    /// monitor by its frame), and whether that removed anything. Nothing left is a refusal — and
    /// so is a rect that lies on no monitor: the virtual screen is the monitors' bounding box, and
    /// with unequal monitors part of that box is shown by none of them.
    /// </summary>
    private static (ScreenRegion Rect, bool Clipped) ClipTarget(string what, Bounds bounds, MonitorInfo[] monitors, string hint)
    {
        var screen = RegionMath.VirtualScreen(monitors);
        if (RegionMath.Clip(bounds, screen) is not { } rect
            || !monitors.Any(m => RegionMath.Clip(new Bounds(rect.X, rect.Y, rect.Width, rect.Height), new ScreenRegion(m.X, m.Y, m.Width, m.Height)) is not null))
            throw new InvalidOperationException(
                $"{what} is off-screen: its bounds {bounds.X},{bounds.Y},{bounds.Width},{bounds.Height} have nothing on any monitor (the virtual screen spans " +
                $"x {screen.X}..{screen.X + screen.Width - 1}, y {screen.Y}..{screen.Y + screen.Height - 1} in virtual-desktop pixels; see multi_monitor). {hint}");
        bool clipped = rect.X != bounds.X || rect.Y != bounds.Y || rect.Width != bounds.Width || rect.Height != bounds.Height;
        return (rect, clipped);
    }

    /// <summary>
    /// The one sentence that tells the model how to turn an image pixel into a virtual-desktop
    /// pixel; null when they are already the same thing (origin 0,0, scale 1). Scale-only keeps
    /// A-9's wording; an off-origin capture (a second monitor, a region) needs the offset too.
    /// </summary>
    internal static string? CoordinateNote(ScreenRegion region, double coordinateScale)
    {
        var s = coordinateScale.ToString(CultureInfo.InvariantCulture);
        if (region.X == 0 && region.Y == 0)
        {
            return coordinateScale == 1.0
                ? null
                : $"multiply image pixel coordinates by {s} before passing them to click/drag/scroll";
        }
        return $"virtual-desktop x = {region.X} + imageX × {s}, y = {region.Y} + imageY × {s} — use these for click/drag/scroll";
    }

    [McpServerTool(Title = "Take screenshot", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Capture a screenshot and return it as MCP image content the model can see directly (parity A-7/A-8/A-9). Result content: a text block with one JSON object of metadata {width, height, originalWidth, originalHeight, format, backend ('gdi' or 'wgc': which capture backend actually produced this picture), coordinateSpace:'virtual-desktop', region (the rect actually captured, in virtual-desktop pixels), displays (every monitor: index, x, y, width, height, isPrimary), selectedDisplays? (when 'display' picked the rect), cursor {x, y, monitorIndex} (always: the mouse pointer in virtual-desktop pixels and which display it is on, -1 = none), cursorDrawn? ('icon' or 'ring', only when the pointer was painted onto the image), path? (file output), coordinateScale? (only when the image was downscaled) and note? (whenever image pixels are not virtual-desktop pixels 1:1 — a downscale, a region origin away from 0,0, or both: multiply image pixel coordinates by coordinateScale and add the region origin — the note spells it out; do this before calling click/drag/scroll)} followed, for inline output, by an image block. Default: the primary display, downscaled to fit max_width x max_height (1920x1080). If a capture comes back black, retry with backend 'wgc' — GDI returns black for GPU-accelerated and DRM-protected windows. With annotate:true (parity A-6) the same call also walks the desktop and returns the element list as a second text block — metadata, the element list (the rows snapshot prints, filtered to what this picture contains), then (inline output) the image — and draws a 2 px coloured box with a matching label chip around every interactive element in the picture; the labels are the snapshot's el_N ids, so label N in the image is row N of the text block from the same call, and they go straight to click/interact_element (valid until the next snapshot or annotated screenshot). Metadata then gains annotated:true, annotations (boxes that landed) and grid:{columns,rows} when a grid was asked for; grid captions are virtual-desktop coordinates, not image pixels. When 'window', 'hwnd' or 'element_id' picked the rect, metadata gains target ({kind:'window', title, hwnd, matchStrategy, score} or {kind:'element', elementId, name, controlType}) and targetClipped:true when part of the target lay outside the virtual screen and was left out; 'region' is always the rect actually captured." + TargetNote)]
    public async Task<CallToolResult> Screenshot(
        [Description(RegionDescription)] string? region = null,
        [Description(DisplayDescription)] string? display = null,
        [Description("Image format: png | jpeg | auto (default: jpeg for inline output, png for file output)")] string format = "auto",
        [Description("Output mode: inline (default) returns the image as an MCP image content block; file saves to %TEMP%\\WindowsMcp and returns only the path in the metadata; base64 is a deprecated alias of inline")] string output = "inline",
        [Description("Downscale so the image is at most this wide, in pixels; 0 = no limit (default 1920)")] int max_width = 1920,
        [Description("Downscale so the image is at most this tall, in pixels; 0 = no limit (default 1080)")] int max_height = 1080,
        [Description("Extra shrink factor applied on top of the max_width/max_height fit, in (0, 1] (default 1.0); the server's --screenshot-scale multiplies it further")] double scale = 1.0,
        [Description("JPEG encoder quality, 1-100 (default 90); ignored for png")] int quality = 90,
        [Description("Draw the mouse cursor onto the capture (default: true): the real cursor image when it can be composited, otherwise a drawn ring — cursorDrawn in the metadata says which. The cursor position is reported either way")] bool include_cursor = true,
        [Description("Draw a labelled box around every interactive element in the picture and return the matching element list as a second text block (default: false). Costs one desktop UI walk (the same snapshot walk, so it evicts the previous snapshot's ids); the labels are the snapshot's el_N ids")] bool annotate = false,
        [Description("Overlay this many equal columns as vertical guide lines, each captioned with its virtual-desktop x coordinate; 0 = no vertical lines (default 0, max 64). Works without annotate")] int grid_columns = 0,
        [Description("Overlay this many equal rows as horizontal guide lines, each captioned with its virtual-desktop y coordinate; 0 = no horizontal lines (default 0, max 64). Works without annotate")] int grid_rows = 0,
        [Description("Capture backend: auto (default) | gdi | wgc. wgc uses Windows.Graphics.Capture, the compositor's own frames, which show GPU-accelerated, hardware-overlay and DRM-protected surfaces that gdi's screen copy returns black for; gdi is the classic screen copy. auto prefers wgc and falls back to gdi silently, while backend 'wgc' fails with an error if the compositor cannot serve the rect. The metadata 'backend' field always says which one produced the image")] string backend = "auto",
        [Description(WindowDescription)] string? window = null,
        [Description(HwndDescription)] long? hwnd = null,
        [Description(ElementIdWithAnnotateDescription)] string? element_id = null)
    {
        // Validate every argument before touching the screen: a bad call must not cost a capture.
        bool toFile = ParseOutput(output);
        var fmt = ResolveFormat(format, toFile);
        if (backend.ToLowerInvariant() is not ("auto" or "gdi" or "wgc"))
            throw new ArgumentException($"Unknown backend '{backend}'; expected auto|gdi|wgc");
        if (max_width < 0)
            throw new ArgumentException($"max_width must be 0 (no limit) or positive, got {max_width}");
        if (max_height < 0)
            throw new ArgumentException($"max_height must be 0 (no limit) or positive, got {max_height}");
        if (!(scale > 0 && scale <= 1))
            throw new ArgumentException($"scale must be in (0, 1], got {scale.ToString(CultureInfo.InvariantCulture)}");
        if (quality is < 1 or > 100)
            throw new ArgumentException($"quality must be 1-100, got {quality}");
        if (grid_columns is < 0 or > MaxGridDivisions)
            throw new ArgumentException($"grid_columns must be 0 (no grid) to {MaxGridDivisions}, got {grid_columns}");
        if (grid_rows is < 0 or > MaxGridDivisions)
            throw new ArgumentException($"grid_rows must be 0 (no grid) to {MaxGridDivisions}, got {grid_rows}");
        var profile = _options.Profile ? Stopwatch.StartNew() : null;
        var stageMs = new Dictionary<string, long>();
        long mark = 0;
        void Stage(string name)
        {
            if (profile is null) return;
            var now = profile.ElapsedMilliseconds;
            stageMs[name] = now - mark;
            mark = now;
        }

        // E-7: a window or element target is resolved here, before annotate's snapshot evicts its id.
        var (r, monitors, selected, target) = await ResolveRegionAsync(region, display, window, hwnd, element_id);
        Stage("resolve");
        // Read before the capture so the reported position is at most one capture old, and so a
        // broken cursor read (a broken desktop) never costs a capture. It is not masked.
        var cursor = await _input.GetCursorPositionAsync();
        Stage("cursor");

        // A-6: the element walk happens BEFORE the capture so label N in the picture is row N of
        // the text block from this same call; only what lies inside the captured rect is kept.
        SnapshotResult? listed = null;
        IReadOnlyList<AnnotationBox>? boxes = null;
        if (annotate)
        {
            var snapshot = await _uia.SnapshotAsync(new SnapshotRequest(SnapshotScope.Desktop));
            var kept = snapshot.Interactive.Where(e => Overlaps(e.Bounds, r)).ToArray();
            var keptScroll = snapshot.Scrollable.Where(e => Overlaps(e.Bounds, r)).ToArray();
            listed = snapshot with { Interactive = kept, Scrollable = keptScroll };
            if (kept.Length > 0)
                boxes = kept.Select(e => new AnnotationBox(e.ElementId, e.Bounds)).ToArray();
            Stage("snapshot");
        }
        GridSpec? grid = grid_columns > 0 || grid_rows > 0 ? new GridSpec(grid_columns, grid_rows) : null;

        // A-14: the glow must never be in a picture — hide it before every capture, whatever the switch says.
        _flash.Hide();

        // The process-level --screenshot-scale applies on top of the call's own scale.
        var result = await _screenshot.CaptureAsync(r,
            new CaptureOptions(fmt, max_width, max_height, scale * _options.Scale, quality, include_cursor, cursor, boxes, grid, _options.Profile, backend));
        Stage("capture");

        // ...and shown around what was just captured, so a person at the machine sees what the agent looked at.
        bool flashed = false;
        if (_options.Flash)
        {
            _flash.Show(r, TimeSpan.FromSeconds(3.5));
            flashed = _flash.IsVisible;   // report what happened (a host with no window station shows nothing), not what was asked
        }

        // Report what was ENCODED, not what was asked for — the image block must never lie
        // about the bytes it carries.
        bool isJpeg = result.Format == ImageFormat.Jpeg;
        var meta = new Dictionary<string, object?>
        {
            ["width"] = result.Width,
            ["height"] = result.Height,
            ["originalWidth"] = result.OriginalWidth,
            ["originalHeight"] = result.OriginalHeight,
            ["format"] = isJpeg ? "jpeg" : "png",
            ["backend"] = result.Backend,   // what produced the picture, not what was asked for
            ["coordinateSpace"] = "virtual-desktop",
            // Always: image (0,0) is this rect's origin, which is not (0,0) on a second monitor.
            ["region"] = new { x = r.X, y = r.Y, width = r.Width, height = r.Height },
            ["displays"] = monitors.Select(m => new { index = m.Index, x = m.X, y = m.Y, width = m.Width, height = m.Height, isPrimary = m.IsPrimary }).ToArray(),
            // Always, drawn or not: where the pointer is, and which of 'displays' it is on (-1 = none).
            ["cursor"] = new { x = cursor.X, y = cursor.Y, monitorIndex = CursorMath.MonitorIndexOf(cursor.X, cursor.Y, monitors) },
        };
        if (result.CursorDrawn is { } drawn)
            meta["cursorDrawn"] = drawn;
        if (selected is not null)
            meta["selectedDisplays"] = selected;
        if (target is not null)
        {
            meta["target"] = target.Json;
            if (target.Clipped) meta["targetClipped"] = true;
        }
        if (result.CoordinateScale != 1.0)
            meta["coordinateScale"] = result.CoordinateScale;
        // Absent when nothing needs translating: the model only sees the instruction when it applies.
        if (CoordinateNote(r, result.CoordinateScale) is { } note)
            meta["note"] = note;
        if (annotate)
        {
            meta["annotated"] = true;
            meta["annotations"] = result.AnnotationsDrawn;
        }
        if (grid is not null)
            meta["grid"] = new { columns = grid.Columns, rows = grid.Rows };
        if (flashed)
            meta["flash"] = true;
        if (profile is not null)
        {
            // The tool's own steps, then the service's finer-grained ones (a name clash: the service wins).
            foreach (var st in result.Stages ?? [])
                stageMs[st.Stage] = st.Ms;
            meta["stages"] = stageMs;
            _log.LogInformation("screenshot stages: {Stages}", string.Join(", ", stageMs.Select(kv => $"{kv.Key} {kv.Value} ms")));
        }

        var elementList = listed is null ? null : new TextContentBlock { Text = SnapshotRenderer.Render(listed) };

        if (toFile)
        {
            var dir = Path.Combine(Path.GetTempPath(), "WindowsMcp");
            Directory.CreateDirectory(dir);
            var filePath = Path.Combine(dir, $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss_fff}.{(isJpeg ? "jpg" : "png")}");
            await File.WriteAllBytesAsync(filePath, result.Bytes);
            meta["path"] = filePath;
            var fileContent = new List<ContentBlock> { new TextContentBlock { Text = JsonSerializer.Serialize(meta) } };
            if (elementList is not null) fileContent.Add(elementList);
            return new CallToolResult { Content = fileContent };
        }

        var content = new List<ContentBlock> { new TextContentBlock { Text = JsonSerializer.Serialize(meta) } };
        if (elementList is not null) content.Add(elementList);
        content.Add(ImageContentBlock.FromBytes(result.Bytes, isJpeg ? "image/jpeg" : "image/png"));
        return new CallToolResult { Content = content };
    }

    /// <summary>Half-open overlap in virtual-desktop pixels: an element touching the rect's far edge is outside.</summary>
    private static bool Overlaps(Bounds b, ScreenRegion r)
        => b.X < r.X + r.Width && b.X + b.Width > r.X && b.Y < r.Y + r.Height && b.Y + b.Height > r.Y;

    /// <summary>True for file output, false for inline; "base64" is the pre-A-7 alias of inline.</summary>
    private static bool ParseOutput(string output) => output.ToLowerInvariant() switch
    {
        "inline" or "base64" => false,
        "file" => true,
        _ => throw new ArgumentException($"Unknown output '{output}'; expected inline|file|base64"),
    };

    private static ImageFormat ResolveFormat(string format, bool toFile) => format.ToLowerInvariant() switch
    {
        "png" => ImageFormat.Png,
        "jpeg" => ImageFormat.Jpeg,
        // Inline goes to the model's context, where a JPEG is a fraction of the PNG's tokens;
        // a file on disk keeps the lossless default it always had.
        "auto" => toFile ? ImageFormat.Png : ImageFormat.Jpeg,
        _ => throw new ArgumentException($"Unknown format '{format}'; expected png|jpeg|auto"),
    };

    [McpServerTool(Title = "OCR the screen", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Run OCR on the screen and return the extracted text. Default: the primary display at full resolution; 'region' (virtual-desktop pixels) or 'display' narrow it exactly as for screenshot. format:json returns where the text is, for applications with no UI tree (a remote-desktop session, a game, a canvas): {coordinateSpace:'virtual-desktop', region (the rect read), target?, targetClipped?, textAngle?, text, lines:[{text, x, y, width, height, words:[{text, x, y, width, height, centerX, centerY}]}]}, every rectangle in virtual-desktop pixels, so a word's centerX,centerY go straight to click. Nothing recognised is text:'' and lines:[], not an error. On a dense or full screen format:json can be very large, so narrow it with window, element_id or region. textAngle appears only for tilted text, and then each rectangle is the upright bounding box around its tilted word or line, so it is larger than the text and may reach past region. For tilted text centerX,centerY are still the centres of the words." + TargetNote)]
    public async Task<string> Ocr(
        [Description(RegionDescription)] string? region = null,
        [Description(DisplayDescription)] string? display = null,
        [Description(WindowDescription)] string? window = null,
        [Description(HwndDescription)] long? hwnd = null,
        [Description(ElementIdDescription)] string? element_id = null,
        [Description("text (default): the recognised text only | json: the text plus every line and word with its rectangle and centre in virtual-desktop pixels")] string format = "text")
    {
        // Validated before any lookup or capture: a bad call must not cost either.
        bool json = format?.ToLowerInvariant() switch
        {
            "text" => false,
            "json" => true,
            _ => throw new ArgumentException($"Unknown format '{UiRefusals.Clip(format ?? "")}'; expected text|json", nameof(format)),
        };

        var (r, _, _, target) = await ResolveRegionAsync(region, display, window, hwnd, element_id);

        // A-14: the glow of an earlier capture is a top-most frame around that capture's rect; it
        // must not be read as text, or hide text, when this rect contains it. ocr shows none itself.
        _flash.Hide();
        if (!json) return await _ocr.ExtractTextAsync(r);

        var read = await _ocr.RecognizeAsync(r);
        var result = new Dictionary<string, object?>
        {
            ["coordinateSpace"] = "virtual-desktop",
            ["region"] = new { x = read.Region.X, y = read.Region.Y, width = read.Region.Width, height = read.Region.Height },
        };
        if (target is not null)
        {
            result["target"] = target.Json;
            if (target.Clipped) result["targetClipped"] = true;
        }
        // The engine reports -0 for straight text: only a real tilt is worth the caller's attention
        // (and a value that is not a number cannot be written as JSON at all).
        if (read.TextAngle is { } angle && double.IsFinite(angle) && angle != 0)
            result["textAngle"] = angle;
        result["text"] = read.Text;
        result["lines"] = read.Lines.Select(l => new
        {
            text = l.Text,
            x = l.Bounds.X,
            y = l.Bounds.Y,
            width = l.Bounds.Width,
            height = l.Bounds.Height,
            words = l.Words.Select(w => new
            {
                text = w.Text,
                x = w.Bounds.X,
                y = w.Bounds.Y,
                width = w.Bounds.Width,
                height = w.Bounds.Height,
                // The point to click: integer division, as click takes whole pixels.
                centerX = w.Bounds.X + w.Bounds.Width / 2,
                centerY = w.Bounds.Y + w.Bounds.Height / 2,
            }).ToArray(),
        }).ToArray();
        return JsonSerializer.Serialize(result);
    }
}
