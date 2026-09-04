# A-7 — `screenshot`: return the image as MCP image content

**Checklist item:** [A-7](../upstream-parity-checklist.md#a-7--return-the-screenshot-as-mcp-image-content--p1--s) ·
**Roadmap:** [A-roadmap](A-roadmap.md) phase 1, first item ·
**Status:** implemented 2026-09-04 (build clean, 476/476 headless + 4/4 desktop-only tests green —
see CHANGELOG [Unreleased]) ·
**Effort:** ~½ day including the RED/GREEN passes.

## Problem

`ScreenTools.Screenshot` returned a JSON **string**: `output:"file"` (the default) gave a path
the model could not look at, `output:"base64"` put the bytes inside a string field no client
renders. Either way the model needed a second tool to see the picture, and in practice never did.
Upstream returns an MCP `ImageContent` block and the model sees the screen directly.

## Decision

- The tool returns `Task<CallToolResult>` (SDK 2.2.0 discovers it unchanged — proven by
  `Screenshot_tool_is_still_discovered_with_a_CallToolResult_return_type`, which retires the
  roadmap's risk #1). Content is **one text block** with a single JSON metadata object, then,
  for inline output, **one image block** built with `ImageContentBlock.FromBytes` (`Data` is
  base64 text as UTF-8 bytes in this SDK; the factory does the encoding, assigning a string does
  not compile).
- `output`: `inline` (**new default**, roadmap C2) | `file` | `base64` (alias of inline, kept one
  release). `format`: `png` | `jpeg` | `auto` (**new default**: jpeg inline, png file — the
  inline image goes into the model's context, where a JPEG is a fraction of the PNG's tokens;
  a file on disk keeps the lossless default it always had).
- Metadata: `{width, height, format, coordinateSpace:"virtual-desktop", region?, path?}`.
  `region` only when one was given, `path` only for file output — absent, not null (roadmap
  C1/A-7). The reported `format` and mime type come from what the service **encoded**
  (`ScreenshotResult.Format`), not the request, so the image block can never lie about its
  bytes; today the service echoes the request, the rule becomes load-bearing with A-10's
  backend fallback.
- Every argument is validated **before** the capture. Unknown `output`/`format` throw
  `ArgumentException` naming the choices; matching is case-insensitive, not trimmed; empty
  string is unknown; `jpg` is rejected (it is the file extension, `jpeg` is the format).

## Changes

- `Tools/ScreenTools.cs` — `Screenshot` rewritten as above; `ParseOutput` / `ResolveFormat`
  helpers; `[Description]` states the content shape and coordinate space. `Ocr` untouched.
- No interface or DTO change; `IScreenshotService` is as it was.

## Tests (test-agent RED → GREEN)

| # | Requirement | Test(s) | Category |
|---|---|---|---|
| R1 | Default output inline; exactly text-then-image | `Screenshot_default_output_is_inline_text_then_image` | Unit |
| R2 | Image block carries the captured bytes; mime matches | `Screenshot_inline_image_block_carries_the_captured_bytes` | Unit |
| R3 | `base64` ≡ `inline` | `Screenshot_base64_output_is_identical_to_inline` | Unit |
| R4 | `file`: one text block, path under `%TEMP%\WindowsMcp`, bytes written, `png`/`jpg` extension | `Screenshot_file_output_*` (4) | Unit |
| R5 | Unknown `output` throws naming `inline\|file\|base64`, before any capture; case-insensitive | `Screenshot_unknown_output_throws_naming_the_choices` (4), `*_output_matching_is_case_insensitive` (4) | Unit |
| R6 | `auto` → jpeg inline / png file; explicit honoured and passed to `CaptureAsync`; unknown throws | `Screenshot_format_*` (18) | Unit |
| R7 | Metadata fields; `region` absent unless given | `Screenshot_*metadata*` (6) | Unit |
| R8 | Region parsing unchanged, reaches `CaptureAsync`, bad arity throws without capture | `Screenshot_passes_the_parsed_region_to_capture` (3), `Screenshot_invalid_region_throws_and_never_captures` (3) | Unit |
| R9 | `ocr` unchanged | `Ocr_*` (2) | Unit |
| R10 | Old base64 test kept against the new contract | `Screenshot_returns_base64_png` | Unit |
| E1 | Report the encoded format, not the requested one | `Screenshot_mime_and_metadata_follow_the_encoded_result_not_the_request` | Unit |
| E2 | Tool still discovered; schema keeps `region/format/output`; refusals cross HTTP | `HttpTransportTests.Screenshot_*` (2) | Integration |
| E3 | Image block survives the real HTTP transport | `HttpTransportScreenshotImageTests.Screenshot_returns_an_image_content_block_over_http` | UIAutomation |
| N1 | Inline metadata never carries `path` | `Screenshot_inline_metadata_does_not_carry_a_path` | Unit |
| N2 | The real service encodes JPEG (the new default) | `ScreenshotServiceTests.CaptureAsync_jpeg_returns_jpeg_bytes` | UIAutomation |
| N3 | Default call returns real JPEG over HTTP | `HttpTransportScreenshotImageTests.Screenshot_default_format_is_jpeg_over_http` | UIAutomation |

Coverage of `ScreenTools`: 100 % line, 100 % branch. Bite check: six one-line breaks (auto
inversion, requested-vs-encoded, capture-before-validation, block order, alias, `path` leak) each
caught by at least one test; the `path` leak was caught by nothing until N1 was added — 100 %
coverage did not see it, which is the reason the bite check exists.

`HttpTransportScreenshotImageTests` is a **separate class** from `HttpTransportTests`: vstest's
`Category!=UIAutomation` filter does not exclude a test that also carries a second `Category`
value, so a desktop-only test inside an `Integration` class would run in headless sweeps.

## Deviations and follow-ups

- **JPEG quality is 90, not the 85 the roadmap said.** 90 is the pre-existing encode constant
  in `ScreenshotService`; A-9 owns the `quality` parameter and will set the inline default there.
- **`BuildHttpApp` has no service-substitution seam**, so the image round-trip has to capture
  the real screen. An optional `Action<IServiceCollection>` on `BuildHttpApp` would let A-8/A-9
  prove their transport surface headless. Small; do it in A-9 alongside the next transport test.
- **Extract the encode step** (`internal static Encode(SKBitmap, ImageFormat, quality)`) so
  format/quality/downscale are unit-testable — A-9 does this, it is rewriting that method anyway.
- **Behaviour change to migrate:** the old `data_base64` JSON key is gone; `output:"file"` must
  now be asked for explicitly; the default inline image is JPEG.
