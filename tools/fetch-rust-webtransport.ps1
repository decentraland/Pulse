$ErrorActionPreference = "Stop"

$repoRoot  = Resolve-Path (Join-Path $PSScriptRoot "..")
$propsFile = Join-Path $repoRoot "src\Directory.Build.props"
$pkgDir    = Join-Path $repoRoot "packages"

$xml     = [xml](Get-Content -Raw -LiteralPath $propsFile)
$version = $xml.Project.PropertyGroup.RustWebTransportVersion
if ([string]::IsNullOrEmpty($version)) { throw "RustWebTransportVersion not found in $propsFile" }

$nupkg = "Decentraland.RustWebTransport.$version.nupkg"
$url   = "https://github.com/decentraland/rust-web-transport/releases/download/v$version/$nupkg"

New-Item -ItemType Directory -Path $pkgDir -Force | Out-Null
$dest = Join-Path $pkgDir $nupkg

if (Test-Path -LiteralPath $dest) {
    Write-Host "$nupkg already present at $dest"
    return
}

$tmp = "$dest.tmp.$PID"
Write-Host "Fetching $nupkg from $url"
Invoke-WebRequest -Uri $url -OutFile $tmp -UseBasicParsing

try {
    Move-Item -LiteralPath $tmp -Destination $dest -ErrorAction Stop
    Write-Host "Saved to $dest"
} catch {
    Remove-Item -Force -LiteralPath $tmp -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $dest) {
        Write-Host "$nupkg already present at $dest (concurrent fetch)"
    } else {
        throw
    }
}
