"""A stand-in for Master Audio Switcher's bridge (protocol 2) that logs every call.

Usage: python tools/fake-mas.py <dir>, then start the dock with LOCALAPPDATA=<dir>;
calls land in <dir>/calls.log. FAKE_SLOW=<s> makes a switch take that
long; FAKE_MUTED=1 reports the sound switched off.
"""
import json, os, sys, time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

root = sys.argv[1]
note_dir = os.path.join(root, 'MasterAudioSwitcher')
os.makedirs(note_dir, exist_ok=True)
log = open(os.path.join(root, 'calls.log'), 'a', encoding='utf-8', buffering=1)
TOKEN = 'tok-123'
SLOW = float(os.environ.get('FAKE_SLOW', '0'))  # seconds a switch takes
MUTED = os.environ.get('FAKE_MUTED') == '1'
devices = [
    {'id': 'A', 'name': 'Speakers', 'kind': 'output', 'icon': 'speakers'},
    {'id': 'B', 'name': 'Headphones', 'kind': 'output', 'icon': 'headphones'},
]
current = 0
hosting = False
SVG = {
    'speakers': '<svg><path d="M4 4h6v16H4z" stroke="currentColor"/></svg>',
    'headphones': '<svg><path d="M4 12a8 8 0 0 1 16 0v6" stroke="currentColor"/></svg>',
}


def state():
    return {'outputs': [dict(d, in_cycle=True, is_default=(i == current)) for i, d in enumerate(devices)],
            'inputs': [], 'known_outputs': [], 'settings': {}}


class H(BaseHTTPRequestHandler):
    def log_message(self, *a):
        pass

    def reply(self, code, body, ctype='application/json'):
        data = body if isinstance(body, bytes) else json.dumps(body).encode()
        self.send_response(code)
        self.send_header('Content-Type', ctype)
        self.send_header('Content-Length', str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        name = self.path.rsplit('/', 1)[-1].removesuffix('.svg')
        log.write(f'{time.strftime("%H:%M:%S")} GET {self.path}\n')
        if name in SVG:
            return self.reply(200, SVG[name].encode(), 'image/svg+xml')
        self.reply(404, b'no', 'text/plain')

    def do_POST(self):
        global current, hosting
        method = self.path.removeprefix('/api/')
        n = int(self.headers.get('Content-Length') or 0)
        args = json.loads(self.rfile.read(n) or b'{}')
        tok = self.headers.get('X-MAS-Token')
        log.write(f'{time.strftime("%H:%M:%S")} POST {method} token={"ok" if tok == TOKEN else tok} args={args}\n')
        if tok != TOKEN:
            return self.reply(403, {'error': 'bad token'})
        if method == 'dock_hello':
            result = {'protocol': 2, 'version': '9.9.9', 'hosting': hosting,
                      'master': {'volume': 0.5, 'muted': MUTED}, 'state': state()}
        elif method == 'dock_take_over':
            hosting = bool(args['hosting'])
            result = {'hosting': hosting}
        elif method == 'switch_next':
            time.sleep(SLOW)
            current = (current + 1) % len(devices)
            result = state()
        elif method == 'bring_forward':
            result = True
        else:
            return self.reply(500, {'ok': False, 'error': 'no such method'})
        self.reply(200, {'ok': True, 'result': result})


srv = ThreadingHTTPServer(('127.0.0.1', 0), H)
port = srv.server_address[1]
json.dump({'protocol': 2, 'url': f'http://127.0.0.1:{port}/', 'token': TOKEN, 'pid': os.getpid(), 'version': '9.9.9'},
          open(os.path.join(note_dir, 'bridge.json'), 'w'))
log.write(f'up on {port}\n')
srv.serve_forever()
