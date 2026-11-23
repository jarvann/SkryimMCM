#!/usr/bin/env python3
"""List or extract files from a Skyrim SE BSA (version 105, LZ4 frame compression), with no third-party modules.

    python3 -I scripts/bsa-extract.py ARCHIVE.bsa --list [REGEX]
    python3 -I scripts/bsa-extract.py ARCHIVE.bsa --extract REGEX OUTDIR

Used to read vanilla compiled scripts (.pex) that ship only inside the game's archives.
"""
import os
import re
import struct
import sys


def lz4_block(src, out):
    """Decode one LZ4 block from src, appending to out (bytearray); out holds earlier blocks for linked matches."""
    i, n = 0, len(src)
    while i < n:
        token = src[i]; i += 1
        lit = token >> 4
        if lit == 15:
            while True:
                b = src[i]; i += 1; lit += b
                if b != 255: break
        out += src[i:i + lit]; i += lit
        if i >= n: break
        off = src[i] | (src[i + 1] << 8); i += 2
        ml = token & 15
        if ml == 15:
            while True:
                b = src[i]; i += 1; ml += b
                if b != 255: break
        ml += 4
        start = len(out) - off
        for k in range(ml):
            out.append(out[start + k])


def lz4_frame(data):
    if struct.unpack_from('<I', data)[0] != 0x184D2204: raise ValueError('not an LZ4 frame')
    flg = data[4]; pos = 6 + (8 if flg & 0x08 else 0) + (4 if flg & 0x01 else 0) + 1
    out = bytearray()
    while True:
        size = struct.unpack_from('<I', data, pos)[0]; pos += 4
        if size == 0: break
        raw = size & 0x80000000; size &= 0x7FFFFFFF
        block = data[pos:pos + size]; pos += size
        if raw: out += block
        else: lz4_block(block, out)
        if flg & 0x10: pos += 4  # block checksum
    return bytes(out)


def entries(path):
    b = open(path, 'rb').read()
    magic, version, offset, aflags, nfolders, nfiles, _, _, _ = struct.unpack_from('<4sIIIIIIII', b)
    if magic != b'BSA\0' or version != 105: raise ValueError(f'{path}: not an SE BSA (version {version})')
    folders = [struct.unpack_from('<QIIQ', b, offset + 24 * k) for k in range(nfolders)]
    pos = offset + 24 * nfolders
    files = []
    for _, count, _, _ in folders:
        name = ''
        if aflags & 1:
            ln = b[pos]; name = b[pos + 1:pos + ln].decode('latin1'); pos += 1 + ln
        for _ in range(count):
            _, size, off = struct.unpack_from('<QII', b, pos); pos += 16
            files.append([name, size, off])
    if aflags & 2:  # file names block
        for f in files:
            end = b.index(b'\0', pos); f.append(b[pos:end].decode('latin1')); pos = end + 1
    for folder, size, off, fname in files:
        yield folder + '\\' + fname, (b, size, off, aflags)


def read(blob):
    b, size, off, aflags = blob
    compressed = bool(aflags & 4) ^ bool(size & 0x40000000)
    size &= 0x3FFFFFFF
    data = b[off:off + size]
    if aflags & 0x100:  # names embedded before the data
        ln = data[0]; data = data[1 + ln:]
    if compressed: data = lz4_frame(data[4:])
    return data


def main():
    if len(sys.argv) < 3: sys.exit(__doc__)
    archive, mode = sys.argv[1], sys.argv[2]
    pattern = re.compile(sys.argv[3] if len(sys.argv) > 3 else '.', re.I)
    for name, blob in entries(archive):
        if not pattern.search(name): continue
        if mode == '--list': print(name)
        elif mode == '--extract':
            dest = os.path.join(sys.argv[4], name.replace('\\', '/'))
            os.makedirs(os.path.dirname(dest), exist_ok=True)
            open(dest, 'wb').write(read(blob)); print(dest)


if __name__ == '__main__':
    main()
