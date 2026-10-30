#!/usr/bin/env bash
# Quotalis for Linux by soybean_alien. MIT, see LICENSE.
# Builds one self-contained binary and dist/quotalis-<version>-linux-x64.tar.gz, plus a PKGBUILD with its checksum filled in.
# Runs on Linux, or on Windows under Git Bash. Needs the .NET 10 SDK.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' src/Quotalis.Linux/Quotalis.Linux.csproj)"
name="quotalis-$version-linux-x64"
stage="dist/$name"

rm -rf "$stage" "dist/$name.tar.gz"
mkdir -p "$stage"

dotnet publish src/Quotalis.Linux -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:DebugType=none \
  -o "$stage/.publish"

cp "$stage/.publish/quotalis" "$stage/quotalis"
rm -rf "$stage/.publish"
cp LICENSE THIRD_PARTY_NOTICES.md README.md packaging/install.sh "$stage/"

# Windows has no execute bit to carry over, so the archive sets modes itself. It records no local account name either.
owner=(--owner=0 --group=0 --numeric-owner)
tar "${owner[@]}" --mode=0755 -cf "dist/$name.tar" -C dist "$name/quotalis" "$name/install.sh"
tar "${owner[@]}" --mode=0644 -rf "dist/$name.tar" -C dist "$name/LICENSE" "$name/THIRD_PARTY_NOTICES.md" "$name/README.md"
gzip -9nf "dist/$name.tar"

sum="$(sha256sum "dist/$name.tar.gz" | cut -d' ' -f1)"
echo "$sum  $name.tar.gz" > "dist/SHA256SUMS-$version.txt"
sed -e "s/@VERSION@/$version/g" -e "s/@SHA256@/$sum/g" packaging/PKGBUILD.in > dist/PKGBUILD

echo "Built dist/$name.tar.gz"
echo "sha256 $sum"
