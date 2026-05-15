$ErrorActionPreference = "Stop"

$repoRoot  = Resolve-Path (Join-Path $PSScriptRoot "..")
$propsFile = Join-Path $repoRoot "src\Directory.Build.props"
$pkgDir    = Join-Path $repoRoot "packages"

$xml     = [xml](Get-Content -Raw -LiteralPath $propsFile)
$version = $xml.Project.PropertyGroup.RustEthereumVersion
if ([string]::IsNullOrEmpty($version)) { throw "RustEthereumVersion not found in $propsFile" }

$nupkg = "Decentraland.RustEthereum.$version.nupkg"
$url   = "https://github.com/decentraland/rust-ethereum/releases/download/v$version/$nupkg"

if (-not (Test-Path $pkgDir)) { New-Item -ItemType Directory -Path $pkgDir | Out-Null }
$dest = Join-Path $pkgDir $nupkg

if (Test-Path $dest) {
    Write-Host "$nupkg already present at $dest"
    return
}

Write-Host "Fetching $nupkg from $url"
Invoke-WebRequest -Uri $url -OutFile $dest -UseBasicParsing
Write-Host "Saved to $dest"
