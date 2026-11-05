# UI style guide — industrial HMI (spec §11.0)

Target look: IITM WaveGen / WinCC / Wonderware / Sysmac Studio. Plain, dense, functional (ISA-101).
Source of truth: `src/WaveFlume.App/Themes/Light.xaml`, `Dark.xaml` (colours) and `Controls.xaml` (styles).
Screenshots of every tab in both themes: `docs/screenshots/`.

## Colours

Both palettes define the same keys. Controls use only `DynamicResource` keys, so swapping the palette re-themes the whole app.

| Key | Light | Dark | Use |
|---|---|---|---|
| WindowBg | `#DCDCDC` | `#2B2B2B` | window background |
| PanelBg | `#EDEDED` | `#3A3A3A` | group boxes, tab content, header/status bars |
| InputBg | `#FFFFFF` | `#242424` | editable fields, grids, combo popups |
| ReadOnlyBg | `#E4E4E4` | `#313131` | read-only outputs, disabled inputs |
| BorderBrush | `#9A9A9A` | `#646464` | all 1 px borders |
| Text | `#1A1A1A` | `#E0E0E0` | normal text |
| TextMuted | `#505050` | `#B0B0B0` | hints, units notes |
| TextDisabled | `#8A8A8A` | `#7A7A7A` | disabled controls |
| ButtonBg / Hover / Pressed | `#E2E2E2` / `#D2D2D2` / `#B6B6B6` | `#4A4A4A` / `#575757` / `#6A6A6A` | buttons, group/tab headers |
| Selection | `#2F5F8F` | `#3F6E9E` | selection and keyboard focus only |
| Alarm | `#C00000` | `#D23C3C` | alarm, fault, BLOCKED, paddle ≥ 95 % |
| Warning | `#D99A00` | `#E0A82E` | warning, paddle ≥ 80 % |
| Ok | `#4C8A4C` | `#5A9A5A` | OK / running lamps only |
| EStopBg | `#CC0000` | `#CC0000` | E-STOP button (white text, 2 px black border) |
| GridLine | `#C8C8C8` | `#4C4C4C` | DataGrid lines |
| Plot bg / fg / grid | `#FFFFFF` / `#1A1A1A` / `#E0E0E0` | `#1C1C1C` / `#E0E0E0` / `#3A3A3A` | ScottPlot |

**Colour only means something.** Gray = normal; red = alarm/E-stop/fault; amber = warning; muted green = OK/running; blue = selection.
Every coloured state also has a text cue: alarm banner prefix `1 E-STOP` / `2 CRITICAL` / `3 WARNING`, gatekeeper `BLOCKED:` / `OK`, paddle `WARN` / `ALARM`.

**Plot palette** (fixed, red reserved for alarms): `#1F77B4 #FF7F0E #2CA02C #9467BD #8C564B #17BECF #7F7F7F #BCBD22`.
Oscilloscope option: black background, `#FFD800` traces, `#303030` grid. Traces 1 px solid, no smoothing.

## Typography

- Segoe UI 12 px (9 pt) everywhere; group headers and selected tabs SemiBold.
- Consolas for numbers: numeric inputs, readouts, live paddle values, statistics, clock.
- Numeric fields are right-aligned. Labels are terse and always carry units: `Water Depth (m)`.

## Layout

- 8 px grid: 8 px outer margins, 4 px between stacked fields, 8 px between group boxes.
- Flat, square: 1 px borders, corner radius 0–1 px. No gradients, shadows, glow, blur, animations, pill buttons or icons.
- `UseLayoutRounding`, `SnapsToDevicePixels`, `TextFormattingMode=Display` on the window.
- Titled frames (`GroupBox`): header band in ButtonBg with a 1 px rule under it.

## Controls and states

| Control | Normal | Hover | Pressed | Focus | Disabled |
|---|---|---|---|---|---|
| Button (min 72 × 24) | ButtonBg | ButtonHover | ButtonPressed | Selection border | TextDisabled text, GridLine border |
| BigButton (Start / Smooth Stop) | min height 48, 14 px SemiBold | same | same | same | same |
| E-STOP | EStopBg, white bold 16 px, min height 56 | no change (never animated) | border 4 px | — | never greyed out |
| TextBox / PasswordBox | InputBg | — | — | Selection border | ReadOnlyBg + TextDisabled |
| Validation error | 2 px Alarm border + tooltip with the message | | | | |
| ComboBox | custom template (stock one ignores Background in dark) | Selection border | | | TextDisabled |
| TabItem | ButtonBg | ButtonHover | — | — | — (selected: PanelBg + SemiBold) |

Only Start, Smooth Stop and E-STOP are large.

## Keyboard

- Access keys on tabs and main buttons (`_Calculate`, `_Start Wave`, `Wave _Paddle`, …).
- Natural tab order; Enter in a text box commits its value (`InputBehaviors.CommitOnEnter`).

## Permanent elements

- Header: title + EMERGENCY STOP, visible on every tab.
- Alarm banner under the header: highest-priority unacknowledged alarm + Acknowledge.
- Alarm / event log (expander above the status bar): priority, time, source, description, ack state.
- Status bar: controller (protocol, target, state, error count), servo state, run state, active alarms, clock, and an amber "Acceleration limit not configured" cell while a_max is unset.

## Confirmations

Start when the stroke exceeds 80 % of the clamp; % Signal Modification > 100 %; closing the window during a run; deleting a recipe.
