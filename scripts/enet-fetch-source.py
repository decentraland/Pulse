#!/usr/bin/env python3
"""Fetches the pinned rusty_enet source into the git-ignored native/enet/vendor folder.

Subcommands:
  fetch     Sparse, blob-filtered fetch of the pinned commit; idempotent.
  verify    Checks commit, tree SHA and a clean working tree; no network.
  selftest  Exercises fetch and verify against a throwaway local repository.
"""

import json
import os
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
PIN_FILE = ROOT / "native" / "enet" / "PIN.json"
VENDOR_DIR = ROOT / "native" / "enet" / "vendor" / "dcl-one"


class PinError(Exception):
    """A pin check failed; the message names what mismatched."""


def git(cwd, *args, check=True):
    """Runs git in cwd and returns its stripped stdout."""
    env = dict(os.environ, GIT_TERMINAL_PROMPT="0")
    result = subprocess.run(
        ["git", *args],
        cwd=str(cwd),
        env=env,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
    )
    if check and result.returncode != 0:
        raise PinError(
            "git %s failed (exit %d):\n%s%s"
            % (" ".join(args), result.returncode, result.stdout, result.stderr)
        )
    return result.stdout.strip()


def load_pin(pin_file):
    with open(pin_file, encoding="utf-8") as handle:
        return json.load(handle)


def verify(pin, vendor_dir):
    """Raises PinError unless vendor_dir holds exactly the pinned folder."""
    if not (vendor_dir / ".git").exists():
        raise PinError("%s is not a git repository; run fetch" % vendor_dir)

    head = git(vendor_dir, "rev-parse", "HEAD", check=False)
    if head != pin["rev"]:
        raise PinError("rev mismatch: HEAD is '%s', pinned '%s'" % (head, pin["rev"]))

    tree = git(vendor_dir, "rev-parse", "HEAD:" + pin["path"], check=False)
    if tree != pin["tree_sha"]:
        raise PinError(
            "tree_sha mismatch: %s is '%s', pinned '%s'" % (pin["path"], tree, pin["tree_sha"])
        )

    status = git(vendor_dir, "status", "--porcelain")
    if status:
        raise PinError("working tree is not clean:\n%s" % status)


def fetch(pin, vendor_dir):
    """Fetches only pin['path'] at pin['rev'] into vendor_dir, then verifies it."""
    try:
        verify(pin, vendor_dir)
        return
    except PinError:
        pass

    vendor_dir.mkdir(parents=True, exist_ok=True)
    if not (vendor_dir / ".git").exists():
        git(vendor_dir, "init", "--quiet")
    git(vendor_dir, "config", "core.autocrlf", "false")
    git(vendor_dir, "config", "core.longpaths", "true")
    git(vendor_dir, "remote", "remove", "origin", check=False)
    git(vendor_dir, "remote", "add", "origin", pin["repository"])
    git(vendor_dir, "sparse-checkout", "set", "--no-cone", "/%s/" % pin["path"])
    git(vendor_dir, "fetch", "--depth", "1", "--filter=blob:none", "origin", pin["rev"])
    git(vendor_dir, "checkout", "--detach", "FETCH_HEAD")
    verify(pin, vendor_dir)


def selftest():
    identity = ["-c", "user.name=selftest", "-c", "user.email=selftest@example.com",
                "-c", "commit.gpgsign=false"]
    with tempfile.TemporaryDirectory() as tmp:
        tmp_path = Path(tmp)
        source = tmp_path / "source"
        (source / "lib" / "vendor" / "pkg").mkdir(parents=True)
        (source / "other").mkdir()
        (source / "lib" / "vendor" / "pkg" / "a.txt").write_text("a\n")
        (source / "other" / "b.txt").write_text("b\n")
        git(source, "init", "--quiet")
        git(source, "config", "uploadpack.allowFilter", "true")
        git(source, "config", "uploadpack.allowAnySHA1InWant", "true")
        git(source, "add", ".")
        git(source, *identity, "commit", "--quiet", "-m", "init")

        pin = {
            "repository": source.as_uri(),
            "rev": git(source, "rev-parse", "HEAD"),
            "path": "lib/vendor/pkg",
            "tree_sha": git(source, "rev-parse", "HEAD:lib/vendor/pkg"),
        }
        vendor = tmp_path / "vendor" / "dcl-one"

        fetch(pin, vendor)
        verify(pin, vendor)
        assert (vendor / pin["path"] / "a.txt").exists(), "pinned file missing"
        assert not (vendor / "other").exists(), "sibling folder was checked out"

        head_before = git(vendor, "rev-parse", "HEAD")
        fetch(pin, vendor)
        assert git(vendor, "rev-parse", "HEAD") == head_before, "second fetch changed HEAD"
        verify(pin, vendor)

        bad = dict(pin, tree_sha="0" * 40)
        try:
            verify(bad, vendor)
        except PinError as error:
            assert "tree_sha" in str(error), "mismatch did not name tree_sha: %s" % error
        else:
            raise AssertionError("wrong tree_sha passed verify")

        (vendor / pin["path"] / "a.txt").write_text("edited\n")
        try:
            verify(pin, vendor)
        except PinError:
            pass
        else:
            raise AssertionError("edited file passed verify")

    print("selftest ok")


def main(argv):
    command = argv[1] if len(argv) > 1 else ""
    try:
        if command == "selftest":
            selftest()
        elif command in ("fetch", "verify"):
            pin = load_pin(PIN_FILE)
            if command == "fetch":
                fetch(pin, VENDOR_DIR)
            else:
                verify(pin, VENDOR_DIR)
            print("%s ok" % command)
        else:
            print("usage: enet-fetch-source.py fetch|verify|selftest", file=sys.stderr)
            return 2
    except (PinError, AssertionError) as error:
        print("error: %s" % error, file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
