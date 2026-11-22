# Simulerer en UEFI-pc der netvaerksbooter: DHCPDISCOVER (arch 7) -> proxyDHCP-svar -> forespoergsel paa port 4011.
# Brug: python dhcpsim.py <mac> [arch]
import random, socket, struct, sys, time

mac = bytes.fromhex(sys.argv[1].replace(':', '').replace('-', ''))
arch = int(sys.argv[2]) if len(sys.argv) > 2 else 7
xid = random.getrandbits(32)
uuid = bytes(random.getrandbits(8) for _ in range(16))

def packet(msgtype, ciaddr='0.0.0.0'):
    p = struct.pack('!BBBBIHH4s4s4s4s16s64s128s', 1, 1, 6, 0, xid, 0, 0x8000,
                    socket.inet_aton(ciaddr), b'\0' * 4, b'\0' * 4, b'\0' * 4, mac + b'\0' * 10, b'', b'')
    vc = f'PXEClient:Arch:{arch:05d}:UNDI:003016'.encode()
    o = b'\x63\x82\x53\x63' + bytes([53, 1, msgtype])
    o += bytes([55, 8, 1, 3, 6, 43, 60, 66, 67, 97])
    o += bytes([60, len(vc)]) + vc
    o += bytes([93, 2]) + struct.pack('!H', arch)
    o += bytes([94, 3, 1, 3, 16])
    o += bytes([97, 17, 0]) + uuid
    return p + o + b'\xff'

def parse(d):
    siaddr = socket.inet_ntoa(d[20:24]); file = d[108:236].split(b'\0')[0].decode(errors='replace')
    opts, i = {}, 240
    while i < len(d) and d[i] != 255:
        if d[i] == 0: i += 1; continue
        opts[d[i]] = d[i + 2:i + 2 + d[i + 1]]; i += 2 + d[i + 1]
    return siaddr, file, opts

s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
s.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
s.bind(('0.0.0.0', 68))
s.settimeout(1)
s.sendto(packet(1), ('255.255.255.255', 67))
proxy = None
end = time.time() + 5
while time.time() < end:
    try: d, a = s.recvfrom(2048)
    except socket.timeout: continue
    if d[4:8] != struct.pack('!I', xid): continue
    siaddr, file, o = parse(d)
    vc = o.get(60, b'').decode(errors='replace')
    print(f'svar fra {a[0]}: type={o.get(53, b"?")[0]} siaddr={siaddr} file={file!r} vendor={vc!r} opt43={o.get(43, b"").hex()}')
    if vc.startswith('PXEClient') and a[0] != '0.0.0.0': proxy = proxy or a[0]
if not proxy:
    print('Intet proxyDHCP-svar'); sys.exit(1)

# Boot-server-forespoergsel (DHCPREQUEST til port 4011)
s2 = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
s2.settimeout(3)
s2.sendto(packet(3, ciaddr=sys.argv[3] if len(sys.argv) > 3 else '192.168.1.50'), (proxy, 4011))
try:
    d, a = s2.recvfrom(2048)
    siaddr, file, o = parse(d)
    print(f'4011-svar fra {a[0]}: siaddr={siaddr} BOOTFIL={file!r} opt67={o.get(67, b"").decode(errors="replace")!r}')
except socket.timeout:
    print('Intet svar paa port 4011')
