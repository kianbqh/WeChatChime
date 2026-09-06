param([switch]$SkipUi)
$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$framework = Split-Path $compiler
$artifacts = Join-Path $taskRoot 'artifacts'
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
$env:TEMP = $artifacts
$env:TMP = $artifacts
$references = @('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Runtime.Serialization.dll',
    (Join-Path $framework 'WPF\UIAutomationClient.dll'),(Join-Path $framework 'WPF\UIAutomationTypes.dll'),(Join-Path $framework 'WPF\WindowsBase.dll')) | ForEach-Object { '/r:' + $_ }
$suites = if ($SkipUi) { @('Core','Monitor','Compatibility') } else { @('Core','Monitor','Compatibility','Ui') }
foreach ($testName in $suites) {
    $exe = Join-Path $artifacts ($testName + 'Tests.exe')
    $entryPoint = if ($testName -eq 'Core') { 'WeChatChime.Tests.CoreTestsMain' } elseif ($testName -eq 'Ui') { 'WeChatChime.Tests.UiTestsMain' } else { $testName+'TestsMain' }
    $arguments = @('/nologo','/utf8output','/codepage:65001','/target:exe','/platform:x64','/optimize+',('/out:'+$exe),('/main:'+$entryPoint)) + $references
    $arguments += @((Join-Path $taskRoot 'src\Core.cs'),(Join-Path $taskRoot 'src\Monitor.cs'),(Join-Path $taskRoot 'src\CompatibilityAccess.cs'),(Join-Path $taskRoot ('tests\'+$testName+'Tests.cs')))
    if ($testName -eq 'Ui') { $arguments += Join-Path $taskRoot 'src\MainForm.cs' }
    & $compiler $arguments
    if ($LASTEXITCODE -ne 0) { throw ($testName+' tests compilation failed') }
    & $exe (Join-Path $artifacts ('t-'+[Guid]::NewGuid().ToString('N').Substring(0,12)))
    if ($LASTEXITCODE -ne 0) { throw ($testName+' tests failed') }
}
