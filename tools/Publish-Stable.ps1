param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$version = ([xml][IO.File]::ReadAllText((Join-Path $projectRoot 'Directory.Build.props'))).Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid release version' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectRoot "artifacts/KuniWhatsThisAsset-v$version-win-x64" }
$publish = [IO.Path]::GetFullPath($OutputDirectory, $projectRoot)
$artifactRoot = (Join-Path $projectRoot 'artifacts') + [IO.Path]::DirectorySeparatorChar
if (-not $publish.StartsWith($artifactRoot, [StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $publish)) { throw 'Use a new output directory below artifacts.' }
$dotnet = Join-Path $projectRoot '.tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.tools/cli-home'
Push-Location $projectRoot
try {
 & $dotnet publish src/KuniWhatsThisAsset.App -c Release -r win-x64 --self-contained true -p:NuGetAudit=false -o $publish
 if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
 $readme = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'DistributionREADME.txt')).Replace('{{VERSION}}', $version)
 [IO.File]::WriteAllText((Join-Path $publish 'README.txt'), $readme)
 Copy-Item -LiteralPath (Join-Path $projectRoot 'THIRD-PARTY-NOTICES.md') -Destination $publish
 $files = @(Get-ChildItem -LiteralPath $publish -Recurse -File | Sort-Object FullName | ForEach-Object {
  $relative = [IO.Path]::GetRelativePath($publish, $_.FullName).Replace('\','/')
  if ($relative -notin @('KuniWhatsThisAsset.App.exe','README.txt','THIRD-PARTY-NOTICES.md') -and $relative -notmatch '^Libs/([A-Za-z0-9_.-]+/)*[A-Za-z0-9_.-]+\.dll$|^Libs/licenses/[A-Za-z0-9_.-]+\.txt$') { throw "Unexpected publish file: $relative" }
  [ordered]@{Path=$relative;Length=$_.Length;Sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
 })
 if (-not ($files.Path -contains 'KuniWhatsThisAsset.App.exe')) { throw 'Missing application executable' }
 $manifest = [ordered]@{Schema=1;Version=$version;Files=$files} | ConvertTo-Json -Depth 5
 [IO.File]::WriteAllText((Join-Path $publish 'Libs/distribution.json'), $manifest)
 $stable = Join-Path $projectRoot 'Stable'; New-Item -ItemType Directory -Path $stable -Force | Out-Null
 $filename = "KuniWhatsThisAsset-v$version-win-x64.zip"; $zip = Join-Path $stable $filename
 Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zip -CompressionLevel Optimal -Force
 $sha = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
 [IO.File]::WriteAllText((Join-Path $stable ($filename.Substring(0,$filename.Length-4)+'.sha256')), "$sha  $filename`n")
 Write-Output "Published $zip ($((Get-Item -LiteralPath $zip).Length) bytes); SHA256 $sha"
} finally { Pop-Location }
