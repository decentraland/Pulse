#!/usr/bin/env bash
DIR="$(dirname "$0")"
exec "$DIR/.venv/bin/python" "$DIR/../../../protocol/protoc-gen-bitwise/plugin.py" "$@"