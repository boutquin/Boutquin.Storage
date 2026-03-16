#!/bin/sh
# Installs git hooks from the hooks/ directory.
# Usage: ./hooks/install.sh

set -e

HOOKS_DIR="$(cd "$(dirname "$0")" && pwd)"
GIT_HOOKS_DIR="$(git rev-parse --show-toplevel)/.git/hooks"

for hook in "$HOOKS_DIR"/pre-commit; do
    name="$(basename "$hook")"
    cp "$hook" "$GIT_HOOKS_DIR/$name"
    chmod +x "$GIT_HOOKS_DIR/$name"
    echo "Installed $name hook."
done
