"""Bounded loopback-only relay to the existing owned qualification DB. No SQL or credentials."""
import argparse
import datetime
import hashlib
import ipaddress
import json
import pathlib
import re
import selectors
import signal
import socket
import subprocess
import threading
import time

NAME = 'tradeops-mainnet-qualification-pg17'
NETWORK = 'tradeops-mainnet-qualification-net'
OWNER = 'tradeops-mainnet-qualification-v1'

def target_address(c, expected):
    if not re.fullmatch('[0-9a-f]{64}', expected) or c['Id'] != expected or c['Name'] != '/' + NAME:
        raise ValueError('Target identity refused')
    if not c['State']['Running'] or c['Config']['Labels'].get('tradeops.qualification.owner') != OWNER:
        raise ValueError('Target ownership refused')
    host = c['HostConfig']
    if host['NetworkMode'] != NETWORK or host['Privileged'] or host.get('PortBindings'):
        raise ValueError('Target isolation refused')
    if not any(m.get('Name') == 'tradeops-mainnet-qualification-data-v1' and m['Destination'] == '/var/lib/postgresql/data' and m.get('RW') for m in c['Mounts']):
        raise ValueError('Target storage refused')
    networks = c['NetworkSettings']['Networks']
    if set(networks) != {NETWORK}:
        raise ValueError('Target network refused')
    address = ipaddress.ip_address(networks[NETWORK]['IPAddress'])
    if address.version != 4 or not address.is_private or address.is_loopback or address.is_unspecified or address.is_multicast:
        raise ValueError('Target address refused')
    return str(address), 5432

def inspect_target(expected):
    raw = subprocess.run(['docker', 'inspect', NAME], check=True, capture_output=True, timeout=5).stdout
    if len(raw) > 1048576:
        raise ValueError('Target metadata capacity exceeded')
    return target_address(json.loads(raw)[0], expected)

def forward(a, b, stop):
    """Preserve both byte streams and half-close; never decode or record PostgreSQL traffic."""
    a.settimeout(5); b.settimeout(5)
    with selectors.DefaultSelector() as selector:
        selector.register(a, selectors.EVENT_READ, b)
        selector.register(b, selectors.EVENT_READ, a)
        while selector.get_map() and not stop.is_set():
            for key, _ in selector.select(.25):
                data = key.fileobj.recv(65536)
                if data:
                    key.data.sendall(data)
                else:
                    selector.unregister(key.fileobj)
                    try: key.data.shutdown(socket.SHUT_WR)
                    except OSError: pass

def main():
    parser = argparse.ArgumentParser(description='Owned qualification DB loopback relay')
    parser.add_argument('--expected-container-id', required=True)
    parser.add_argument('--port', type=int, required=True)
    parser.add_argument('--lifetime', type=int, default=900)
    parser.add_argument('--ready-path', required=True)
    args = parser.parse_args()
    if not 49152 <= args.port <= 65535 or not 60 <= args.lifetime <= 900:
        raise ValueError('Relay bounds refused')
    ready = pathlib.Path(args.ready_path).absolute()
    folder = pathlib.Path(__file__).resolve().parents[2] / '.tradeops/fix-host'
    if ready.parent != folder or not re.fullmatch(r'relay-[0-9a-f]{32}\.json', ready.name):
        raise ValueError('Relay evidence path refused')
    for path in [ready.parent, *ready.parent.parents]:
        if not path.is_dir() or path.is_symlink():
            raise ValueError('Relay evidence directory refused')
    inspect_target(args.expected_container_id)
    stop = threading.Event(); slots = threading.BoundedSemaphore(16); clients = set(); sync = threading.Lock()
    signal.signal(signal.SIGTERM, lambda *_: stop.set())
    signal.signal(signal.SIGINT, lambda *_: stop.set())
    def serve(client):
        try:
            with client:
                with socket.create_connection(inspect_target(args.expected_container_id), timeout=5) as upstream:
                    forward(client, upstream, stop)
        except (OSError, ValueError, KeyError, subprocess.SubprocessError):
            pass  # No SQL/authentication bytes or exception payload is logged.
        finally:
            with sync: clients.discard(client)
            slots.release()
    started = datetime.datetime.now(datetime.timezone.utc)
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as server:
        server.bind(('127.0.0.1', args.port)); server.listen(16); server.settimeout(.25)
        state = {'SchemaVersion': 1, 'Status': 'LOOPBACK_SQL_RELAY_READY', 'Host': '127.0.0.1', 'Port': args.port,
                 'ContainerId': args.expected_container_id, 'StartedUtc': started.isoformat(),
                 'ExpiresUtc': (started + datetime.timedelta(seconds=args.lifetime)).isoformat(),
                 'SourceSha256': hashlib.sha256(pathlib.Path(__file__).read_bytes().replace(b'\r\n', b'\n')).hexdigest()}
        with ready.open('x') as stream: json.dump(state, stream)
        print(json.dumps({'Status': state['Status'], 'Port': args.port}), flush=True)
        deadline = time.monotonic() + args.lifetime
        try:
            while not stop.is_set() and time.monotonic() < deadline:
                try: client, peer = server.accept()
                except socket.timeout: continue
                if peer[0] != '127.0.0.1' or not slots.acquire(blocking=False):
                    client.close(); continue
                with sync: clients.add(client)
                threading.Thread(target=serve, args=(client,), daemon=True).start()
        finally:
            stop.set()
            with sync:
                for client in clients:
                    try: client.shutdown(socket.SHUT_RDWR)
                    except OSError: pass
    return 0

if __name__ == '__main__':
    try: raise SystemExit(main())
    except (ValueError, KeyError, OSError, subprocess.SubprocessError):
        print('{"Status":"LOOPBACK_SQL_RELAY_REFUSED"}', flush=True)
        raise SystemExit(1)
