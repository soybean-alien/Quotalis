# Quotalis by soybean_alien. Publishes a built release to the soybean-alien/Quotalis repository, so installed
# copies can update themselves (Settings, About, Updates). Run it after tools/build-release.ps1 and after the vX.Y.Z tag
# is pushed:
#   powershell -ExecutionPolicy Bypass -File tools/publish-release.ps1 [-Version 0.5.4]
# It uploads the installer, the portable zip and SHA256SUMS-X.Y.Z.txt, with the version's CHANGELOG section as notes,
# plus the same installer as Claudex-Setup-X.Y.Z.exe for copies still called Claudex (their updater looks for that name).
# Needs the GitHub CLI signed in (gh auth login) with access to the repository.
param([string]$Version)
$ErrorActionPreference = "Stop"
$root = Resolve-Path "$PSScriptRoot\.."
$repo = "soybean-alien/Quotalis"
if (-not $Version) {
    $Version = ([xml](Get-Content "$root\Directory.Build.props")).Project.PropertyGroup.Version | Select-Object -First 1
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must be X.Y.Z." }
$tag = "v$Version"

$installer = Join-Path $root "artifacts\Quotalis-Setup-$Version.exe"
$zip = Join-Path $root "artifacts\Quotalis-$Version-win-x64-portable.zip"
$legacyInstaller = Join-Path $root "artifacts\Claudex-Setup-$Version.exe"
$sums = Join-Path $root "artifacts\SHA256SUMS-$Version.txt"
foreach ($f in $installer, $legacyInstaller, $zip, $sums) { if (-not (Test-Path $f)) { throw "Missing $f. Run tools/build-release.ps1 first." } }

# The checksums must describe these exact files (the updater refuses an installer that doesn't match).
foreach ($f in $installer, $legacyInstaller) {
    $leaf = Split-Path $f -Leaf
    $listed = Get-Content $sums | Where-Object { ($_ -split '\s+')[1] -eq $leaf }
    $actual = (Get-FileHash $f -Algorithm SHA256).Hash.ToLowerInvariant()
    if (-not $listed -or ($listed -split '\s+')[0] -ne $actual) { throw "SHA256SUMS does not match $leaf. Rebuild with tools/build-release.ps1." }
}

$gh = Get-Command gh -ErrorAction SilentlyContinue
if (-not $gh) { throw "GitHub CLI not found. Install it with: winget install GitHub.cli" }

# Windows PowerShell turns a native tool's stderr into a terminating error under "Stop"; probes expect stderr.
function Test-Gh([string[]]$GhArgs) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try { & gh @GhArgs *> $null; return $LASTEXITCODE -eq 0 } finally { $ErrorActionPreference = $previous }
}

if (-not (Test-Gh @("api", "repos/$repo/git/ref/tags/$tag", "--silent"))) { throw "Tag $tag is not on GitHub yet. Push it first: git push origin $tag" }

# Release notes: this version's section of the changelog.
$changelog = Get-Content (Join-Path $root "CHANGELOG.md") -Raw
$m = [regex]::Match($changelog, "(?ms)^## \[$([regex]::Escape($Version))\][^\n]*\n(.*?)(?=^## \[)")
$notes = if ($m.Success) { $m.Groups[1].Value.Trim() } else { "Quotalis $Version" }
$notesFile = Join-Path $env:TEMP "quotalis-release-notes-$Version.md"
Set-Content -Path $notesFile -Value $notes -Encoding utf8

if (Test-Gh @("release", "view", $tag, "--repo", $repo)) {
    & gh release upload $tag $installer $legacyInstaller $zip $sums --repo $repo --clobber
} else {
    & gh release create $tag $installer $legacyInstaller $zip $sums --repo $repo --title "Quotalis $Version" --notes-file $notesFile --latest
}
if ($LASTEXITCODE -ne 0) { throw "gh failed to publish $tag." }
Write-Host "Published $tag to $repo" -ForegroundColor Green
