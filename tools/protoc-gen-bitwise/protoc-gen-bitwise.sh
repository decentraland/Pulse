#!/usr/bin/env bash
exec python3 "$(dirname "$0")/../../../protocol/protoc-gen-bitwise/plugin.py" "$@"
