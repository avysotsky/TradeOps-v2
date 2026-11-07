import copy
import importlib.util
import pathlib
import socket
import threading
import unittest

path = pathlib.Path(__file__).resolve().parents[1] / 'deribit/fix_sql_relay.py'
spec = importlib.util.spec_from_file_location('fix_sql_relay', path)
relay = importlib.util.module_from_spec(spec); spec.loader.exec_module(relay)

class RelayTests(unittest.TestCase):
    def metadata(self):
        return {'Id': 'a'*64, 'Name': '/'+relay.NAME, 'State': {'Running': True},
                'Config': {'Labels': {'tradeops.qualification.owner': relay.OWNER}},
                'HostConfig': {'NetworkMode': relay.NETWORK, 'Privileged': False, 'PortBindings': {}},
                'Mounts': [{'Name': 'tradeops-mainnet-qualification-data-v1', 'Destination': '/var/lib/postgresql/data', 'RW': True}],
                'NetworkSettings': {'Networks': {relay.NETWORK: {'IPAddress': '172.25.0.2'}}}}
    def test_owned_target(self):
        self.assertEqual(('172.25.0.2', 5432), relay.target_address(self.metadata(), 'a'*64))
    def test_foreign_targets_are_refused(self):
        variants = [('Id', 'b'*64), ('Name', '/foreign')]
        for key, value in variants:
            with self.subTest(key=key):
                data = self.metadata(); data[key] = value
                with self.assertRaises(ValueError): relay.target_address(data, 'a'*64)
        for variant in ['stopped', 'label', 'network', 'privileged', 'port', 'volume', 'read-only-volume', 'extra-network', 'public-ip', 'loopback', 'unspecified']:
            with self.subTest(variant=variant):
                c = self.metadata()
                if variant == 'stopped': c['State']['Running'] = False
                if variant == 'label': c['Config']['Labels'] = {}
                if variant == 'network': c['HostConfig']['NetworkMode'] = 'foreign'
                if variant == 'privileged': c['HostConfig']['Privileged'] = True
                if variant == 'port': c['HostConfig']['PortBindings'] = {'5432/tcp': [{'HostPort': '5432'}]}
                if variant == 'volume': c['Mounts'] = []
                if variant == 'read-only-volume': c['Mounts'][0]['RW'] = False
                if variant == 'extra-network': c['NetworkSettings']['Networks']['foreign'] = {'IPAddress': '172.26.0.2'}
                if variant in ['public-ip', 'loopback', 'unspecified']:
                    c['NetworkSettings']['Networks'][relay.NETWORK]['IPAddress'] = {'public-ip': '8.8.8.8', 'loopback': '127.0.0.1', 'unspecified': '0.0.0.0'}[variant]
                with self.assertRaises(ValueError): relay.target_address(c, 'a'*64)
    def test_binary_and_half_close(self):
        client, near = socket.socketpair(); far, server = socket.socketpair(); stop = threading.Event(); errors = []
        def worker():
            try: relay.forward(near, far, stop)
            except Exception as error: errors.append(type(error).__name__)
        t = threading.Thread(target=worker); t.start()
        def read(sock, count):
            sock.settimeout(2); data = b''
            while len(data) < count:
                chunk = sock.recv(count-len(data)); self.assertTrue(chunk); data += chunk
            return data
        try:
            payload = bytes(range(256))*200
            client.sendall(payload); self.assertEqual(payload, read(server, len(payload)))
            server.sendall(payload[::-1]); self.assertEqual(payload[::-1], read(client, len(payload)))
            client.shutdown(socket.SHUT_WR); server.settimeout(2); self.assertEqual(b'', server.recv(1))
            server.sendall(b'last'); self.assertEqual(b'last', read(client, 4)); server.shutdown(socket.SHUT_WR)
            t.join(2); self.assertFalse(t.is_alive()); self.assertEqual([], errors)
        finally:
            stop.set()
            for sock in [client, near, far, server]: sock.close()
            t.join(2)
    def test_idle_stop_is_bounded(self):
        a, client = socket.socketpair(); b, server = socket.socketpair(); stop = threading.Event()
        t = threading.Thread(target=relay.forward, args=(a,b,stop)); t.start(); stop.set(); t.join(2)
        self.assertFalse(t.is_alive())
        for sock in [a,b,client,server]: sock.close()

if __name__ == '__main__': unittest.main()
