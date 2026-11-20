#!/usr/bin/env python3
"""Summarise a Windows minidump (.dmp) of Skyrim without a debugger.

For crashes Crash Logger doesn't catch (shutdown, returning to the main menu): Windows' LocalDumps writes a .dmp instead.
Prints the exception, the faulting module + offset, and a stack scan of the crashing thread: every value on its stack
that points into a loaded module, the way Crash Logger's "stack scan" works. Without symbols that is a list of leads
(module+offset), not a call stack.

Written from Microsoft's published minidump format (MINIDUMP_HEADER, MINIDUMP_DIRECTORY, the exception, module list,
thread list and memory list streams). Read-only.

Usage: scripts/minidump-summary.py <file.dmp> [--stack-entries N]
"""
import struct
import sys
from collections import Counter

EXCEPTION_STREAM, MODULE_LIST, THREAD_LIST, MEMORY_LIST, MEMORY64_LIST = 6, 4, 3, 5, 9

CODES = {
    0xC0000005: "EXCEPTION_ACCESS_VIOLATION",
    0xC0000374: "STATUS_HEAP_CORRUPTION",
    0xC0000409: "STATUS_STACK_BUFFER_OVERRUN (fail-fast)",
    0x80000003: "EXCEPTION_BREAKPOINT",
    0xC00000FD: "EXCEPTION_STACK_OVERFLOW",
    0xE06D7363: "C++ exception (MSVC throw)",
    0xC0000096: "EXCEPTION_PRIV_INSTRUCTION",
    0xC000001D: "EXCEPTION_ILLEGAL_INSTRUCTION",
}


def read_string(f, rva):
    f.seek(rva)
    (length,) = struct.unpack("<I", f.read(4))
    return f.read(length).decode("utf-16-le", "replace")


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    path = sys.argv[1]
    entries = 40
    if "--stack-entries" in sys.argv:
        entries = int(sys.argv[sys.argv.index("--stack-entries") + 1])

    with open(path, "rb") as f:
        sig, ver, nstreams, dir_rva, _checksum, timestamp, flags = struct.unpack("<IIIIIIQ", f.read(32))
        if sig != 0x504D444D:
            print("not a minidump")
            return 1
        f.seek(dir_rva)
        streams = {}
        for _ in range(nstreams):
            stype, size, rva = struct.unpack("<III", f.read(12))
            streams.setdefault(stype, (size, rva))

        # Modules: base, size, name.
        modules = []
        if MODULE_LIST in streams:
            _, rva = streams[MODULE_LIST]
            f.seek(rva)
            (count,) = struct.unpack("<I", f.read(4))
            for i in range(count):
                f.seek(rva + 4 + i * 108)
                base, size, _cs, _ts, name_rva = struct.unpack("<QIIII", f.read(24))
                modules.append((base, size, read_string(f, name_rva)))
        modules.sort()

        def where(addr):
            for base, size, name in modules:
                if base <= addr < base + size:
                    return name.split("\\")[-1], addr - base
            return None

        # Memory ranges, to read the crashing thread's stack.
        ranges = []
        if MEMORY64_LIST in streams:
            _, rva = streams[MEMORY64_LIST]
            f.seek(rva)
            n, base_rva = struct.unpack("<QQ", f.read(16))
            off = base_rva
            for _ in range(n):
                start, size = struct.unpack("<QQ", f.read(16))
                ranges.append((start, size, off))
                off += size
        elif MEMORY_LIST in streams:
            _, rva = streams[MEMORY_LIST]
            f.seek(rva)
            (n,) = struct.unpack("<I", f.read(4))
            for _ in range(n):
                start, dsize, drva = struct.unpack("<QII", f.read(16))
                ranges.append((start, dsize, drva))

        def read_mem(addr, size):
            for start, rsize, off in ranges:
                if start <= addr < start + rsize:
                    take = min(size, start + rsize - addr)
                    f.seek(off + addr - start)
                    return f.read(take)
            return b""

        print(f"file: {path}")
        print(f"modules: {len(modules)}, memory ranges: {len(ranges)}")
        if EXCEPTION_STREAM not in streams:
            print("no exception stream (a dump taken by hand, not on a crash)")
            return 0
        _, rva = streams[EXCEPTION_STREAM]
        f.seek(rva)
        thread_id, _align = struct.unpack("<II", f.read(8))
        code, eflags, _rec, addr, nparams, _a2 = struct.unpack("<IIQQII", f.read(32))
        params = struct.unpack("<15Q", f.read(120))[:nparams]
        ctx_size, ctx_rva = struct.unpack("<II", f.read(8))
        print(f"exception: 0x{code:08X} {CODES.get(code, '')} in thread {thread_id}")
        loc = where(addr)
        print(f"at: 0x{addr:X}" + (f"  {loc[0]}+0x{loc[1]:X}" if loc else "  (outside every module)"))
        if code == 0xC0000005 and len(params) >= 2:
            print(f"access violation: {'write' if params[0] == 1 else 'execute' if params[0] == 8 else 'read'} at 0x{params[1]:X}")

        # x64 CONTEXT: Rsp at 0x98, Rip at 0xF8.
        f.seek(ctx_rva)
        ctx = f.read(ctx_size)
        rsp = struct.unpack_from("<Q", ctx, 0x98)[0] if len(ctx) >= 0x100 else 0
        regs = ["Rax", "Rcx", "Rdx", "Rbx", "Rsp", "Rbp", "Rsi", "Rdi", "R8", "R9", "R10", "R11", "R12", "R13", "R14", "R15"]
        if len(ctx) >= 0x100:
            vals = struct.unpack_from("<16Q", ctx, 0x78)
            print("registers pointing into modules:")
            for name, v in zip(regs, vals):
                w = where(v)
                if w:
                    print(f"  {name} = {w[0]}+0x{w[1]:X}")

        stack = read_mem(rsp, 0x4000)
        hits = []
        for i in range(0, len(stack) - 7, 8):
            v = struct.unpack_from("<Q", stack, i)[0]
            w = where(v)
            if w:
                hits.append((i, w))
        print(f"stack scan from RSP 0x{rsp:X} ({len(stack)} bytes read), values inside modules, nearest first:")
        for i, (mod, off) in hits[:entries]:
            print(f"  [RSP+{i:X}] {mod}+0x{off:X}")
        print("modules most often on the stack:")
        for mod, n in Counter(m for _, (m, _) in hits).most_common(10):
            print(f"  {n:3d}  {mod}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
