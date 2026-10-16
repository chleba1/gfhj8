<#
  bandc-gate.ps1 - PreToolUse gate for MMOCore Band C surfaces.

  Blocks Write/Edit/MultiEdit on files whose breakage is silent and expensive
  (tick loop, authoritative combat, AoI, wire types). Names the invariant at
  risk in the block message, so the constraint reloads into context exactly
  when it matters.

  Install: C:\Users\Easy\.claude\hooks\bandc-gate.ps1
  No-ops outside MMOCore. Unlock with scripts\bandc-unlock.ps1 (20 min, auto-expires).

  DRY RUN (rule 15a - run this once before calling it installed):
    powershell -NoProfile -File bandc-gate.ps1 -SelfTest

  Exit codes: 0 = allow, 2 = block (stderr goes to Claude).
#>
param([switch]$SelfTest)

$ErrorActionPreference = 'Stop'

# --- Band C table: pattern -> invariant at risk -------------------------
$BandC = @(
  @{ Rx = 'Server\.NetTick\.cs$'
     Why = '20 Hz _simLock tick loop. Silent state-loss here is this project''s most expensive failure mode (CLAUDE.md, model policy).' }
  @{ Rx = 'Server\.Combat\.cs$'
     Why = 'Authoritative combat. Server is sole authority; clients send intent only. A validation gap here is an exploit, not a bug.' }
  @{ Rx = 'SpatialGrid'
     Why = 'Per-client AoI snapshots (cell 32, radius 80). Errors show up as desync / invisible entities, not as exceptions.' }
  @{ Rx = '\\Shared\\.*\.cs$'
     Why = 'Wire types. Shared.dll auto-copies to UnityClient\Assets\Plugins\Shared - a change here can break client/server compat silently.' }
  @{ Rx = '(Persistence|Repository|Save|Migration)'
     Why = 'Durable state. Wrong here is unrecoverable, not just broken.' }
)

function Get-UnlockFile([string]$PathLike) {
  # Derive repo root from the path being edited. Hardcoding the repo location
  # would mean moving MMOCore silently orphans the unlock file - the gate would
  # then block forever with no diagnostic. (Found by running -SelfTest.)
  if ($PathLike -match '^(?<root>.*?MMOCore)([\\/]|$)') {
    # String concat, not Join-Path: Join-Path resolves the drive, which throws
    # when the path is inspected off the machine that owns it.
    return ($Matches.root + '\scripts\state\bandc-unlock')
  }
  return $null
}

function Test-Unlocked([string]$UnlockFile) {
  if ([string]::IsNullOrWhiteSpace($UnlockFile)) { return $false }
  if (-not (Test-Path $UnlockFile)) { return $false }
  try {
    $expiry = [datetime]::Parse((Get-Content $UnlockFile -Raw).Trim())
    return (Get-Date) -lt $expiry
  } catch {
    # Unreadable/corrupt unlock file is NOT an unlock (rule 15b).
    [Console]::Error.WriteLine("bandc-gate: unlock file unreadable - treating as LOCKED.")
    return $false
  }
}

function Invoke-Gate([string]$ToolName, [string]$FilePath, [string]$Cwd) {
  # Scope: MMOCore only. Mirrors session-cwd-guard.
  if (("$Cwd$FilePath") -notmatch 'MMOCore') { return @{ Block = $false } }
  if ($ToolName -notmatch '^(Write|Edit|MultiEdit|NotebookEdit)$') { return @{ Block = $false } }
  if (-not $FilePath) { return @{ Block = $false } }

  foreach ($b in $BandC) {
    if ($FilePath -match $b.Rx) {
      $u = Get-UnlockFile $FilePath
      if (Test-Unlocked $u) { return @{ Block = $false; Matched = $b.Rx } }
      return @{ Block = $true; Why = $b.Why; Matched = $b.Rx; Unlock = $u }
    }
  }
  return @{ Block = $false }
}

# --- Self-test (rule 15a: execute the mechanism, assert the observable) --
if ($SelfTest) {
  $cases = @(
    @{ N='tick loop blocks';    T='Edit';  F='C:\Users\Easy\Desktop\MMOCore\GameServer\Server.NetTick.cs'; Want=$true }
    @{ N='combat blocks';       T='Write'; F='C:\Users\Easy\Desktop\MMOCore\GameServer\Server.Combat.cs';  Want=$true }
    @{ N='shared wire blocks';  T='Edit';  F='C:\Users\Easy\Desktop\MMOCore\Shared\Net\Packets.cs';        Want=$true }
    @{ N='client UI allows';    T='Edit';  F='C:\Users\Easy\Desktop\MMOCore\UnityClient\Assets\Scripts\InventoryHud.Grid.cs'; Want=$false }
    @{ N='docs allow';          T='Write'; F='C:\Users\Easy\Desktop\MMOCore\docs\plan.md';                 Want=$false }
    @{ N='other repo allows';   T='Edit';  F='C:\Users\Easy\Desktop\AI-Brain\CLAUDE.md';                   Want=$false }
    @{ N='Read allows';         T='Read';  F='C:\Users\Easy\Desktop\MMOCore\GameServer\Server.NetTick.cs'; Want=$false }
  )
  $fail = 0
  foreach ($c in $cases) {
    $got = (Invoke-Gate $c.T $c.F 'C:\Users\Easy\Desktop\MMOCore').Block
    $ok  = ($got -eq $c.Want)
    if (-not $ok) { $fail++ }
    "{0}  {1,-20} block={2} want={3}" -f $(if($ok){'PASS'}else{'FAIL'}), $c.N, $got, $c.Want
  }
  $probe = Get-UnlockFile 'C:\Users\Easy\Desktop\MMOCore\GameServer\Server.NetTick.cs'
  "resolved unlock file: $probe"
  "currently unlocked:  $(Test-Unlocked $probe)"
  if (-not $probe) { "FAIL - unlock path did not resolve"; exit 1 }
  if ($fail) { "FAIL - $fail case(s)"; exit 1 } else { "PASS - gate logic sound"; exit 0 }
}

# --- Hook path ----------------------------------------------------------
$raw = [Console]::In.ReadToEnd()
if ([string]::IsNullOrWhiteSpace($raw)) { exit 0 }

try { $j = $raw | ConvertFrom-Json } catch {
  # Fail open, but loudly (rule 15b: never let a failed probe look like a clean pass).
  [Console]::Error.WriteLine("bandc-gate: UNPARSEABLE hook input - gate did NOT run. Not a clean pass.")
  exit 0
}

$r = Invoke-Gate $j.tool_name $j.tool_input.file_path $j.cwd
if (-not $r.Block) { exit 0 }

[Console]::Error.WriteLine(@"
BAND C - blocked: $($j.tool_input.file_path)

$($r.Why)

This file is Band C: breakage is silent, not loud. verify.ps1 will pass anyway.

Before editing, state to Samuel:
  1. which invariant this change touches
  2. how it will be verified beyond 'builds + smoke tests pass'

Then he runs:  scripts\bandc-unlock.ps1    (20 min, auto-expires)
  unlock file: $($r.Unlock)
"@)
exit 2
