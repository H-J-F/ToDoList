param([string]$OutputRoot = (Join-Path $PSScriptRoot '..\artifacts\publish'))
$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$publishRoot = [System.IO.Path]::GetFullPath($OutputRoot)
$publishDirectory = Join-Path $publishRoot 'ToDoList-win-x64'
dotnet publish (Join-Path $repoRoot 'src\ToDoList.App\ToDoList.App.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:DebugType=None -o $publishDirectory
if ($LASTEXITCODE -ne 0) { throw '发布失败。' }
Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination (Join-Path $publishDirectory '使用说明.md')
$validation = Join-Path $repoRoot 'docs\VALIDATION.md'
if (Test-Path -LiteralPath $validation) { Copy-Item -LiteralPath $validation -Destination (Join-Path $publishDirectory '验证记录.md') }
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs\THIRD-PARTY.md') -Destination (Join-Path $publishDirectory 'THIRD-PARTY.md')
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs\licenses') -Destination $publishDirectory -Recurse -Force
# Package only shipped files; never include a user's Data directory in the zip.
$packageFiles = Get-ChildItem -LiteralPath $publishDirectory -Force | Where-Object { $_.Name -ne 'Data' }
Compress-Archive -LiteralPath $packageFiles.FullName -DestinationPath (Join-Path $publishRoot 'ToDoList-win-x64.zip') -Force
Get-FileHash -LiteralPath (Join-Path $publishRoot 'ToDoList-win-x64.zip') -Algorithm SHA256
