#!/bin/sh
# Simulates a phone behind the CHR hotspot redeeming a voucher through the Kibo portal.
# Usage (inside the CHR lab container): phone-voucher.sh <phone number> <voucher code> [target url]
set -u
PHONE="phone$1"
CODE="$2"
TARGET="${3:-http://files.kibo.lab/}"
JAR="/tmp/$PHONE.jar"
run() { ip netns exec "$PHONE" "$@"; }
rm -f "$JAR"

run udhcpc -i eth0 -n -q -t 5 -s /usr/share/udhcpc/default.script >/dev/null 2>&1

LOGIN_PAGE=$(run curl -s -m 15 -o /dev/null -w '%{redirect_url}' "$TARGET")
echo "intercepted -> $LOGIN_PAGE"
PORTAL=$(run curl -s -m 15 "$LOGIN_PAGE" | grep -o 'url=[^"]*' | head -1 | sed 's/^url=//')
echo "portal -> ${PORTAL%%\?*}"
BASE=$(echo "$PORTAL" | sed 's#\(https\?://[^/]*\)/p/\([^?]*\).*#\1/p/\2#')

run curl -s -m 20 -c "$JAR" -b "$JAR" -o /dev/null "$PORTAL"
FORM=$(run curl -s -m 20 -c "$JAR" -b "$JAR" "$BASE/voucher")
TOKEN=$(echo "$FORM" | grep -o 'name="__RequestVerificationToken" value="[^"]*"' | head -1 | sed 's/.*value="//;s/"$//')
HANDLER=$(echo "$FORM" | grep -o 'name="_handler" value="[^"]*"' | head -1 | sed 's/.*value="//;s/"$//')

ROUTER_LOGIN=$(run curl -s -m 20 -c "$JAR" -b "$JAR" -o /dev/null -w '%{redirect_url}' \
    --data-urlencode "__RequestVerificationToken=$TOKEN" --data-urlencode "_handler=$HANDLER" \
    --data-urlencode "Code=$CODE" "$BASE/voucher")
echo "portal answered with router login -> $(echo "$ROUTER_LOGIN" | sed 's/password=[^&]*/password=<chap-hash>/')"

RESULT=$(run curl -s -m 20 -L -o /tmp/$PHONE-result.html -w '%{http_code} %{url_effective}' "$ROUTER_LOGIN")
echo "after login -> $RESULT"
grep -o 'error[^<]*' /tmp/$PHONE-result.html | head -2

run curl -s -m 15 -o /dev/null -w 'internet check -> %{http_code} %{redirect_url}\n' "$TARGET"
