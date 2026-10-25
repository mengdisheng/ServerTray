"""Scan publishable source and optional Git history without printing matched values."""
import argparse
import ipaddress
import pathlib
import re
import subprocess
import sys

root = pathlib.Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--history', action='store_true', help='Also scan all reachable historical file versions.')
args = parser.parse_args()

patterns = [
    re.compile(r'-----BEGIN (?:OPENSSH |RSA |EC |DSA |ENCRYPTED )?PRIVATE KEY-----'),
    re.compile(r'\b(?:gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{40,}|AKIA[A-Z0-9]{16})\b'),
    re.compile(r'(?i)\b(?:bearer|api[_-]?key)\s*[:= ]\s*[A-Za-z0-9_+/=-]{32,}'),
    re.compile(r'(?i)\b[A-Z]:[\\/](?:Users|Documents and Settings)[\\/][^\\/\s"<>]+'),
    re.compile(r'/(?:home|Users)/[A-Za-z0-9_.-]+/'),
]
forbidden_names = {'credentials.json', 'known-hosts.json', 'alert-state.json', 'config.json', '.env'}
forbidden_suffixes = {'.pem', '.key', '.pfx', '.p12', '.ppk', '.sqlite', '.db', '.log', '.dmp', '.zip', '.exe', '.dll', '.pdb'}
binary_suffixes = {'.ico', '.png', '.jpg', '.jpeg'}
documentation_networks = tuple(ipaddress.ip_network(n) for n in ('192.0.2.0/24', '198.51.100.0/24', '203.0.113.0/24'))
bad = set()


def git(*arguments):
    return subprocess.run(['git', *arguments], cwd=root, capture_output=True, check=True).stdout


def check(relative, content, context='working tree'):
    path = pathlib.PurePosixPath(relative.replace('\\', '/'))
    name = path.name.lower()
    parts = {part.lower() for part in path.parts}
    if (name in forbidden_names or name.startswith('.env.') or name.startswith(('id_rsa', 'id_ed25519', 'id_ecdsa'))
            or path.suffix.lower() in forbidden_suffixes or parts.intersection({'artifacts', '.ssh', 'private', 'bin', 'obj'})
            or name.endswith(('.db-wal', '.db-shm', '.sqlite-wal', '.sqlite-shm'))):
        bad.add((relative, context, 'private/generated-data file'))
        return
    if path.suffix.lower() in binary_suffixes:
        return
    try:
        text = content.decode('utf-8-sig')
    except UnicodeDecodeError:
        bad.add((relative, context, 'unexpected binary file'))
        return
    if any(pattern.search(text) for pattern in patterns):
        bad.add((relative, context, 'credential or personal-path pattern'))
    for match in re.finditer(r'(?<![\w.])(?:\d{1,3}\.){3}\d{1,3}(?![\w.])', text):
        prefix = text[max(0, match.start() - 40):match.start()]
        if re.search(r'''(?i)(?:version|assemblyversion)\s*=\s*["']$''', prefix):
            continue
        try:
            address = ipaddress.ip_address(match[0])
        except ValueError:
            continue
        if not (address.is_loopback or address.is_unspecified or any(address in network for network in documentation_networks)):
            bad.add((relative, context, 'non-example IP address'))
            break


paths = sorted(set(git('ls-files', '--cached', '--others', '--exclude-standard', '-z').decode('utf-8').split('\0')) - {''})
for relative in paths:
    path = root / relative
    if path.is_file():
        check(relative, path.read_bytes())

historical_count = 0
if args.history:
    # Include deleted files by examining every reachable historical blob.
    objects = {}
    for line in git('-c', 'core.quotePath=false', 'rev-list', '--objects', '--all').decode('utf-8').splitlines():
        oid, separator, relative = line.partition(' ')
        if separator:
            objects[oid] = relative
    process = subprocess.Popen(['git', 'cat-file', '--batch'], cwd=root, stdin=subprocess.PIPE, stdout=subprocess.PIPE)
    try:
        for oid, relative in objects.items():
            process.stdin.write((oid + '\n').encode('ascii'))
            process.stdin.flush()
            header = process.stdout.readline().split()
            if len(header) != 3:
                raise RuntimeError('Cannot read historical object')
            kind, size = header[1], int(header[2])
            content = process.stdout.read(size)
            if len(content) != size or process.stdout.read(1) != b'\n':
                raise RuntimeError('Truncated historical object')
            if kind == b'blob':
                check(relative, content, 'history ' + oid[:12])
                historical_count += 1
    finally:
        process.stdin.close()
        process.stdout.close()
        if process.wait() != 0:
            raise RuntimeError('Historical scan failed')

if bad:
    for relative, context, reason in sorted(bad):
        print(f'FAIL {relative} ({context}): {reason}')
    sys.exit(1)
print(f'PASS: checked {len(paths)} source files and {historical_count} historical blobs; no prohibited data patterns found.')
