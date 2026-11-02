#!/bin/sh
NS="phone$1"
shift
exec ip netns exec "$NS" "$@"
