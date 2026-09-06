param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '需要 Windows 10/11 的 .NET Framework 4.x。' }
$framework = Split-Path $compiler
$release = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { Join-Path $taskRoot 'release' } else { [IO.Path]::GetFullPath($OutputDirectory) }
$artifacts = Join-Path $taskRoot 'artifacts'
New-Item -ItemType Directory -Force -Path $release,$artifacts | Out-Null
$env:TEMP = $artifacts
$env:TMP = $artifacts
$references = @('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Runtime.Serialization.dll',
    (Join-Path $framework 'WPF\UIAutomationClient.dll'),(Join-Path $framework 'WPF\UIAutomationTypes.dll'),(Join-Path $framework 'WPF\WindowsBase.dll'))
$arguments = @('/nologo','/utf8output','/codepage:65001','/target:winexe','/platform:x64','/optimize+','/warn:4',('/out:' + (Join-Path $release 'WeChatChime.exe')),('/win32manifest:'+(Join-Path $taskRoot 'src\app.manifest')))
$arguments += $references | ForEach-Object { '/r:' + $_ }
$arguments += Get-ChildItem -LiteralPath (Join-Path $taskRoot 'src') -Filter '*.cs' | ForEach-Object { $_.FullName }
& $compiler $arguments
if ($LASTEXITCODE -ne 0) { throw '编译失败。' }
Get-Item -LiteralPath (Join-Path $release 'WeChatChime.exe') | Select-Object FullName,Length
