"""Make the pair of keys that decides who may publish an update.

Run once, on a machine you trust, and never again unless the private half is
lost or leaked. The private half signs releases and lives only in the
repository's secrets; the public half is compiled into the program
(src/MCD.Core/Update/ReleaseKey.cs), and it is what makes an update from anybody
else impossible to install.

    pip install cryptography
    python tools/make_update_key.py

Writes the private key to a file the repository ignores and the public key into
ReleaseKey.cs. Put the file's contents into the repository secret
UPDATE_SIGNING_KEY, then delete it.
"""
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PRIVATE_FILE = ROOT / ".update-signing-key.pem"
KEY_SOURCE = ROOT / "src" / "MCD.Core" / "Update" / "ReleaseKey.cs"

try:
    from cryptography.hazmat.primitives import serialization
    from cryptography.hazmat.primitives.asymmetric import ec
except ImportError:
    raise SystemExit("pip install cryptography")

if PRIVATE_FILE.exists():
    raise SystemExit(f"{PRIVATE_FILE.name} is already here. Delete it on purpose "
                     "if you really mean to replace the release key.")

private = ec.generate_private_key(ec.SECP256R1())
numbers = private.public_key().public_numbers()
public = numbers.x.to_bytes(32, "big") + numbers.y.to_bytes(32, "big")

PRIVATE_FILE.write_bytes(private.private_bytes(
    serialization.Encoding.PEM,
    serialization.PrivateFormat.PKCS8,
    serialization.NoEncryption()))

text = KEY_SOURCE.read_text(encoding="utf-8")
text = re.sub(r'Convert\.FromHexString\(\s*"[0-9a-fA-F]*"\s*\)',
              f'Convert.FromHexString(\n        "{public.hex()}")', text, count=1)
KEY_SOURCE.write_text(text, encoding="utf-8")

print(f"public key written to {KEY_SOURCE.relative_to(ROOT)}")
print(f"The private key is in {PRIVATE_FILE.name} (the repository ignores it).")
print("Put its whole contents into the repository secret UPDATE_SIGNING_KEY")
print("(GitHub: Settings > Secrets and variables > Actions > New repository secret),")
print("then delete the file.")
