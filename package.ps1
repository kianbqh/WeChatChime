param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
$dist = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { Join-Path $taskRoot 'dist' } else { [IO.Path]::GetFullPath($OutputDirectory) }
$staging = Join-Path $taskRoot ('artifacts\package-' + [Guid]::NewGuid().ToString('N'))
$build = Join-Path $staging 'build'
New-Item -ItemType Directory -Force -Path $dist,$staging | Out-Null

# Build into an isolated folder: packaging never touches a running copy or its data.
& (Join-Path $taskRoot 'build.ps1') -OutputDirectory $build
$executable = Join-Path $build 'WeChatChime.exe'
$version = [Reflection.AssemblyName]::GetAssemblyName($executable).Version.ToString(3)
$name = 'WeChatChime-v' + $version + '-windows-x64'
$payload = Join-Path $staging $name
New-Item -ItemType Directory -Path $payload | Out-Null

# Explicit allowlist: personal settings, imported audio and diagnostic artifacts stay local.
Copy-Item -LiteralPath $executable -Destination $payload
Copy-Item -LiteralPath (Join-Path $taskRoot 'release\使用说明.txt') -Destination $payload
Copy-Item -LiteralPath (Join-Path $taskRoot 'CHANGELOG.md') -Destination $payload
$lines = @(Get-ChildItem -LiteralPath $payload -File | Sort-Object Name | ForEach-Object {
    (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $_.Name
})
[IO.File]::WriteAllLines((Join-Path $payload 'SHA256SUMS.txt'),$lines,(New-Object Text.UTF8Encoding($false)))
$archive = Join-Path $dist ($name + '.zip')
Compress-Archive -LiteralPath $payload -DestinationPath $archive -CompressionLevel Optimal -Force
$checksum = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText(($archive + '.sha256'),($checksum + '  ' + [IO.Path]::GetFileName($archive) + "`n"),(New-Object Text.UTF8Encoding($false)))
Get-Item -LiteralPath $archive,($archive + '.sha256') | Select-Object FullName,Length
