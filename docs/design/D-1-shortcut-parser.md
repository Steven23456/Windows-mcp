# D-1 — `shortcut` / `key` accept letters, digits, punctuation, and bare keys

**Checklist item:** [D-1](../upstream-parity-checklist.md#d-1--shortcut-and-key-reject-letters-digits-and-bare-keys--p1--s) ·
**Status:** implemented 2026-09-04 (build clean, tests green — see CHANGELOG [Unreleased]) · **Order:** independent of D-2/D-3; do it second. Effort: half a day.

## Problem

`src/WindowsMcp/Services/InputService.cs:16` `KeyMap` knows 19 names plus `f1`–`f12` (static ctor,
`:39`). `PressShortcutAsync` (`:126`) throws `Unknown key in shortcut` for any part not in that map
and rejects fewer than two parts; `PressKeyAsync` (`:117`) uses the same map. Of the checklist's
acceptance examples only `alt+f4` works today: `ctrl+c`, `ctrl+shift+s`, `win+r`, `ctrl+1` fail on
the letter/digit, bare `win` fails the two-part rule, and `key("a")` fails outright. These are the
chords an agent reaches for most.

## Decision

Extract a pure `internal static class ShortcutParser` (`src/WindowsMcp/Services/ShortcutParser.cs`)
that turns a token or chord into H.InputSimulator `VirtualKeyCode`s; `InputService` only sends.
`InternalsVisibleTo("WindowsMcp.Tests")` already exists, so tests can call it without injecting input.

**Token resolution, in order** (case-insensitive, trimmed):

1. **Named key / alias.** The existing 19 plus: `windows` `super` `cmd` `meta` → `LWIN`, `rwin` →
   `RWIN`; `control` `lctrl` → `CONTROL`, `rctrl` → `RCONTROL`; `option` `lalt` → `MENU`, `ralt` →
   `RMENU`; `lshift` / `rshift`; `return` → `RETURN`; `del` → `DELETE`; `ins` `insert` → `INSERT`;
   `prtsc` `prtscn` `printscreen` → `SNAPSHOT`; `capslock` `caps` → `CAPITAL`; `numlock` →
   `NUMLOCK`; `scrolllock` → `SCROLL`; `apps` `menu` `context` → `APPS`; `pause` `break` → `PAUSE`;
   `pgup` `pgdn`; `arrowup` `arrowdown` `arrowleft` `arrowright`; `numpad0`–`numpad9` and
   `num0`–`num9` → `NUMPAD0..9`; `add` `numpadplus`, `subtract` `numpadminus`, `multiply`, `divide`,
   `decimal`; `volumeup` `volumedown` `mute` → `VOLUME_*`, `playpause` → `MEDIA_PLAY_PAUSE`;
   `f1`–`f24` (extend the loop; the enum has all 24).
2. **Punctuation by name** — needed because `+` is the separator: `plus` → `'+'`, `minus` → `'-'`,
   `comma`, `period` `dot`, `slash`, `backslash`, `semicolon`, `quote`, `backtick` `grave`,
   `lbracket`, `rbracket`, `equals`. Each maps to a character and is then resolved by step 3, so it
   follows the user's keyboard layout.
3. **Single character.** `a`–`z` → `VK_A..VK_Z`, `0`–`9` → `VK_0..VK_9` (layout-independent). Any
   other printable character → `VkKeyScan(char)`: low byte = VK (cast to `VirtualKeyCode`; the enum
   values are the Win32 codes), high-byte bits 1 / 2 / 4 = Shift / Ctrl / Alt to **add** to the
   chord's modifiers; `-1` = not on this layout → unknown.
4. Otherwise `ArgumentException`:
   `Unknown key 'foo' in 'ctrl+foo'. Use a character, a-z, 0-9, f1-f24, or a named key such as enter, tab, esc, win, printscreen.`

**Chord rules.** Split on `+` (trim, drop empties — hence `plus` for the `+` key). All parts but
the last are modifiers; the last is the key. Modifiers are de-duplicated with order preserved (so
`shift+!` does not press Shift twice). **A single part is allowed** and is sent as `KeyPress` —
`win` opens Start, `esc` dismisses — matching upstream. `key` resolves exactly one token; if the
token is longer than one character and contains `+`, it throws telling the caller to use `shortcut`.

`VkKeyScan` is layout-dependent on purpose (a French user's `;` is where it is on *their*
keyboard). The parser takes `Func<char, short>` (default `PInvoke.VkKeyScan`) so unit tests are
deterministic and never touch the real layout.

## Changes

- `src/WindowsMcp/NativeMethods.txt`: add `VkKeyScan` (CsWin32 emits the `W` variant, as it does
  for `FindWindow` / `GetMonitorInfo` already listed there).
- New `src/WindowsMcp/Services/ShortcutParser.cs`:
  `internal readonly record struct Chord(VirtualKeyCode[] Modifiers, VirtualKeyCode Key);`
  `internal static Chord Parse(string shortcut, Func<char, short>? vkKeyScan = null);`
  `internal static VirtualKeyCode ResolveKey(string token, Func<char, short>? vkKeyScan = null);`
  (`ResolveKey` may also return extra modifiers for shifted characters — model as
  `ResolveToken(token) → (VirtualKeyCode Key, VirtualKeyCode[] ImpliedModifiers)` and have
  `Parse` merge them.)
- `src/WindowsMcp/Services/InputService.cs`: delete `KeyMap` and the static ctor.
  `PressKeyAsync` → `_sim.Keyboard.KeyPress(ShortcutParser.ResolveKey(key))` (implied Shift for a
  shifted character becomes a `ModifiedKeyStroke`). `PressShortcutAsync` → `var c = Parse(shortcut);`
  `c.Modifiers.Length == 0 ? KeyPress(c.Key) : ModifiedKeyStroke(c.Modifiers, c.Key)`.
- `src/WindowsMcp/Tools/InputTools.cs` descriptions:
  `key` — *"Press one key: a character (a, 7, /), f1-f24, or a name (enter, tab, esc, backspace,
  delete, up/down/left/right, home, end, pageup, pagedown, win, printscreen). For chords use shortcut."*
  `shortcut` — *"Press a chord: ctrl+c, ctrl+shift+s, win+r, alt+f4, ctrl+1. A single key such as
  win (opens Start) also works. Parts are joined with '+'; write plus for the + key."*
- `skills/windows/SKILL.md` §4: one sentence — chords like `ctrl+c` / `win+r` go through
  `shortcut`, a single key through `key`.
- `docs/architecture/COMPONENTS.md:456` (InputService): mention `ShortcutParser`.

## Tests

New `tests/WindowsMcp.Tests/Services/ShortcutParserTests.cs`, `[Trait("Category","Unit")]`, no
input injection, a fake `vkKeyScan` (`'+'` → `OEM_PLUS | shift`, `'/'` → `OEM_2`, unknown → `-1`):

- Table-driven happy path: `ctrl+c` → `[CONTROL]`, `VK_C`; `ctrl+shift+s`; `win+r` → `[LWIN]`,
  `VK_R`; `alt+f4` → `[MENU]`, `F4`; `ctrl+1` → `VK_1`; `win` → `[]`, `LWIN`; `esc`; `f24`;
  `printscreen` → `SNAPSHOT`; `numpad5`; `CTRL + C` (case and spaces) equals `ctrl+c`.
- Shift merging: `ctrl+plus` → `[CONTROL, SHIFT]`, `OEM_PLUS`; `shift+plus` de-dups to one `SHIFT`.
- Errors: `ctrl+foo` → message contains `'foo'` and `'ctrl+foo'`; `""` and `"+"` → "empty";
  `ResolveKey("ctrl+c")` → message mentions `shortcut`; a char the fake layout lacks → unknown.
- Existing `InputServiceTests.PressShortcutAsync_throws_on_invalid_format` (`not+a+real+key`) must
  still throw — tighten it to assert the message names `'not'`.

## Docs / CHANGELOG

One bullet under `### Fixed`. No tool-count change. Tick D-1 in the checklist and board.

## Done when

Checklist bar, verbatim: `shortcut("ctrl+c")`, `("ctrl+shift+s")`, `("win+r")`, `("alt+f4")`,
`("win")`, `("ctrl+1")` all succeed against a live Notepad; `key("a")` types `a`; the error for a
bad token names the token.
