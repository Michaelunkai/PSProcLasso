#Requires -Version 5.1
<#
================================================================================
 PSProcLasso  v1.0
 A Process Lasso style process monitor that lives entirely inside your
 PowerShell terminal.  No GUI, no external dependencies, no exceptions.

 Real-time, ranked view of EVERY running process with exact per-process:
     - CPU %            (computed from kernel thread time deltas, 0.1% precision)
     - RAM  (working set) and private memory
     - GPU engine utilization % and dedicated VRAM  (Windows GPU performance counters)
 and system totals: CPU, RAM, GPU, VRAM, available + standby memory.

 Sort by CPU / RAM / GPU / Name / PID with one key.  Filter, select, and act:

     Priority control          (Idle ... Realtime)
     CPU affinity control      (pin to specific cores / ranges)
     Per-process CPU limiting  (soft limiter, like ProBalance's per-app cap)
     ProBalance auto-balancing (auto-lower background hoggers under load)
     Watchdog auto-restart     (restart watched apps if they die)
     Persistent rules          (priority / affinity / CPU limit / watchdog saved
                                to JSON and re-applied to new processes)
     Kill, relaunch, launch new programs, inspect deep details
     Standby/available memory readout

 Persistence:  %USERPROFILE%\.psproclasso\rules.json  (rules)  prefs.json  events.log

 RUN:   powershell -ExecutionPolicy Bypass -File .\PSProcLasso.ps1
        pwsh    -ExecutionPolicy Bypass -File .\PSProcLasso.ps1
 TIP:   run from a normal (non-elevated) console; processes owned by elevated
        processes need an elevated console for priority/affinity/kill to apply.

 SWITCHES:
   -RefreshMs N   monitor print period in ms (TUI samples continuously ~200ms)
   -Monitor       non-interactive mode: print a snapshot table every refresh
   -SelfTest      sample everything once, print diagnostics, exit (never throws)
   -UITest        exercise the full render + input stack headlessly, exit (never throws)
   -Snapshot      print one JSON snapshot of all processes to stdout, exit (for dashboards/tooling)
   -MaxRows N     cap rows shown in Monitor mode (default 20)
   -NoAnsi        plain output, no colors
================================================================================
#>

[CmdletBinding()]
param(
    [int]    $RefreshMs = 1000,
    [switch] $Monitor,
    [switch] $SelfTest,
    [switch] $UITest,
    [switch] $Snapshot,
    [int]    $MaxRows  = 20,
    [switch] $NoAnsi
)

# ----------------------------------------------------------------------------
#  Global state
# ----------------------------------------------------------------------------
$script:ConfigDir  = if ($env:USERPROFILE) { Join-Path $env:USERPROFILE '.psproclasso' } else { Join-Path $HOME '.psproclasso' }
$script:RulesFile  = Join-Path $script:ConfigDir 'rules.json'
$script:PrefsFile  = Join-Path $script:ConfigDir 'prefs.json'
$script:LogFile    = Join-Path $script:ConfigDir 'events.log'
$script:esc        = [char]27
$script:noAnsi     = [bool]$NoAnsi

# ANSI color table (empty when -NoAnsi)
$reset=''; $red=''; $yellow=''; $cyan=''; $dim=''; $bold=''; $green=''; $rev=''; $magenta=''; $blue=''; $white=''
if (-not $NoAnsi) {
    $reset   = "$($script:esc)[0m"
    $red     = "$($script:esc)[91m"
    $yellow  = "$($script:esc)[93m"
    $cyan    = "$($script:esc)[96m"
    $dim     = "$($script:esc)[2m"
    $bold    = "$($script:esc)[1m"
    $green   = "$($script:esc)[92m"
    $rev     = "$($script:esc)[7m"
    $magenta = "$($script:esc)[95m"
    $blue    = "$($script:esc)[94m"
    $white   = "$($script:esc)[97m"
}
$script:C = @{ reset=$reset; red=$red; yellow=$yellow; cyan=$cyan; dim=$dim; bold=$bold; green=$green; rev=$rev; magenta=$magenta; blue=$blue; white=$white }

# box/arrow glyphs as [char] escapes: the script is ANSI-encoded (no BOM), so
# literal non-ASCII characters would corrupt the parser on PowerShell 5.1.
$script:chPipe = [string][char]0x2502   # |
$script:chDash = [string][char]0x2500   # -
$script:chDot  = [string][char]0x00B7   # .
$script:chDown = [string][char]0x25BC   # v

# runtime state
$script:prevCpu     = @{}      # pid -> TimeSpan (last TotalProcessorTime)
$script:lastSample  = $null    # datetime of previous sample (for CPU deltas)
$script:procList    = @()      # current full table rows
$script:gpuMap      = @{}      # pid -> gpu %  (sum across engines)
$script:gpuMemMap   = @{}      # pid -> VRAM bytes
$script:sys         = @{ totalCpu=0.0; ramUsed=0; ramTotal=0; availMB=0; gpuPct=0.0; vramUsed=0; vramTotal=0; standby=0 }
$script:selPid      = 0
$script:sortKey     = 'cpu'    # cpu | ram | gpu | name | pid
$script:filter      = ''
$script:running     = $true
$script:mode        = 'nav'    # nav | filter | priority | affinity | limit | kill | exec | confirmRule | ruleDel
$script:view        = 'main'   # main | help | rules
$script:prompt      = ''
$script:inputBuf    = ''
$script:pendingRuleName = ''
$script:lastApplied = $null
$script:status      = 'Press ? for help'
$script:errorCount  = 0
$script:errors      = New-Object System.Collections.Generic.Queue[string]
$script:proBalance  = $true
$script:pbState     = @{}      # pid -> @{ orig='Normal'; until=[datetime] }
$script:cpuLimits   = @{}      # pid -> @{ limit=50.0; orig='Normal'; limited=$false }
$script:gpuLimits   = @{}      # pid -> @{ pct=50.0; suspended=$false; t0=0 }   (duty-cycle GPU throttle)
$script:ramLimits   = @{}      # pid -> @{ mb=512; trimmed=$false }              (working-set cap)
$script:throttleType = $null   # cached Add-Type for NtSuspendProcess/NtResumeProcess/EmptyWorkingSet
$script:rules       = @{}      # name -> rule object
$script:lastNames   = @{}      # name -> $true (for rule-on-new-process detection)
$script:gpuOn       = $true
$script:showDetails = $false
$script:cmdCache    = @{}      # pid -> @{ line=...; at=[datetime] }
$script:mouseOn     = $false    # mouse input enabled (real console only)
$script:hIn         = [IntPtr]::Zero
$script:oldInMode   = $null     # saved console input mode, restored on exit
$script:rec         = $null     # reusable INPUT_RECORD for Peek/ReadConsoleInput
$script:countersInit = $false  # open PerformanceCounter objects created once
$script:countersReinit = $false # set to force GPU instance re-enumeration
$script:ramTotalDone = $false   # WMI RAM-total query runs once, then cached
$script:pcCpuTotal = $null; $script:pcAvail = $null; $script:pcStbyN = $null; $script:pcStbyC = $null
$script:gpuPcs = @(); $script:gpuMemPcs = @(); $script:gpuAdapPcs = @()
$script:menu       = $null    # open menu: @{Title; Items=@(...); Sel; Parent}
$script:jumpBuf    = ''       # quick-jump buffer ('/' key)
$script:viewOffset = 0        # first visible row index in the ranked view
$script:ruleSel    = 0        # selected rule index in the rules view
$script:ownName     = ''
try { $script:ownName = (Get-Process -Id $PID -ErrorAction Stop).ProcessName } catch { $script:ownName = 'powershell' }

$sortOrder = @('cpu','ram','gpu','name','pid')
$priNames  = @{ 0='Idle'; 1='BelowNormal'; 2='Normal'; 3='AboveNormal'; 4='High'; 5='Realtime' }

# ----------------------------------------------------------------------------
#  Utilities
# ----------------------------------------------------------------------------
function Ensure-ConfigDir {
    try {
        if (-not (Test-Path -LiteralPath $script:ConfigDir)) {
            $null = New-Item -ItemType Directory -Path $script:ConfigDir -Force -ErrorAction Stop
        }
    } catch {}
}

function Write-Log { param([string]$Msg)
    Ensure-ConfigDir
    try { Add-Content -LiteralPath $script:LogFile -Value ("[" + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + "] " + $Msg) -ErrorAction SilentlyContinue } catch {}
}

function Set-Status { param([string]$Msg) $script:status = $Msg }

function Add-Error { param([string]$Msg)
    $script:errorCount++
    $script:errors.Enqueue($Msg)
    while ($script:errors.Count -gt 3) { $null = $script:errors.Dequeue() }
    Set-Status ("ERROR: " + $Msg)
    Write-Log ("ERROR: " + $Msg)
}

function Format-Bytes { param([long]$B)
    if ($B -lt 0) { return 'n/a' }
    if ($B -ge 1TB) { return ('{0:N1} TB' -f ($B / 1TB)) }
    if ($B -ge 1GB) { return ('{0:N1} GB' -f ($B / 1GB)) }
    if ($B -ge 1MB) { return ('{0:N0} MB' -f ($B / 1MB)) }
    if ($B -ge 1KB) { return ('{0:N0} KB' -f ($B / 1KB)) }
    return "$B B"
}

# --- pretty meters: █ filled / ░ empty, colored by load (ASCII when -NoAnsi) ---
function Get-PctColor { param([double]$Pct)
    $C = $script:C
    if ($Pct -ge 80) { return $C.red }
    if ($Pct -ge 50) { return $C.yellow }
    return $C.green
}

function Get-Meter { param([double]$Pct, [int]$Width = 10)
    $p = [math]::Max(0, [math]::Min(100, $Pct))
    $fill = [int][math]::Floor($p / 100.0 * $Width)
    if ($script:noAnsi) { return (('#' * $fill) + ('-' * ($Width - $fill))) }
    $C = $script:C; $res = $C.reset
    $col = Get-PctColor $p
    return ($col + ([string][char]0x2588) * $fill + $C.dim + ([string][char]0x2591) * ($Width - $fill) + $res + $res)
}

function Get-Marker {  # ▸ when ANSI, > otherwise
    if ($script:noAnsi) { return '>' }
    return [string][char]0x25B8
}

function Get-ProcessPath { param([int]$Id)
    $p = Get-Process -Id $Id -ErrorAction SilentlyContinue
    if ($null -ne $p) {
        try { return [string]$p.Path } catch {}
    }
    try {
        $w = Get-CimInstance Win32_Process -Filter "ProcessId=$Id" -ErrorAction SilentlyContinue
        if ($null -ne $w) { return [string]$w.ExecutablePath }
    } catch {}
    return ''
}

function Sanitize-Name { param([string]$N)
    try { $N = $N -replace "[\p{Cc}]", '?' } catch {}
    if ($N.Length -gt 60) { $N = $N.Substring(0, 60) }
    return $N
}

function Get-VisibleLen { param([string]$S)
    $t = $S
    try { $t = $S -replace "$($script:esc)\[[0-9;]*m", '' } catch {}
    return $t.Length
}

function Enable-Ansi {
    try {
        if (-not ('PSPL.VT' -as [type])) {
            $null = Add-Type -MemberDefinition '[DllImport("kernel32.dll")] public static extern IntPtr GetStdHandle(int n); [DllImport("kernel32.dll")] public static extern bool GetConsoleMode(IntPtr h, out uint m); [DllImport("kernel32.dll")] public static extern bool SetConsoleMode(IntPtr h, uint m);' -Name 'VT' -Namespace 'PSPL' -ErrorAction Stop
        }
        $h = [PSPL.VT]::GetStdHandle(-11)
        $m = 0
        $null = [PSPL.VT]::GetConsoleMode($h, [ref]$m)
        $null = [PSPL.VT]::SetConsoleMode($h, ($m -bor 0x0004))
    } catch {}
}

function Convert-CoresToMask { param([string]$Spec)
    $Spec = [string]$Spec
    $Spec = $Spec.Trim()
    if ($Spec -eq '' -or $Spec -ieq 'all') {
        $n = [math]::Max(1, [System.Environment]::ProcessorCount)
        $m = 0L
        for ($i = 0; $i -lt $n; $i++) { $m = $m -bor (1L -shl $i) }
        return $m
    }
    $mask = 0L
    foreach ($part in ($Spec -split ',')) {
        $part = $part.Trim()
        if ($part -match '^(\d+)-(\d+)$') {
            $lo = [int]$Matches[1]; $hi = [int]$Matches[2]
            if ($hi -gt 63) { $hi = 63 }
            for ($i = $lo; $i -le $hi; $i++) { $mask = $mask -bor (1L -shl $i) }
        } elseif ($part -match '^\d+$') {
            $i = [int]$part
            if ($i -le 63) { $mask = $mask -bor (1L -shl $i) }
        } else {
            return 0L
        }
    }
    return $mask
}

function Get-MaskBits { param([long]$Mask)
    $bits = @()
    for ($b = 0; $b -lt 64; $b++) { if (($Mask -band (1L -shl $b)) -ne 0) { $bits += $b } }
    return $bits
}

function Format-Affinity { param([long]$Mask)
    $bits = @(Get-MaskBits $Mask)
    if ($bits.Count -eq 0) { return 'none' }
    if ($bits.Count -eq [System.Environment]::ProcessorCount) { return 'all' }
    $parts = New-Object System.Collections.Generic.List[string]
    $i = 0
    while ($i -lt $bits.Count) {
        $start = $bits[$i]; $end = $start
        while ($i + 1 -lt $bits.Count -and $bits[$i + 1] -eq ($end + 1)) { $i++; $end = $bits[$i] }
        if ($end -eq $start) { $parts.Add([string]$start) } else { $parts.Add($start.ToString() + '-' + $end.ToString()) }
        $i++
    }
    return ($parts -join ',')
}

# ----------------------------------------------------------------------------
#  Sampling: system totals + per-process table (CPU delta, RAM, GPU, VRAM)
# ----------------------------------------------------------------------------
function Update-SystemStats {
    $s = $script:sys
    $s.totalCpu = 0.0; $s.gpuPct = 0.0; $s.vramUsed = 0; $s.vramTotal = 0; $s.standby = 0

    # --- RAM total: WMI costs ~1s per call, so query it once and cache ---
    if (-not $script:ramTotalDone) {
        try {
            $os = Get-CimInstance Win32_OperatingSystem -ErrorAction Stop
            $s.ramTotal = [long]$os.TotalVisibleMemorySize * 1KB
            $script:ramTotalDone = $true
        } catch {}
    }

    # --- ONE batched PDH call: total CPU + available RAM + GPU engine % + GPU
    #     memory + adapter totals + standby.  Get-Counter has ~1s startup per
    #     invocation, so batching keeps a full refresh cycle fast. ---
    $paths = @('\Processor(_Total)\% Processor Time',
               '\Memory\Available MBytes',
               '\Memory\Standby Cache Normal Priority Bytes',
               '\Memory\Standby Cache Core Bytes')
    if ($script:gpuOn) {
        $paths += '\GPU Engine(*)\Utilization Percentage',
                  '\GPU Process Memory(*)\Dedicated Usage',
                  '\GPU Adapter Memory(*)\Total Committed'
    }
    $gpuMap = @{}; $gpuMem = @{}
    try {
        $c = Get-Counter $paths -ErrorAction Stop
        $gpuTot = 0.0; $vramTot = 0L; $vramUsed = 0L; $stby = 0L; $cpuT = 0.0
        foreach ($cs in $c.CounterSamples) {
            $pth = $cs.Path.ToLowerInvariant()
            if ($pth -match 'gpu engine\(') {
                $v = [double]$cs.CookedValue
                $gpuTot += $v
                # GPU instance names are "pid_1234_luid_..." (no leading underscore) —
                # "_pid_" never matches, which used to zero every process's GPU %.
                if ($cs.InstanceName -match 'pid_(\d+)_') {
                    $procId = [int]$Matches[1]
                    if (-not $gpuMap.ContainsKey($procId)) { $gpuMap[$procId] = 0.0 }
                    $gpuMap[$procId] += $v
                }
            } elseif ($pth -match 'gpu process memory\(') {
                $val = [long]$cs.CookedValue
                $vramUsed += $val
                if ($cs.InstanceName -match 'pid_(\d+)_') {
                    $procId = [int]$Matches[1]
                    if (-not $gpuMem.ContainsKey($procId)) { $gpuMem[$procId] = 0L }
                    $gpuMem[$procId] += $val
                }
            } elseif ($pth -match 'gpu adapter memory\(') {
                $vramTot += [long]$cs.CookedValue
            } elseif ($pth -match 'processor\(_total\)') {
                $cpuT = [double]$cs.CookedValue
            } elseif ($pth -match 'memory\\available') {
                $s.availMB = [math]::Round([double]$cs.CookedValue, 1)
                $s.ramUsed = $s.ramTotal - ([long]($s.availMB * 1MB))
            } elseif ($pth -match 'memory\\standby') {
                $stby += [long]$cs.CookedValue
            }
        }
        $s.totalCpu  = [math]::Round($cpuT, 1)
        $s.gpuPct    = [math]::Round($gpuTot, 1)
        $s.vramUsed  = $vramUsed
        $s.vramTotal = $vramTot
        $s.standby   = $stby
        $script:gpuMap    = $gpuMap
        $script:gpuMemMap = $gpuMem
    } catch {
        if ($script:gpuOn) {
            $script:gpuOn = $false
            Set-Status 'GPU counters unavailable on this system (GPU shows n/a)'
        }
        # last-resort total CPU without the GPU paths
        try {
            $pc = Get-Counter '\Processor(_Total)\% Processor Time' -ErrorAction Stop
            $s.totalCpu = [math]::Round($pc.CounterSamples[0].CookedValue, 1)
        } catch {}
    }
    $script:sys = $s
}

function Get-ProcessTable {
    $rows = New-Object System.Collections.Generic.List[object]
    $now  = Get-Date
    $cores = [math]::Max(1, [System.Environment]::ProcessorCount)
    $prev = $script:prevCpu
    $cpuMap = $script:gpuMap
    $memMap = $script:gpuMemMap
    $el = 0.0
    if ($null -ne $script:lastSample) { $el = ($now - $script:lastSample).TotalMilliseconds }

    # .NET GetProcesses() is 2-3x faster than the Get-Process cmdlet; both read
    # the same live snapshot, so CPU deltas stay exact.
    try { $procs = @([System.Diagnostics.Process]::GetProcesses()) } catch { $procs = @(Get-Process -ErrorAction SilentlyContinue) }
    $curIds = @{}
    foreach ($p in $procs) {
        $id = 0; try { $id = $p.Id } catch { continue }
        $curIds[$id] = $true

        $tp = $null
        try { $tp = $p.TotalProcessorTime } catch {}
        $cpu = 0.0
        if ($null -ne $tp -and $prev.ContainsKey($id) -and $el -gt 0) {
            $dt = ($tp - $prev[$id]).TotalMilliseconds
            if ($dt -gt 0) { $cpu = ($dt / $el) / $cores * 100.0 }
        }
        if ($cpu -gt 999.0) { $cpu = 999.0 }
        $cpu = [math]::Round($cpu, 1)

        $mem = 0L;  try { $mem  = [long]$p.WorkingSet64 } catch {}
        $priv = 0L; try { $priv = [long]$p.PrivateMemorySize64 } catch {}

        $gpu = 0.0; if ($cpuMap.ContainsKey($id)) { $gpu = [math]::Round($cpuMap[$id], 1) }
        $vram = 0L; if ($memMap.ContainsKey($id)) { $vram = $memMap[$id] }

        $pri = 'n/a'
        try { $pri = [string]$p.PriorityClass } catch {}

        $aff = '?'
        try {
            $mask = [long]$p.ProcessorAffinity
            $aff = Format-Affinity $mask
        } catch {}

        # NOTE: do not fetch $p.Path here - it takes ~6s over all processes.
        # Path is resolved lazily in the details panel / watchdog instead.
        $thr = 0
        try { $thr = $p.Threads.Count } catch {}

        try {
            $rows.Add([pscustomobject]@{
                Id=$id; Name=[string]$p.ProcessName; Cpu=$cpu; Mem=$mem; Priv=$priv
                Gpu=$gpu; Vram=$vram; Priority=$pri; Affinity=$aff
                Path=''; Threads=$thr
            })
        } catch {
            Write-Log ("row skipped pid " + $id + ": " + $_.Exception.Message)
        }
    }

    # keep only live pids in the delta cache
    $dead = @()
    foreach ($k in @($prev.Keys)) { if (-not $curIds.ContainsKey($k)) { $dead += $k } }
    foreach ($k in $dead) { $null = $script:prevCpu.Remove($k) }
    $script:prevCpu = @{}
    foreach ($p in $procs) {
        try { $script:prevCpu[$p.Id] = $p.TotalProcessorTime } catch {}
    }

    # NOTE: do not use @($rows) here - PS 5.1 throws 'Argument types do not
    # match' when wrapping List[object] of PSCustomObjects; ToArray() is safe.
    return $rows.ToArray()
}

# --- Real-time sampling: open PerformanceCounter objects read in ~1ms instead
#     of ~2s per Get-Counter batch, so CPU/RAM can tick at ~200ms and GPU at
#     ~1Hz.  The old batched path (Update-SystemStats) stays as the fallback. ---
function Init-Counters {
    if ($script:countersInit -and -not $script:countersReinit) { return }
    $script:countersReinit = $false

    # cheap system counters (kept open, NextValue ~1ms)
    try {
        $script:pcCpuTotal = New-Object System.Diagnostics.PerformanceCounter('Processor', '% Processor Time', '_Total', $true)
        $null = $script:pcCpuTotal.NextValue()
    } catch { $script:pcCpuTotal = $null }
    try {
        $script:pcAvail = New-Object System.Diagnostics.PerformanceCounter('Memory', 'Available MBytes', '', $true)
        $null = $script:pcAvail.NextValue()
    } catch { $script:pcAvail = $null }
    try {
        $script:pcStbyN = New-Object System.Diagnostics.PerformanceCounter('Memory', 'Standby Cache Normal Priority Bytes', '', $true)
        $script:pcStbyC = New-Object System.Diagnostics.PerformanceCounter('Memory', 'Standby Cache Core Bytes', '', $true)
        $null = $script:pcStbyN.NextValue(); $null = $script:pcStbyC.NextValue()
    } catch { $script:pcStbyN = $null; $script:pcStbyC = $null }

    # GPU engine / process-memory / adapter counters, one open object per instance
    $script:gpuPcs     = @()   # @{ name=..; pc=.. }  (GPU Engine, Utilization Percentage)
    $script:gpuMemPcs  = @()   # @{ name=..; pc=.. }  (GPU Process Memory, Dedicated Usage)
    $script:gpuAdapPcs = @()   # @{ name=..; pc=.. }  (GPU Adapter Memory, Total Committed)
    if ($script:gpuOn) {
        try {
            $cat = New-Object System.Diagnostics.PerformanceCounterCategory('GPU Engine')
            foreach ($i in @($cat.GetInstanceNames())) {
                $pc = New-Object System.Diagnostics.PerformanceCounter('GPU Engine', 'Utilization Percentage', $i, $true)
                $null = $pc.NextValue()
                $script:gpuPcs += @{ name=[string]$i; pc=$pc }
            }
            $cat2 = New-Object System.Diagnostics.PerformanceCounterCategory('GPU Process Memory')
            foreach ($i in @($cat2.GetInstanceNames())) {
                $pc = New-Object System.Diagnostics.PerformanceCounter('GPU Process Memory', 'Dedicated Usage', $i, $true)
                $null = $pc.NextValue()
                $script:gpuMemPcs += @{ name=[string]$i; pc=$pc }
            }
            $cat3 = New-Object System.Diagnostics.PerformanceCounterCategory('GPU Adapter Memory')
            foreach ($i in @($cat3.GetInstanceNames())) {
                $pc = New-Object System.Diagnostics.PerformanceCounter('GPU Adapter Memory', 'Total Committed', $i, $true)
                $null = $pc.NextValue()
                $script:gpuAdapPcs += @{ name=[string]$i; pc=$pc }
            }
        } catch {
            $script:gpuPcs = @(); $script:gpuMemPcs = @(); $script:gpuAdapPcs = @()
        }
    }
    $script:countersInit = $true
}

function Update-Fast {
    # RAM total: WMI costs ~1s per call, so query it once and cache
    if (-not $script:ramTotalDone) {
        try {
            $os = Get-CimInstance Win32_OperatingSystem -ErrorAction Stop
            $script:sys.ramTotal = [long]$os.TotalVisibleMemorySize * 1KB
            $script:ramTotalDone = $true
        } catch {}
    }
    # open counters: total CPU, available RAM, standby (~1ms each)
    $s = $script:sys
    if ($null -ne $script:pcCpuTotal) {
        try { $v = $script:pcCpuTotal.NextValue(); if ($v -ge 0) { $s.totalCpu = [math]::Round($v, 1) } } catch {}
    }
    if ($null -ne $script:pcAvail) {
        try {
            $a = $script:pcAvail.NextValue()
            if ($a -ge 0) {
                $s.availMB = [math]::Round($a, 1)
                if ($s.ramTotal -gt 0) { $s.ramUsed = $s.ramTotal - ([long]($a * 1MB)) }
            }
        } catch {}
    }
    if ($null -ne $script:pcStbyN) {
        try { $s.standby = [long]$script:pcStbyN.NextValue() + [long]$script:pcStbyC.NextValue() } catch {}
    }
    # per-process table (CPU thread-time deltas + working set)
    try { $script:procList = Get-ProcessTable } catch { Add-Error ("procs: " + $_.Exception.Message) }
    # fallback total CPU: sum of per-process % (matches the column sum)
    if ($null -eq $script:pcCpuTotal) {
        $tot = 0.0
        foreach ($r in $script:procList) { $tot += $r.Cpu }
        $s.totalCpu = [math]::Round($tot, 1)
    }
    $script:lastSample = Get-Date
}

function Update-Gpu {
    if (-not $script:gpuOn) { return }
    if (-not $script:countersInit) { Init-Counters }
    if ($script:gpuPcs.Count -eq 0 -and $script:gpuMemPcs.Count -eq 0) {
        # open GPU counters unavailable -> fall back to the batched path
        try { Update-SystemStats } catch { Add-Error ("stats: " + $_.Exception.Message) }
        return
    }
    $gpuMap = @{}; $gpuMem = @{}; $gpuTot = 0.0; $vramUsed = 0L; $vramTot = 0L
    try {
        foreach ($g in $script:gpuPcs) {
            $v = [double]$g.pc.NextValue()
            $gpuTot += $v
            if ($g.name -match 'pid_(\d+)_') {
                $procId = [int]$Matches[1]
                if (-not $gpuMap.ContainsKey($procId)) { $gpuMap[$procId] = 0.0 }
                $gpuMap[$procId] += $v
            }
        }
        foreach ($g in $script:gpuMemPcs) {
            $val = [long]$g.pc.NextValue()
            $vramUsed += $val
            if ($g.name -match 'pid_(\d+)_') {
                $procId = [int]$Matches[1]
                if (-not $gpuMem.ContainsKey($procId)) { $gpuMem[$procId] = 0L }
                $gpuMem[$procId] += $val
            }
        }
        foreach ($g in $script:gpuAdapPcs) { $vramTot += [long]$g.pc.NextValue() }
        $s = $script:sys
        $s.gpuPct    = [math]::Round($gpuTot, 1)
        $s.vramUsed  = $vramUsed
        $s.vramTotal = $vramTot
        $script:gpuMap    = $gpuMap
        $script:gpuMemMap = $gpuMem
    } catch {
        try { Update-SystemStats } catch { Add-Error ("stats: " + $_.Exception.Message) }
    }
}

function Update-All {
    if (-not $script:countersInit) { Init-Counters }
    # GPU first: Update-Fast builds the table from $script:gpuMap, so the map must be
    # refreshed before the table, or every snapshot lags one pass behind (0% GPU).
    Update-Gpu
    Update-Fast
}

# ----------------------------------------------------------------------------
#  View helpers (filter + sort)
# ----------------------------------------------------------------------------
function Get-View {
    $rows = $script:procList
    if ($script:filter) {
        $f = $script:filter
        $rows = @($rows | Where-Object { $_.Name -like "*$f*" -or [string]$_.Id -eq $f })
    }
    switch ($script:sortKey) {
        'cpu'  { $rows = @($rows | Sort-Object -Property Cpu -Descending -ErrorAction SilentlyContinue) }
        'ram'  { $rows = @($rows | Sort-Object -Property Mem -Descending -ErrorAction SilentlyContinue) }
        'gpu'  { $rows = @($rows | Sort-Object -Property Gpu -Descending -ErrorAction SilentlyContinue) }
        'name' { $rows = @($rows | Sort-Object -Property Name -ErrorAction SilentlyContinue) }
        'pid'  { $rows = @($rows | Sort-Object -Property Id  -ErrorAction SilentlyContinue) }
    }
    # keep selection valid; when it resets, restart the view at the top
    if ($rows.Count -gt 0 -and -not ($rows | Where-Object { $_.Id -eq $script:selPid })) {
        $script:selPid = $rows[0].Id
        $script:viewOffset = 0
    }
    return $rows
}

function Get-Selected {
    foreach ($r in $script:procList) { if ($r.Id -eq $script:selPid) { return $r } }
    return $null
}

function Get-VisibleRowCount {
    $h = 40
    try { $h = [Console]::WindowHeight } catch {}
    if ($h -lt 8) { $h = 8 }
    $n = $h - 5
    if ($script:showDetails -and $script:mode -ne 'menu') { $n -= 13 }
    if ($script:mode -eq 'menu' -and $null -ne $script:menu) { $n -= ($script:menu.Items.Count + 1) }
    return [math]::Max(1, $n)
}

function Ensure-Visible {
    $rows = Get-View
    if ($rows.Count -eq 0) { $script:viewOffset = 0; return }
    $idx = 0
    for ($i = 0; $i -lt $rows.Count; $i++) { if ($rows[$i].Id -eq $script:selPid) { $idx = $i; break } }
    $vis = Get-VisibleRowCount
    if ($idx -lt $script:viewOffset) { $script:viewOffset = $idx }
    elseif ($idx -ge ($script:viewOffset + $vis)) { $script:viewOffset = $idx - $vis + 1 }
    $script:viewOffset = [math]::Max(0, [math]::Min($script:viewOffset, [math]::Max(0, $rows.Count - $vis)))
}

function Jump-To { param([string]$Buf)
    if (-not $Buf) { return $null }
    $nb = $Buf.ToLowerInvariant()
    foreach ($r in $script:procList) {
        try { if ($r.Name.ToLowerInvariant().StartsWith($nb)) { $script:selPid = $r.Id; Ensure-Visible; return $r.Name } } catch {}
    }
    foreach ($r in $script:procList) {
        try { if ($r.Name.ToLowerInvariant().Contains($nb)) { $script:selPid = $r.Id; Ensure-Visible; return $r.Name } } catch {}
    }
    return $null
}

function Move-Sel { param([int]$Delta, [switch]$Wrap)
    $rows = Get-View
    if ($rows.Count -eq 0) { return }
    $idx = 0
    for ($i = 0; $i -lt $rows.Count; $i++) { if ($rows[$i].Id -eq $script:selPid) { $idx = $i; break } }
    if ($Wrap) { $idx = ($idx + $Delta + $rows.Count) % $rows.Count }
    else { $idx = [math]::Max(0, [math]::Min($rows.Count - 1, $idx + $Delta)) }
    $script:selPid = $rows[$idx].Id
    Ensure-Visible
}

function Cycle-Sort { param([int]$Delta)
    $i = [Array]::IndexOf($sortOrder, $script:sortKey)
    if ($i -lt 0) { $i = 0 }
    $i = ($i + $Delta + $sortOrder.Count) % $sortOrder.Count
    Set-SortKey $sortOrder[$i]
}

function Set-SortKey { param([string]$Key)
    $script:sortKey = $Key
    $script:viewOffset = 0
    Set-Status ("Sort: " + $Key.ToUpper())
}

# ----------------------------------------------------------------------------
#  Menus: full keyboard navigation (arrows, Home/End, shortcut letters, Enter/Esc)
# ----------------------------------------------------------------------------
function New-MenuItem { param([string]$Label, [string]$Value, [string]$Shortcut = '', [string]$Sub = '')
    [pscustomobject]@{ Label = $Label; Value = $Value; Shortcut = $Shortcut; Sub = $Sub }
}

function Open-Menu { param([string]$Title, $Items, [int]$Sel = 0, $Parent = $null)
    $script:menu = @{ Title = $Title; Items = @($Items); Sel = $Sel; Parent = $Parent }
    $script:mode = 'menu'
}

function Close-Menu {
    $script:menu = $null
    $script:mode = 'nav'
}

function Menu-Move { param([int]$Delta)
    if ($null -eq $script:menu) { return }
    $n = $script:menu.Items.Count
    if ($n -eq 0) { return }
    $script:menu.Sel = ($script:menu.Sel + $Delta + $n) % $n
}

function Menu-Sub { param($Item)
    $parent = $script:menu
    switch ($Item.Sub) {
        'sort' {
            Open-Menu 'Sort by' (Get-SortMenuItems) 0 $parent
        }
        'priority' {
            $row = Get-Selected
            Open-Menu ('Priority for ' + $(if ($null -ne $row) { $row.Name } else { '?' })) (Get-PriorityMenuItems) 0 $parent
        }
        'view' {
            Open-Menu 'View' (Get-ViewMenuItems) 0 $parent
        }
    }
}

function Get-YesNoItems { param([string]$Prefix)
    @(
        (New-MenuItem 'Yes' ($Prefix + 'yes') 'y'),
        (New-MenuItem 'No'  ($Prefix + 'no')  'n')
    )
}

function Get-SortMenuItems {
    @(
        (New-MenuItem 'CPU'  'sort:cpu'  '1'),
        (New-MenuItem 'RAM'  'sort:ram'  '2'),
        (New-MenuItem 'GPU'  'sort:gpu'  '3'),
        (New-MenuItem 'Name' 'sort:name' '4'),
        (New-MenuItem 'PID'  'sort:pid'  '5')
    )
}

function Get-PriorityMenuItems {
    @(
        (New-MenuItem 'Idle'         'pri:Idle'        '0'),
        (New-MenuItem 'Below Normal' 'pri:BelowNormal' '1'),
        (New-MenuItem 'Normal'       'pri:Normal'      '2'),
        (New-MenuItem 'Above Normal' 'pri:AboveNormal' '3'),
        (New-MenuItem 'High'         'pri:High'        '4'),
        (New-MenuItem 'Realtime'     'pri:Realtime'    '5')
    )
}

function Get-LimitMenuItems {
    @(
        (New-MenuItem 'CPU limit ...'    'limit:cpu' '1'),
        (New-MenuItem 'GPU limit ...'    'limit:gpu' '2'),
        (New-MenuItem 'RAM limit ...'    'limit:ram' '3'),
        (New-MenuItem 'Back'             'back'      '')
    )
}

function Get-ViewMenuItems {
    @(
        (New-MenuItem 'Main process list' 'view:main' 'm'),
        (New-MenuItem ('Details ' + $(if ($script:showDetails) { '(on)' } else { '(off)' })) 'details' 'd'),
        (New-MenuItem 'Rules (persisted)' 'view:rules' 'r'),
        (New-MenuItem 'Help / all keys'   'view:help'  'h')
    )
}

function Get-MainMenuItems {
    $row = Get-Selected
    $wd = $false
    if ($null -ne $row -and $script:rules.ContainsKey($row.Name) -and $script:rules[$row.Name].watchdog) { $wd = $true }
    @(
        (New-MenuItem 'Sort by ...'            ''       ''  'sort'),
        (New-MenuItem 'Priority ...'           ''       ''  'priority'),
        (New-MenuItem 'Set CPU affinity ...'   'affinity' 'a' ''),
        (New-MenuItem 'Set limits ... (CPU/GPU/RAM)' 'limitmenu' 'l' ''),
        (New-MenuItem 'Kill selected process'        'kill'      'k'  ''),
        (New-MenuItem ('Watchdog ' + $(if ($wd) { 'ON' } else { 'OFF' })) 'watchdog' 'w' ''),
        (New-MenuItem 'Remove all limits'             'unlimit'   'u'  ''),
        (New-MenuItem 'Launch program ...'     'launch'  'e'  ''),
        (New-MenuItem 'View ...'               ''       ''  'view'),
        (New-MenuItem ('ProBalance ' + $(if ($script:proBalance) { 'ON' } else { 'OFF' })) 'balance' 'b' ''),
        (New-MenuItem ('GPU sampling ' + $(if ($script:gpuOn) { 'ON' } else { 'OFF' })) 'gpu' 'g' ''),
        (New-MenuItem 'Quit'                   'quit'   'q'  '')
    )
}

function Persist-LastApplied {
    $la = $script:lastApplied
    if ($null -eq $la -or -not $la.Name) { return }
    $n = $la.Name
    if (-not $script:rules.ContainsKey($n)) { $script:rules[$n] = New-Rule }
    $r = $script:rules[$n]
    if ($la.Priority) { $r.priority = $la.Priority }
    if ($la.Affinity) { $r.affinity = @($la.Affinity) }
    if ($la.Limit -gt 0) { $r.cpuLimit = [int]$la.Limit }
    if ($la.gpuLimit -gt 0) { $r.gpuLimit = [int]$la.gpuLimit }
    if ($la.ramLimit -gt 0) { $r.ramLimit = [int]$la.ramLimit }
    $r.enabled = $true
    Save-Rules
    Set-Status ('Persisted rule for ' + $n)
    Write-Log ('Rule persisted: ' + $n)
}

function Apply-MenuItem { param($Item)
    if ($null -eq $Item) { Close-Menu; return }
    if ($Item.Sub) { Menu-Sub $Item; return }
    switch -Regex ([string]$Item.Value) {
        '^sort:(cpu|ram|gpu|name|pid)$' {
            Set-SortKey $Matches[1]
            Close-Menu
            Ensure-Visible
        }
        '^pri:(.+)$' {
            $row = Get-Selected
            if ($null -eq $row) { Set-Status 'Nothing selected'; Close-Menu }
            elseif ($row.Id -eq $PID) { Set-Status 'Cannot change priority of this console host'; Close-Menu }
            elseif (Set-PriorityLive $row.Id $Matches[1]) {
                Open-Menu ('Persist priority rule for ' + $row.Name + '?') (Get-YesNoItems 'persist:') 1
            }
        }
        '^persist:yes$' { Persist-LastApplied; Close-Menu }
        '^persist:no$'  { Set-Status 'Rule not persisted'; Close-Menu }
        '^kill$' {
            $row = Get-Selected
            if ($null -eq $row) { Set-Status 'Nothing selected'; Close-Menu }
            else { Open-Menu ('Kill ' + $row.Name + '  (PID ' + $row.Id + ')?') (Get-YesNoItems 'kill:') 1 }
        }
        '^kill:yes$' { $row = Get-Selected; if ($null -ne $row) { Invoke-Kill $row.Id $row.Name }; Close-Menu }
        '^kill:no$'  { Set-Status 'Kill cancelled'; Close-Menu }
        '^ruledel:yes$' {
            if ($script:pendingRuleName) { Remove-Rule $script:pendingRuleName }
            Close-Menu
        }
        '^ruledel:no$' { Set-Status 'Rule kept'; Close-Menu }
        '^affinity$' { Close-Menu; Start-Mode 'affinity' 'Cores (e.g. 0-3,5  |  Enter=all cores): ' }
        '^limitmenu$' {
            $row = Get-Selected
            Close-Menu
            if ($null -eq $row) { Set-Status 'Nothing selected' }
            else { Open-Menu ('Set limit for ' + $row.Name) (Get-LimitMenuItems) 0 }
        }
        '^limit:cpu$' { Close-Menu; Start-Mode 'limit:cpu' 'CPU limit %, 0=off (e.g. 30): ' }
        '^limit:gpu$' { Close-Menu; Start-Mode 'limit:gpu' 'GPU limit %, 0=off (e.g. 30): ' }
        '^limit:ram$' { Close-Menu; Start-Mode 'limit:ram' 'RAM limit MB, 0=off (e.g. 512): ' }
        '^watchdog$' { Toggle-Watchdog; Close-Menu }
        '^unlimit$'  { $row = Get-Selected; if ($null -ne $row) { Unlimit-Proc $row.Id }; Close-Menu }
        '^launch$'   { Close-Menu; Start-Mode 'exec' 'Launch program or command: ' }
        '^view:(main|help|rules)$' {
            $script:view = $Matches[1]
            if ($Matches[1] -eq 'rules') { $script:ruleSel = 0 }
            Close-Menu
        }
        '^details$' {
            $script:showDetails = -not $script:showDetails
            Set-Status $(if ($script:showDetails) { 'Details ON (Enter/i to toggle)' } else { 'Details OFF' })
            Close-Menu
        }
        '^balance$' {
            $script:proBalance = -not $script:proBalance
            Set-Status ("ProBalance " + $(if ($script:proBalance) { 'ON' } else { 'OFF' }))
            Close-Menu
        }
        '^gpu$' {
            $script:gpuOn = -not $script:gpuOn
            Set-Status ("GPU sampling " + $(if ($script:gpuOn) { 'ON' } else { 'OFF' }))
            Close-Menu
        }
        '^quit$'    { $script:running = $false; Close-Menu }
        '^back$'    { if ($null -ne $script:menu -and $null -ne $script:menu.Parent) { $script:menu = $script:menu.Parent } else { Close-Menu } }
    }
}

function Handle-MenuKey { param($key, $char)
    if ($null -eq $script:menu) { $script:mode = 'nav'; return }
    $it = $script:menu.Items[$script:menu.Sel]
    switch ($key) {
        'UpArrow'    { Menu-Move -1; return }
        'DownArrow'  { Menu-Move 1; return }
        'Home'       { $script:menu.Sel = 0; return }
        'End'        { $script:menu.Sel = $script:menu.Items.Count - 1; return }
        'RightArrow' { if ($it.Sub) { Menu-Sub $it }; return }
        'LeftArrow'  { if ($null -ne $script:menu.Parent) { $script:menu = $script:menu.Parent } else { Close-Menu }; return }
        'Enter'      { Apply-MenuItem $it; return }
        'Escape'     { if ($null -ne $script:menu.Parent) { $script:menu = $script:menu.Parent } else { Close-Menu }; return }
    }
    # shortcut letters apply the item that owns them
    $lc = ''
    try { $lc = $char.ToLowerInvariant() } catch {}
    # sort menu: letters c/r/g/n/p are aliases for the digit shortcuts 1-5
    if ($lc -and $script:menu.Title -eq 'Sort by') {
        $alias = @{ 'c'='1'; 'r'='2'; 'g'='3'; 'n'='4'; 'p'='5' }
        if ($alias.ContainsKey($lc)) { $lc = $alias[$lc] }
    }
    foreach ($item in $script:menu.Items) {
        if ($item.Shortcut -and $item.Shortcut.ToLowerInvariant() -eq $lc) { Apply-MenuItem $item; return }
    }
}

function Handle-JumpKey { param($key, $char)
    if ($key -eq 'Escape') { $script:mode = 'nav'; $script:jumpBuf = ''; Set-Status 'Jump cancelled'; return }
    if ($key -eq 'Enter') {
        $name = Jump-To $script:jumpBuf
        $script:mode = 'nav'
        $script:jumpBuf = ''
        Set-Status ('Jumped to ' + $(if ($name) { $name } else { 'first match' }))
        return
    }
    if ($key -eq 'Backspace') {
        if ($script:jumpBuf.Length -gt 0) { $script:jumpBuf = $script:jumpBuf.Substring(0, $script:jumpBuf.Length - 1) }
    } else {
        try { if ($char -match '[\p{L}\p{N} _.\-]') { $script:jumpBuf += $char } } catch {}
    }
    $name = Jump-To $script:jumpBuf
    Set-Status ('Jump: "' + $script:jumpBuf + '"  ->  ' + $(if ($name) { $name } else { 'no match' }))
}

function Handle-ViewKey { param($key, $char)
    # help view: any key returns to the main list
    if ($script:view -eq 'help') { $script:view = 'main'; return }
    if ($script:view -ne 'rules') { return }
    $names = @($script:rules.Keys | Sort-Object)
    if ($names.Count -eq 0) { $script:view = 'main'; return }
    if ($script:ruleSel -lt 0 -or $script:ruleSel -ge $names.Count) { $script:ruleSel = 0 }
    switch ($key) {
        'UpArrow'   { $script:ruleSel = ($script:ruleSel - 1 + $names.Count) % $names.Count; return }
        'DownArrow' { $script:ruleSel = ($script:ruleSel + 1) % $names.Count; return }
        'Escape'    { $script:view = 'main'; return }
        'Enter'     { $script:view = 'main'; return }
    }
    $c = ''
    try { $c = $char.ToLowerInvariant() } catch {}
    switch ($c) {
        'q' { $script:view = 'main' }
        'r' { $script:view = 'main' }
        'x' {
            $name = $names[$script:ruleSel]
            $script:pendingRuleName = $name
            Open-Menu ('Delete rule for ' + $name + '?') (Get-YesNoItems 'ruledel:') 1
        }
        'w' {
            $name = $names[$script:ruleSel]
            $r = $script:rules[$name]
            $r.watchdog = -not $r.watchdog
            if (-not $r.watchdog) { $r.wdPath = '' }
            Save-Rules
            Set-Status ("Watchdog for " + $name + " " + $(if ($r.watchdog) { 'ON' } else { 'OFF' }))
        }
        default { $script:view = 'main' }
    }
}

# ----------------------------------------------------------------------------
#  Actions: priority / affinity / CPU limit / kill / launch / watchdog / rules
# ----------------------------------------------------------------------------
function Set-ProcPriority { param($Proc, [string]$Pri)
    if (-not $Pri) { return $false }
    try {
        if (-not [Enum]::IsDefined([System.Diagnostics.ProcessPriorityClass], $Pri)) { return $false }
        $pp = [System.Diagnostics.ProcessPriorityClass]::Parse([System.Diagnostics.ProcessPriorityClass], $Pri)
        $Proc.PriorityClass = $pp
        return $true
    } catch { return $false }
}

function Set-PriorityLive { param([int]$Id, [string]$Name)
    $p = Get-Process -Id $Id -ErrorAction SilentlyContinue
    if ($null -eq $p) { Set-Status ("PID $Id not found"); return $false }
    if (Set-ProcPriority $p $Name) {
        $script:lastApplied = @{ Name=$p.ProcessName; Priority=$Name; Affinity=''; Limit=0 }
        Set-Status ("Priority of " + $p.ProcessName + " -> " + $Name)
        Write-Log ("Priority set: " + $p.ProcessName + " -> " + $Name)
        return $true
    }
    Add-Error ("Cannot set priority on " + $p.ProcessName + " (elevated/system process needs admin console)")
    return $false
}

function Set-AffinityLive { param([int]$Id, [string]$Spec)
    $p = Get-Process -Id $Id -ErrorAction SilentlyContinue
    if ($null -eq $p) { Set-Status ("PID $Id not found"); return $false }
    $mask = Convert-CoresToMask $Spec
    if ($mask -lt 1) { Set-Status ("Invalid core list: " + $Spec); return $false }
    try {
        $p.ProcessorAffinity = [IntPtr]$mask
        $bits = Get-MaskBits $mask
        $script:lastApplied = @{ Name=$p.ProcessName; Priority=''; Affinity=@($bits); Limit=0 }
        Set-Status ("Affinity of " + $p.ProcessName + " -> cores " + ($bits -join ','))
        Write-Log ("Affinity set: " + $p.ProcessName + " -> " + ($bits -join ','))
        return $true
    } catch {
        Add-Error ("Cannot set affinity on " + $p.ProcessName + " (may need admin)")
        return $false
    }
}

function Set-CpuLimit { param([int]$Id, [double]$Limit)
    $p = Get-Process -Id $Id -ErrorAction SilentlyContinue
    if ($null -eq $p) { Set-Status ("PID $Id not found"); return $false }
    if ($Limit -le 0) {
        $null = $script:cpuLimits.Remove($Id)
        Set-Status ("CPU limit removed for " + $p.ProcessName)
        return $true
    }
    $orig = 'Normal'
    try { $orig = [string]$p.PriorityClass } catch {}
    $script:cpuLimits[$Id] = @{ limit=$Limit; orig=$orig; limited=$false }
    $script:lastApplied = @{ Name=$p.ProcessName; Priority=''; Affinity=''; Limit=$Limit }
    Set-Status ("CPU limit " + $Limit + "% set on " + $p.ProcessName + " (soft: drops priority while over)")
    Write-Log ("CPU limit: " + $p.ProcessName + " -> " + $Limit + "%")
    return $true
}

# --- native process control: suspend/resume (throttle) + trim working set ---
function Get-ThrottleNative {
    if ($null -ne $script:throttleType) { return $script:throttleType }
    try {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class PsplThrottle {
    [DllImport("ntdll.dll")] public static extern int NtSuspendProcess(IntPtr hProcess);
    [DllImport("ntdll.dll")] public static extern int NtResumeProcess(IntPtr hProcess);
    [DllImport("psapi.dll")] public static extern bool EmptyWorkingSet(IntPtr hProcess);
    [DllImport("kernel32.dll", SetLastError=true)] public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr hObject);
}
'@ -ErrorAction Stop
        $script:throttleType = [PsplThrottle]
    } catch { $script:throttleType = $null }
    return $script:throttleType
}

# Dedicated PROCESS_SUSPEND_RESUME handles, cached per pid: resuming through the
# plain .NET Process.Handle fails silently, so we always use our own handle.
$PROC_SUSPEND_RESUME = 0x0800
$PROC_QUERY_INFORMATION = 0x0400
$script:throttleHandles = @{}   # pid -> IntPtr

function Get-ProcHandle { param([int]$Id)
    if ($script:throttleHandles.ContainsKey($Id)) {
        $h = $script:throttleHandles[$Id]
        if ($h -ne [IntPtr]::Zero) { return $h }
    }
    if ($null -eq (Get-ThrottleNative)) { return [IntPtr]::Zero }
    $p = Get-Process -Id $Id -ErrorAction SilentlyContinue
    if ($null -eq $p) { return [IntPtr]::Zero }
    $h = [PsplThrottle]::OpenProcess(($PROC_SUSPEND_RESUME -bor $PROC_QUERY_INFORMATION), $false, $Id)
    if ($h -ne [IntPtr]::Zero) { $script:throttleHandles[$Id] = $h }
    return $h
}

function Close-ProcHandle { param([int]$Id)
    if ($script:throttleHandles.ContainsKey($Id)) {
        $h = $script:throttleHandles[$Id]
        if ($h -ne [IntPtr]::Zero) { try { $null = [PsplThrottle]::CloseHandle($h) } catch {} }
        $null = $script:throttleHandles.Remove($Id)
    }
}

function Suspend-Proc { param([int]$Id)
    $h = Get-ProcHandle $Id
    if ($h -eq [IntPtr]::Zero) { return $false }
    try { $null = [PsplThrottle]::NtSuspendProcess($h); return $true } catch { return $false }
}

function Resume-Proc { param([int]$Id)
    $h = Get-ProcHandle $Id
    if ($h -eq [IntPtr]::Zero) { return $false }
    try { $null = [PsplThrottle]::NtResumeProcess($h); return $true } catch { return $false }
}

# --- GPU limit: duty-cycle throttle (suspend part of each second). The only
#     pure-PowerShell way to cap GPU use; also caps its CPU use. 100% = off.
function Set-GpuLimit { param([int]$Id, [double]$Pct)
    $p = Get-Process -Id $Id -ErrorAction SilentlyContinue
    if ($null -eq $p) { Set-Status ("PID $Id not found"); return $false }
    if ($Pct -le 0 -or $Pct -ge 100) {
        $null = $script:gpuLimits.Remove($Id)
        Set-Status ("GPU limit removed for " + $p.ProcessName)
        return $true
    }
    if ($Id -eq $PID) { Set-Status 'Cannot throttle this console host'; return $false }
    if ($null -eq (Get-ThrottleNative)) {
        Set-Status 'GPU throttle unavailable (Add-Type failed)'
        return $false
    }
    $script:gpuLimits[$Id] = @{ pct=[double]$Pct; suspended=$false; t0=[Environment]::TickCount }
    $script:lastApplied = @{ Name=$p.ProcessName; Priority=''; Affinity=''; Limit=0; gpuLimit=[double]$Pct; ramLimit=0 }
    Set-Status ("GPU limit " + $Pct + "% set on " + $p.ProcessName + " (suspends it part of each second)")
    Write-Log ("GPU limit: " + $p.ProcessName + " -> " + $Pct + "%")
    return $true
}

# --- RAM limit: trim the working set whenever the process exceeds the cap.
#     Windows cannot hard-cap a process's RAM, so this is an enforced trim.
function Set-RamLimit { param([int]$Id, [long]$Mb)
    $p = Get-Process -Id $Id -ErrorAction SilentlyContinue
    if ($null -eq $p) { Set-Status ("PID $Id not found"); return $false }
    if ($Mb -le 0) {
        $null = $script:ramLimits.Remove($Id)
        Set-Status ("RAM limit removed for " + $p.ProcessName)
        return $true
    }
    $script:ramLimits[$Id] = @{ mb=$Mb; trimmed=$false }
    $script:lastApplied = @{ Name=$p.ProcessName; Priority=''; Affinity=''; Limit=0; gpuLimit=0; ramLimit=$Mb }
    Set-Status ("RAM limit " + $Mb + " MB set on " + $p.ProcessName + " (trims working set when exceeded)")
    Write-Log ("RAM limit: " + $p.ProcessName + " -> " + $Mb + " MB")
    return $true
}

function Unlimit-Proc { param([int]$Id)
    $wasGpu = $script:gpuLimits.ContainsKey($Id)
    $null = $script:cpuLimits.Remove($Id)
    $null = $script:gpuLimits.Remove($Id)
    $null = $script:ramLimits.Remove($Id)
    $p = Get-Process -Id $Id -ErrorAction SilentlyContinue
    if ($null -ne $p) {
        if ($wasGpu) { $null = Resume-Proc $Id }
        if ($script:pbState.ContainsKey($Id)) {
            $st = $script:pbState[$Id]
            $null = Set-ProcPriority $p $st.orig
            $null = $script:pbState.Remove($Id)
        }
        Set-Status ("All limits removed for " + $p.ProcessName)
    } else { Set-Status 'Limits removed' }
    Close-ProcHandle $Id
}

function Restore-AllLimits {
    # safety net on exit: never leave a process suspended
    foreach ($id in @($script:gpuLimits.Keys)) {
        $st = $script:gpuLimits[$id]
        if ($st.suspended) { $null = Resume-Proc $id }
    }
    $script:gpuLimits = @{}
    $script:cpuLimits = @{}
    $script:ramLimits = @{}
    foreach ($id in @($script:throttleHandles.Keys)) { Close-ProcHandle $id }
}

# --- duty-cycle step: run while the main loop breathes (cheap when no limits) ---
function Step-GpuThrottle {
    if ($script:gpuLimits.Count -eq 0) { return }
    $now = [Environment]::TickCount
    foreach ($id in @($script:gpuLimits.Keys)) {
        if ($id -eq $PID) { $null = $script:gpuLimits.Remove($id); continue }
        $st = $script:gpuLimits[$id]
        $p = Get-Process -Id $id -ErrorAction SilentlyContinue
        if ($null -eq $p) { $null = $script:gpuLimits.Remove($id); Close-ProcHandle $id; continue }
        $onMs = [long]($st.pct * 1000.0 / 100.0)
        $phase = ($now - $st.t0) % 1000
        if ($phase -lt 0) { $phase += 1000 }
        if ($phase -ge $onMs) {
            if (-not $st.suspended) {
                if (Suspend-Proc $id) { $st.suspended = $true }
                else {
                    $null = $script:gpuLimits.Remove($id)
                    Close-ProcHandle $id
                    Add-Error ("Cannot throttle PID $id (elevated? run console as admin)")
                }
            }
        } elseif ($st.suspended) {
            if (Resume-Proc $id) { $st.suspended = $false }
        }
    }
}

# --- RAM trim step: empty the working set of processes over their cap ---
function Step-RamLimits {
    foreach ($id in @($script:ramLimits.Keys)) {
        $st = $script:ramLimits[$id]
        $row = $null
        foreach ($r in $script:procList) { if ($r.Id -eq $id) { $row = $r; break } }
        if ($null -eq $row) {
            $p = Get-Process -Id $id -ErrorAction SilentlyContinue
            if ($null -eq $p) { $null = $script:ramLimits.Remove($id) }
            continue
        }
        if ($row.Mem -gt ($st.mb * 1MB)) {
            $p = Get-Process -Id $id -ErrorAction SilentlyContinue
            if ($null -eq $p) { $null = $script:ramLimits.Remove($id); continue }
            if ($null -eq (Get-ThrottleNative)) { continue }
            try {
                $null = [PsplThrottle]::EmptyWorkingSet($p.Handle)
                $st.trimmed = $true
                Set-Status ("Trimmed RAM of " + $row.Name + " (over " + $st.mb + " MB cap)")
                Write-Log ("RAM trimmed: " + $row.Name + " (cap " + $st.mb + " MB)")
            } catch {}
        }
    }
}

# --- shared input handler for the three limit modes ---
function Handle-LimitMode { param($key, $char, [string]$Res)
    if ($key -eq 'Escape') { $script:mode = 'nav'; return }
    elseif ($key -eq 'Enter') {
        $row = Get-Selected
        $v = 0.0
        if ($null -ne $row -and [double]::TryParse($script:inputBuf, [ref]$v)) {
            $ok = $false
            $label = $Res.ToUpper()
            if ($Res -eq 'cpu') { $ok = Set-CpuLimit $row.Id $v }
            elseif ($Res -eq 'gpu') { if ($v -gt 100) { $v = 100 }; $ok = Set-GpuLimit $row.Id $v }
            elseif ($Res -eq 'ram') { $ok = Set-RamLimit $row.Id ([long]$v) }
            if ($ok) {
                if ($v -gt 0) {
                    Open-Menu ('Persist ' + $label + ' limit rule for ' + $row.Name + '?') (Get-YesNoItems 'persist:') 1
                } else { $script:mode = 'nav' }
            } else { $script:mode = 'nav' }
        } else {
            if ($null -eq $row) { Set-Status 'Nothing selected' }
            else { Set-Status 'Invalid number' }
            $script:mode = 'nav'
        }
    }
    elseif ($key -eq 'Backspace') {
        if ($script:inputBuf.Length -gt 0) { $script:inputBuf = $script:inputBuf.Substring(0, $script:inputBuf.Length - 1) }
    }
    else { try { if ($char -match '[\d.]') { $script:inputBuf += $char } } catch {} }
}

function Invoke-Kill { param([int]$Id, [string]$Name)
    if ($Id -eq $PID) { Set-Status 'Refusing to kill the console host running this app'; return }
    $p = Get-Process -Id $Id -ErrorAction SilentlyContinue
    if ($null -eq $p) { Set-Status ("PID $Id already gone"); return }
    try {
        $p.Kill()
        Set-Status ("Killed " + $Name + " (PID " + $Id + ")")
        Write-Log ("Killed: " + $Name + " (PID " + $Id + ")")
    } catch {
        Add-Error ("Cannot kill " + $Name + " (may need admin)")
    }
}

function Invoke-Launch { param([string]$CmdLine)
    if (-not $CmdLine) { Set-Status 'Nothing to launch'; return }
    try {
        Start-Process -FilePath $CmdLine -ErrorAction Stop
        Set-Status ("Launched: " + $CmdLine)
        Write-Log ("Launched: " + $CmdLine)
    } catch {
        try {
            Start-Process -FilePath 'cmd.exe' -ArgumentList @('/c', $CmdLine) -ErrorAction Stop
            Set-Status ("Launched via cmd: " + $CmdLine)
            Write-Log ("Launched (cmd): " + $CmdLine)
        } catch {
            Add-Error ("Cannot launch: " + $CmdLine)
        }
    }
}

function New-Rule {
    [pscustomobject]@{
        priority=''; affinity=@(); cpuLimit=0; gpuLimit=0; ramLimit=0; watchdog=$false
        wdPath=''; wdArgs=''; lastRestart=[datetime]::MinValue; restarts=0; enabled=$true
    }
}

function Save-Rules {
    try {
        $clean = @{}
        foreach ($k in @($script:rules.Keys)) {
            $r = $script:rules[$k]
            $clean[$k] = [ordered]@{
                priority=[string]$r.priority
                affinity=@($r.affinity)
                cpuLimit=[int]$r.cpuLimit
                gpuLimit=[int]$r.gpuLimit
                ramLimit=[int]$r.ramLimit
                watchdog=[bool]$r.watchdog
                wdPath=[string]$r.wdPath
                wdArgs=[string]$r.wdArgs
                restarts=[int]$r.restarts
                enabled=[bool]$r.enabled
            }
        }
        if (-not (Test-Path -LiteralPath $script:ConfigDir)) {
            $null = New-Item -ItemType Directory -Path $script:ConfigDir -Force -ErrorAction Stop
        }
        $clean | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $script:RulesFile -Encoding UTF8 -ErrorAction Stop
    } catch { Add-Error 'Failed to save rules file' }
}

function Load-Rules {
    try {
        if (Test-Path -LiteralPath $script:RulesFile) {
            $o = Get-Content -Raw -LiteralPath $script:RulesFile | ConvertFrom-Json -ErrorAction Stop
            foreach ($prop in $o.PSObject.Properties) {
                $r = $prop.Value
                $rule = New-Rule
                if ($null -ne $r) {
                    $rule.priority = [string]$r.priority
                    if ($null -ne $r.affinity) { $rule.affinity = @($r.affinity | ForEach-Object { [int]$_ }) }
                    $rule.cpuLimit   = [int]$r.cpuLimit
                    $rule.gpuLimit   = [int]$r.gpuLimit
                    $rule.ramLimit   = [int]$r.ramLimit
                    $rule.watchdog   = [bool]$r.watchdog
                    $rule.wdPath     = [string]$r.wdPath
                    $rule.wdArgs     = [string]$r.wdArgs
                    $rule.restarts   = [int]$r.restarts
                    $rule.enabled    = [bool]$r.enabled
                }
                $script:rules[$prop.Name] = $rule
            }
        }
    } catch { Add-Error 'Failed to load rules file' }
}

function Save-Prefs {
    try {
        if (-not (Test-Path -LiteralPath $script:ConfigDir)) {
            $null = New-Item -ItemType Directory -Path $script:ConfigDir -Force -ErrorAction Stop
        }
        [ordered]@{ sortKey=$script:sortKey; filter=$script:filter; proBalance=$script:proBalance; gpuOn=$script:gpuOn; refreshMs=$RefreshMs } |
            ConvertTo-Json | Set-Content -LiteralPath $script:PrefsFile -Encoding UTF8 -ErrorAction Stop
    } catch {}
}

function Load-Prefs {
    try {
        if (Test-Path -LiteralPath $script:PrefsFile) {
            $o = Get-Content -Raw -LiteralPath $script:PrefsFile | ConvertFrom-Json -ErrorAction Stop
            if ($null -ne $o) {
                if ($o.sortKey)  { $script:sortKey  = [string]$o.sortKey }
                if ($null -ne $o.filter) { $script:filter = [string]$o.filter }
                if ($null -ne $o.proBalance) { $script:proBalance = [bool]$o.proBalance }
                if ($null -ne $o.gpuOn) { $script:gpuOn = [bool]$o.gpuOn }
            }
        }
    } catch {}
}

function Apply-RuleToProcess { param($Proc, $Rule)
    if ($null -eq $Proc -or $null -eq $Rule) { return }
    if ($Rule.priority) { $null = Set-ProcPriority $Proc $Rule.priority }
    if ($Rule.affinity -and @($Rule.affinity).Count -gt 0) {
        try { $Proc.ProcessorAffinity = [IntPtr](Convert-CoresToMask (@($Rule.affinity) -join ',')) } catch {}
    }
    if ($Rule.cpuLimit -gt 0) {
        $orig = 'Normal'
        try { $orig = [string]$Proc.PriorityClass } catch {}
        $script:cpuLimits[$Proc.Id] = @{ limit=[double]$Rule.cpuLimit; orig=$orig; limited=$false }
    }
    if ($Rule.gpuLimit -gt 0 -and $Proc.Id -ne $PID) {
        $script:gpuLimits[$Proc.Id] = @{ pct=[double]$Rule.gpuLimit; suspended=$false; t0=[Environment]::TickCount }
    }
    if ($Rule.ramLimit -gt 0) {
        $script:ramLimits[$Proc.Id] = @{ mb=[long]$Rule.ramLimit; trimmed=$false }
    }
}

function Apply-AllRules {
    foreach ($name in @($script:rules.Keys)) {
        $rule = $script:rules[$name]
        if (-not $rule.enabled) { continue }
        $ps = @(Get-Process -Name $name -ErrorAction SilentlyContinue)
        foreach ($p in $ps) { Apply-RuleToProcess $p $rule }
    }
}

function Apply-RulesToNew {
    $nowNames = @{}
    foreach ($r in $script:procList) { $nowNames[$r.Name] = $true }
    foreach ($name in @($nowNames.Keys)) {
        if ($script:lastNames.ContainsKey($name)) { continue }
        if (-not $script:rules.ContainsKey($name)) { continue }
        $rule = $script:rules[$name]
        if (-not $rule.enabled) { continue }
        $p = Get-Process -Name $name -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($null -ne $p) {
            Apply-RuleToProcess $p $rule
            Set-Status ("Rule applied to new process: " + $name)
            Write-Log ("Rule applied to new process: " + $name)
        }
    }
    $script:lastNames = $nowNames
}

function Toggle-Watchdog {
    $row = Get-Selected
    if ($null -eq $row) { return }
    $n = $row.Name
    if (-not $script:rules.ContainsKey($n)) { $script:rules[$n] = New-Rule }
    $r = $script:rules[$n]
    if (-not $r.watchdog) {
        $pth = Get-ProcessPath $row.Id
        if (-not $pth) {
            Set-Status ("Cannot watchdog " + $n + ": no executable path known")
            return
        }
    }
    $r.watchdog = -not $r.watchdog
    if ($r.watchdog) {
        if (-not $r.wdPath) { $r.wdPath = Get-ProcessPath $row.Id }
        $r.enabled = $true
        Set-Status ("Watchdog ON for " + $n + " (restarts: " + $r.restarts + ")")
        Write-Log ("Watchdog ON: " + $n)
    } else {
        $r.wdPath = ''
        $r.wdArgs = ''
        Set-Status ("Watchdog OFF for " + $n)
        Write-Log ("Watchdog OFF: " + $n)
    }
    Save-Rules
}

function Remove-Rule { param([string]$Name)
    if (-not $Name) { Set-Status 'No rule name given'; return }
    if ($script:rules.ContainsKey($Name)) {
        $null = $script:rules.Remove($Name)
        # drop live-applied limits for every process of that name
        foreach ($r in @($script:procList)) {
            if ($r.Name -eq $Name) { Unlimit-Proc $r.Id }
        }
        Save-Rules
        Set-Status ("Rule removed for " + $Name)
        Write-Log ("Rule removed: " + $Name)
    } else {
        Set-Status ("No rule exists for " + $Name)
    }
}

# --- ProBalance: when the system is overloaded, briefly demote background hogs ---
function Step-ProBalance {
    $sys = $script:sys
    $now = Get-Date
    # restore expired demotions
    $expired = @()
    foreach ($id in @($script:pbState.Keys)) {
        if ($now -gt $script:pbState[$id].until) { $expired += $id }
    }
    foreach ($id in $expired) {
        $st = $script:pbState[$id]
        $p = Get-Process -Id $id -ErrorAction SilentlyContinue
        if ($null -ne $p) { $null = Set-ProcPriority $p $st.orig }
        $null = $script:pbState.Remove($id)
    }
    if (-not $script:proBalance -or $sys.totalCpu -lt 80) { return }
    $cand = @($script:procList | Where-Object {
        $_.Cpu -ge 15 -and $_.Priority -eq 'Normal' -and $_.Gpu -lt 40 -and
        $_.Name -ne $script:ownName -and $_.Name -ne 'Idle' -and $_.Name -ne 'System'
    })
    $cnt = 0
    foreach ($row in $cand) {
        if ($cnt -ge 5) { break }
        if ($script:pbState.ContainsKey($row.Id)) { continue }
        if ($script:cpuLimits.ContainsKey($row.Id)) { continue }
        $p = Get-Process -Id $row.Id -ErrorAction SilentlyContinue
        if ($null -eq $p) { continue }
        $hasWin = $false
        try { $hasWin = $p.MainWindowHandle -ne 0 } catch {}
        if ($hasWin) { continue }
        if (Set-ProcPriority $p 'BelowNormal') {
            $script:pbState[$row.Id] = @{ orig='Normal'; until=$now.AddSeconds(30) }
            $cnt++
        }
    }
    if ($cnt -gt 0) {
        Set-Status ("ProBalance: demoted " + $cnt + " background process(es) while system is loaded")
        Write-Log ("ProBalance demoted " + $cnt + " processes")
    }
}

# --- Soft CPU limiter: drop priority while a limited process is over its cap ---
function Step-CpuLimits {
    foreach ($id in @($script:cpuLimits.Keys)) {
        $st = $script:cpuLimits[$id]
        $row = $null
        foreach ($r in $script:procList) { if ($r.Id -eq $id) { $row = $r; break } }
        if ($null -eq $row) { $null = $script:cpuLimits.Remove($id); continue }
        $p = Get-Process -Id $id -ErrorAction SilentlyContinue
        if ($null -eq $p) { $null = $script:cpuLimits.Remove($id); continue }
        if ($row.Cpu -gt $st.limit -and -not $st.limited) {
            if (Set-ProcPriority $p 'BelowNormal') {
                $st.limited = $true
                Set-Status ("Limited " + $row.Name + " to " + $st.limit + "% CPU (priority -> BelowNormal)")
            }
        } elseif ($row.Cpu -lt ($st.limit * 0.7) -and $st.limited) {
            if (Set-ProcPriority $p $st.orig) {
                $st.limited = $false
                Set-Status ("Released " + $row.Name + " from CPU limit")
            }
        }
    }
}

# --- Watchdog: restart watched apps that have died ---
function Step-Watchdog {
    $now = Get-Date
    $names = @{}
    foreach ($r in $script:procList) { $names[$r.Name] = $true }
    foreach ($key in @($script:rules.Keys)) {
        $rule = $script:rules[$key]
        if (-not $rule.watchdog -or -not $rule.enabled) { continue }
        if ($names.ContainsKey($key)) { continue }
        if (-not $rule.wdPath -or -not (Test-Path -LiteralPath $rule.wdPath)) { continue }
        if (($now - $rule.lastRestart).TotalSeconds -lt 30) { continue }
        try {
            if ($rule.wdArgs) {
                Start-Process -FilePath $rule.wdPath -ArgumentList $rule.wdArgs -ErrorAction Stop
            } else {
                Start-Process -FilePath $rule.wdPath -ErrorAction Stop
            }
            $rule.lastRestart = $now
            $rule.restarts++
            Save-Rules
            Set-Status ("Watchdog restarted " + $key + " (x" + $rule.restarts + ")")
            Write-Log ("Watchdog restarted: " + $key)
        } catch {
            Add-Error ("Watchdog failed to restart " + $key)
        }
    }
}

# --- Details: extra process info (on-demand, cached) ---
function Get-CommandLine { param([int]$Id)
    if ($script:cmdCache.ContainsKey($Id)) {
        $c = $script:cmdCache[$Id]
        if (([datetime]::Now - $c.at).TotalSeconds -lt 15) { return $c.line }
    }
    $line = ''
    try {
        $w = Get-CimInstance Win32_Process -Filter "ProcessId=$Id" -ErrorAction Stop
        $line = [string]$w.CommandLine
    } catch {}
    $script:cmdCache[$Id] = @{ line=$line; at=[datetime]::Now }
    return $line
}

function Get-DetailLines { param($Row)
    $lines = New-Object System.Collections.Generic.List[string]
    $C = $script:C; $res = $C.reset
    try {
        $p = Get-Process -Id $Row.Id -ErrorAction SilentlyContinue
        $cmd = Get-CommandLine $Row.Id
        $resp = 'n/a'
        try { if ($null -ne $p) { $resp = [string]$p.Responding } } catch {}
        $started = 'n/a'
        try { if ($null -ne $p) { $started = [string]$p.StartTime } } catch {}
        $handles = 'n/a'
        try { if ($null -ne $p) { $handles = [string]$p.HandleCount } } catch {}
        $parent = 'n/a'
        try {
            $pp = Get-CimInstance Win32_Process -Filter "ProcessId=$($Row.Id)" -ErrorAction SilentlyContinue
            if ($null -ne $pp) { $parent = [string]$pp.ParentProcessId }
        } catch {}

        $lab = $C.cyan + $C.bold   # label style
        $lines.Add($lab + (Get-Marker) + ' Details: ' + $Row.Name + $res + $C.dim + '   (PID ' + $Row.Id + ')' + $res)
        $pth = Get-ProcessPath $Row.Id
        $lines.Add(' ' + $lab + 'Path' + $res + $C.dim + '       ' + $res + $(if ($pth) { $pth } else { $C.dim + 'n/a' + $res }))
        $lines.Add(' ' + $lab + 'CPU' + $res + $C.dim + '        ' + $res + (Get-PctColor $Row.Cpu) + $Row.Cpu + '%' + $res + $C.dim + '    Working set ' + $res + (Format-Bytes $Row.Mem) + $C.dim + '   Private ' + $res + (Format-Bytes $Row.Priv))
        $lines.Add(' ' + $lab + 'GPU' + $res + $C.dim + '        ' + $res + (Get-PctColor $Row.Gpu) + $Row.Gpu + '%' + $res + $C.dim + '    VRAM ' + $res + (Format-Bytes $Row.Vram))
        $lines.Add(' ' + $lab + 'Priority' + $res + $C.dim + '   ' + $res + $Row.Priority + $C.dim + '    Affinity cores ' + $res + $Row.Affinity)
        $lines.Add(' ' + $lab + 'Threads' + $res + $C.dim + '    ' + $res + $Row.Threads + $C.dim + '    Handles ' + $res + $handles + $C.dim + '    Parent PID ' + $res + $parent)
        $lines.Add(' ' + $lab + 'Responding' + $res + $C.dim + ' ' + $res + $(if ($resp -eq 'True') { $C.green + $resp + $res } elseif ($resp -eq 'False') { $C.red + $resp + $res } else { $resp }) + $C.dim + '    Started ' + $res + $started)
        $lines.Add(' ' + $lab + 'CommandLine' + $res + $C.dim + ' ' + $res + $(if ($cmd) { $cmd } else { $C.dim + 'n/a' + $res }))
        $lines.Add(' ' + $C.dim + ($script:chDash * 78) + $res)
        $lims = @()
        if ($script:cpuLimits.ContainsKey($Row.Id)) { $lims += ('CPU ' + $script:cpuLimits[$Row.Id].limit + '%') }
        if ($script:gpuLimits.ContainsKey($Row.Id)) { $lims += ('GPU ' + $script:gpuLimits[$Row.Id].pct + '%') }
        if ($script:ramLimits.ContainsKey($Row.Id)) { $lims += ('RAM ' + $script:ramLimits[$Row.Id].mb + ' MB') }
        if ($lims.Count) { $lines.Add(' ' + $lab + 'Limits' + $res + $C.dim + '    ' + $res + $C.yellow + ($lims -join '   ') + $res) }
        if ($script:rules.ContainsKey($Row.Name)) {
            $r = $script:rules[$Row.Name]
            $lines.Add(' ' + $lab + 'Rule' + $res + $C.dim + '      ' + $res +
                       'priority=' + $(if ($r.priority) { $r.priority } else { '-' }) +
                       $C.dim + ' affinity=' + $res + $(if (@($r.affinity).Count) { @($r.affinity) -join ',' } else { '-' }) +
                       $C.dim + ' cpu=' + $res + $(if ($r.cpuLimit) { $r.cpuLimit } else { '-' }) +
                       $C.dim + ' gpu=' + $res + $(if ($r.gpuLimit) { $r.gpuLimit } else { '-' }) +
                       $C.dim + ' ram=' + $res + $(if ($r.ramLimit) { $r.ramLimit } else { '-' }) +
                       $C.dim + ' watchdog=' + $res + $(if ($r.watchdog) { $C.green + 'ON' + $res } else { 'OFF' }))
        }
    } catch {
        $lines.Add($C.dim + '(could not read details for this process)' + $res)
    }
    return @($lines)
}

# ----------------------------------------------------------------------------
#  UI: input modes
# ----------------------------------------------------------------------------
function Start-Mode { param([string]$Mode, [string]$Prompt)
    $script:mode = $Mode
    $script:prompt = $Prompt
    $script:inputBuf = ''
}

function Handle-Key { param($K)
    $key  = $K.Key
    $char = [string]$K.KeyChar
    $ctrl  = (($K.Modifiers -band [ConsoleModifiers]::Control) -ne 0)
    $shift = (($K.Modifiers -band [ConsoleModifiers]::Shift) -ne 0)

    # Ctrl+C anywhere quits cleanly
    if ($ctrl -and ($key -eq 'C' -or $key -eq 'Break')) { $script:running = $false; return }

    # ---- Tab / Shift+Tab: universal next / previous navigation ----
    #   list        next/prev process row (wraps)        rules view  next/prev rule
    #   menu        next/prev item                       prompts     Tab=submit, Shift+Tab=cancel
    if ($key -eq 'Tab') {
        $tabNext = -not $shift
        switch ($script:mode) {
            'menu'   { if ($tabNext) { Menu-Move 1 } else { Menu-Move -1 }; return }
            'jump'   {
                if ($tabNext) {
                    $name = Jump-To $script:jumpBuf
                    $script:mode = 'nav'; $script:jumpBuf = ''
                    Set-Status ('Jumped to ' + $(if ($name) { $name } else { 'first match' }))
                } else {
                    $script:mode = 'nav'; $script:jumpBuf = ''
                    Set-Status 'Jump cancelled'
                }
                return
            }
            'filter' {
                if ($tabNext) {
                    $script:mode = 'nav'
                    $script:filter = $script:inputBuf
                    $script:viewOffset = 0
                    if ($script:filter) { Set-Status ("Filter: " + $script:filter) } else { Set-Status 'Filter cleared' }
                } else {
                    $script:mode = 'nav'; $script:inputBuf = ''
                    Set-Status 'Filter cleared'
                }
                return
            }
            'affinity'   { if ($tabNext) { $key = 'Enter' } else { $script:mode = 'nav'; return } }
            'limit:cpu'  { if ($tabNext) { $key = 'Enter' } else { $script:mode = 'nav'; return } }
            'limit:gpu'  { if ($tabNext) { $key = 'Enter' } else { $script:mode = 'nav'; return } }
            'limit:ram'  { if ($tabNext) { $key = 'Enter' } else { $script:mode = 'nav'; return } }
            'exec'       { if ($tabNext) { $key = 'Enter' } else { $script:mode = 'nav'; return } }
            default {
                # nav mode: rules view moves the rule highlight, help returns
                if ($script:view -eq 'rules') {
                    $names = @($script:rules.Keys | Sort-Object)
                    if ($names.Count) {
                        $script:ruleSel = ($script:ruleSel + $(if ($tabNext) { 1 } else { $names.Count - 1 })) % $names.Count
                    }
                } elseif ($script:view -eq 'main') {
                    if ($tabNext) { Move-Sel 1 -Wrap } else { Move-Sel -1 -Wrap }
                } else {
                    $script:view = 'main'   # help view: Tab returns to the list
                }
                return
            }
        }
    }

    # ---- modal input modes ----
    switch ($script:mode) {
        'menu'   { Handle-MenuKey $key $char; return }
        'jump'   { Handle-JumpKey $key $char; return }
        'filter' {
            if ($key -eq 'Escape') { $script:mode='nav'; $script:inputBuf=''; Set-Status 'Filter cleared' }
            elseif ($key -eq 'Enter') {
                $script:mode='nav'
                $script:filter = $script:inputBuf
                $script:viewOffset = 0
                if ($script:filter) { Set-Status ("Filter: " + $script:filter) } else { Set-Status 'Filter cleared' }
            }
            elseif ($key -eq 'Backspace') {
                if ($script:inputBuf.Length -gt 0) { $script:inputBuf = $script:inputBuf.Substring(0, $script:inputBuf.Length - 1) }
            }
            else {
                try { if ($char -match '[\p{L}\p{N} _.\-]') { $script:inputBuf += $char } } catch {}
            }
            return
        }
        'affinity' {
            if ($key -eq 'Escape') { $script:mode='nav'; return }
            elseif ($key -eq 'Enter') {
                $row = Get-Selected
                if ($null -ne $row) {
                    $spec = if ($script:inputBuf) { $script:inputBuf } else { 'all' }
                    if (Set-AffinityLive $row.Id $spec) {
                        Open-Menu ('Persist affinity rule for ' + $row.Name + '?') (Get-YesNoItems 'persist:') 1
                    }
                }
            }
            elseif ($key -eq 'Backspace') {
                if ($script:inputBuf.Length -gt 0) { $script:inputBuf = $script:inputBuf.Substring(0, $script:inputBuf.Length - 1) }
            }
            else { try { if ($char -match '[\d,\-]') { $script:inputBuf += $char } } catch {} }
            return
        }
        'limit:cpu' { Handle-LimitMode $key $char 'cpu'; return }
        'limit:gpu' { Handle-LimitMode $key $char 'gpu'; return }
        'limit:ram' { Handle-LimitMode $key $char 'ram'; return }
        'exec' {
            if ($key -eq 'Escape') { $script:mode='nav'; return }
            elseif ($key -eq 'Enter') { Invoke-Launch $script:inputBuf; $script:mode='nav' }
            elseif ($key -eq 'Backspace') {
                if ($script:inputBuf.Length -gt 0) { $script:inputBuf = $script:inputBuf.Substring(0, $script:inputBuf.Length - 1) }
            }
            else { $script:inputBuf += $char }
            return
        }
    }

    # ---- nav mode ----
    # help / rules views are modal: keys there navigate that view only
    if ($script:view -ne 'main') { Handle-ViewKey $key $char; return }

    switch ($key) {
        'UpArrow'    { Move-Sel -1 -Wrap }
        'DownArrow'  { Move-Sel 1 -Wrap }
        'PageUp'     { Move-Sel (-(Get-VisibleRowCount)) }
        'PageDown'   { Move-Sel (Get-VisibleRowCount) }
        'Home'       { $rows = Get-View; if ($rows.Count) { $script:selPid = $rows[0].Id; $script:viewOffset = 0 } }
        'End'        { $rows = Get-View; if ($rows.Count) { $script:selPid = $rows[$rows.Count-1].Id; Ensure-Visible } }
        'LeftArrow'  { Cycle-Sort -1 }
        'RightArrow' { Cycle-Sort 1 }
        'Enter'      { $script:showDetails = -not $script:showDetails; Set-Status $(if ($script:showDetails) { 'Details ON' } else { 'Details OFF' }) }
        'Escape'     { Open-Menu 'PSProcLasso' (Get-MainMenuItems) 0 }
    }
    $c = ''
    try { $c = $char.ToLowerInvariant() } catch {}
    switch ($c) {
        # one-key sorting: 1 = CPU, 2 = RAM, 3 = GPU
        '1' { Set-SortKey 'cpu' }
        '2' { Set-SortKey 'ram' }
        '3' { Set-SortKey 'gpu' }
        'q' { $script:running = $false }
        'h' { $script:view = 'help' }
        '?' { $script:view = 'help' }
        's' { Open-Menu 'Sort by' (Get-SortMenuItems) 0 }
        'p' {
            $row = Get-Selected
            if ($null -eq $row) { Set-Status 'Nothing selected' }
            else { Open-Menu ('Priority for ' + $row.Name) (Get-PriorityMenuItems) 0 }
        }
        'a' { Start-Mode 'affinity' 'Cores (e.g. 0-3,5  |  Enter=all cores): ' }
        'l' {
            $row = Get-Selected
            if ($null -eq $row) { Set-Status 'Nothing selected' }
            else { Open-Menu ('Set limit for ' + $row.Name) (Get-LimitMenuItems) 0 }
        }
        'k' {
            $row = Get-Selected
            if ($null -eq $row) { Set-Status 'Nothing selected' }
            else { Open-Menu ('Kill ' + $row.Name + '  (PID ' + $row.Id + ')?') (Get-YesNoItems 'kill:') 1 }
        }
        'w' { Toggle-Watchdog }
        'x' {
            $row = Get-Selected
            if ($null -eq $row) { Set-Status 'Nothing selected' }
            elseif (-not $script:rules.ContainsKey($row.Name)) { Set-Status ('No rule exists for ' + $row.Name) }
            else {
                $script:pendingRuleName = $row.Name
                Open-Menu ('Delete rule for ' + $row.Name + '?') (Get-YesNoItems 'ruledel:') 1
            }
        }
        'b' { $script:proBalance = -not $script:proBalance; Set-Status ("ProBalance " + $(if ($script:proBalance) { 'ON' } else { 'OFF' })) }
        'i' { $script:showDetails = -not $script:showDetails; Set-Status $(if ($script:showDetails) { 'Details ON (Enter/i to toggle)' } else { 'Details OFF' }) }
        'v' { Open-Menu 'View' (Get-ViewMenuItems) 0 }
        'r' { $script:view = 'rules'; $script:ruleSel = 0 }
        'e' { Start-Mode 'exec' 'Launch program or command: ' }
        'g' { $script:gpuOn = -not $script:gpuOn; Set-Status ("GPU sampling " + $(if ($script:gpuOn) { 'ON' } else { 'OFF' })) }
        'u' { $row = Get-Selected; if ($null -ne $row) { Unlimit-Proc $row.Id } }
        '/' { $script:mode = 'jump'; $script:jumpBuf = ''; Set-Status 'Jump: type a process name' }
    }
}

# ----------------------------------------------------------------------------
#  Mouse: console input records -> click columns to sort, click rows to select,
#  double-click for details, wheel to scroll, click menu items to apply.
# ----------------------------------------------------------------------------
function Enable-Mouse {
    $script:mouseOn = $false
    try {
        if (-not ('PSPL.Inp' -as [type])) {
            Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace PSPL {
  public static class Inp {
    [StructLayout(LayoutKind.Explicit)] public struct KEY_EVENT_RECORD {
      [FieldOffset(0)] public int bKeyDown;
      [FieldOffset(4)] public ushort wRepeatCount;
      [FieldOffset(6)] public ushort wVirtualKeyCode;
      [FieldOffset(8)] public ushort wVirtualScanCode;
      [FieldOffset(10)] public ushort UnicodeChar;
      [FieldOffset(12)] public uint dwControlKeyState;
    }
    [StructLayout(LayoutKind.Explicit)] public struct MOUSE_EVENT_RECORD {
      [FieldOffset(0)] public short X;
      [FieldOffset(2)] public short Y;
      [FieldOffset(4)] public uint dwButtonState;
      [FieldOffset(8)] public uint dwControlKeyState;
      [FieldOffset(12)] public uint dwEventFlags;
    }
    [StructLayout(LayoutKind.Explicit)] public struct INPUT_RECORD {
      [FieldOffset(0)] public ushort EventType;
      [FieldOffset(4)] public KEY_EVENT_RECORD KeyEvent;
      [FieldOffset(4)] public MOUSE_EVENT_RECORD MouseEvent;
    }
    [DllImport("kernel32.dll", SetLastError=true)] public static extern IntPtr GetStdHandle(int n);
    [DllImport("kernel32.dll", SetLastError=true)] public static extern bool GetConsoleMode(IntPtr h, out uint m);
    [DllImport("kernel32.dll", SetLastError=true)] public static extern bool SetConsoleMode(IntPtr h, uint m);
    [DllImport("kernel32.dll", SetLastError=true)] public static extern bool ReadConsoleInput(IntPtr h, out INPUT_RECORD rec, uint len, out uint num);
    [DllImport("kernel32.dll", SetLastError=true)] public static extern bool PeekConsoleInput(IntPtr h, out INPUT_RECORD rec, uint len, out uint num);
  }
}
'@ -ErrorAction Stop
        }
        $script:hIn = [PSPL.Inp]::GetStdHandle(-10)   # STD_INPUT_HANDLE
        if ($script:hIn -eq [IntPtr]::Zero) { return }
        [uint32]$m = 0
        if ([PSPL.Inp]::GetConsoleMode($script:hIn, [ref]$m)) {
            $script:oldInMode = $m
            # ENABLE_MOUSE_INPUT(0x10) | ENABLE_EXTENDED_FLAGS(0x80), clear QUICK_EDIT(0x40)
            $null = [PSPL.Inp]::SetConsoleMode($script:hIn, (($m -band (-bnot 0x40)) -bor 0x0010 -bor 0x0080))
        }
        $script:rec = New-Object 'PSPL.Inp+INPUT_RECORD'
        $script:mouseOn = $true
    } catch { $script:mouseOn = $false }
}

function Restore-Mouse {
    if ($script:mouseOn -and $script:hIn -ne [IntPtr]::Zero -and $null -ne $script:oldInMode) {
        try { $null = [PSPL.Inp]::SetConsoleMode($script:hIn, $script:oldInMode) } catch {}
    }
    $script:mouseOn = $false
}

function Read-Input {
    # returns $true when at least one input record was processed
    if (-not $script:mouseOn) {
        try { if ([Console]::KeyAvailable) { Handle-Key ([Console]::ReadKey($true)); return $true } } catch {}
        return $false
    }
    try {
        [uint32]$n = 0
        if (-not [PSPL.Inp]::PeekConsoleInput($script:hIn, [ref]$script:rec, 1, [ref]$n) -or $n -eq 0) { return $false }
        $et = $script:rec.EventType
        $null = [PSPL.Inp]::ReadConsoleInput($script:hIn, [ref]$script:rec, 1, [ref]$n)
        if ($et -eq 1) {   # KEY_EVENT
            if ($script:rec.KeyEvent.bKeyDown -ne 0) {
                Handle-Key (New-KeyInfo $script:rec.KeyEvent.wVirtualKeyCode $script:rec.KeyEvent.UnicodeChar $script:rec.KeyEvent.dwControlKeyState)
            }
            return $true
        } elseif ($et -eq 2) {   # MOUSE_EVENT
            Handle-Mouse $script:rec.MouseEvent
            return $true
        }
        return $true   # resize / focus / other records: drained
    } catch {
        try { if ([Console]::KeyAvailable) { Handle-Key ([Console]::ReadKey($true)); return $true } } catch {}
        return $false
    }
}

function New-KeyInfo { param([uint16]$Vk, [uint16]$Char, [uint32]$State)
    # convert a KEY_EVENT_RECORD to ConsoleKeyInfo (modifiers from control-key state)
    return New-Object System.ConsoleKeyInfo([char]$Char, [System.ConsoleKey]$Vk,
               (($State -band 0x0010) -ne 0), (($State -band 0x0002) -ne 0), (($State -band 0x000C) -ne 0))
}

function Handle-Mouse { param($M)
    $x = [int]$M.X; $y = [int]$M.Y
    $flags = [int]$M.dwEventFlags

    # wheel: scroll the list / move the menu highlight
    if ($flags -eq 4) {
        $delta = [int](($M.dwButtonState -shr 16) -band 0xFFFF)
        if ($script:mode -eq 'menu' -and $null -ne $script:menu) { Menu-Move $(if ($delta -gt 0) { -1 } else { 1 }); return }
        if ($delta -gt 0) { Move-Sel -3 -Wrap } else { Move-Sel 3 -Wrap }
        return
    }
    if (($M.dwButtonState -band 1) -eq 0) { return }   # left button press only
    if ($flags -ne 0 -and $flags -ne 2) { return }     # ignore move/drag events

    # menu: click an item to apply it (like a real GUI)
    if ($script:mode -eq 'menu' -and $null -ne $script:menu) {
        $rowsAvail = Get-VisibleRowCount
        $itemStartY = 3 + $rowsAvail + 1
        $idx = $y - $itemStartY
        if ($idx -ge 0 -and $idx -lt $script:menu.Items.Count) {
            $script:menu.Sel = $idx
            Apply-MenuItem $script:menu.Items[$idx]
        }
        return
    }
    if ($script:view -ne 'main' -or $script:mode -ne 'nav') { return }

    # column header (3rd line): click a column to sort by it
    if ($y -eq 2) {
        $w = 120; try { $w = [Console]::WindowWidth } catch {}
        $nameW = [math]::Max(8, [math]::Min(26, $w - 78))
        $hdrFmt = '{0,7} {1} {2,-' + $nameW + '} {3,6} {4,10} {5,6} {6,10} {7,-12} {8,-18}'
        $hdr = $hdrFmt -f 'PID','','NAME','CPU%','MEMORY','GPU%','VRAM','PRIORITY','AFFINITY'
        $zones = @()
        foreach ($t in @('PID','NAME','CPU%','MEMORY','GPU%','VRAM','PRIORITY','AFFINITY')) {
            $zones += [pscustomobject]@{ col=$t; start=$hdr.IndexOf($t) }
        }
        $zones = @($zones | Sort-Object start)
        for ($i = 0; $i -lt $zones.Count; $i++) {
            $end = if ($i + 1 -lt $zones.Count) { $zones[$i + 1].start } else { $hdr.Length }
            if ($x -ge $zones[$i].start -and $x -lt $end) {
                $key = @{ 'CPU%'='cpu'; 'MEMORY'='ram'; 'GPU%'='gpu'; 'NAME'='name'; 'PID'='pid' }[$zones[$i].col]
                if ($key) { Set-SortKey $key; Set-Status ("Sort: " + $key.ToUpper() + " (clicked)") }
                break
            }
        }
        return
    }

    # process row: select it; double-click toggles details
    if ($y -ge 3) {
        $rows = Get-View
        $idx = $script:viewOffset + ($y - 3)
        if ($idx -ge 0 -and $idx -lt $rows.Count) {
            if ($flags -eq 2) { $script:showDetails = -not $script:showDetails }
            $script:selPid = $rows[$idx].Id
        }
        return
    }
}

# ----------------------------------------------------------------------------
#  UI: screen rendering
# ----------------------------------------------------------------------------
function Write-Screen { param([string[]]$Lines)
    try {
        $w = 0; $h = 0
        try { $w = [Console]::WindowWidth;  $h = [Console]::WindowHeight } catch { $w = 120; $h = 40 }
        if ($w -lt 40) { $w = 40 }
        if ($h -lt 8)  { $h = 8 }
        [Console]::SetCursorPosition(0, 0)
        $sb = New-Object System.Text.StringBuilder
        for ($i = 0; $i -lt $h; $i++) {
            $line = ''
            if ($i -lt $Lines.Count) { $line = $Lines[$i] }
            $vl = Get-VisibleLen $line
            if ($vl -gt $w) {
                # trim while keeping ANSI codes balanced: just cut visible chars
                $line = $line.Substring(0, $w)
            } else {
                $null = $sb.Append($line)
                if ($vl -lt $w) { $null = $sb.Append(' ' * ($w - $vl)) }
            }
            $null = $sb.Append([char]10)
        }
        [Console]::Write($sb.ToString())
    } catch {}
}

function Build-Lines {
    $w = 0; $h = 0
    try { $w = [Console]::WindowWidth;  $h = [Console]::WindowHeight } catch { $w = 120; $h = 40 }
    if ($w -lt 40) { $w = 40 }
    if ($h -lt 8)  { $h = 8 }

    $C   = $script:C
    $res = $C.reset
    $lines = New-Object System.Collections.Generic.List[string]

    # ---- help / rules views ----
    if ($script:view -eq 'help') {
        $help = @(
            ('  ' + $C.bold + $C.cyan + (Get-Marker) + ' PSProcLasso' + $res + $C.dim + '  -  keyboard reference' + $res),
            '',
            '  Navigation          Up/Down/PgUp/PgDn/Home/End   move selection (Up/Down wrap around)',
            '                      Tab / Shift+Tab             next / previous (works everywhere)',
            '                      Left/Right                  cycle sort metric',
            '                      Esc                         open the main menu',
            '                      /                           jump to a process by typing its name',
            '  Mouse               click a column header        sort by CPU / RAM / GPU / name / PID',
            '                      click a process row          select it   double-click = details',
            '                      mouse wheel                 scroll   click a menu item = apply',
            '  Sort (s)            menu: CPU / RAM / GPU / Name / PID   (1=CPU 2=RAM 3=GPU one-key)',
            '  Filter (f)          type text, Enter to apply, Esc to clear',
            '  Priority (p)        menu: 0 Idle 1 Below 2 Normal 3 Above 4 High 5 Realtime',
            '  Affinity (a)        type cores e.g. "0-3,5"  (Enter = all cores)',
            '  Limits (l)          menu: CPU % (soft) / GPU % (throttle) / RAM MB (trim)',
            '  Kill (k)            menu with confirmation',
            '  Watchdog (w)        toggle auto-restart rule for selected process',
            '  Details (i/Enter)   deep info: path, threads, cmdline, rule',
            '  View (v)            menu: main list / details / rules / help',
            '  Rules (r)           persisted rules: Up/Down select, x delete, w watchdog',
            '  ProBalance (b)      toggle auto-demote of background hogs on load',
            '  Launch (e)          run a program / command',
            '  GPU (g)             toggle GPU sampling    u  remove all limits',
            '  Quit (q)            exit (Ctrl+C also works)',
            '',
            ('  Persisted rules live in: ' + $script:RulesFile),
            ('  Log: ' + $script:LogFile),
            ('  Note: priority/affinity/kill need admin for elevated processes.'),
            '',
            '  Press any key to return...'
        )
        # bold the leading section keyword of each plain help line
        foreach ($t in $help) {
            $mt = [regex]::Match($t, '^(  )([\w][\w ()]*?)( {4,})')
            if ($mt.Success) {
                $lines.Add($mt.Groups[1].Value + $C.bold + $mt.Groups[2].Value + $res + $C.dim + $mt.Groups[3].Value + $t.Substring($mt.Index + $mt.Length) + $res)
            } else {
                $lines.Add($t)
            }
        }
        return @($lines)
    }

    if ($script:view -eq 'rules') {
        $names = @($script:rules.Keys | Sort-Object)
        $lines.Add($C.bold + $C.cyan + (Get-Marker) + ' Persisted rules (' + $names.Count + ')' + $res)
        $lines.Add($C.dim + '  NAME              PRIORITY      AFFINITY   CPU  GPU  RAM  WATCHDOG  RESTARTS  PATH' + $res)
        $lines.Add($C.dim + '  ' + ($script:chDash * 82) + $res)
        if ($names.Count -eq 0) {
            $lines.Add($C.dim + '  (no rules yet - select a process, press p/a/l to create, w for watchdog)' + $res)
        } else {
            if ($script:ruleSel -lt 0 -or $script:ruleSel -ge $names.Count) { $script:ruleSel = 0 }
            for ($i = 0; $i -lt $names.Count; $i++) {
                $k = $names[$i]
                $r = $script:rules[$k]
                $aff = if (@($r.affinity).Count) { @($r.affinity) -join ',' } else { '-' }
                $pri = if ($r.priority) { $r.priority } else { '-' }
                $wd  = if ($r.watchdog) { $C.green + 'ON' + $res } else { $C.dim + 'OFF' + $res }
                $cl = if ($r.cpuLimit) { $r.cpuLimit } else { '-' }
                $gl = if ($r.gpuLimit) { $r.gpuLimit } else { '-' }
                $rl = if ($r.ramLimit) { $r.ramLimit } else { '-' }
                $pth = if ($r.wdPath) { $r.wdPath } else { $C.dim + '-' + $res }
                $mark = if ($i -eq $script:ruleSel) { (Get-Marker) } else { ' ' }
                $line = ('  {0} {1,-16} {2,-13} {3,-10} {4,4} {5,5} {6,5}  {7,-10} {8,7}  {9}' -f $mark, $k, $pri, $aff, $cl, $gl, $rl, $wd, $r.restarts, $pth)
                if ($i -eq $script:ruleSel) { $line = $C.rev + $line + $res }
                $lines.Add($line)
            }
        }
        $lines.Add('')
        $lines.Add($C.dim + '  [Up/Down] select   [Tab] next   [x] delete   [w] watchdog   [Enter/Esc/r] back' + $res)
        return @($lines)
    }

    # ---- header: live meters (█ filled / ░ empty, colored by load) ----
    $s = $script:sys
    $memPct = 0.0
    if ($s.ramTotal -gt 0) { $memPct = [math]::Round(($s.ramUsed / $s.ramTotal) * 100.0, 1) }
    $barW = 10
    if ($w -lt 112) { $barW = 8 }
    if ($w -lt 98)  { $barW = 6 }
    if ($w -lt 84)  { $barW = 4 }
    if ($w -lt 66)  { $barW = 3 }
    $gpuPctTxt = if ($script:gpuOn) { (Get-PctColor $s.gpuPct) + $s.gpuPct + '%' + $res } else { $C.dim + 'n/a' + $res }

    $t0 = $C.bold + $C.cyan + 'PSProcLasso' + $res + $C.dim + ' v1.0 ' + $res +
          $C.dim + ' ' + $script:chPipe + ' ' + $res +
          $C.bold + 'CPU ' + $res + (Get-Meter $s.totalCpu $barW) + ' ' + (Get-PctColor $s.totalCpu) + $s.totalCpu + '%' + $res +
          '  ' + $C.bold + 'RAM ' + $res + (Get-Meter $memPct $barW) + ' ' + (Get-PctColor $memPct) + $memPct + '%' + $res +
          '  ' + $C.bold + 'GPU ' + $res + (Get-Meter $s.gpuPct $barW) + ' ' + $gpuPctTxt +
          $C.dim + ' ' + $script:chPipe + ' ' + $res + $C.dim + (Get-Date -Format 'HH:mm:ss') + $res
    $lines.Add($t0)

    $avail = (Format-Bytes ([long]($s.availMB * 1MB)))
    $t1 = $C.dim + 'RAM ' + $res + $C.white + (Format-Bytes $s.ramUsed) + $res + $C.dim + '/' + $res + $C.white + (Format-Bytes $s.ramTotal) + $res +
          $C.dim + ' ' + $script:chDot + ' Available ' + $res + $C.white + $avail + $res +
          $C.dim + ' ' + $script:chDot + ' Standby ' + $res + $C.white + (Format-Bytes $s.standby) + $res +
          $(if ($script:gpuOn) { $C.dim + ' ' + $script:chDot + ' VRAM ' + $res + $C.white + (Format-Bytes $s.vramUsed) + $res + $(if ($s.vramTotal -gt 0) { $C.dim + '/' + $res + $C.white + (Format-Bytes $s.vramTotal) + $res } else { '' }) } else { $C.dim + ' ' + $script:chDot + ' VRAM n/a' + $res }) +
          $C.dim + '  ' + $script:chPipe + '  ' + $res +
          $C.dim + 'ProBalance ' + $res + $(if ($script:proBalance) { $C.green + 'ON' + $res } else { $C.red + 'OFF' + $res }) +
          $C.dim + '  GPU ' + $res + $(if ($script:gpuOn) { $C.green + 'ON' + $res } else { $C.red + 'OFF' + $res }) +
          $(if ($script:filter) { $C.dim + '  Filter ' + $res + $C.yellow + $script:filter + $res } else { '' }) +
          $C.dim + '  Sort ' + $res + $C.cyan + $C.bold + $script:sortKey.ToUpper() + $res + $C.cyan + ' ' + $script:chDown + $res
    $lines.Add($t1)

    # ---- column header (active sort column in bold cyan) ----
    $nameW = [math]::Max(8, [math]::Min(26, $w - 78))
    $hdrFmt = '{0,7} {1} {2,-' + $nameW + '} {3,6} {4,10} {5,6} {6,10} {7,-12} {8,-18}'
    $hdr = $hdrFmt -f 'PID','','NAME','CPU%','MEMORY','GPU%','VRAM','PRIORITY','AFFINITY'
    $sortCol = @{ cpu='CPU%'; ram='MEMORY'; gpu='GPU%'; name='NAME'; pid=' PID' }[$script:sortKey]
    if ($sortCol) { $hdr = $hdr.Replace($sortCol, $C.bold + $C.cyan + $sortCol + $res) }
    $lines.Add($C.dim + $hdr + $res)

    # ---- menu box (reserves space above the footer so it is always visible) ----
    $menuLines = $null
    if ($script:mode -eq 'menu' -and $null -ne $script:menu) {
        $m = $script:menu
        $ml = New-Object System.Collections.Generic.List[string]
        $ml.Add($C.bold + $C.cyan + (Get-Marker) + ' ' + $m.Title + $res + $C.dim + '    [arrows/Tab] move   [Enter] ok   [Esc/Left] back   [shortcut] apply' + $res)
        for ($i = 0; $i -lt $m.Items.Count; $i++) {
            $it = $m.Items[$i]
            $sh = if ($it.Shortcut) { $C.cyan + '(' + $it.Shortcut + ') ' + $res } else { '      ' }
            $selTxt = if ($i -eq $m.Sel) { (Get-Marker) + ' ' } else { '  ' }
            $txt = '   ' + $selTxt + $sh + $it.Label
            if ($i -eq $m.Sel) { $txt = $C.rev + $txt + $res }
            $ml.Add($txt)
        }
        $menuLines = @($ml)
    }

    # ---- process rows (scrolling window around the selection) ----
    $rows = Get-View
    $availRows = Get-VisibleRowCount
    $start = $script:viewOffset
    if ($start -gt $rows.Count) { $start = $rows.Count }
    $end = [math]::Min($rows.Count, $start + $availRows)

    $fmt = ('{0,7} {1} {2,-' + $nameW + '} {3,6} {4,10} {5,6} {6,10} {7,-12} {8,-18}')
    $marker = Get-Marker
    $shown = 0
    for ($i = $start; $i -lt $end; $i++) {
        $row = $rows[$i]
        $name = Sanitize-Name $row.Name
        if ($name.Length -gt $nameW) { $name = $name.Substring(0, $nameW - 1) + '~' }
        $isSel = ($row.Id -eq $script:selPid)

        # badges: * = limit active, W = watchdog, P = ProBalance-demoted
        $marks = ''
        if ($script:cpuLimits.ContainsKey($row.Id) -or $script:gpuLimits.ContainsKey($row.Id) -or $script:ramLimits.ContainsKey($row.Id)) { $marks += '*' }
        if ($script:rules.ContainsKey($row.Name) -and $script:rules[$row.Name].watchdog) { $marks += 'W' }
        if ($script:pbState.ContainsKey($row.Id)) { $marks += 'P' }
        if ($isSel) { $marks = $marker } elseif ($marks.Length -eq 0) { $marks = ' ' }

        $cpuS  = ('{0,6:N1}' -f $row.Cpu)
        $memS  = ('{0,10}' -f (Format-Bytes $row.Mem))
        $gpuS  = ('{0,6:N1}' -f $row.Gpu)
        $vramS = ('{0,10}' -f (Format-Bytes $row.Vram))
        $priS  = ('{0,-12}' -f $row.Priority)
        $affS  = ('{0,-18}' -f $row.Affinity)
        $idS   = ('{0,7}' -f $row.Id)

        $line = $fmt -f $idS, $marks, $name, $cpuS, $memS, $gpuS, $vramS, $priS, $affS

        if (-not $script:noAnsi) {
            if ($isSel) {
                $line = $C.rev + $line + $res
            } else {
                # subtle hierarchy: PID dim, usage cells colored by load
                $line = [regex]::Replace($line, [regex]::Escape($idS), $C.dim + $idS + $res, 1)
                if ($row.Cpu -ge 80) { $line = [regex]::Replace($line, [regex]::Escape($cpuS), $C.red + $C.bold + $cpuS + $res, 1) }
                elseif ($row.Cpu -ge 50) { $line = [regex]::Replace($line, [regex]::Escape($cpuS), $C.yellow + $cpuS + $res, 1) }
                if ($row.Gpu -ge 80) { $line = [regex]::Replace($line, [regex]::Escape($gpuS), $C.red + $C.bold + $gpuS + $res, 1) }
                elseif ($row.Gpu -ge 50) { $line = [regex]::Replace($line, [regex]::Escape($gpuS), $C.yellow + $gpuS + $res, 1) }
                if ($marks -eq 'W') { $line = $line.Replace(' W ', ' ' + $C.magenta + 'W' + $res + ' ') }
                elseif ($marks -eq '*') { $line = $line.Replace(' * ', ' ' + $C.yellow + '*' + $res + ' ') }
                elseif ($marks -eq 'P') { $line = $line.Replace(' P ', ' ' + $C.blue + 'P' + $res + ' ') }
            }
        }
        $lines.Add($line)
        $shown++
    }
    while ($shown -lt $availRows) { $lines.Add(''); $shown++ }

    # ---- details box (hidden while a menu is open so the menu stays visible) ----
    if ($script:showDetails -and $script:mode -ne 'menu') {
        $row = Get-Selected
        if ($null -ne $row) {
            foreach ($d in (Get-DetailLines $row)) { $lines.Add($d) }
        } else {
            $lines.Add($C.dim + '(nothing selected)' + $res)
        }
    }

    if ($null -ne $menuLines) { foreach ($t in $menuLines) { $lines.Add($t) } }

    # ---- footer ----
    $status = $script:status
    if ($script:errors.Count -gt 0) {
        $errs = @($script:errors)
        $status = $status + '   [!] ' + ($errs -join ' | ')
    }
    if ($status.Length -gt $w) { $status = $status.Substring(0, $w) }
    $lines.Add($C.dim + $status + $res)

    $hint = ''
    if ($script:mode -eq 'menu') {
        $hint = $C.dim + 'Menu: [arrows] move   [Enter] select   [Esc] close   [left arrow] back   [shortcut] apply' + $res
    } elseif ($script:mode -eq 'jump') {
        $hint = '> jump: ' + $script:jumpBuf + $C.yellow + '_' + $res
    } elseif ($script:mode -ne 'nav') {
        $hint = '> ' + $script:prompt + $script:inputBuf + $C.yellow + '_' + $res
    } else {
        $hint = $C.dim + '[Tab]next [Shift+Tab]prev  [Esc]menu [q]uit [1]CPU [2]RAM [3]GPU [f]ilter [/]jump [p]riority [a]ffinity [l]imit [k]ill [w]atchdog [v]iew [r]ules [b]alance [g]pu [?]help' + $res
    }
    if ($hint.Length -gt $w) { $hint = $hint.Substring(0, $w) }
    $lines.Add($hint)

    return @($lines)
}

# ----------------------------------------------------------------------------
#  Entry points
# ----------------------------------------------------------------------------
function Invoke-Snapshot {
    # Headless one-shot JSON snapshot for scripts / dashboards / tooling.
    # Prints a single JSON object to stdout and exits.  Never throws.
    try { Update-All; Update-All } catch {}
    $s = $script:sys
    $rows = @()
    foreach ($r in $script:procList) {
        $rows += [pscustomobject]@{
            pid=$r.Id; name=$r.Name; cpu=$r.Cpu; mem=$r.Mem; gpu=$r.Gpu
            vram=$r.Vram; priority=$r.Priority; affinity=$r.Affinity; threads=$r.Threads
        }
    }
    $obj = [pscustomobject]@{
        app='PSProcLasso'; version='1.0'; timestamp=(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
        totalCpu=$s.totalCpu; ramUsed=$s.ramUsed; ramTotal=$s.ramTotal; availMB=$s.availMB
        gpuPct=$s.gpuPct; vramUsed=$s.vramUsed; vramTotal=$s.vramTotal; standby=$s.standby
        gpuOn=$script:gpuOn; rules=$script:rules.Count; errors=$script:errorCount
        processes=$rows.Count; procs=$rows
    }
    $obj | ConvertTo-Json -Depth 4 -Compress
    exit 0
}

function Invoke-SelfTest {
    Write-Output '=== PSProcLasso self-test ==='
    Write-Output ('PowerShell : ' + $PSVersionTable.PSVersion)
    Write-Output ('OS         : ' + [System.Environment]::OSVersion.VersionString)
    Write-Output ('Logical CPUs: ' + [System.Environment]::ProcessorCount)
    $errs0 = $script:errorCount
    try {
        Update-All
        Update-All   # second pass gives real CPU deltas
    } catch {
        Write-Output ('FATAL: ' + $_.Exception.Message)
        exit 1
    }
    $s = $script:sys
    Write-Output ('Total CPU  : ' + $s.totalCpu + '%')
    Write-Output ('RAM        : ' + (Format-Bytes $s.ramUsed) + ' / ' + (Format-Bytes $s.ramTotal) + '  (avail ' + $s.availMB + ' MB)')
    Write-Output ('GPU        : ' + $(if ($script:gpuOn) { $s.gpuPct.ToString() + '%  VRAM ' + (Format-Bytes $s.vramUsed) + $(if ($s.vramTotal -gt 0) { '/' + (Format-Bytes $s.vramTotal) } else { '' }) } else { 'n/a' }))
    Write-Output ('Standby    : ' + (Format-Bytes $s.standby))
    Write-Output ('Processes  : ' + $script:procList.Count)
    $top = @($script:procList | Sort-Object -Property Cpu -Descending -ErrorAction SilentlyContinue | Select-Object -First 10)
    Write-Output ''
    Write-Output ('  {0,7}  {1,-24} {2,7} {3,10} {4,6} {5,10}' -f 'PID','NAME','CPU%','MEM','GPU%','VRAM')
    foreach ($r in $top) {
        Write-Output ('  {0,7}  {1,-24} {2,7:N1} {3,10} {4,6:N1} {5,10}' -f $r.Id, (Sanitize-Name $r.Name), $r.Cpu, (Format-Bytes $r.Mem), $r.Gpu, (Format-Bytes $r.Vram))
    }
    $newErrs = $script:errorCount - $errs0
    Write-Output ''
    if ($newErrs -eq 0) {
        Write-Output 'RESULT: OK - sampled every process with zero exceptions.'
    } else {
        Write-Output ("RESULT: " + $newErrs + " non-fatal error(s) swallowed:")
        foreach ($e in @($script:errors)) { Write-Output ('   - ' + $e) }
    }
    exit 0
}

function Invoke-UiTest {
    # Run the full rendering + input-handling stack headlessly (no console
    # needed) against a throwaway config dir, to prove the UI never throws.
    Write-Output '=== PSProcLasso UI test ==='
    $script:ConfigDir = Join-Path ([System.IO.Path]::GetTempPath()) ('pspl-ui-' + $PID)
    $script:RulesFile = Join-Path $script:ConfigDir 'rules.json'
    $script:PrefsFile = Join-Path $script:ConfigDir 'prefs.json'
    $script:LogFile   = Join-Path $script:ConfigDir 'events.log'
    $errs0 = $script:errorCount

    # seed a rule so the rules view has content
    $script:rules = @{}
    $script:rules['notepad'] = New-Rule
    $script:rules['notepad'].priority = 'Normal'
    $script:rules['notepad'].watchdog = $true
    $script:rules['notepad'].wdPath  = 'C:\Windows\System32\notepad.exe'

    try { Update-All; Update-All } catch { Write-Output ('FATAL sample: ' + $_.Exception.Message); exit 1 }

    # every sort order, main view
    foreach ($sk in @('cpu','ram','gpu','name','pid')) {
        $script:sortKey = $sk
        $lines = Build-Lines
        Write-Output ('view[' + $sk + ']: lines=' + $lines.Count + ' first=' + (Get-VisibleLen $lines[0]) + ' visible chars')
    }
    # show what the main view actually renders (ANSI stripped)
    $script:sortKey = 'cpu'
    $sample = Build-Lines
    Write-Output '--- main view (first 4 lines, ANSI stripped) ---'
    foreach ($i in 0..3) {
        $plain = $sample[$i] -replace "$($script:esc)\[[0-9;]*m", ''
        Write-Output ('  |' + $plain + '|')
    }

    # help and rules views
    $script:view = 'help';  $h = Build-Lines; Write-Output ('help view lines: ' + $h.Count)
    $script:view = 'rules'; $r = Build-Lines; Write-Output ('rules view lines: ' + $r.Count)
    $script:view = 'main'

    # details for the current top process
    $script:sortKey = 'cpu'
    $top = Get-View | Select-Object -First 1
    if ($null -ne $top) {
        $script:selPid = $top.Id
        $d = Get-DetailLines $top
        Write-Output ('details for ' + $top.Name + ': lines=' + $d.Count)
    }

    function K($ch, $key, $ctrl, $shift) { New-Object System.ConsoleKeyInfo($ch, $key, $shift, $false, $ctrl) }

    # arrow nav (selection moves, no exceptions)
    $script:running = $true; $script:mode = 'nav'; $script:view = 'main'
    $before = $script:selPid
    Handle-Key (K ([char]0) ([System.ConsoleKey]::UpArrow) $false $false)
    Write-Output ('arrow ok, selPid=' + $script:selPid)

    # Tab navigates: next row, then Shift+Tab back to the original selection
    $tabStart = $script:selPid
    Handle-Key (K ([char]9) ([System.ConsoleKey]::Tab) $false $false)
    $tabMid = $script:selPid
    Handle-Key (K ([char]9) ([System.ConsoleKey]::Tab) $false $true)
    Write-Output ('tab next=' + $tabMid + ' shift+tab back=' + $script:selPid + ' ok=' + ($script:selPid -eq $tabStart))

    # wrap-around: Home to the first row, UpArrow wraps to the last row
    Handle-Key (K ([char]0) ([System.ConsoleKey]::Home) $false $false)
    $firstId = $script:selPid
    Handle-Key (K ([char]0) ([System.ConsoleKey]::UpArrow) $false $false)
    $rowsNow = Get-View
    $lastId = $rowsNow[$rowsNow.Count - 1].Id
    Write-Output ('wrap: first=' + $firstId + ' up-from-first=' + $script:selPid + ' last=' + $lastId + ' ok=' + ($script:selPid -eq $lastId))
    Handle-Key (K ([char]0) ([System.ConsoleKey]::DownArrow) $false $false)
    Write-Output ('wrap back to first ok=' + ($script:selPid -eq $firstId))

    # Esc opens the main menu; submenu: Down to 'Priority ...', Enter, then Left back, Esc close
    Handle-Key (K ([char]27) ([System.ConsoleKey]::Escape) $false)
    Write-Output ('main menu: title=' + $script:menu.Title + ' items=' + $script:menu.Items.Count)
    # Tab moves the menu highlight, Shift+Tab returns it
    Handle-Key (K ([char]9) ([System.ConsoleKey]::Tab) $false $false)
    $tabSel = $script:menu.Sel
    Handle-Key (K ([char]9) ([System.ConsoleKey]::Tab) $false $true)
    Write-Output ('menu tab: sel=' + $tabSel + ' shift+tab back=' + $script:menu.Sel + ' ok=' + ($script:menu.Sel -eq 0))
    Handle-Key (K ([char]0) ([System.ConsoleKey]::DownArrow) $false)
    Handle-Key (K ([char]13) ([System.ConsoleKey]::Enter) $false)
    Write-Output ('submenu: title=' + $script:menu.Title)
    Handle-Key (K ([char]0) ([System.ConsoleKey]::LeftArrow) $false)
    Write-Output ('back to main menu: title=' + $script:menu.Title)
    Handle-Key (K ([char]27) ([System.ConsoleKey]::Escape) $false)
    Write-Output ('menu closed, mode=' + $script:mode)

    # sort menu via 's', apply RAM with its shortcut letter (alias c/r/g/n/p -> 1-5)
    Handle-Key (K ([char]'s') ([System.ConsoleKey]::S) $false)
    Handle-Key (K ([char]'r') ([System.ConsoleKey]::R) $false)
    Write-Output ('after sort menu: sortKey=' + $script:sortKey + ' mode=' + $script:mode)
    # one-key sorting: 1=CPU, 3=GPU, 2=RAM
    Handle-Key (K ([char]'1') ([System.ConsoleKey]::D1) $false)
    Write-Output ('1 -> sortKey=' + $script:sortKey)
    Handle-Key (K ([char]'3') ([System.ConsoleKey]::D3) $false)
    Write-Output ('3 -> sortKey=' + $script:sortKey)
    Handle-Key (K ([char]'2') ([System.ConsoleKey]::D2) $false)
    Write-Output ('2 -> sortKey=' + $script:sortKey)

    # mouse: click column headers to sort, click rows to select, wheel scrolls, menu click applies
    $wM = 120; try { $wM = [Console]::WindowWidth } catch {}
    $nwM = [math]::Max(8, [math]::Min(26, $wM - 78))
    $hdrM = ('{0,7} {1} {2,-' + $nwM + '} {3,6} {4,10} {5,6} {6,10} {7,-12} {8,-18}') -f 'PID','','NAME','CPU%','MEMORY','GPU%','VRAM','PRIORITY','AFFINITY'
    Handle-Mouse ([pscustomobject]@{ X=$hdrM.IndexOf('GPU%'); Y=2; dwButtonState=1; dwEventFlags=0 })
    Write-Output ('click GPU col -> sortKey=' + $script:sortKey)
    Handle-Mouse ([pscustomobject]@{ X=$hdrM.IndexOf('MEMORY'); Y=2; dwButtonState=1; dwEventFlags=0 })
    Write-Output ('click RAM col -> sortKey=' + $script:sortKey)
    $rowsM = Get-View
    Handle-Mouse ([pscustomobject]@{ X=20; Y=5; dwButtonState=1; dwEventFlags=0 })
    $expM = $rowsM[$script:viewOffset + 2].Id
    Write-Output ('click row -> selPid=' + $script:selPid + ' ok=' + ($script:selPid -eq $expM))
    $selBefore = $script:selPid
    Handle-Mouse ([pscustomobject]@{ X=20; Y=10; dwButtonState=0x00780000; dwEventFlags=4 })
    Write-Output ('wheel moved ok=' + ($script:selPid -ne $selBefore))
    # menu click applies: open the sort menu, click the RAM item (index 1)
    Handle-Key (K ([char]'s') ([System.ConsoleKey]::S) $false)
    $availM = Get-VisibleRowCount
    $itemY = 3 + $availM + 1 + 1
    Handle-Mouse ([pscustomobject]@{ X=20; Y=$itemY; dwButtonState=1; dwEventFlags=0 })
    Write-Output ('menu click -> sortKey=' + $script:sortKey + ' mode=' + $script:mode)

    # input-record -> ConsoleKeyInfo conversion: synthesize a 'g' KEY_EVENT_RECORD
    Enable-Mouse   # defines the PSPL.Inp native types (no console here, so it only defines + disables)
    if ('PSPL.Inp' -as [type]) {
        $krec = New-Object 'PSPL.Inp+KEY_EVENT_RECORD'
        $krec.wVirtualKeyCode = [uint16]0x47   # VK_G
        $krec.UnicodeChar     = [uint16][char]'g'
        $krec.dwControlKeyState = [uint32]0
        $gpuBefore = $script:gpuOn
        Handle-Key (New-KeyInfo $krec.wVirtualKeyCode $krec.UnicodeChar $krec.dwControlKeyState)
        Write-Output ('key record -> gpuOn=' + $script:gpuOn + ' ok=' + ($script:gpuOn -ne $gpuBefore))
    } else {
        Write-Output 'key record conversion: type unavailable'
    }

    # filter: type then Enter
    Handle-Key (K ([char]'f') ([System.ConsoleKey]::F) $false)
    Handle-Key (K ([char]'o') ([System.ConsoleKey]::O) $false)
    Handle-Key (K ([char]13) ([System.ConsoleKey]::Enter) $false)
    Write-Output ('after filter: filter=' + $script:filter + ' mode=' + $script:mode)
    $script:filter = ''
    $script:viewOffset = 0

    # priority menu flow on a real (non-system) process: Normal is a no-op
    $target = Get-View | Where-Object { $_.Id -ne $PID -and $_.Priority -eq 'Normal' -and $_.Name -notin @('System','Idle','Registry') } | Select-Object -First 1
    if ($null -ne $target) {
        $script:selPid = $target.Id
        Handle-Key (K ([char]'p') ([System.ConsoleKey]::P) $false)
        Write-Output ('priority menu: title=' + $script:menu.Title)
        Handle-Key (K ([char]'2') ([System.ConsoleKey]::D2) $false)   # shortcut: Normal
        if ($script:mode -eq 'menu') { Handle-Key (K ([char]'n') ([System.ConsoleKey]::N) $false) }  # don't persist
        Write-Output ('priority flow ok, mode=' + $script:mode)
    }

    # unified limit flow: 'l' opens the resource menu, pick CPU, type a value, persist-decline
    $lt = Get-View | Where-Object { $_.Id -ne $PID -and $_.Name -notin @('System','Idle','Registry','explorer') } | Select-Object -First 1
    if ($null -ne $lt) {
        $script:selPid = $lt.Id
        Handle-Key (K ([char]'l') ([System.ConsoleKey]::L) $false)
        Write-Output ('limit menu: title=' + $script:menu.Title + ' items=' + $script:menu.Items.Count)
        Handle-Key (K ([char]'1') ([System.ConsoleKey]::D1) $false)   # CPU
        Handle-Key (K ([char]'2') ([System.ConsoleKey]::D2) $false)
        Handle-Key (K ([char]'5') ([System.ConsoleKey]::D5) $false)
        Handle-Key (K ([char]13) ([System.ConsoleKey]::Enter) $false)
        if ($script:mode -eq 'menu') { Handle-Key (K ([char]'n') ([System.ConsoleKey]::N) $false) }
        Write-Output ('cpu limit active: ' + $script:cpuLimits.ContainsKey($lt.Id))
        # GPU limit: pick '2', type 50, Enter, persist-decline
        Handle-Key (K ([char]'l') ([System.ConsoleKey]::L) $false)
        Handle-Key (K ([char]'2') ([System.ConsoleKey]::D2) $false)
        Handle-Key (K ([char]'5') ([System.ConsoleKey]::D5) $false)
        Handle-Key (K ([char]'0') ([System.ConsoleKey]::D0) $false)
        Handle-Key (K ([char]13) ([System.ConsoleKey]::Enter) $false)
        if ($script:mode -eq 'menu') { Handle-Key (K ([char]'n') ([System.ConsoleKey]::N) $false) }
        Write-Output ('gpu limit active: ' + $script:gpuLimits.ContainsKey($lt.Id))
        # RAM limit: pick '3', type 256 MB, Enter, persist-decline
        Handle-Key (K ([char]'l') ([System.ConsoleKey]::L) $false)
        Handle-Key (K ([char]'3') ([System.ConsoleKey]::D3) $false)
        Handle-Key (K ([char]'2') ([System.ConsoleKey]::D2) $false)
        Handle-Key (K ([char]'5') ([System.ConsoleKey]::D5) $false)
        Handle-Key (K ([char]'6') ([System.ConsoleKey]::D6) $false)
        Handle-Key (K ([char]13) ([System.ConsoleKey]::Enter) $false)
        if ($script:mode -eq 'menu') { Handle-Key (K ([char]'n') ([System.ConsoleKey]::N) $false) }
        Write-Output ('ram limit active: ' + $script:ramLimits.ContainsKey($lt.Id))
        # exercise the throttle + trim steppers (no loop is running, so nothing suspends)
        Step-GpuThrottle
        Step-RamLimits
        Write-Output ('steps ok, mode=' + $script:mode)
        # 'u' removes all three at once
        Handle-Key (K ([char]'u') ([System.ConsoleKey]::U) $false)
        Write-Output ('after u: cpu=' + $script:cpuLimits.ContainsKey($lt.Id) +
                      ' gpu=' + $script:gpuLimits.ContainsKey($lt.Id) +
                      ' ram=' + $script:ramLimits.ContainsKey($lt.Id) +
                      ' mode=' + $script:mode)
    } else {
        Write-Output 'limit flow: no eligible target process found'
    }

    # kill confirm menu (decline)
    Handle-Key (K ([char]'k') ([System.ConsoleKey]::K) $false)
    Write-Output ('kill menu: title=' + $script:menu.Title)
    Handle-Key (K ([char]'n') ([System.ConsoleKey]::N) $false)
    Write-Output ('kill-decline ok, mode=' + $script:mode)

    # quick-jump: '/' + type + Enter
    Handle-Key (K ([char]'/') ([System.ConsoleKey]::Divide) $false)
    Handle-Key (K ([char]'c') ([System.ConsoleKey]::C) $false)
    Handle-Key (K ([char]'h') ([System.ConsoleKey]::H) $false)
    Write-Output ('jump buffer: ' + $script:jumpBuf)
    Handle-Key (K ([char]13) ([System.ConsoleKey]::Enter) $false)
    Write-Output ('jump done, mode=' + $script:mode + ' selPid=' + $script:selPid)

    # rules view: select, delete-confirm (decline), Esc back
    Handle-Key (K ([char]'r') ([System.ConsoleKey]::R) $false)
    Write-Output ('rules view, ruleSel=' + $script:ruleSel)
    Handle-Key (K ([char]0) ([System.ConsoleKey]::DownArrow) $false)
    Handle-Key (K ([char]'x') ([System.ConsoleKey]::X) $false)
    Write-Output ('rule delete menu: title=' + $script:menu.Title)
    Handle-Key (K ([char]'n') ([System.ConsoleKey]::N) $false)
    Handle-Key (K ([char]27) ([System.ConsoleKey]::Escape) $false)
    Write-Output ('back to main, view=' + $script:view)

    # scrolling: End jumps to the last row and scrolls, Home returns to the top
    Handle-Key (K ([char]0) ([System.ConsoleKey]::End) $false)
    Write-Output ('End: viewOffset=' + $script:viewOffset)
    Handle-Key (K ([char]0) ([System.ConsoleKey]::Home) $false)
    Write-Output ('Home: viewOffset=' + $script:viewOffset)

    # rules save / load round-trip
    Save-Rules
    $script:rules = @{}
    Load-Rules
    Write-Output ('rules round-trip count: ' + $script:rules.Count)

    # Ctrl+C quits
    Handle-Key (K ([char]'c') ([System.ConsoleKey]::C) $true)
    Write-Output ('ctrl+c handled, running=' + $script:running)
    $script:running = $true

    $newErrs = $script:errorCount - $errs0
    Write-Output ''
    if ($newErrs -eq 0) {
        Write-Output 'RESULT: UI OK - all views rendered and all input paths ran with zero exceptions.'
    } else {
        Write-Output ("RESULT: " + $newErrs + " non-fatal error(s) (all caught). See " + $script:LogFile)
    }
    exit 0
}

function Invoke-Monitor {
    Write-Output ('PSProcLasso monitor mode - Ctrl+C to stop. Refresh ' + $script:RefreshMs + ' ms')
    Write-Output ''
    # prime so the first snapshot already shows real CPU%
    Update-All
    Start-Sleep -Milliseconds 1100
    while ($true) {
        Update-All
        Apply-RulesToNew
        Step-CpuLimits
        Step-GpuThrottle
        Step-RamLimits
        Step-ProBalance
        Step-Watchdog
        $s = $script:sys
        Write-Output ('=== ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + '   CPU ' + $s.totalCpu + '%   RAM ' +
            (Format-Bytes $s.ramUsed) + '/' + (Format-Bytes $s.ramTotal) +
            $(if ($script:gpuOn) { '   GPU ' + $s.gpuPct + '%' } else { '' }) + ' ===')
        $rows = Get-View | Select-Object -First $MaxRows
        Write-Output ('  {0,7}  {1,-24} {2,7} {3,10} {4,6} {5,10}' -f 'PID','NAME','CPU%','MEM','GPU%','VRAM')
        foreach ($r in $rows) {
            Write-Output ('  {0,7}  {1,-24} {2,7:N1} {3,10} {4,6:N1} {5,10}' -f $r.Id, (Sanitize-Name $r.Name), $r.Cpu, (Format-Bytes $r.Mem), $r.Gpu, (Format-Bytes $r.Vram))
        }
        Write-Output ''
        Start-Sleep -Milliseconds $script:RefreshMs
    }
}

function Invoke-Tui {
    # fall back to monitor mode when there is no real console to draw on
    $ok = $true
    try {
        if ([Console]::IsInputRedirected -or [Console]::IsOutputRedirected) { $ok = $false }
    } catch { $ok = $false }
    if (-not $ok) {
        Write-Warning 'No interactive console detected - switching to monitor mode.'
        Invoke-Monitor
        return
    }

    Enable-Ansi
    try { [Console]::CursorVisible = $false } catch {}
    try { [Console]::TreatControlCAsInput = $true } catch {}
    try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}
    Enable-Mouse   # click columns to sort, click rows to select, wheel to scroll

    try {
        # prime: build the open counter set once, then take the first samples so
        # the very first screen is already live (GPU included)
        Init-Counters
        Update-Gpu
        Update-Fast
        Apply-AllRules
        Apply-RulesToNew
        Write-Log 'Session started'
        $script:lastGpu     = Get-Date
        $script:lastGpuInit = Get-Date
        Start-Sleep -Milliseconds 350   # give CPU deltas a real window so the first screen is live

        while ($script:running) {
            $cycleStart = [datetime]::Now

            # 1. fast sample: processes + cheap system counters (~50ms)
            try { Update-Fast } catch { Add-Error ("procs: " + $_.Exception.Message) }
            try { Apply-RulesToNew } catch { Add-Error ("rules: " + $_.Exception.Message) }
            try { Step-CpuLimits } catch { Add-Error ("limits: " + $_.Exception.Message) }
            try { Step-GpuThrottle } catch { Add-Error ("throttle: " + $_.Exception.Message) }
            try { Step-RamLimits } catch { Add-Error ("ramlimits: " + $_.Exception.Message) }
            try { Step-ProBalance } catch { Add-Error ("probalance: " + $_.Exception.Message) }
            try { Step-Watchdog } catch { Add-Error ("watchdog: " + $_.Exception.Message) }

            # 2. render now (CPU/RAM is fresh; never wait on GPU for this)
            try {
                $lines = Build-Lines
                Write-Screen $lines
            } catch { Add-Error ('render: ' + $_.Exception.Message) }

            # 3. GPU refresh ~1Hz, after the render so the screen never stalls;
            #    re-enumerate GPU instances ~1x/minute so new processes appear
            if (([datetime]::Now - $script:lastGpu).TotalMilliseconds -ge 900) {
                try { Update-Gpu } catch { Add-Error ("gpu: " + $_.Exception.Message) }
                $script:lastGpu = [datetime]::Now
                try {
                    $lines = Build-Lines
                    Write-Screen $lines
                } catch { Add-Error ('render: ' + $_.Exception.Message) }
            }
            if (([datetime]::Now - $script:lastGpuInit).TotalMinutes -ge 1) {
                $script:countersReinit = $true
                Init-Counters
                try { Update-Gpu } catch { Add-Error ("gpu: " + $_.Exception.Message) }
                $script:lastGpuInit = [datetime]::Now
            }

            # 4. input (keys + mouse) + throttle until the next ~200ms tick
            $deadline = $cycleStart.AddMilliseconds(200)
            while ($script:running -and ([datetime]::Now -lt $deadline)) {
                try { Step-GpuThrottle } catch {}
                if (Read-Input) {
                    try {
                        $lines = Build-Lines
                        Write-Screen $lines
                    } catch { Add-Error ('render: ' + $_.Exception.Message) }
                    $deadline = [datetime]::Now.AddMilliseconds(200)
                } else {
                    Start-Sleep -Milliseconds 10
                }
            }
        }
    } finally {
        Restore-AllLimits
        Restore-Mouse
        Save-Prefs
        Write-Log ('Session ended (errors swallowed: ' + $script:errorCount + ')')
        try { [Console]::CursorVisible = $true } catch {}
        try { [Console]::TreatControlCAsInput = $false } catch {}
        try {
            [Console]::SetCursorPosition(0, 0)
            $lines = @(
                'PSProcLasso exited.  Errors swallowed: ' + $script:errorCount,
                'Rules: ' + $script:RulesFile,
                'Log:   ' + $script:LogFile
            )
            foreach ($l in $lines) { [Console]::WriteLine($l) }
        } catch {}
    }
}

# ----------------------------------------------------------------------------
#  Main
# ----------------------------------------------------------------------------
Load-Prefs
Load-Rules
Write-Log ('Started (mode=' + $(if ($Snapshot) { 'snapshot' } elseif ($SelfTest) { 'selftest' } elseif ($UITest) { 'uitest' } elseif ($Monitor) { 'monitor' } else { 'tui' }) + ')')

if ($Snapshot) {
    Invoke-Snapshot
} elseif ($SelfTest) {
    Invoke-SelfTest
} elseif ($UITest) {
    Invoke-UiTest
} elseif ($Monitor) {
    Invoke-Monitor
} else {
    Invoke-Tui
}
