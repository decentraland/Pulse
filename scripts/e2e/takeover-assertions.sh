#!/usr/bin/env bash
# Pure evidence checks shared by the live harness and its offline regression tests.
# Inputs are the harness variables named below; no service or filesystem access.
assert_takeover_variant() {
  if [[ "$VARIANT" != order2 && "$VARIANT" != ghost ]]; then
    echo "normal connection order; room membership checked by L1-L6"
    return 0
  fi
  if [[ -z "$TAKEOVER_TS" || -z "$B_CONNECT_TS" || ! "$TAKEOVER_TS" < "$B_CONNECT_TS" ]]; then
    echo "takeover must precede B connect: takeover=${TAKEOVER_TS:-missing}, connect=${B_CONNECT_TS:-missing}"
    return 1
  fi
  if [[ "$VARIANT" == ghost ]]; then
    if [[ -z "$GHOST_JOIN_LINE" || "$GHOST_PARTICIPANTS_BEFORE_COUNT" != 1 || -z "$GHOST_JOIN_TS" || ! "$GHOST_JOIN_TS" < "$B_CONNECT_TS" ]]; then
      echo "ghost injection missing or late: snapshot=${GHOST_JOIN_TS:-missing}, participants=$GHOST_PARTICIPANTS_BEFORE_COUNT, B connect=$B_CONNECT_TS"
      return 1
    fi
    if [[ "$GHOST_ATTRIBUTE" == none ]]; then
      if (( REANNOUNCE_SUPPRESSED_DELTA < 1 || REANNOUNCE_EVICTED_STALE_DELTA != 0 )); then
        echo "unidentified ghost requires suppression without stale eviction: suppressed=$REANNOUNCE_SUPPRESSED_DELTA, evicted=$REANNOUNCE_EVICTED_STALE_DELTA"
        return 1
      fi
      echo "ghost without session injected before B; suppression observed (L4 is expected to fail)"
      return 0
    fi
    if (( REANNOUNCE_EVICTED_STALE_DELTA < 1 )); then
      echo "foreign ghost was not evicted by the re-announce path"
      return 1
    fi
  fi
  if (( ISLAND_B_AFTER_CONNECT_COUNT < 1 )) || [[ "$VARIANT" == order2 && "$REANNOUNCE_ATTEMPTED_DELTA" -lt 1 ]]; then
    echo "missing connect recovery: attempted=$REANNOUNCE_ATTEMPTED_DELTA, B deliveries after connect=$ISLAND_B_AFTER_CONNECT_COUNT"
    return 1
  fi
  echo "takeover before B connect; recovery attempted=$REANNOUNCE_ATTEMPTED_DELTA, deliveries=$ISLAND_B_AFTER_CONNECT_COUNT, stale evictions=$REANNOUNCE_EVICTED_STALE_DELTA"
}
