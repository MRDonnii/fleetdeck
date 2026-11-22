#!/bin/sh
# Fleetdeck: én container – web/API (.NET) + netværksboot (dnsmasq proxy-DHCP og TFTP).
# PXE=0 slår netværksboot fra. PXE_INTERFACE vælger netkort (standard: det med standardruten).

start_pxe() {
    tftp=/winsetup/PXE/tftp
    if [ ! -d "$tftp" ]; then echo "[pxe] $tftp findes ikke - netværksboot startes ikke"; return; fi
    mkdir -p /winsetup/PXE/deploy/hosts /winsetup/PXE/pi/hosts
    iface="${PXE_INTERFACE:-$(awk '$2=="00000000" {print $1; exit}' /proc/net/route)}"
    cidr="$(ip -o -4 addr show dev "$iface" 2>/dev/null | awk '{print $4; exit}')"
    if [ -z "$iface" ] || [ -z "$cidr" ]; then echo "[pxe] fandt intet netkort med IPv4 - netværksboot startes ikke"; return; fi
    # Netværksadresse og maske ud fra fx 192.168.1.29/24
    set -- $(echo "$cidr" | awk -F'[./]' '{
        ip=($1*16777216)+($2*65536)+($3*256)+$4; bits=$5; m=0;
        for(i=0;i<bits;i++) m+=2^(31-i);
        n=ip-(ip%(2^(32-bits)));
        printf "%d.%d.%d.%d %d.%d.%d.%d\n", int(n/16777216)%256, int(n/65536)%256, int(n/256)%256, n%256,
                                          int(m/16777216)%256, int(m/65536)%256, int(m/256)%256, m%256 }')
    sed -e "s/@IFACE@/$iface/" -e "s/@SUBNET@/$1/" -e "s/@NETMASK@/$2/" /app/pxe/dnsmasq.conf > /tmp/dnsmasq.conf
    echo "[pxe] netværksboot på $iface ($cidr), proxy-DHCP for $1/$2"
    (
        dnsmasq -k --log-facility=- -C /tmp/dnsmasq.conf &
        pid=$!
        # SIGHUP får dnsmasq til at glemme Ubuntu-udrulninger der er fjernet
        while kill -0 "$pid" 2>/dev/null; do sleep 30; kill -HUP "$pid" 2>/dev/null; done
        echo "[pxe] dnsmasq stoppede (er port 67/69 optaget? netværk 'host' deles med andre PXE-servere)"
    ) &
}

[ "${PXE:-1}" != "0" ] && start_pxe
exec dotnet /app/Fleetdeck.Server.dll "$@"
