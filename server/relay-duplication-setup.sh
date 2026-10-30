#!/bin/bash
# Adds the Gaming Enjoyer relay's packet duplication front-end on the relay server:
#   ssh <server> 'bash -s' < server/relay-duplication-setup.sh
# UDP 51821 takes the home PC's packets from any of its connections (Starlink, phone), each prefixed with a 16-byte
# token, and hands them to WireGuard on 127.0.0.1:51820; WireGuard's replies go back to every connection seen in the
# last 30 s. WireGuard's own replay protection drops whichever copy arrives second, in both directions.
set -e

install -d -m 700 /etc/ge-relay-dup
[ -s /etc/ge-relay-dup/token ] || (umask 077; head -c 16 /dev/urandom | od -An -tx1 | tr -d ' \n' > /etc/ge-relay-dup/token)

cat > /usr/local/bin/ge-relay-dup <<"EOF"
#!/usr/bin/env python3
"""Gaming Enjoyer game relay: packet duplication front-end for WireGuard (wg0).

The home PC sends every WireGuard packet over each of its internet connections to UDP 51821, prefixed with a
16-byte token. Packets with the right token are passed to WireGuard on 127.0.0.1:51820 (WireGuard drops the second
copy of each by its replay counter). Every packet WireGuard sends back goes to each address that sent a valid
packet in the last 30 s (most recent 4), with the token in front, so replies also arrive over every connection.
"""
import hmac
import os
import select
import socket
import time

TOKEN = bytes.fromhex(open(os.path.join(os.environ.get("CREDENTIALS_DIRECTORY", "/etc/ge-relay-dup"), "token")).read().strip())
LISTEN = ("0.0.0.0", 51821)
WIREGUARD = ("127.0.0.1", 51820)
FORGET_AFTER = 30.0
MAX_PATHS = 4

public = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
public.bind(LISTEN)
wg = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
wg.bind(("127.0.0.1", 0))
wg.connect(WIREGUARD)
paths = {}  # (ip, port) -> last valid packet, monotonic seconds

while True:
    ready, _, _ = select.select([public, wg], [], [], 5)
    now = time.monotonic()
    for addr in [a for a, seen in paths.items() if now - seen > FORGET_AFTER]:
        del paths[addr]
    for sock in ready:
        try:
            if sock is public:
                data, addr = public.recvfrom(65535)
                if len(data) <= len(TOKEN) or not hmac.compare_digest(data[:len(TOKEN)], TOKEN):
                    continue
                paths[addr] = now
                if len(paths) > MAX_PATHS:
                    del paths[min(paths, key=paths.get)]
                wg.send(data[len(TOKEN):])
            else:
                data = wg.recv(65535)
                for addr in paths:
                    public.sendto(TOKEN + data, addr)
        except OSError:
            pass  # an unreachable path or a WireGuard restart: the next packet carries on
EOF
chmod 755 /usr/local/bin/ge-relay-dup

cat > /etc/systemd/system/ge-relay-dup.service <<"EOF"
[Unit]
Description=Gaming Enjoyer relay: packet duplication front-end for WireGuard (UDP 51821 -> wg0)
After=network-online.target wg-quick@wg0.service
Wants=network-online.target

[Service]
ExecStart=/usr/bin/python3 /usr/local/bin/ge-relay-dup
LoadCredential=token:/etc/ge-relay-dup/token
DynamicUser=yes
Restart=always
RestartSec=2
NoNewPrivileges=yes
ProtectSystem=strict
ProtectHome=yes
PrivateTmp=yes
PrivateDevices=yes
ProtectKernelTunables=yes
ProtectKernelModules=yes
ProtectControlGroups=yes
RestrictAddressFamilies=AF_INET
CapabilityBoundingSet=
MemoryMax=64M

[Install]
WantedBy=multi-user.target
EOF

# Open UDP 51821 in the relay's own firewalld policy (the RunCloud zone stays untouched).
firewall-cmd -q --permanent --policy=wgrelay-in --add-port=51821/udp
firewall-cmd -q --policy=wgrelay-in --add-port=51821/udp

systemctl daemon-reload
systemctl enable --now ge-relay-dup
sleep 1
echo "== state"
systemctl is-active ge-relay-dup
ss -ulnp | grep -E ":5182[01]"
firewall-cmd --info-policy=wgrelay-in | grep ports
echo "== sites"
for d in $(cat /root/wg-relay-sites.txt); do
  printf "%s %s\n" "$(curl -s -o /dev/null -w "%{http_code}" -m 10 --resolve $d:443:127.0.0.1 https://$d/)" "$d"
done | diff - /root/wg-relay-sites-before.txt && echo "all sites unchanged"
