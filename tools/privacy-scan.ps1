# Claudex privacy scan: fails when personal data would leave this PC.
# Run before every commit (no arguments: every file Git would track, staged or not) and before every release
# (pass the publish folder and/or the portable zip). Matches are printed as file and pattern NAME only, never the text.
#
# Patterns:
#   - built in: this PC's user name and profile path, private keys, JWTs (Nous/OAuth tokens), bearer tokens;
#   - personal: one pattern per line in %USERPROFILE%\.claudex\privacy-scan-patterns.txt (plain text, '#' comments,
#     optional "name = text" lines). Keep account details there: server addresses, emails, org ids. That file lives
#     outside the repository and must never be committed.
param([string[]]$Path)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$patterns = [ordered]@{
    'windows user name'  = [regex]::Escape("\Users\$env:USERNAME\")
    'user profile path'  = [regex]::Escape($env:USERPROFILE)
    'private key'        = '-----BEGIN [A-Z ]*PRIVATE KEY-----'
    'JWT token'          = 'eyJ[A-Za-z0-9_-]{10,}\.eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}'
    'bearer token'       = 'Bearer\s+[A-Za-z0-9._~+/-]{24,}'
}
$personal = Join-Path $env:USERPROFILE '.claudex\privacy-scan-patterns.txt'
if (Test-Path $personal) {
    $n = 0
    foreach ($line in Get-Content $personal) {
        $t = $line.Trim()
        if ($t -eq '' -or $t.StartsWith('#')) { continue }
        $n++
        if ($t -match '^(?<name>[^=]+?)\s*=\s*(?<text>.+)$') { $patterns["personal: $($Matches.name)"] = [regex]::Escape($Matches.text) }
        else { $patterns["personal pattern $n"] = [regex]::Escape($t) }
    }
} else {
    Write-Warning "No personal pattern file at ${personal}: only the built-in patterns are checked."
}

# What to scan: the given paths (a zip is unpacked to a temp folder), else every file Git would commit.
$files = @()
$temp = $null
if ($Path) {
    foreach ($p in $Path) {
        $full = (Resolve-Path $p).Path
        if ($full -like '*.zip') {
            $temp = Join-Path ([IO.Path]::GetTempPath()) ("claudex-scan-" + [guid]::NewGuid().ToString('N'))
            Expand-Archive $full $temp
            $files += Get-ChildItem $temp -Recurse -File
        } elseif (Test-Path $full -PathType Container) {
            $files += Get-ChildItem $full -Recurse -File
        } else {
            $files += Get-Item $full
        }
    }
} else {
    Push-Location $repo
    try {
        $names = @(git ls-files --cached --others --exclude-standard) | Where-Object { $_ -and (Test-Path $_ -PathType Leaf) }
        $files = $names | ForEach-Object { Get-Item $_ }
    } finally { Pop-Location }
}

$hits = 0
foreach ($file in $files) {
    if ($file.FullName -like '*\tools\privacy-scan.ps1') { continue }
    $bytes = [IO.File]::ReadAllBytes($file.FullName)
    # Text as UTF-8 and as UTF-16 (strings inside .NET binaries), so DLLs and EXEs are covered too.
    $texts = @([Text.Encoding]::UTF8.GetString($bytes), [Text.Encoding]::Unicode.GetString($bytes))
    foreach ($name in $patterns.Keys) {
        $regex = [regex]::new($patterns[$name], 'IgnoreCase')
        if ($texts | Where-Object { $regex.IsMatch($_) } | Select-Object -First 1) {
            $rel = if ($temp -and $file.FullName.StartsWith($temp)) { $file.FullName.Substring($temp.Length + 1) } else { $file.FullName.Replace("$repo\", '') }
            Write-Host "FOUND  $name  in  $rel" -ForegroundColor Red
            $hits++
        }
    }
}
if ($temp) { Remove-Item $temp -Recurse -Force }

if ($hits -gt 0) {
    Write-Host "$hits finding(s) in $($files.Count) files. Remove them before committing or releasing." -ForegroundColor Red
    exit 1
}
Write-Host "Privacy scan clean: $($files.Count) files, $($patterns.Count) patterns." -ForegroundColor Green
exit 0
