# Testing & Inspection Tools — Design Plan

**Date**: 2026-03-07
**Target Version**: 0.5.0
**Scope**: 10 new MCP tools + 1 shared helper

## Element Lookup Strategy

All tools that target a UI element use **text search** as primary lookup (matching element Name) with **coordinates** as fallback. Text search is more stable than label indices which change between State-Tool calls.

Shared helper `find_control_by_search(search)` returns raw `uiautomation.Control` for pattern access.
Coordinate lookup uses `uiautomation.ControlFromPoint(x, y)`.

## Tools

### Phase 1 — Foundation

**1. Get-Element-Property-Tool**
- Params: `search: str`, `loc: tuple[int,int] = None`
- Reads: Name, ControlType, IsEnabled, IsOffscreen, BoundingRectangle, IsKeyboardFocusable
- Patterns: ValuePattern, TogglePattern, SelectionItemPattern, RangeValuePattern
- Returns: formatted property string

**2. Get-Text-Tool**
- Params: `search: str`, `loc: tuple[int,int] = None`
- Priority: ValuePattern.Value → Name → LegacyIAccessible.Value
- Returns: text content string

**3. Assert-Element-Tool**
- Params: `search: str`, `property: str`, `expected: str`, `loc: tuple[int,int] = None`
- Properties: exists, enabled, disabled, checked, unchecked, value, name, visible, focused
- Returns: "PASS: ..." or "FAIL: expected X, got Y"

### Phase 2 — Pattern Interaction

**4. Checkbox-Toggle-Tool**
- Params: `search: str`, `loc: tuple[int,int] = None`, `target_state: Literal["on","off","toggle"] = "toggle"`
- Uses TogglePattern, click fallback
- Returns: before/after state

**5. Select-Option-Tool**
- Params: `search: str`, `option: str`, `loc: tuple[int,int] = None`
- Uses ExpandCollapsePattern + SelectionItemPattern, click fallback
- Returns: confirmation

**6. Focus-Tool**
- Params: `search: str`, `loc: tuple[int,int] = None`
- Calls control.SetFocus()
- Returns: confirmation with element details

### Phase 3 — Independent Tools

**7. Hover-Tool**
- Params: `loc: tuple[int, int]`, `duration: float = 1.0`
- Moves cursor, holds position, reports element under cursor
- Returns: element name (useful for tooltips)

**8. Compare-Screenshot-Tool**
- Params: `baseline: str` (file path), `region: tuple[int,int,int,int] = None`, `threshold: float = 5.0`
- Pixel comparison via PIL, saves baseline if none exists
- Returns: "MATCH (diff: X%)" or "MISMATCH (diff: X%)"

### Phase 4 — Complex Tools

**9. Get-Table-Tool**
- Params: `search: str`, `loc: tuple[int,int] = None`, `max_rows: int = 50`
- Uses GridPattern, fallback to child traversal
- Returns: markdown table

**10. Record-Replay-Tool**
- Params: `action: Literal["replay","save","load","list"]`, `name: str = None`, `steps: list[dict] = None`
- Steps format: `{"tool": "Click-Tool", "params": {"loc": [100, 200]}}`
- Saves to recordings/ dir as JSON
- Returns: execution results

## Implementation Order

| Phase | Tools | Depends On |
|-------|-------|------------|
| 1 | find_control helper, Get-Element-Property, Get-Text, Assert-Element | nothing |
| 2 | Checkbox-Toggle, Select-Option, Focus | Phase 1 helper |
| 3 | Hover, Compare-Screenshot | nothing (parallel with Phase 2) |
| 4 | Get-Table, Record-Replay | Phase 1 helper |

## No New Dependencies

All tools use existing: uiautomation, PIL, pyautogui, humancursor, json (stdlib).
