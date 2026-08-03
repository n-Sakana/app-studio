param(
    [Parameter(Mandatory = $true)]
    [string]$BaseDir
)

$ErrorActionPreference = 'Stop'
[Console]::InputEncoding = New-Object Text.UTF8Encoding($false)
[Console]::OutputEncoding = New-Object Text.UTF8Encoding($false)
& (Join-Path $BaseDir 'app-studio.ps1') -CompileOnly
[AppStudio.WorkerServer]::Run()

