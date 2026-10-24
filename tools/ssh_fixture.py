"""Loopback-only SSH protocol fixture. No real hosts, credentials, or user data.

Requires paramiko. Password is deliberately synthetic. Supports exec, slow/failing
targets, bounded-output tests and direct-tcpip channels for single-hop tests.
"""
import argparse, base64, hashlib, json, logging, os, socket, threading, time, uuid
import paramiko

parser = argparse.ArgumentParser()
parser.add_argument('--port', type=int, default=22228)
parser.add_argument('--receipt', required=True)
parser.add_argument('--client-key', help='Optional ignored test-key output for private-key integration tests.')
args = parser.parse_args()
if args.client_key:
    paramiko.RSAKey.generate(2048).write_private_key_file(args.client_key, password='fixture-passphrase')
logging.basicConfig(filename=args.receipt + '.debug.log', level=logging.DEBUG)
key = paramiko.RSAKey.generate(2048)
fingerprint = 'SHA256:' + base64.b64encode(hashlib.sha256(key.asbytes()).digest()).decode().rstrip('=')
stats = dict(connections=0, commands=0, bytes_sent=0, bytes_received=0, active=0)
lock = threading.Lock()
started = time.monotonic()
boot = str(uuid.uuid4())

def response(command):
    sec = time.monotonic() - started
    if 'ST1' not in command:
        return 'OS\nPRETTY_NAME="Fixture Linux"\nARCH\nx86_64\nCORES\n2\n'
    ticks = int(sec * 100)
    network = int(sec * 10000)
    data = f'''ST1
STAT
cpu {1000+ticks//5} 0 100 {5000+ticks*4//5} 0 0 0 0 0 0
BOOT
{boot}
MEM
MemTotal: 2097152 kB
MemAvailable: 1572864 kB
SwapTotal: 1048576 kB
SwapFree: 1048576 kB
UP
{sec+1000:.3f} 100.00
LOAD
0.10 0.20 0.15 1/100 123
NET
Inter-| Receive | Transmit
 face |bytes packets errs drop fifo frame compressed multicast|bytes packets errs drop fifo colls carrier compressed
eth0: {100000+network} 1 0 0 0 0 0 0 {200000+network//2} 1 0 0 0 0 0 0
lo: 100 1 0 0 0 0 0 0 100 1 0 0 0 0 0 0
IO
8 0 sda 1 0 {100+int(sec)*2} 1 1 0 {200+int(sec)} 1 0 0 0
'''
    if "printf 'DISK" in command:
        data += 'DISK\n/dev/sda1 20971520 4194304 16777216 20% /\n'
    return data

class Server(paramiko.ServerInterface):
    def __init__(self): self.username = ''; self.destinations = {}
    def check_auth_password(self, username, password):
        self.username = username
        return paramiko.AUTH_SUCCESSFUL if password == 'fixture-only' else paramiko.AUTH_FAILED
    def check_auth_publickey(self, username, key):
        self.username = username
        return paramiko.AUTH_SUCCESSFUL
    def get_allowed_auths(self, username): return 'password,publickey'
    def check_channel_request(self, kind, chanid): return paramiko.OPEN_SUCCEEDED if kind == 'session' else paramiko.OPEN_FAILED_ADMINISTRATIVELY_PROHIBITED
    def check_channel_direct_tcpip_request(self, chanid, origin, destination):
        if destination[0] != '127.0.0.1' or destination[1] != args.port: return paramiko.OPEN_FAILED_ADMINISTRATIVELY_PROHIBITED
        self.destinations[chanid] = destination
        return paramiko.OPEN_SUCCEEDED
    def check_channel_exec_request(self, channel, command):
        def execute():
            try:
                text = command.decode()
                with lock: stats['commands'] += 1; stats['bytes_received'] += len(command)
                if self.username == 'slow': time.sleep(4)
                elif self.username == 'fail': channel.send_exit_status(1); return
                payload = (('x' * 100000) if self.username == 'oversize' else response(text)).encode()
                channel.sendall(payload)
                with lock: stats['bytes_sent'] += len(payload)
                channel.send_exit_status(0)
            except (OSError, EOFError): pass
            finally: channel.close()
        threading.Thread(target=execute, daemon=True).start()
        return True

def pump(source, dest):
    try:
        while True:
            chunk = source.recv(32768)
            if not chunk: break
            dest.sendall(chunk)
    except (OSError, EOFError): pass
    finally:
        source.close(); dest.close()

def handle(sock):
    transport = paramiko.Transport(sock)
    with lock: stats['connections'] += 1; stats['active'] += 1
    try:
        transport.add_server_key(key); server = Server(); transport.start_server(server=server)
        while transport.is_active():
            channel = transport.accept(1)
            if channel is None: continue
            if channel.chanid in server.destinations:
                target = socket.create_connection(server.destinations[channel.chanid], timeout=5)
                threading.Thread(target=pump, args=(channel, target), daemon=True).start()
                threading.Thread(target=pump, args=(target, channel), daemon=True).start()
    except (OSError, EOFError, paramiko.SSHException): pass
    finally:
        transport.close()
        with lock: stats['active'] -= 1

def receipt():
    while True:
        with lock: snapshot = dict(stats)
        snapshot.update(port=args.port, fingerprint=fingerprint, elapsed=time.monotonic()-started)
        try:
            temp = args.receipt + '.tmp'
            with open(temp, 'w') as f: json.dump(snapshot, f)
            os.replace(temp, args.receipt)
        except OSError: pass
        time.sleep(1)

threading.Thread(target=receipt, daemon=True).start()
listener = socket.socket(); listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
listener.bind(('127.0.0.1', args.port)); listener.listen(64)
print(json.dumps(dict(port=args.port, fingerprint=fingerprint)), flush=True)
while True:
    sock, _ = listener.accept()
    threading.Thread(target=handle, args=(sock,), daemon=True).start()
