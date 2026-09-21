param(
    [ValidateSet('Both', 'Lite', 'Portable')][string]$Variant = 'Both',
    [string]$OutputRoot = (Join-Path $PSScriptRoot '..\artifacts\build-2.1.0')
)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$publishRoot = [IO.Path]::GetFullPath($OutputRoot)
$project = Join-Path $repoRoot 'src\ToDoList.App\ToDoList.App.csproj'
[xml]$projectXml = Get-Content -LiteralPath $project
$version = [string]($projectXml.Project.PropertyGroup.Version | Where-Object { $_ })
$sourceCommit = git -C $repoRoot rev-parse HEAD
$dirty = [bool](git -C $repoRoot status --porcelain)
$variants = if ($Variant -eq 'Both') { @('lite', 'portable') } else { @($Variant.ToLowerInvariant()) }
$checksums = @(); $sizes = @()
New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null
foreach ($kind in $variants) {
    $name = "ToDoList-$version-win-x64-$kind"
    $destination = Join-Path $publishRoot $name
    # Never overwrite an older installation or package leftover files or Data.
    if (Test-Path -LiteralPath $destination) { throw "输出目录已存在，请选择新的 OutputRoot：$destination" }
    $portable = $kind -eq 'portable'
    $publishArgs = @('publish', $project, '-c', 'Release', '-r', 'win-x64', '--self-contained', "$portable",
        '-p:RestoreLockedMode=true', '-p:DebugType=None', '-p:DebugSymbols=false', '-p:PublishTrimmed=false', '-p:SatelliteResourceLanguages=zh-Hans',
        "-p:PublishSingleFile=$portable", '-o', $destination)
    if ($portable) { $publishArgs += @('-p:EnableCompressionInSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true') }
    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "构建 $kind 失败。" }
    Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination $destination
    Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination (Join-Path $destination '使用说明.md')
    foreach ($file in @('THIRD-PARTY.md', 'DEPENDENCIES.json', 'VALIDATION-2.1.md')) {
        Copy-Item -LiteralPath (Join-Path $repoRoot "docs\$file") -Destination $destination
    }
    Copy-Item -LiteralPath (Join-Path $repoRoot 'docs\licenses') -Destination $destination -Recurse
    [ordered]@{ Version=$version; Variant=$kind; Commit=$sourceCommit; WorkingTreeDirty=$dirty; Signed=$false; Runtime='10.0.10' } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $destination 'BUILD.json') -Encoding utf8
    $files = Get-ChildItem -LiteralPath $destination -Recurse -File
    if ($files | Where-Object { $_.Extension -in '.db', '.pdb' -or $_.FullName -match '[\\/]Data[\\/]' }) { throw '构建中含有非交付文件。' }
    $bytes = ($files | Measure-Object Length -Sum).Sum
    if (!$portable -and $bytes -ge 50000000) { throw "精简版超出 50,000,000 字节：$bytes" }
    $zip = Join-Path $publishRoot "$name.zip"
    Compress-Archive -Path (Join-Path $destination '*') -DestinationPath $zip -CompressionLevel Optimal
    $checksums += (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant() + "  $name.zip"
    $sizes += [ordered]@{Variant=$kind; FileBytes=$bytes; ZipBytes=(Get-Item -LiteralPath $zip).Length; Path=$destination}
}
$checksums | Set-Content -LiteralPath (Join-Path $publishRoot 'SHA256SUMS.txt') -Encoding ascii
$sizes | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $publishRoot 'sizes.json') -Encoding utf8
$sizes | Format-Table
# Local only: no tags or GitHub Releases are created by this script.
