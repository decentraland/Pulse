#!/usr/bin/env python3
"""Records and checks which sources and libraries the committed ENet glue came from.

Subcommands:
  write     Writes native/enet/PROVENANCE.json. The source digest reads the git index,
            so the files under native/enet must be staged or committed first.
  check     Recomputes the record and compares it with PROVENANCE.json; no network.
  selftest  Exercises write and check against a throwaway git repository.

Both write and check accept --root to name another repository root.
"""

import argparse
import hashlib
import json
import os
import re
import subprocess
import sys
import tempfile
from pathlib import Path

DEFAULT_ROOT = Path(__file__).resolve().parent.parent
ENET_DIR = "native/enet"
PROVENANCE_FILE = "native/enet/PROVENANCE.json"
PIN_FILE = "native/enet/PIN.json"
TOOLCHAIN_FILE = "native/enet/rust-toolchain.toml"
LIBRARIES = (
    "src/DCLPulse/runtimes/win-x64/native/enet.dll",
    "src/DCLPulse/runtimes/linux-x64/native/libenet.so",
    "src/DCLPulse/runtimes/macos-arm64/native/libenet.dylib",
)


class ProvenanceError(Exception):
    """The record cannot be produced or compared; the message says why."""


def git(cwd, *args):
    """Runs git in cwd and returns its raw stdout bytes."""
    env = dict(os.environ, GIT_TERMINAL_PROMPT="0")
    result = subprocess.run(
        ["git", *args], cwd=str(cwd), env=env, stdout=subprocess.PIPE, stderr=subprocess.PIPE
    )
    if result.returncode != 0:
        raise ProvenanceError(
            "git %s failed (exit %d): %s"
            % (" ".join(args), result.returncode, result.stderr.decode("utf-8", "replace"))
        )
    return result.stdout


def source_digest(root):
    """SHA-256 of the staged listing of native/enet, excluding the provenance file."""
    listing = git(root, "ls-files", "-s", "--", ENET_DIR, ":!" + PROVENANCE_FILE)
    if not listing.strip():
        raise ProvenanceError("no files of %s are staged or committed" % ENET_DIR)
    return hashlib.sha256(listing.replace(b"\r\n", b"\n")).hexdigest()


def file_sha256(path):
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for chunk in iter(lambda: handle.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def read_toolchain(root):
    path = root / TOOLCHAIN_FILE
    if not path.is_file():
        raise ProvenanceError("%s is missing" % TOOLCHAIN_FILE)
    match = re.search(r'^\s*channel\s*=\s*"([^"]+)"', path.read_text(encoding="utf-8"), re.M)
    if not match:
        raise ProvenanceError("%s has no channel" % TOOLCHAIN_FILE)
    return match.group(1)


def compute(root):
    """Returns the record for the current sources and libraries, in fixed key order."""
    pin_path = root / PIN_FILE
    if not pin_path.is_file():
        raise ProvenanceError("%s is missing" % PIN_FILE)
    with open(pin_path, encoding="utf-8") as handle:
        pin = json.load(handle)

    libraries = {}
    for relative in LIBRARIES:
        path = root / relative
        if not path.is_file():
            raise ProvenanceError("library %s is missing" % relative)
        libraries[relative] = file_sha256(path)

    return {
        "dcl_one_rev": pin["rev"],
        "rusty_enet_tree_sha": pin["tree_sha"],
        "toolchain": read_toolchain(root),
        "source_digest": source_digest(root),
        "libraries": libraries,
    }


def write(root):
    record = compute(root)
    with open(root / PROVENANCE_FILE, "w", encoding="utf-8", newline="\n") as handle:
        json.dump(record, handle, indent=2)
        handle.write("\n")


def check(root):
    """Returns one message per item that differs from the recorded value."""
    path = root / PROVENANCE_FILE
    if not path.is_file():
        return ["%s is missing" % PROVENANCE_FILE]
    with open(path, encoding="utf-8") as handle:
        recorded = json.load(handle)
    current = compute(root)

    errors = []
    for key in ("source_digest", "dcl_one_rev", "rusty_enet_tree_sha", "toolchain"):
        if recorded.get(key) != current[key]:
            errors.append(
                "%s differs: recorded '%s', current '%s'" % (key, recorded.get(key), current[key])
            )
    recorded_libraries = recorded.get("libraries", {})
    for relative in LIBRARIES:
        if recorded_libraries.get(relative) != current["libraries"][relative]:
            errors.append(
                "%s differs: recorded '%s', current '%s'"
                % (relative, recorded_libraries.get(relative), current["libraries"][relative])
            )
    return errors


def selftest():
    identity = ["-c", "user.name=selftest", "-c", "user.email=selftest@example.com",
                "-c", "commit.gpgsign=false"]
    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        (root / "native" / "enet" / "src").mkdir(parents=True)
        (root / "native" / "enet" / "PIN.json").write_text(
            json.dumps({"rev": "a" * 40, "tree_sha": "b" * 40}), encoding="utf-8"
        )
        (root / "native" / "enet" / "rust-toolchain.toml").write_text(
            '[toolchain]\nchannel = "1.0.0"\n', encoding="utf-8"
        )
        (root / "native" / "enet" / "src" / "lib.rs").write_text("fn main() {}\n")
        for relative in LIBRARIES:
            (root / relative).parent.mkdir(parents=True, exist_ok=True)
            (root / relative).write_bytes(relative.encode("utf-8"))
        git(root, "init", "--quiet")
        git(root, "add", ".")
        git(root, *identity, "commit", "--quiet", "-m", "init")

        assert check(root) == ["%s is missing" % PROVENANCE_FILE], "missing file not reported"
        write(root)
        assert (root / PROVENANCE_FILE).read_text().endswith("}\n"), "no trailing newline"
        assert check(root) == [], "fresh record failed check: %s" % check(root)

        library = root / LIBRARIES[1]
        original = library.read_bytes()
        library.write_bytes(original[:-1] + b"X")
        errors = check(root)
        assert len(errors) == 1 and LIBRARIES[1] in errors[0], "library change: %s" % errors
        library.write_bytes(original)
        assert check(root) == [], "restored library still fails"

        (root / "native" / "enet" / "src" / "lib.rs").write_text("fn main() { }\n")
        git(root, "add", ".")
        errors = check(root)
        assert len(errors) == 1 and "source_digest" in errors[0], "source change: %s" % errors

    print("selftest ok")


def main(argv):
    parser = argparse.ArgumentParser(description="Record and check ENet glue provenance.")
    commands = parser.add_subparsers(dest="command", required=True)
    for name, text in (
        ("write", "write native/enet/PROVENANCE.json (sources must be staged or committed)"),
        ("check", "compare PROVENANCE.json with the sources and libraries"),
    ):
        sub = commands.add_parser(name, help=text, description=text)
        sub.add_argument("--root", type=Path, default=DEFAULT_ROOT,
                         help="repository root (default: the repository of this script)")
    commands.add_parser("selftest", help="run against a temporary repository")
    args = parser.parse_args(argv[1:])

    try:
        if args.command == "selftest":
            selftest()
        elif args.command == "write":
            write(args.root.resolve())
            print("write ok")
        else:
            errors = check(args.root.resolve())
            if errors:
                for message in errors:
                    print("error: %s" % message, file=sys.stderr)
                return 1
            print("provenance ok")
    except (ProvenanceError, AssertionError, OSError, KeyError, ValueError) as error:
        print("error: %s" % error, file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
