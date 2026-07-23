#!/usr/bin/env bash
set -euo pipefail
D=/tmp/tman-vhs-proj
rm -rf "$D" /tmp/tman-vhs-home
mkdir -p "$D"
cat > "$D/package.json" <<'EOF'
{
  "name": "demo",
  "scripts": {
    "test": "node -e \"console.log('ok 42 tests passed')\"",
    "hang": "node -e \"setInterval(() => {}, 1000)\""
  }
}
EOF
