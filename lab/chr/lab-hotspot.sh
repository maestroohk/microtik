#!/bin/sh
# Gives a lab CHR the network an operator would already have before adopting Kibo:
# a customer LAN on ether2 with DHCP, a hotspot server, NAT and local DNS names.
# Usage: lab-hotspot.sh <router number 1|2>
set -eu
cd "$(dirname "$0")/../../deploy"
. ./.env
N="$1"
API="http://127.0.0.1:5873$N/rest"
AUTH="lab-provisioner:$KIBO_CHR_ADMIN_PASSWORD"
NET="10.5.6$N"

call() {
    method="$1"; path="$2"; body="${3:-}"
    if [ -n "$body" ]; then
        response=$(curl -s -u "$AUTH" -X "$method" -H "content-type: application/json" -d "$body" "$API/$path")
    else
        response=$(curl -s -u "$AUTH" -X "$method" "$API/$path")
    fi
    case "$response" in
        *'"error"'*) echo "  $method /$path -> $response" ;;
        *) echo "  $method /$path ok" ;;
    esac
}

call PUT ip/address "{\"address\":\"$NET.1/24\",\"interface\":\"ether2\",\"comment\":\"customer-lan\"}"
call PUT ip/pool "{\"name\":\"hs-pool\",\"ranges\":\"$NET.10-$NET.200\"}"
call PUT ip/dhcp-server "{\"name\":\"hs-dhcp\",\"interface\":\"ether2\",\"address-pool\":\"hs-pool\"}"
call PUT ip/dhcp-server/network "{\"address\":\"$NET.0/24\",\"gateway\":\"$NET.1\",\"dns-server\":\"$NET.1\"}"
call POST ip/dns/set '{"allow-remote-requests":"yes"}'
call PUT ip/dns/static '{"name":"portal.kibo.lab","address":"192.168.65.254"}'
call PUT ip/dns/static '{"name":"files.kibo.lab","address":"10.247.0.50"}'
call PUT ip/hotspot/profile "{\"name\":\"kibo-hs\",\"hotspot-address\":\"$NET.1\",\"dns-name\":\"wifi$N.kibo.lab\",\"login-by\":\"http-chap,http-pap\"}"
call PUT ip/hotspot "{\"name\":\"hotspot1\",\"interface\":\"ether2\",\"address-pool\":\"hs-pool\",\"profile\":\"kibo-hs\",\"disabled\":\"no\"}"
call PUT ip/firewall/nat '{"chain":"srcnat","out-interface":"ether1","action":"masquerade","comment":"customer-lan"}'
echo "done"
