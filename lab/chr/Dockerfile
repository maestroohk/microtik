FROM alpine:3.20
RUN apk add --no-cache qemu-system-x86_64 qemu-img iproute2 unzip expect curl busybox-extras jq
COPY run.sh bootstrap.exp phone.sh console.exp phone-voucher.sh /usr/local/bin/
RUN chmod +x /usr/local/bin/run.sh /usr/local/bin/phone.sh
ENTRYPOINT ["/usr/local/bin/run.sh"]
