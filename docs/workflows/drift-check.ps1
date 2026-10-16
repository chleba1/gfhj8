<#
  drift-check.ps1 - Kaizen structural-drift metrics.

  Implements research finding #7: GitClear's 2026 "Maintainability Gap" (623M changes)
  found duplication +81%, copy/paste +41%, error-masking +47%, churn +15% and refactoring
  down ~70% as AI authorship scaled. A solo dev is the worst case - no reviewer catches
  the fourth near-copy of the same function.

  Measures what is cheaply computable from git + the working tree, appends one row per run
  to a CSV so the TREND is visible (a single reading means nothing).

  DRY RUN (rule 15a):
    powershell -NoProfile -File drift-check.ps1 -Repo <path> -WhatIfOnly

  Usage:
    scripts\drift-check.ps1 -Repo C:\Users\Easy\Desktop\MMOCore
    scripts\drift-check.ps1 -Repo ... -Days 30
#>
param(
  [Parameter(Mandatory=$true)][string]$Repo,
  [int]$Days = 14,
  [string]$LogPath,
  [string[]]$Ext = @('.cs'),
  [switch]$WhatIfOnly
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path (Join-Path $Repo '.git'))) { Write-Host "ABORT: no .git under $Repo" -ForegroundColor Red; exit 1 }

# powershell -File binds "-Ext .cs,.json" as ONE string, not an array. Without this split the
# extension filter silently matches nothing and every metric reports a confident zero.
$Ext = @($Ext | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$Ext = @($Ext | ForEach-Object { if ($_.StartsWith('.')) { $_ } else { ".$_" } })
if (-not $LogPath) { $LogPath = Join-Path $Repo 'docs\drift-metrics.csv' }

Push-Location $Repo
try {
  $since = (Get-Date).AddDays(-$Days).ToString('yyyy-MM-dd')

  # ---- git volume over the window -------------------------------------
  $numstat = git log --since=$since --numstat --format='%H' 2>$null
  $added = 0; $deleted = 0; $touched = @{}
  foreach ($line in $numstat) {
    if ($line -match '^(\d+)\s+(\d+)\s+(.+)$') {
      $a=[int]$Matches[1]; $d=[int]$Matches[2]; $f=$Matches[3]
      if ($Ext -contains ([IO.Path]::GetExtension($f))) {
        $added += $a; $deleted += $d
        $touched[$f] = ($touched[$f] + 1)
      }
    }
  }
  $commits = @(git log --since=$since --format='%H' 2>$null).Count

  # Churn proxy: deletions as a share of additions. Rewriting your own recent work
  # shows up here before it shows up anywhere else.
  $churn = if ($added -gt 0) { [math]::Round(100.0 * $deleted / $added, 1) } else { 0 }

  # Rework proxy: share of touched files edited in 3+ separate commits in the window.
  $reworked = @($touched.GetEnumerator() | Where-Object { $_.Value -ge 3 }).Count
  $reworkPct = if ($touched.Count -gt 0) { [math]::Round(100.0 * $reworked / $touched.Count, 1) } else { 0 }

  # Refactor ratio: renames/moves as a share of file-changes. GitClear's headline
  # drop was in "moved" code - refactoring stops when generating is cheaper.
  $renames = @(git log --since=$since --diff-filter=R --format='' --name-only 2>$null | Where-Object { $_ }).Count
  $fileChanges = ($touched.Values | Measure-Object -Sum).Sum
  $refactorPct = if ($fileChanges -gt 0) { [math]::Round(100.0 * $renames / $fileChanges, 1) } else { 0 }

  # ---- working-tree scans ---------------------------------------------
  $files = Get-ChildItem -Recurse -File -EA 0 |
    Where-Object { $Ext -contains $_.Extension -and $_.FullName -notmatch '\\(obj|bin|Library|Temp|node_modules|\.git)\\' }

  # Error-masking: swallowed exceptions. GitClear measured +47%; it is the construct that
  # turns a loud failure into a silent one, which is this codebase's worst failure shape.
  $masking = 0
  foreach ($f in $files) {
    $txt = Get-Content $f.FullName -Raw -EA 0
    if (-not $txt) { continue }
    $masking += ([regex]::Matches($txt, 'catch\s*(\([^)]*\))?\s*\{\s*\}')).Count          # catch {}
    $masking += ([regex]::Matches($txt, 'catch\s*(\([^)]*\))?\s*\{\s*//[^\r\n]*\s*\}')).Count # catch { // comment }
  }

  # Duplication: identical normalised 6-line blocks appearing in 2+ places.
  $blockSize = 6
  $seen = @{}; $dupBlocks = 0; $totalBlocks = 0
  foreach ($f in $files) {
    $lines = @(Get-Content $f.FullName -EA 0 |
      ForEach-Object { $_.Trim() } |
      Where-Object { $_ -ne '' -and $_ -notmatch '^(//|/\*|\*|#)' })
    if ($lines.Count -lt $blockSize) { continue }
    for ($i = 0; $i -le $lines.Count - $blockSize; $i++) {
      $key = ($lines[$i..($i+$blockSize-1)] -join "`n")
      $totalBlocks++
      if ($seen.ContainsKey($key)) { if ($seen[$key] -eq 1) { $dupBlocks++ }; $seen[$key]++ }
      else { $seen[$key] = 1 }
    }
  }
  $dupPct = if ($totalBlocks -gt 0) { [math]::Round(100.0 * $dupBlocks / $totalBlocks, 2) } else { 0 }

  # Rule 15b: a scan that matched nothing is NOT a measurement of zero drift.
  if ($files.Count -eq 0) {
    Write-Host "ABORT: 0 files matched extensions [$($Ext -join ', ')] under $Repo." -ForegroundColor Red
    Write-Host "Nothing was measured - this is not a reading of zero drift. Check -Ext." -ForegroundColor Red
    exit 2
  }

  $row = [pscustomobject]@{
    date         = (Get-Date).ToString('yyyy-MM-dd')
    windowDays   = $Days
    commits      = $commits
    linesAdded   = $added
    linesDeleted = $deleted
    churnPct     = $churn
    reworkPct    = $reworkPct
    refactorPct  = $refactorPct
    dupBlockPct  = $dupPct
    errorMasking = $masking
    filesScanned = $files.Count
  }

  $row | Format-List | Out-String | Write-Host

  if ($WhatIfOnly) { Write-Host "(dry run - nothing written)"; exit 0 }

  $dir = Split-Path $LogPath -Parent
  if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
  if (Test-Path $LogPath) { $row | Export-Csv $LogPath -Append -NoTypeInformation }
  else { $row | Export-Csv $LogPath -NoTypeInformation }
  Write-Host "appended -> $LogPath"

  # Trend, once there is one. A single reading is not a signal.
  $hist = @(Import-Csv $LogPath -EA 0)
  if ($hist.Count -ge 2) {
    $prev = $hist[-2]
    Write-Host "`nvs previous run ($($prev.date)):"
    foreach ($m in 'churnPct','reworkPct','dupBlockPct','errorMasking') {
      $d = [double]$row.$m - [double]$prev.$m
      $arrow = if ($d -gt 0) { "UP  +$([math]::Round($d,2))" } elseif ($d -lt 0) { "DOWN $([math]::Round($d,2))" } else { "flat" }
      Write-Host ("  {0,-13} {1,8} -> {2,-8} {3}" -f $m, $prev.$m, $row.$m, $arrow)
    }
  } else {
    Write-Host "`n(first reading - trend needs a second run; a single number here means nothing)"
  }
}
finally { Pop-Location }
