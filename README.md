# PSProcLasso

A native Windows real-time system monitor and process manager with an optional
PowerShell terminal edition. The included `PSProcLassoGUI.exe` is ready to run;
its complete WinForms source is in `PSProcLassoGUI.cs`.

It shows every running process with live **CPU %, RAM working set, GPU engine %,
and dedicated VRAM**, ranked by the resource you select. The default one-PID
view is directly comparable with Process Lasso and Task Manager; an optional
**Group apps** checkbox aggregates related processes when that view is useful.
The GUI uses stable in-place rows and double buffering, so live updates remain
calm instead of blinking.

The adaptive performance mode continuously measures the 20 heaviest application
groups by CPU, RAM, and GPU, removes app-owned resource caps, expands safe
workloads to all logical processors, disables execution-speed throttling where
Windows permits it, and preserves protected Windows scheduling decisions.

```
PSProcLasso v1.0  |  CPU 23.4%  RAM 8.1/16.0 GB (51%)  GPU 12.0%  VRAM 1.2/8.0 GB  |  14:30:22
Available RAM: 7.9 GB   Standby cache: 2.3 GB   ProBalance: ON   GPU sampling: ON   Filter: (none)   Sort: CPU
    PID      NAME                      CPU%     MEMORY     GPU%        VRAM    PRIORITY      AFFINITY
   1234  chrome                       12.3   1.2 GB      3.4    512 MB      Normal       0,1,2,3,4,5
   5678  msedge                        9.8   800 MB      1.2    340 MB      Normal       0,1,2,3,4,5
   ...
[q]uit [s]ort [f]ilter [p]riority [a]ffinity [l]imit [k]ill [w]atchdog [b]alance [i]nfo [r]ules [e]exec [g]pu [?]help
```

## Requirements

* Windows 10 / Windows 11 (GPU counters require Windows 10 1709+; if your GPU exposes none,
  the app degrades gracefully to CPU/RAM and shows GPU `n/a`)
* Windows PowerShell 5.1 (ships with Windows) **or** PowerShell 7 (`pwsh`)
* A normal terminal window (Windows Terminal, conhost, VS Code terminal, etc.)

## Run it

From the folder containing these files, in **PowerShell**:

```powershell
.\run.ps1
```

or directly (no execution-policy change needed):

```powershell
powershell -ExecutionPolicy Bypass -File .\PSProcLasso.ps1
```

Switches:

| Switch | Meaning |
|---|---|
| `-RefreshMs 2000` | monitor print period in milliseconds (default 1000); the interactive TUI samples continuously at ~200ms regardless |
| `-Monitor` | non-interactive: prints a snapshot table every refresh (great for `watch` style logging) |
| `-SelfTest` | samples everything once, prints a diagnostic table, exits with 0 — verifies "no exceptions" |
| `-UITest` | runs every render view (all 5 sort orders, help, rules, details) plus every input path headlessly, exits with 0 |
| `-Snapshot` | prints one JSON snapshot of all processes + system totals to stdout, then exits — used by the live dashboard and scripts |
| `-MaxRows 20` | rows shown per snapshot in `-Monitor` mode |
| `-NoAnsi` | plain text, no colors |

## PSProcLassoGUI — the real GUI version

There is also a **native Windows GUI** compiled from C# (WinForms, .NET Framework 4.x) —
no runtime install, no dependencies, runs on any Windows 10/11 machine. It uses the same
sampling core as the TUI (kernel thread-time CPU, `PerformanceCounter` objects held open
for ~1 ms reads, GPU/VRAM counters at ~1 Hz, 150 ms warm-up and ~500 ms steady
CPU/RAM samples) and **shares the same
`rules.json`** — limits, priorities, watchdog and ProBalance rules you set in one app apply
in the other.

**Run it — double-click, or from a terminal:**

```powershell
cd F:\study\projects\SystemMonitor\PSProcLasso
.\PSProcLassoGUI.exe
```

What you get:

* Dark real-time dashboard: animated CPU / RAM / GPU / VRAM meter bars with load colors
  (green → yellow → red), live system totals, and every process ranked by CPU
  / RAM / GPU / VRAM / name / PID — **click any column header to re-rank instantly**, or use the three
  **CPU / RAM / GPU sort buttons** in the header (every click sorts that metric descending,
  highest usage first; the active button glows cyan). Column headers can still toggle
  direction when an ascending view is intentionally needed.
* The default **Processes** view always shows one stable row per PID, making the resource
  leader easy to verify against other Windows monitors. Enable **Group apps** to fold
  exact-name workers and known families such as WSL and Windows Script Host into an
  aggregated application row while retaining every member PID for copy, details, and actions.
* **Instant search** — use the always-visible search box or press `Ctrl+F`, then type any
  combination of process/application name, PID, executable path, priority, affinity, or
  active control. Tokens are matched together against the current view. Press
  `Esc` or the clear button to restore the complete live table immediately.
* **Safe whole-system optimization** — the **OPTIMIZE** button reviews every observed PID
  and records an explicit decision. It never terminates a process, never applies High or
  Realtime priority, preserves Windows-critical and Session 0 processes, visible/foreground
  applications, AI-session infrastructure, and existing user or Process Lasso priority
  rules. Only recognized noninteractive background workers at default priority can be
  moved to Below Normal. Optimizer-owned persistent rules are marked, reversible, and
  preserve unrelated CPU/RAM/GPU/watchdog settings.
* **Adaptive top-20 performance mode** — while enforcement is active, the monitor
  continuously takes the union of the 20 heaviest applications by CPU, RAM, and GPU.
  A five-minute rolling window keeps bursty heavy applications optimized instead of
  dropping them because of a single quiet sample, while stale selections expire.
  For every member process it removes PSProcLasso CPU/GPU/RAM caps, restores all-core
  affinity, disables Windows execution-speed throttling where the OS permits it, and
  keeps safe user workloads at Above Normal priority. Kernel, Session 0, and protected
  or self-managed application scheduling stays under Windows/application control. The
  selected application rules are persisted at a calm 30-second cadence with reversible
  original settings, so new instances receive the same performance policy immediately.
* The lightweight monitor process corrects inherited Idle, Below Normal, or Normal
  launcher priority to **Above Normal**, keeping sampling and the UI responsive under
  heavy load. It never promotes itself to Realtime, and inherited High stays High.
* Per-process actions cover priority (Idle → Realtime), core affinity, **Windows Job Object
  hard CPU and RAM caps** where the target permits assignment, a whole-process **GPU duty
  limit**, kill, watchdog, ProBalance, and deep details. In grouped mode, actions expand
  to every member process.
* **Bulk AI copy** — press and hold the left mouse button, drag across rows, and keep
  dragging at an edge (or use the wheel) to auto-scroll and turn the whole range blue.
  Ctrl/Shift-selection remains available. Then press `Ctrl+C` or click
  **COPY** to place a tab-separated snapshot on the clipboard with system totals plus
  visible-row count, every member PID, CPU, RAM, private commit, GPU, VRAM, priority,
  affinity, and active controls.
  `Ctrl+A` / **SELECT ALL** selects the complete visible process table. Clipboard writes
  use bounded retries so a brief lock by another application does not lose the copy.
* **System tray** — minimize (or click X) and the app hides to the tray while limits,
  watchdog and ProBalance **keep enforcing in the background**. Right-click the tray
  icon for quick actions: **Sort by** (CPU/RAM/GPU/VRAM/name/PID), **Limit presets**
  (cap the top CPU/GPU process at 50%, trim the top RAM process, remove limits), per-rule
  **Watchdog on/off**, **ProBalance** and **GPU sampling** toggles, and a real **Exit**.
  Double-click the tray icon to restore the window.
* **Optional silent persistence after Windows restart** — off unless manually enabled
  from the tray menu. When enabled, it installs a hidden,
  least-privilege logon task. It starts the GUI binary in background mode with no
  terminal window, applies saved rules to every matching process instance, and ignores
  duplicate launches while allowing a normal launch to reveal the existing window.
  Least privilege prevents user-writable rules or binaries from becoming an elevation path.
* Persistent rules shared with the TUI. Concurrent GUI/background updates are serialized,
  writes are atomic, and a last-known-good backup repairs a missing or corrupt primary
  rules file without losing independent rule changes.
* Background failures are caught and shown on the status bar. Unexpected enforcer exits
  use a bounded rolling restart budget with exponential backoff, preventing a rapid
  restart storm while Task Scheduler retains slower durable recovery.

Build it yourself (only needed if you edit `PSProcLassoGUI.cs`):

```powershell
& "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe" -nologo -optimize+ `
  -target:winexe -platform:anycpu -win32icon:PSProcLassoGUI.ico `
  -out:PSProcLassoGUI.exe `
  -r:System.dll -r:System.Core.dll -r:System.Drawing.dll -r:System.Windows.Forms.dll `
  -r:System.Management.dll -r:System.Web.Extensions.dll `
  PSProcLassoGUI.cs
```

Sanity checks (all headless, all print `RESULT: OK` with zero exceptions):

| Switch | What it proves |
|---|---|
| `--selftest` | 5 s of real sampling — CPU / GPU / process count / RAM / VRAM / available / standby, per cycle |
| `--accuracycheck` | repeated independent Windows readings compared with the sampler's top CPU, RAM, and GPU process sets |
| `--uicheck` | hidden full-GUI construction and paint check, including default one-PID rows, optional grouping, honest unavailable-GPU markers, embedded executable icon, click-drag range selection, correct visible CPU/RAM/GPU leaders, stable row reuse, and bulk copy |
| `--timing` | measured pass times: fast cycle, GPU pass, and the GPU pass-to-pass wall gap |
| `--startup` | time to first process table, first CPU/RAM totals, and first GPU data |
| `--cadence` | steady-state CPU/RAM refresh is ~500 ms and GPU refresh is ~1 s |
| `--report [path]` | **all eight reports in one run** — includes independent accuracy, sampling cadence, startup, UI paint, enforcement, and recovery checks, then writes a consolidated verdict (`ALL REPORTS PASSED` / `FAIL`); defaults to `%TEMP%\pspl-gui-report.txt`, or pass your own path |
| `--ai-snapshot [path]` | writes `psproclasso.snapshot.v1` JSON with live system totals and grouped application metrics; defaults to `%TEMP%\psproclasso-snapshot.json` |
| `--optimize-plan [path]` | dry-run classification for every observed PID; writes `psproclasso.optimization.v1` JSON and changes nothing |
| `--optimize-apply [path]` | applies only the safe plan, atomically merges optimizer-owned persistent rules, and writes a per-PID receipt |
| `--optimize-restore [path]` | restores priorities owned by the optimizer without removing unrelated user limits or rules |
| `--top20-plan [path]` | measures the live top 20 CPU, RAM, and GPU applications and writes a no-change `psproclasso.top20-performance.v1` plan |
| `--top20-apply [path]` | applies the measured adaptive performance policy, removes managed limits, and writes a per-PID JSON receipt |
| `--enforcementcheck` | disposable child-process proof of hard CPU/RAM assignment, GPU duty cycling, saved-rule persistence, removal, and resume |
| `--backgroundguardcheck` | hidden crash-recovery proof: kills a disposable background instance, verifies its companion relaunches it invisibly, then verifies a clean exit stays stopped |

Background-recovery diagnostics use an isolated per-run mutex/event scope, so the full
report can exercise crash recovery while the real GUI remains open.

Optimization apply is idempotent: when every process is already correctly configured,
it still writes a fresh receipt but does not rewrite `rules.json` or rotate its backup.

**Visual design:** a polished dark theme throughout — gradient header and status panels with
hairline separators, redesigned meters (rounded gradient bars with a soft glow, load-colored
status dot, bold caption, live value), **Task-Manager-style in-cell usage bars** behind the
CPU %, MEMORY, GPU % and VRAM values of every row, a cyan accent caret on the selected row,
gradient column headers with hairlines, an accent-highlighted active sort button, and a
multi-resolution green/cyan monitor icon embedded in the executable for Explorer,
taskbar pinning, the title bar, and the notification area.

**Performance & startup guarantees (measured on a 100%-CPU-loaded machine):**

* The window appears with the full process table in **under 1 second** (735–931 ms
  measured); CPU/RAM totals land in **under 2 seconds** (1.77–1.95 s) — the sampler
  starts *before* the UI finishes building, and GPU counter creation runs on its own
  thread so it can never delay the table or totals. GPU data arrives once Windows
  finishes creating the ~600 GPU counters (~3.5 s at 100% CPU, ~1 s on an idle machine;
  the meter shows `initializing…` until then).
* **Refresh cadence:** CPU/RAM/process data is sampled every 500 ms and GPU data about
  every 1 second. The visible table commits the newest coherent snapshot once per second,
  reducing visual churn without slowing collection. GPU counter instances are reconciled on every GPU pass,
  so newly GPU-active applications appear in the next update and stopped applications
  clear instead of retaining stale usage.
* **Correct GPU semantics:** each process shows its busiest GPU engine, matching the
  familiar Task Manager interpretation instead of adding unrelated 3D/copy/video
  engines into impossible values above 100%. The total meter uses the busiest physical
  engine across the system. Windows rate counters are explicitly warmed before their
  first value is published, so their primer sample remains `initializing...` instead of
  appearing as a false zero. Missing or stale GPU data is shown as `--`, never as a
  fabricated `0%`; a fresh valid zero remains `0%`. Multi-selection reports `GPU top` rather than adding
  unrelated per-process engine peaks into a misleading aggregate percentage.
  A grouped application sums its members on each physical engine and then takes that
  application's busiest engine, avoiding both under-counting and impossible totals.
* RAM totals come directly from `GlobalMemoryStatusEx`; process RAM is the live working
  set and private commit is included in copied/details output. Dedicated GPU memory uses
  Windows' live dedicated-usage counters without presenting committed bytes as a false
  physical-capacity denominator.
* **Always-correct descending ranking:** selecting CPU, RAM, GPU, or VRAM immediately
  places the current highest user first. Cells update in place when the order is
  unchanged, preserving selection and scroll position without weakening ranking
  correctness with a deadband.
* **Readable small percentages without false zeroes:** genuine zero usage is shown as
  `0%`, unavailable GPU usage is `--`, nonzero usage below `0.01%` is shown as
  `<0.01%`, sub-1% usage keeps two decimal places, and larger values use one decimal place.
* **Calm live updates:** the process list is double-buffered and reuses existing rows.
  Changed cells are committed as one buffered frame, and a live re-sort moves only the
  rows whose order changed. This avoids the visible clear-and-rebuild blink while still
  updating values and rankings at their normal cadence.
* **Stability architecture:** the sampler loop is fully exception-guarded and stamps a
  heartbeat every cycle; the UI timer detects a wedged/dead sampler (or GPU thread) and
  auto-restarts it, so monitoring never silently stops. Sampler threads run at
  AboveNormal priority and the UI thread does too, so the app stays live even at 100%
  CPU. A separate hidden companion also relaunches a background-mode enforcer after an
  unexpected process death, while a clean-exit marker prevents unwanted relaunch after
  a deliberate exit. Verified: 60–75 s live runs and every headless mode log zero
  exceptions.

## Controls

| Key | Action |
|---|---|
| `↑ ↓ PgUp PgDn Home End` | move selection (the view scrolls to keep it visible; `↑`/`↓` wrap around at the edges) |
| `Tab` / `Shift+Tab` | **next / previous — works everywhere**: next/prev process row, next/prev menu item, next/prev rule, and in any typing prompt `Tab` submits / `Shift+Tab` cancels |
| **Mouse** | **click a column header** (CPU / RAM / GPU / NAME / PID) to rank by it; **press and drag across rows** to select a blue range with edge/wheel auto-scroll; **double-click** for details; **right-click** for actions |
| `← →` | cycle sort metric |
| `1` `2` `3` | **one-key sort**: `1` = ranked by CPU, `2` = ranked by RAM, `3` = ranked by GPU — instant, no menu |
| `Esc` | open the main menu (Sort / Priority / Affinity / Limit / Kill / Watchdog / View / ProBalance / GPU / Quit) |
| `/` | **jump** to any process by typing its name (Enter confirms, Esc cancels) |
| `s` | sort menu: CPU / RAM / GPU / Name / PID — arrows + Enter, or `1`–`5` (letters `c`/`r`/`g`/`n`/`p` also work) |
| `f` | filter by name substring or exact PID (`Esc` clears) |
| `p` | priority menu: `0` Idle `1` BelowNormal `2` Normal `3` AboveNormal `4` High `5` Realtime |
| `a` | set CPU affinity, e.g. `0-3,5` (Enter = all cores) |
| `l` | **limit menu**: pick CPU % / GPU % / RAM MB for the selected process (see below) |
| `k` | kill selected process (Yes/No menu) |
| `w` | toggle **watchdog** (auto-restart) rule for the selected process |
| `i` / `Enter` | deep details: path, threads, handles, parent PID, command line, applied rule + limits |
| `v` | view menu: main list / details / rules / help |
| `r` | rules view — `↑ ↓` select a rule, `x` delete, `w` toggle watchdog, `Esc` back |
| `x` (main view) | delete the rule for the selected process (Yes/No menu) |
| `b` | toggle **ProBalance** (auto-demote background CPU hogs when the system is loaded) |
| `e` | launch a program or command |
| `u` | remove **all** limits from the selected process |
| `g` | toggle GPU sampling |
| `q` / `Ctrl+C` | quit |

**Every menu is fully keyboard-navigable:** `↑ ↓` / `Home` / `End` move the
highlight, `Enter` applies, `Esc` (or `←` in a submenu) goes back, and each item
has a shortcut letter shown in parens that applies it directly. Menus stay
visible above the footer no matter the window size.

**The interface** uses live meters and load colors, system-monitor style: the header
shows real-time `█` bars for total CPU / RAM / GPU (filled in green → yellow → red by
load), and the ranked table colors each process's CPU/GPU percentage by load, dims
PIDs, and marks the selected process with `▸`. Badge letters next to a name flag
managed processes: `*` = a limit is active, `W` = watchdog, `P` = ProBalance-demoted.
With `-NoAnsi` the same layout degrades to ASCII (`#`/`-` bars, `>` marker).

**Setting a limit is one flow for any resource:** select the process → `l` → pick
CPU / GPU / RAM → type one number → Enter. `1` = CPU %, `2` = GPU %, `3` = RAM MB
(press `1`/`2`/`3` directly in the menu to skip the arrows). `0` removes that limit;
`u` removes every limit at once. Each limit is applied live the moment you press Enter
and, if you answer Yes, persisted as a rule for that process name.

After `p` / `a` / `l` you are asked whether to **persist the change as a rule** for that
process — persisted rules are re-applied automatically whenever that process starts, and
saved to JSON so they survive restarts.

## Process Lasso feature map

| Process Lasso capability | PSProcLasso equivalent |
|---|---|
| Live ranked process list | Full ranked table, sort by CPU/RAM/GPU/name/PID (`1`/`2`/`3` or `s` menu) |
| Per-process CPU % / RAM / GPU % / VRAM | exact, from kernel thread times + Windows GPU performance counters |
| Set process priority | `p` (Idle → Realtime) |
| Set CPU affinity (pin to cores) | `a` (single cores, ranges `0-3`, lists `0,2,5`) |
| Per-process CPU limiting | GUI: Windows Job Object hard cap when assignment is allowed; priority fallback is labeled when Windows refuses |
| Per-process GPU limiting | `l` → GPU — duty-cycle throttle: the process is suspended part of each second (caps both GPU and CPU use) |
| Per-process memory limiting | GUI: Windows Job Object process-memory cap when assignment is allowed; working-set trim fallback is labeled when Windows refuses |
| ProBalance automatic priority balancing | `b` — under load (≥80% CPU) background non-windowed processes are briefly demoted, then restored |
| Watchdog (restart dead processes) | `w` — restarts the app if it exits, with restart counter + cooldown |
| Persistent rules (priority/affinity/limits) | saved to `rules.json`, applied on process start |
| Process termination | `k` |
| Launch applications | `e` |
| Process detail inspection | `i` — path, threads, handles, parent PID, command line |
| Standby list / RAM readout | available + standby cache shown live (shown, not cleaned — cleaning requires admin/driver) |
| I/O priority control | not exposed — Windows has no stable managed API for it (Process Lasso uses a driver) |

**Honest differences from Process Lasso:** the GUI uses native Windows Job Objects for
hard CPU-rate and process-memory caps, but Windows can refuse assignment for protected
processes or processes already held in an incompatible job. Those cases are reported and
use a labeled priority/working-set fallback. GPU limiting is necessarily a whole-process
duty-cycle throttle (rapid suspend/resume): it cuts GPU and CPU execution together and can
make interactive apps less smooth. Windows does not expose a universal per-process hard
GPU percentage API or stable managed I/O-priority API; Process Lasso can provide additional
driver-backed controls.

## Where things live

```
%USERPROFILE%\.psproclasso\
    rules.json    persisted priority / affinity / CPU-limit / watchdog rules
    prefs.json    last sort key, filter, ProBalance & GPU settings
    events.log    full activity + error log (every caught exception)
```

## Performance & accuracy notes

* While the dashboard is visible, per-process CPU % and RAM are sampled every
  500 ms, GPU % / VRAM every ~1 second, and the table paints one coherent latest
  frame each second. Hidden/tray enforcement
  avoids repainting the invisible table, samples CPU/RAM every second, and
  samples GPU plus reapplies the adaptive top set every 5 seconds. Showing the
  window immediately restores the full visible cadence.
  Instead of rebuilding the Windows counter query each pass (which costs ~2 s
  per call), the app keeps `PerformanceCounter` objects open and reads them in
  ~1 ms, so the screen ticks continuously instead of freezing between refreshes.
* Per-process CPU is computed from kernel thread-time deltas over the refresh
  window (0.1% precision); total CPU comes from the `\Processor(_Total)\% Processor
  Time` counter, so it matches Task Manager.
* RAM is the process working set; GPU % and VRAM come from the same `GPU Engine`
  / `GPU Process Memory` counters Task Manager uses.
* Every failure is caught, shown on the status line, and written to `events.log`;
  the app never stops for an error. Run `-SelfTest` / `-UITest` any time to prove
  it on your machine.

## Notes & troubleshooting

* **Admin:** processes owned by an elevated process can only have their priority/affinity
  changed or be killed from an **elevated** console. The app never crashes on this — it
  reports it in the status line.
* The app refuses to kill its own console host.
* The Idle-process delta is used for total CPU (instant); the classic
  `\Processor(_Total)\% Processor Time` counter is the automatic fallback.
* GPU numbers come from the same `GPU Engine` / `GPU Process Memory` counters Task Manager
  uses, so they match what you see there.
* If the GPU section stays `n/a`, your GPU/driver exposes no performance counters — the
  rest of the app is unaffected.
* In a window smaller than ~80 columns the table truncates gracefully; enlarge the window
  for the full view.
