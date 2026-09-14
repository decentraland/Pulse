#!/usr/bin/env bash
set -euo pipefail
source "$(cd "$(dirname "$0")" && pwd)/takeover-assertions.sh"
reset_evidence() {
  VARIANT=order2
  TAKEOVER_TS=2026-09-14T00:00:01.000Z
  B_CONNECT_TS=2026-09-14T00:00:04.000Z
  GHOST_JOIN_TS=2026-09-14T00:00:03.000Z
  GHOST_JOIN_LINE="joined ghost"
  GHOST_PARTICIPANTS_BEFORE_COUNT=1
  GHOST_ATTRIBUTE=foreign
  REANNOUNCE_ATTEMPTED_DELTA=1
  ISLAND_B_AFTER_CONNECT_COUNT=1
  REANNOUNCE_SUPPRESSED_DELTA=0
  REANNOUNCE_EVICTED_STALE_DELTA=1
}
expect() {
  local expected=$1 actual=0
  shift
  assert_takeover_variant >/dev/null || actual=1
  if [[ "$actual" != "$expected" ]]; then
    echo "FAIL: $* (expected $expected, got $actual)" >&2
    exit 1
  fi
  echo "PASS: $*"
}
reset_evidence
expect 0 "delayed connect recovered"
TAKEOVER_TS=2026-09-14T00:00:05.000Z
expect 1 "ordinary ordering cannot pass as delayed connect"
reset_evidence
ISLAND_B_AFTER_CONNECT_COUNT=0
expect 1 "direct publish alone is not recovery"
reset_evidence
REANNOUNCE_ATTEMPTED_DELTA=0
expect 1 "recovery requires the re-announce metric"
reset_evidence
VARIANT=ghost
REANNOUNCE_ATTEMPTED_DELTA=0
expect 0 "foreign ghost recovery uses stale-eviction metric, not attempted"
GHOST_JOIN_LINE=""
expect 1 "missing ghost cannot pass"
reset_evidence
VARIANT=ghost
GHOST_PARTICIPANTS_BEFORE_COUNT=0
expect 1 "join log alone cannot prove ghost membership"
reset_evidence
VARIANT=ghost
GHOST_JOIN_TS=2026-09-14T00:00:05.000Z
expect 1 "late ghost cannot pass"
reset_evidence
VARIANT=ghost
REANNOUNCE_EVICTED_STALE_DELTA=0
expect 1 "foreign ghost requires stale eviction"
reset_evidence
VARIANT=ghost
GHOST_ATTRIBUTE=none
REANNOUNCE_EVICTED_STALE_DELTA=0
REANNOUNCE_SUPPRESSED_DELTA=1
ISLAND_B_AFTER_CONNECT_COUNT=0
expect 0 "unidentified ghost suppression is the expected negative case"
REANNOUNCE_SUPPRESSED_DELTA=0
expect 1 "unidentified ghost requires observed suppression"
reset_evidence
VARIANT=order1
TAKEOVER_TS=""
expect 0 "normal order has no delayed-connect precondition"
