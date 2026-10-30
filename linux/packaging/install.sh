#!/usr/bin/env bash
# Quotalis for Linux by soybean_alien. MIT, see LICENSE.
# Puts quotalis in ~/.local/bin and runs its setup. No root needed.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
target="$HOME/.local/bin/quotalis"

install -Dm755 "$here/quotalis" "$target"
"$target" setup

case ":$PATH:" in
  *":$HOME/.local/bin:"*) ;;
  *) echo "Add $HOME/.local/bin to your PATH to run quotalis by name." ;;
esac
