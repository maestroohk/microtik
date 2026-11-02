#!/bin/sh
# Docker Desktop's bridge does not deliver ARP broadcasts from the bridged CHR VM, so the CHR
# cannot discover lab neighbours on its own. Pin the ones it must initiate traffic to.
# Lab-only workaround. Usage: lab-arp.sh <router number 1|2>
set -eu
cd "$(dirname "$0")/../../deploy"
. ./.env
API="http://127.0.0.1:5873$1/rest"
AUTH="lab-provisioner:$KIBO_CHR_ADMIN_PASSWORD"

mac_of_container() {
    docker inspect -f '{{range .NetworkSettings.Networks}}{{.MacAddress}}{{end}}' "$1"
}

gateway_mac=$(docker run --rm --network kibo-lab-wan alpine:3.20 sh -c "ping -c1 -W1 10.247.0.1 >/dev/null; ip neigh show 10.247.0.1" | awk '{print $5}')

for pair in "10.247.0.1 $gateway_mac" "10.247.0.10 $(mac_of_container kibo-lab-freeradius-1)" "10.247.0.50 $(mac_of_container kibo-lab-lab-files-1)"; do
    set -- $pair
    for id in $(curl -s -u "$AUTH" "$API/ip/arp?address=$1" | grep -o '"\.id":"[^"]*"' | cut -d'"' -f4); do
        curl -s -g -u "$AUTH" -X DELETE "$API/ip/arp/$id" >/dev/null
    done
    curl -s -u "$AUTH" -X PUT -H "content-type: application/json" \
        -d "{\"address\":\"$1\",\"mac-address\":\"$2\",\"interface\":\"ether1\",\"comment\":\"lab-static-arp\"}" "$API/ip/arp" >/dev/null
    echo "pinned $1 -> $2"
done
