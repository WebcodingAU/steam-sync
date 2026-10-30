#!/bin/bash
# Finishes the Gaming Enjoyer game relay on the relay server. Run from Windows with:
#   ssh <server> 'bash -s' < server/relay-setup-finish.sh
# Already done: wireguard-tools installed, /etc/wireguard/wg0.conf + keys, firewalld zone wgrelay (wg0) and
# policies wgrelay-in (UDP 51820 in) / wgrelay-out (wg0 -> internet), saved but not yet loaded.
# The RunCloud zone is not edited. Backups: /root/wg-relay-backup-*
set -e

# 1. NAT for the tunnel only, in its own nftables table (firewalld reloads don't touch it).
cat > /etc/wireguard/wgrelay-nat.nft <<"EOF"
# NAT for the game relay only (10.66.66.0/24 leaving eth0). wg-quick adds it on up and deletes it on down.
table ip wgrelay {
  chain postrouting {
    type nat hook postrouting priority srcnat + 20; policy accept;
    ip saddr 10.66.66.0/24 oifname "eth0" masquerade
  }
}
EOF
grep -q "^PostUp" /etc/wireguard/wg0.conf || sed -i "/^PrivateKey/a PreUp = nft delete table ip wgrelay 2>/dev/null || true\nPostUp = nft -f /etc/wireguard/wgrelay-nat.nft\nPostDown = nft delete table ip wgrelay" /etc/wireguard/wg0.conf

# 2. Forwarding on (firewalld still rejects every forward except wg0 -> internet), then re-apply the box's own sysctls.
printf "# Gaming Enjoyer game relay (wg0). firewalld still rejects all forwarding except wg0 -> internet (policy wgrelay-out).\nnet.ipv4.ip_forward = 1\n" > /etc/sysctl.d/99-wgrelay.conf
sysctl -q -p /etc/sysctl.d/99-wgrelay.conf
sysctl -q --system

# 3. Load the saved firewalld zone/policies (runtime was checked to equal the saved config) and start the tunnel.
firewall-cmd --reload
systemctl enable --now wg-quick@wg0

echo "== state"
sysctl net.ipv4.ip_forward net.ipv4.conf.all.rp_filter net.ipv4.tcp_congestion_control
firewall-cmd --get-active-zones
firewall-cmd --get-policies
firewall-cmd --zone=runcloud --list-ports
wg show wg0 | grep -vi private
nft list table ip wgrelay | grep masquerade

echo "== sites (before -> now)"
for d in $(cat /root/wg-relay-sites.txt); do
  printf "%s -> %s %s\n" "$(grep " $d\$" /root/wg-relay-sites-before.txt | cut -d" " -f1)" \
    "$(curl -s -o /dev/null -w "%{http_code}" -m 10 --resolve $d:443:127.0.0.1 https://$d/)" "$d"
done

echo "== client config (keep private)"
echo "server public key: $(cat /root/wg-relay/server.pub)"
