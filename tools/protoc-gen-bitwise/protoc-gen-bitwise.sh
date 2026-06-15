#!/usr/bin/env bash
DIR="$(dirname "$0")"
exec node "$DIR/../../../protocol/protoc-gen-bitwise/plugin.js" "$@"
