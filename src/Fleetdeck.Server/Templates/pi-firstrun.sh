#!/bin/bash
# Fleetdeck: første start af et SD-kort, som Fleetdeck har bygget (Raspberry Pi OS Bookworm).
# Køres én gang af systemd.run fra cmdline.txt – samme mekanisme som Raspberry Pi Imager bruger – efter at billedets
# egen førstestart har gjort plads på kortet. Sætter værtsnavn, bruger (kun SSH-nøgle, ingen adgangskode), tidszone,
# tastatur og evt. fast IP, og lægger tjenesten fleetdeck-firstboot ind, som tilmelder maskinen, når netværket er oppe.
# Indstillingerne står i fleetdeck/settings.env på boot-partitionen; de flyttes til /var/lib/fleetdeck-firstboot (kun root),
# og fleetdeck/ fjernes fra boot-partitionen, før scriptet slutter.
set +e
FW=/boot/firmware; [ -d "$FW/fleetdeck" ] || FW=/boot
SRC="$FW/fleetdeck"; DST=/var/lib/fleetdeck-firstboot
LOG=/var/log/fleetdeck-firstrun.log
exec >>"$LOG" 2>&1
echo "== $(date -Is) Fleetdeck førstestart"
if [ ! -f "$SRC/settings.env" ]; then echo "fleetdeck/settings.env mangler – intet at gøre"; else
  # shellcheck disable=SC1091
  . "$SRC/settings.env"
  install -d -m 700 "$DST"
  cp -a "$SRC/." "$DST/"
  chmod 600 "$DST"/*

  CUSTOM=/usr/lib/raspberrypi-sys-mods/imager_custom
  if [ -x "$CUSTOM" ]; then
    "$CUSTOM" set_hostname "$FD_NAME"
  else
    echo "$FD_NAME" > /etc/hostname
    sed -i "s/127\.0\.1\.1.*/127.0.1.1\t$FD_NAME/" /etc/hosts
  fi

  # første bruger: omdøbes til den valgte, uden adgangskode (login kun med SSH-nøgle)
  if [ -x /usr/lib/userconf-pi/userconf ]; then /usr/lib/userconf-pi/userconf "$FD_USER"; fi
  KEYS=()
  while IFS= read -r k; do [ -n "$k" ] && KEYS+=("$k"); done < "$SRC/authorized_keys"
  if [ -x "$CUSTOM" ]; then
    "$CUSTOM" enable_ssh -k "${KEYS[@]}"
  else
    H=$(getent passwd "$FD_USER" | cut -d: -f6)
    install -d -m 700 -o "$FD_USER" -g "$FD_USER" "$H/.ssh"
    install -m 600 -o "$FD_USER" -g "$FD_USER" "$SRC/authorized_keys" "$H/.ssh/authorized_keys"
    sed -i 's/^[#[:space:]]*PasswordAuthentication[[:space:]].*/PasswordAuthentication no/' /etc/ssh/sshd_config
    systemctl enable ssh
  fi

  if [ -n "${FD_TIMEZONE:-}" ] && [ -x "$CUSTOM" ]; then "$CUSTOM" set_timezone "$FD_TIMEZONE"; fi
  if [ -n "${FD_KEYMAP:-}" ] && [ -x "$CUSTOM" ]; then "$CUSTOM" set_keymap "$FD_KEYMAP"; fi
  if [ -n "${FD_WLAN_COUNTRY:-}" ] && [ -x "$CUSTOM" ]; then "$CUSTOM" set_wlan_country "$FD_WLAN_COUNTRY"; fi

  # fast IP: en NetworkManager-forbindelse (mode 600). Ellers DHCP som billedet selv gør.
  if [ "${FD_IP_MODE:-dhcp}" = static ]; then
    NM=/etc/NetworkManager/system-connections
    install -d -m 700 "$NM"
    cat > "$NM/fleetdeck-$FD_IFACE.nmconnection" <<EOF
[connection]
id=Fleetdeck $FD_IFACE
type=ethernet
interface-name=$FD_IFACE
autoconnect=true
autoconnect-priority=100

[ipv4]
method=manual
address1=$FD_ADDRESS,$FD_GATEWAY
dns=${FD_DNS:-$FD_GATEWAY};

[ipv6]
method=auto
EOF
    chmod 600 "$NM/fleetdeck-$FD_IFACE.nmconnection"
  fi

  install -m 755 "$SRC/firstboot.sh" /usr/local/sbin/fleetdeck-firstboot
  install -m 644 "$SRC/fleetdeck-firstboot.service" /etc/systemd/system/fleetdeck-firstboot.service
  systemctl enable fleetdeck-firstboot.service
  # engangskoden ligger nu kun i /var/lib/fleetdeck-firstboot
  rm -rf "$SRC"
fi
rm -f "$FW/firstrun.sh"
sed -i 's| systemd\.run=[^ ]*||g; s| systemd\.run_success_action=[^ ]*||g; s| systemd\.unit=kernel-command-line\.target||g' "$FW/cmdline.txt"
echo "== $(date -Is) færdig – genstarter"
exit 0
