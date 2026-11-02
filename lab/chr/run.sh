#!/bin/sh
set -eu
IMAGE=/data/chr.img
if [ ! -f "$IMAGE" ]; then
    unzip -p "/images/chr-${CHR_VERSION}.img.zip" > "$IMAGE"
fi

ADDRESS=$(ip -4 -o addr show eth0 | awk '{print $4}')
GATEWAY=$(ip route | awk '/default/ {print $3}')
echo "$ADDRESS $GATEWAY" > /data/network

ip link add br0 type bridge
ip tuntap add tap0 mode tap
ip link set tap0 master br0
ip addr flush dev eth0
ip link set eth0 master br0
ip link set br0 up
ip link set tap0 up

ip link add br1 type bridge
ip tuntap add tap1 mode tap
ip link set tap1 master br1
ip link set br1 up
ip link set tap1 up

for n in 1 2 3; do
    ip netns add "phone$n"
    ip link add "veth$n" type veth peer name eth0 netns "phone$n"
    ip link set "veth$n" master br1
    ip link set "veth$n" up
    ip netns exec "phone$n" ip link set lo up
    ip netns exec "phone$n" ip link set eth0 address "02:42:c0:a8:58:0$n"
    ip netns exec "phone$n" ip link set eth0 up
done

ACCEL=tcg
if [ -e /dev/kvm ]; then
    ACCEL=kvm
fi

qemu-system-x86_64 -name "chr-${CHR_NAME}" -machine accel=$ACCEL -m 256 -smp 1 -nographic \
    -drive file="$IMAGE",format=raw,if=virtio \
    -netdev tap,id=wan,ifname=tap0,script=no,downscript=no -device virtio-net-pci,netdev=wan,mac="${CHR_MAC}" \
    -netdev tap,id=lan,ifname=tap1,script=no,downscript=no -device virtio-net-pci,netdev=lan \
    -serial tcp:127.0.0.1:5555,server,nowait -monitor none -display none &
QEMU=$!

if [ ! -f /data/bootstrapped ]; then
    sleep 5
    expect /usr/local/bin/bootstrap.exp "${ADDRESS}" "${GATEWAY}" && touch /data/bootstrapped || echo "bootstrap failed"
fi

wait $QEMU
