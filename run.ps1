#Requires -Version 5.1
<#
  PSProcLasso launcher.
  Ensures the current user's execution policy permits running scripts, then
  starts the app in the interactive terminal.  Forwards every switch:
      .\run.ps1                       (interactive TUI)
      .\run.ps1 -RefreshMs 2000
      .\run.ps1 -SelfTest
      .\run.ps1 -UITest
      .\run.ps1 -Monitor
#>
[CmdletBinding()]
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$ForwardArgs
)

$ErrorActionPreference = 'Stop'
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$scriptFile = Join-Path $dir 'PSProcLasso.ps1'

if (-not (Test-Path -LiteralPath $scriptFile)) {
    Write-Host "PSProcLasso.ps1 not found next to run.ps1" -ForegroundColor Red
    exit 1
}

try {
    $cur = Get-ExecutionPolicy -Scope CurrentUser
} catch {
    $cur = 'Undefined'
}
if ($cur -eq 'Restricted' -or $cur -eq 'Undefined') {
    Write-Host 'Setting execution policy to RemoteSigned for the current user...' -ForegroundColor DarkGray
    try {
        Set-ExecutionPolicy -Scope CurrentUser -ExecutionPolicy RemoteSigned -Force -ErrorAction Stop
    } catch {
        Write-Host 'Could not change execution policy. Run once as admin:' -ForegroundColor Yellow
        Write-Host "  Set-ExecutionPolicy -Scope CurrentUser RemoteSigned -Force" -ForegroundColor Yellow
        exit 1
    }
}

# Re-invoke through powershell.exe so switches pass through as literal native
# arguments (array-splatting into the script itself hits PS parameter binding
# quirks for switches like -SelfTest).
$psExe = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$argsLine = @()
if ($ForwardArgs) { $argsLine = @($ForwardArgs) }
& $psExe -NoProfile -ExecutionPolicy Bypass -File $scriptFile @argsLine
exit $LASTEXITCODE
