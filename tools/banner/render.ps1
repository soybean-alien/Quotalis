# Quotalis by soybean_alien. Renders tools/banner/banner.html to docs/assets/banner.png (2560x1280) and
# docs/assets/social-preview.png (1280x640, GitHub's social preview; must stay under 1 MB) with a local headless Chrome or Edge.
#   powershell -ExecutionPolicy Bypass -File tools/banner/render.ps1
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$browser = @("C:\Program Files\Google\Chrome\Application\chrome.exe", "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
             "C:\Program Files\Microsoft\Edge\Application\msedge.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $browser) { throw "Chrome or Edge is needed" }

# The page is laid out at 1280x640 and loads its fonts from Google Fonts.
$html = (Resolve-Path "$PSScriptRoot\banner.html").Path
$profile = Join-Path $env:TEMP "quotalis-banner-profile"
foreach ($r in @(@{ Out = "$root\docs\assets\banner.png"; Scale = "2" }, @{ Out = "$root\docs\assets\social-preview.png"; Scale = "1" })) {
    $p = Start-Process $browser -PassThru -WindowStyle Hidden -ArgumentList @("--headless=new", "--disable-gpu", "--hide-scrollbars",
        "--user-data-dir=$profile", "--force-device-scale-factor=$($r.Scale)", "--window-size=1280,640",
        "--virtual-time-budget=8000", "--screenshot=$($r.Out)", ([Uri]$html).AbsoluteUri)
    $p.WaitForExit(60000) | Out-Null
    Write-Host "$(Split-Path $r.Out -Leaf): $([math]::Round((Get-Item $r.Out).Length / 1KB)) KB"
}
