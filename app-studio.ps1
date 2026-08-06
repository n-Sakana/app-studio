param(
    [switch]$CompileOnly,
    [switch]$Headless,
    [string]$DiagnosticsPath,
    [int]$AutoCloseMs = 0
)

$ErrorActionPreference = 'Stop'
$baseDir = $PSScriptRoot
$sourceDir = Join-Path $baseDir 'src'
$sources = @(Get-ChildItem -LiteralPath $sourceDir -Filter '*.cs' | Sort-Object Name)
if ($sources.Count -eq 0) {
    throw 'No C# source files were found.'
}

$env:APPSTUDIO_PS_VERSION = $PSVersionTable.PSVersion.ToString()
$env:APPSTUDIO_PS_EDITION = if ($PSVersionTable.PSEdition) { [string]$PSVersionTable.PSEdition } else { 'Desktop' }
$env:APPSTUDIO_PS_EXECUTION_POLICY = [string](Get-ExecutionPolicy)
$env:APPSTUDIO_PS_LANGUAGE_MODE = [string]$ExecutionContext.SessionState.LanguageMode

if ($null -eq ('AppStudio.App' -as [type])) {
    $source = ($sources | ForEach-Object { [IO.File]::ReadAllText($_.FullName) }) -join [Environment]::NewLine
    $referenceNames = @(
        'System',
        'System.Core',
        'System.Xml',
        'System.Web.Extensions',
        # A macro enabled workbook is a zip, and the VBA artefact is written by
        # replacing one part inside it. This is what reads and writes that zip.
        'System.IO.Compression',
        'System.Drawing',
        'System.Windows.Forms',
        'WindowsBase',
        'PresentationCore',
        'PresentationFramework',
        'System.Xaml',
        'UIAutomationClient',
        'UIAutomationTypes',
        'Accessibility'
    )
    $references = @()
    foreach ($referenceName in $referenceNames) {
        $assembly = [Reflection.Assembly]::LoadWithPartialName($referenceName)
        if ($null -eq $assembly -or [string]::IsNullOrWhiteSpace($assembly.Location)) {
            throw ('Required .NET Framework assembly was not found: ' + $referenceName)
        }
        $references += $assembly.Location
    }
    Add-Type -TypeDefinition $source -Language CSharp -ReferencedAssemblies $references | Out-Null
}

if ($CompileOnly) {
    return
}

if ([string]::IsNullOrWhiteSpace($DiagnosticsPath)) {
    $DiagnosticsPath = Join-Path $baseDir 'runtime\diagnostics.json'
}

if ($Headless) {
    [AppStudio.App]::RunHeadless($baseDir, $DiagnosticsPath)
    return
}

[AppStudio.App]::Run($baseDir, $DiagnosticsPath, $AutoCloseMs)
