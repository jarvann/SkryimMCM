#!/usr/bin/env python3
"""Which game versions an SKSE plugin DLL says it supports (its SKSEPlugin_Version data), without loading it.

    scripts/skse-dll-versions.py <dll or folder> [...]

Address Library or signature independence means any AE version the Address Library covers; otherwise only the listed versions.
"""
import os, struct, sys

def rva_to_off(sections, rva):
    for va, vs, raw, rs in sections:
        if va <= rva < va + max(vs, rs): return rva - va + raw
    return None

def info(path):
    b = open(path, 'rb').read()
    pe = struct.unpack_from('<I', b, 0x3c)[0]
    if b[pe:pe + 4] != b'PE\0\0': return None
    n = struct.unpack_from('<H', b, pe + 6)[0]; opt = struct.unpack_from('<H', b, pe + 20)[0]
    sec = [struct.unpack_from('<IIII', b, pe + 24 + opt + 40 * i + 8) for i in range(n)]
    sections = [(va, vs, raw, rs) for vs, va, rs, raw in sec]
    exp = struct.unpack_from('<I', b, pe + 24 + 112)[0]
    eo = rva_to_off(sections, exp)
    if eo is None: return {'plugin': 'no exports'}
    count, funcs, names, ords = struct.unpack_from('<I', b, eo + 24)[0], *struct.unpack_from('<III', b, eo + 28)
    query = False
    for i in range(count):
        no = rva_to_off(sections, struct.unpack_from('<I', b, rva_to_off(sections, names) + 4 * i)[0])
        name = b[no:no + 40].split(b'\0')[0]
        if name == b'SKSEPlugin_Version':
            o = struct.unpack_from('<H', b, rva_to_off(sections, ords) + 2 * i)[0]
            d = rva_to_off(sections, struct.unpack_from('<I', b, rva_to_off(sections, funcs) + 4 * o)[0])
            ver, = struct.unpack_from('<I', b, d + 4)
            pname = b[d + 8:d + 264].split(b'\0')[0].decode('latin1')
            ex, ind = struct.unpack_from('<II', b, d + 8 + 256 + 256 + 252)
            comp = [v for v in struct.unpack_from('<16I', b, d + 8 + 256 + 256 + 252 + 8) if v]
            skse, = struct.unpack_from('<I', b, d + 8 + 256 + 256 + 252 + 8 + 64)
            fmt = lambda v: f"{v >> 24}.{(v >> 16) & 0xff}.{(v >> 4) & 0xfff}"
            how = 'Address Library' if ind & 1 else 'signatures' if ind & 2 else 'listed versions only'
            return {'plugin': pname, 'independent': how, 'structs': {0: 'any', 4: '1.6.629+', 8: '1.6.1130+', 12: '1.6.629+/1.6.1130+'}.get(ind & 0xC, hex(ind)), 'versions': [fmt(v) for v in comp], 'minimumSkse': fmt(skse) if skse else 'any'}
        if name == b'SKSEPlugin_Query': query = True
    return {'plugin': 'SE-style only (Query, no Version): SE 1.5.97'} if query else {'plugin': 'not an SKSE plugin'}

for arg in sys.argv[1:]:
    files = [arg] if os.path.isfile(arg) else [os.path.join(r, f) for r, _, fs in os.walk(arg) for f in fs if f.lower().endswith('.dll')]
    for f in files:
        try: print(os.path.relpath(f, os.path.dirname(arg.rstrip('/'))), info(f))
        except Exception as e: print(f, 'unreadable', e)
