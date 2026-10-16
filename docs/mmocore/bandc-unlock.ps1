<#
  bandc-unlock.ps1 - time-boxed unlock for the Band C gate.
  Install: C:\Users\Easy\Desktop\MMOCore\scripts\bandc-unlock.ps1
  Usage:   scripts\bandc-unlock.ps1 [-Minutes 20]
           scripts\bandc-unlock.ps1 -Lock     # re-lock now
#>
param([int]$Minutes = 20, [switch]$Lock)

$state = Join-Path $PSScriptRoot 'state'
$file  = Join-Path $state 'bandc-unlock'
New-Item -ItemType Directory -Force -Path $state | Out-Null

if ($Lock) {
  Remove-Item $file -Force -ErrorAction SilentlyContinue
  "Band C re-locked."
  exit 0
}

$expiry = (Get-Date).AddMinutes($Minutes)
$expiry.ToString('o') | Set-Content $file -Encoding ascii
"Band C unlocked until $($expiry.ToString('HH:mm:ss')) ($Minutes min). Auto-expires - no cleanup needed."
