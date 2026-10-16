<#
  install.ps1 - place the downloaded workflow artifacts where AI-Brain expects them.
  Finds the files wherever the browser put them. Run from anywhere.
#>
param(
  [string]$Vault  = 'C:\Users\Easy\Desktop\AI-Brain',
  [string]$Skills = "$env:USERPROFILE\.claude\skills",
  [switch]$WhatIfOnly
)
$ErrorActionPreference = 'Stop'

$searchIn = @("$env:USERPROFILE\Downloads", "$env:USERPROFILE\Desktop", (Get-Location).Path) |
            Where-Object { Test-Path $_ } | Select-Object -Unique

function Find-One([string]$name, [string]$mustContain) {
  foreach ($d in $searchIn) {
    $hits = @(Get-ChildItem $d -Recurse -Depth 2 -File -Filter $name -EA 0 |
              Sort-Object LastWriteTime -Descending)
    foreach ($h in $hits) {
      if (-not $mustContain) { return $h.FullName }
      $txt = Get-Content $h.FullName -Raw -EA 0
      if ($txt -and $txt -match [regex]::Escape($mustContain)) { return $h.FullName }
    }
  }
  return $null
}

$drift = Find-One 'drift-check.ps1'      'Kaizen structural-drift'
$quar  = Find-One 'quarantine-fetch.ps1' 'quarantine'
$skill = Find-One 'SKILL.md'             'spec-ramp'

$missing = @()
if (-not $drift) { $missing += 'drift-check.ps1' }
if (-not $quar)  { $missing += 'quarantine-fetch.ps1' }
if (-not $skill) { $missing += 'SKILL.md (spec-ramp)' }

if ($missing.Count) {
  Write-Host "ABORT: could not find: $($missing -join ', ')" -ForegroundColor Red
  Write-Host "Searched (depth 2): $($searchIn -join '; ')" -ForegroundColor Red
  Write-Host "Nothing was installed. Move the downloads into one of those folders, or cd to them and rerun." -ForegroundColor Red
  exit 1
}

$scriptDir = Join-Path $Vault 'scripts'
$skillDir  = Join-Path $Skills 'spec-ramp'

$plan = @(
  [pscustomobject]@{ From=$drift; To=(Join-Path $scriptDir 'drift-check.ps1') }
  [pscustomobject]@{ From=$quar;  To=(Join-Path $scriptDir 'quarantine-fetch.ps1') }
  [pscustomobject]@{ From=$skill; To=(Join-Path $skillDir  'SKILL.md') }
)
$plan | ForEach-Object { "{0}`n   -> {1}" -f $_.From, $_.To } | Write-Host

if ($WhatIfOnly) { Write-Host "`n(dry run - nothing copied)"; exit 0 }

foreach ($d in @($scriptDir, $skillDir)) {
  if (-not (Test-Path $d)) { New-Item -ItemType Directory -Force -Path $d | Out-Null }
}
foreach ($p in $plan) { Copy-Item $p.From $p.To -Force }

# Verify what actually landed - do not report success from the copy call alone (rule 15).
$bad = @($plan | Where-Object { -not (Test-Path $_.To) })
if ($bad.Count) {
  Write-Host "`nFAILED - missing after copy:" -ForegroundColor Red
  $bad | ForEach-Object { Write-Host "  $($_.To)" -ForegroundColor Red }
  exit 2
}
Write-Host "`nOK - 3/3 installed and verified on disk."
Write-Host "Next (dry-run before trusting either):"
Write-Host "  powershell -NoProfile -File `"$scriptDir\drift-check.ps1`" -Repo C:\Users\Easy\Desktop\MMOCore -WhatIfOnly"
