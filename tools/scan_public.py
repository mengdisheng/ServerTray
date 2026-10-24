"""Check tracked and non-ignored source files before publishing; never print matches."""
import ipaddress, pathlib, re, subprocess, sys

root = pathlib.Path(__file__).resolve().parents[1]
result = subprocess.run(['git','ls-files','--cached','--others','--exclude-standard','-z'], cwd=root, capture_output=True, check=True)
paths = sorted(set(result.stdout.decode().split('\0')) - {''})
patterns = [
    re.compile(r'-----BEGIN (?:OPENSSH |RSA |EC |DSA |ENCRYPTED )?PRIVATE KEY-----'),
    re.compile(r'\b(?:gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{40,}|AKIA[A-Z0-9]{16})\b'),
    re.compile(r'(?i)\b(?:bearer|api[_-]?key)\s*[:= ]\s*[A-Za-z0-9_+/=-]{32,}'),
]
forbidden_names = {'credentials.json','known-hosts.json','alert-state.json','config.json'}
bad = []
for relative in paths:
    p = root / relative
    if not p.is_file(): continue
    if p.name in forbidden_names or p.suffix.lower() in {'.pem','.key','.pfx','.p12','.sqlite','.db','.log'}:
        bad.append((relative, 'private-data file')); continue
    if p.suffix.lower() in {'.ico','.png','.jpg','.zip'}: continue
    try: text = p.read_text(encoding='utf-8-sig')
    except (UnicodeDecodeError, OSError): continue
    if any(pattern.search(text) for pattern in patterns): bad.append((relative, 'credential pattern'))
    for match in re.finditer(r'(?<![\w.])(?:\d{1,3}\.){3}\d{1,3}(?![\w.])', text):
        if re.search(r'''(?i)(?:version|assemblyversion)\s*=\s*["']$''',text[max(0,match.start()-40):match.start()]): continue
        try: address = ipaddress.ip_address(match[0])
        except ValueError: continue
        documentation = any(address in ipaddress.ip_network(network) for network in ('192.0.2.0/24','198.51.100.0/24','203.0.113.0/24'))
        if address.is_global and not documentation: bad.append((relative, 'non-documentation public IP')); break
if bad:
    for relative, reason in bad: print(f'FAIL {relative}: {reason}')
    sys.exit(1)
print(f'PASS: checked {len(paths)} source files; no private-data files or credential patterns found.')
