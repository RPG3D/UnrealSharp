#!/usr/bin/env python3
"""Verify that no generated calli invocation carries a marshalling-requiring signature.

Why this exists
---------------
CoreCLR's interpreter rejects every calli whose signature needs P/Invoke
marshalling ( src/coreclr/interpreter/compiler.cpp: BADCODE "PInvoke marshalling
for calli is not supported in interpreted code" ). `PInvoke::MarshalingRequired`
(vm/dllimport.cpp) returns TRUE for:

  * ELEMENT_TYPE_BYREF  (ref / out)   - no case in the switch, falls to "non-primitive"
  * ELEMENT_TYPE_BOOLEAN / CHAR       - when runtime marshalling is enabled
  * ELEMENT_TYPE_STRING               - non-primitive

Both dotnet 10 and 11 behave identically here, so any target that may run the
interpreter (iOS CoreCLR, Mono INTERP) must call through an ABI-normalized twin:
string -> char*/byte*, ref/out -> T*, bool -> byte.

NativeCallbacksWrapperGenerator emits those twins automatically; this script
guards the generator against regressions by checking the *call sites* it emits.

Note the distinction: a `(delegate* unmanaged<..., string, ...>)` **cast** in the
static constructor is harmless (no calli happens there). Only invocations count.

Usage
-----
    Tools/verify-calli-abi.py [--keep] [--generated-dir DIR]

Exit code 0 = clean, 1 = violations found, 2 = could not produce generated files.
"""

from __future__ import annotations

import argparse
import os
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

PLUGIN_ROOT = Path(__file__).resolve().parent.parent
SOLUTION = PLUGIN_ROOT / "Managed" / "UnrealSharp" / "UnrealSharp.sln"

# `Type Name = (delegate* unmanaged<SIG>)expr;`  -> captures Name and SIG
DECL_RE = re.compile(r"\b([A-Za-z_]\w*)\s*=\s*\(delegate\* unmanaged<([^>]*)>")
FORBIDDEN = re.compile(r"(?<![\w.])(string|bool)(?![\w])|\b(ref|out)\s")


def find_dotnet() -> str:
    """Locate the dotnet CLI without relying on the caller's PATH.

    A packaging run started from a fresh shell (or after a reboot) may not have
    /usr/local/share/dotnet on PATH even though the SDK is installed; a missing
    CLI then looks like an ABI violation, which is the wrong failure.
    """
    for candidate in (os.environ.get("DOTNET"), shutil.which("dotnet"),
                      "/usr/local/share/dotnet/dotnet", "/usr/share/dotnet/dotnet",
                      "/opt/homebrew/share/dotnet/dotnet"):
        if candidate and Path(candidate).is_file():
            return candidate
    return "dotnet"


def emit_generated_sources(out_dir: Path) -> bool:
    out_dir.mkdir(parents=True, exist_ok=True)
    cmd = [
        find_dotnet(), "build", str(SOLUTION), "-c", "Release",
        "-p:EmitCompilerGeneratedFiles=true",
        f"-p:CompilerGeneratedFilesOutputPath={out_dir}",
    ]
    print(f"[gate] {subprocess.list2cmdline(cmd)}")
    proc = subprocess.run(cmd, capture_output=True, text=True)
    if proc.returncode != 0:
        sys.stderr.write(proc.stdout[-4000:] + proc.stderr[-4000:])
        return False
    return True


def scan(generated_dir: Path) -> list[str]:
    violations: list[str] = []
    files = list(generated_dir.rglob("*.generated.cs"))
    print(f"[gate] scanning {len(files)} generated file(s)")

    for path in files:
        lines = path.read_text(encoding="utf-8", errors="replace").splitlines()

        calli_types: dict[str, str] = {}
        for line in lines:
            for name, sig in DECL_RE.findall(line):
                calli_types[name] = sig

        for lineno, line in enumerate(lines, 1):
            stripped = line.strip()
            if stripped.startswith("//") or DECL_RE.search(line):
                continue
            for name, sig in calli_types.items():
                # An invocation of this calli variable on this line?
                if not re.search(rf"(?<![\w.]){re.escape(name)}\s*\(", line):
                    continue
                if FORBIDDEN.search(sig):
                    violations.append(
                        f"{path.name}:{lineno}: calli '{name}' invoked with "
                        f"marshalling-required signature <{sig.strip()}>"
                    )

    return violations


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--generated-dir", help="reuse an existing emit directory")
    ap.add_argument("--keep", action="store_true", help="keep the temp emit directory")
    args = ap.parse_args()

    tmp: Path | None = None
    if args.generated_dir:
        gen_dir = Path(args.generated_dir)
        if not gen_dir.is_dir():
            sys.stderr.write(f"not a directory: {gen_dir}\n")
            return 2
    else:
        tmp = Path(tempfile.mkdtemp(prefix="calli-gate-"))
        gen_dir = tmp
        if not emit_generated_sources(gen_dir):
            shutil.rmtree(tmp, ignore_errors=True)
            return 2

    try:
        violations = scan(gen_dir)
    finally:
        if tmp is not None and not args.keep:
            shutil.rmtree(tmp, ignore_errors=True)

    if violations:
        print("\n[gate] FAILED - ABI twin missing at these call sites:")
        for v in violations:
            print("  " + v)
        print("\nEvery string/ref/out/bool parameter must reach native code through "
              "the *Abi twin (char*/T*/byte). See the module docstring.")
        return 1

    print("[gate] OK - every calli invocation uses an ABI-normalized signature")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
