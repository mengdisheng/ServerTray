"""Loopback TCP proxy measuring encrypted SSH bytes and controlled outages.

Use only with ssh_fixture.py. Control JSON: {"offline": true} disconnects
clients, {"offline": false} permits recovery. No traffic payloads are stored.
"""
import argparse
import json
import os
import socket
import threading
import time

p = argparse.ArgumentParser()
p.add_argument('--fixture', required=True)
p.add_argument('--receipt', required=True)
p.add_argument('--control', required=True)
p.add_argument('--port', type=int, default=22229)
a = p.parse_args()
with open(a.fixture) as f:
    target = json.load(f)
gate = threading.Lock()
connections = {}
offline = False
counter = 0

def relay(source, dest, record, direction):
    try:
        while True:
            data = source.recv(32768)
            if not data:
                break
            dest.sendall(data)
            with gate:
                record[direction] += len(data)
    except OSError:
        pass
    finally:
        with gate:
            record['active'] = False
        for s in (source, dest):
            try:
                s.shutdown(socket.SHUT_RDWR)
            except OSError:
                pass
            s.close()

def snapshot():
    global offline
    while True:
        try:
            with open(a.control) as f:
                requested = bool(json.load(f).get('offline', False))
        except (OSError, ValueError):
            requested = False
        with gate:
            offline = requested
            if offline:
                for record in connections.values():
                    if record['active']:
                        for s in record['sockets']:
                            try:
                                s.shutdown(socket.SHUT_RDWR)
                            except OSError:
                                pass
            rows = [{k: v for k, v in r.items() if k != 'sockets'} for r in connections.values()]
        report = dict(port=a.port, fingerprint=target['fingerprint'], offline=offline,
                      connections=rows, measuredAt=time.time())
        try:
            with open(a.receipt + '.tmp', 'w') as f:
                json.dump(report, f)
            os.replace(a.receipt + '.tmp', a.receipt)
        except OSError:
            pass
        time.sleep(.1)

threading.Thread(target=snapshot, daemon=True).start()
listener = socket.socket()
listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
listener.bind(('127.0.0.1', a.port))
listener.listen(64)
while True:
    incoming, _ = listener.accept()
    if offline:
        incoming.close()
        continue
    try:
        outgoing = socket.create_connection(('127.0.0.1', target['port']), timeout=5)
        outgoing.settimeout(None)
    except OSError:
        incoming.close()
        continue
    with gate:
        counter += 1
        record = dict(id=counter, sent=0, received=0, active=True, sockets=(incoming, outgoing))
        connections[counter] = record
        # Keep counters bounded during long failure/recovery experiments.
        for ident in list(connections):
            if len(connections) <= 1000:
                break
            if not connections[ident]['active']:
                del connections[ident]
    threading.Thread(target=relay, args=(incoming, outgoing, record, 'sent'), daemon=True).start()
    threading.Thread(target=relay, args=(outgoing, incoming, record, 'received'), daemon=True).start()
