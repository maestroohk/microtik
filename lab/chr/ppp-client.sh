#!/bin/sh
# Boots a second, throwaway RouterOS VM on the hotspot LAN to act as a PPPoE customer router.
# Usage (inside the CHR lab container): ppp-client.sh start | stop
# The PPPoE login comes from PPP_USER / PPP_PASSWORD in the environment.
set -eu
IMAGE=/data/ppp-client.img
PIDFILE=/run/ppp-client.pid

case "${1:-start}" in
    stop)
        [ -f "$PIDFILE" ] && kill "$(cat "$PIDFILE")" 2>/dev/null || true
        rm -f "$PIDFILE" "$IMAGE"
        ip link del tap2 2>/dev/null || true
        exit 0 ;;
esac

[ -f "$IMAGE" ] || unzip -p "/images/chr-${CHR_VERSION}.img.zip" > "$IMAGE"
ip link show tap2 >/dev/null 2>&1 || {
    ip tuntap add tap2 mode tap
    ip link set tap2 master br1
    ip link set tap2 up
}

qemu-system-x86_64 -name ppp-client -machine accel=tcg -m 160 -smp 1 -nographic \
    -drive file="$IMAGE",format=raw,if=virtio \
    -netdev tap,id=lan,ifname=tap2,script=no,downscript=no -device virtio-net-pci,netdev=lan,mac=52:54:00:4b:00:71 \
    -serial tcp:127.0.0.1:5556,server,nowait -monitor none -display none &
echo $! > "$PIDFILE"
sleep 5
expect /usr/local/bin/ppp-client.exp
