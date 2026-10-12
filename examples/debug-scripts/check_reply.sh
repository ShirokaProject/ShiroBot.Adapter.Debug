#!/bin/sh
# Entire JSON record arrives through stdin. This example needs no Python or Node.
record=$(cat)
case "$record" in
  *pong*) printf 'PASS: reply contains pong\n' ;;
  *) printf 'FAIL: expected pong in reply\n' >&2; exit 1 ;;
esac
