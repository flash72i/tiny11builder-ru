# -*- coding: utf-8 -*-
"""Add RT_ICON + RT_GROUP_ICON resources to a PE (apphost) in-place by rebuilding .rsrc
(last section) and extending it. Keeps existing resource blobs byte-identical."""
import struct, sys, shutil

EXE, ICO = sys.argv[1], sys.argv[2]

d = bytearray(open(EXE, 'rb').read())
e = struct.unpack_from('<I', d, 0x3C)[0]
nsec = struct.unpack_from('<H', d, e+6)[0]
opt = e+24
magic = struct.unpack_from('<H', d, opt)[0]
assert magic == 0x20b, "expect PE32+"
secalign, filealign = struct.unpack_from('<II', d, opt+32)
sizeimage, = struct.unpack_from('<I', d, opt+56)
dd = opt+112
rsva, rssize = struct.unpack_from('<II', d, dd+2*8)
so = opt+struct.unpack_from('<H', d, e+20)[0]
secs = []
for i in range(nsec):
    b = so+i*40
    nm = d[b:b+8].rstrip(b'\0').decode()
    vs, va, rs, pr = struct.unpack_from('<IIII', d, b+8)
    secs.append([nm, va, vs, rs, pr, b])
rsrc = secs[-1]
assert rsrc[0] == '.rsrc', f"last section is {rsrc[0]}"
base = rsrc[4]  # raw pointer

# ---- parse existing tree ----
def rd(off, fmt): return struct.unpack_from(fmt, d, off)
leaves = {}   # (type,name,lang) -> blob bytes
def walk_dir(off, ids):
    nn, ni = rd(off+12, '<HH')
    for i in range(nn+ni):
        eo = off+16+i*8
        name, nxt = rd(eo, '<II')
        assert not (name & 0x80000000), "named resources unsupported"
        name &= 0x7FFFFFFF
        if nxt & 0x80000000:
            walk_dir(base+(nxt & 0x7FFFFFFF), ids+[name])
        else:
            do = base+(nxt & 0x7FFFFFFF)
            drva, dsz = rd(do, '<II')
            offf = drva - rsrc[1] + base
            leaves[tuple(ids+[name])] = bytes(d[offf:offf+dsz])
walk_dir(base, [])
old_types = sorted({k[0] for k in leaves})
assert 3 not in old_types and 14 not in old_types, "icons already present"

# ---- parse ICO ----
ico = open(ICO, 'rb').read()
_, typ, cnt = struct.unpack_from('<HHH', ico, 0)
assert typ == 1
icons = []
for i in range(cnt):
    o = 6+i*16
    w, h, colors, res, planes, bpp, size, off = struct.unpack_from('<BBBBHHII', ico, o)
    icons.append(dict(w=w, h=h, colors=colors, planes=planes, bpp=bpp, blob=ico[off:off+size]))
icons.sort(key=lambda x: (x['w'] or 256))
group = struct.pack('<HHH', 0, 1, len(icons))
for i, ic in enumerate(icons):
    group += struct.pack('<BBBBHHIH', ic['w'], ic['h'], ic['colors'], 0, 1, ic['bpp'], len(ic['blob']), i+1)
for i, ic in enumerate(icons):
    leaves[(3, i+1, 0x0409)] = ic['blob']
leaves[(14, 100, 0x0409)] = group

# ---- rebuild tree ----
types = sorted({k[0] for k in leaves})
buf = bytearray()
def pad8():
    while len(buf) % 8: buf.append(0)
# sizes: root + type dirs + name dirs + lang dirs, then data entries, then blobs
ntypes = len(types)
type_entries = {t: sorted({k[1] for k in leaves if k[0] == t}) for t in types}
name_entries = {(t, n): sorted(k[2] for k in leaves if k[0] == t and k[1] == n) for t in types for n in type_entries[t]}
nleaves = len(leaves)
hdr = 16; ent = 8; dat = 16
tree_size = hdr + ent*ntypes
for t in types:
    tree_size += hdr + ent*len(type_entries[t])
    for n in type_entries[t]:
        tree_size += hdr + ent*len(name_entries[(t, n)])
data_off0 = tree_size
blob_off = data_off0 + dat*nleaves
# blob placement
blob_pos = {}
cur = blob_off
for k in sorted(leaves):
    pad_to = (cur + 7) & ~7
    cur = pad_to
    blob_pos[k] = cur
    cur += len(leaves[k])
total = cur
# write dirs
def dirhdr(n):
    return struct.pack('<IIHHHH', 0, 0, 0, 0, 0, n)  # chars,timestamp,major,minor,nnamed=0,nid=n
pos = 0
# compute sub-dir offsets
type_dir_off = {}
o = hdr + ent*ntypes
for t in types:
    type_dir_off[t] = o
    o += hdr + ent*len(type_entries[t])
name_dir_off = {}
for t in types:
    for n in type_entries[t]:
        name_dir_off[(t, n)] = o
        o += hdr + ent*len(name_entries[(t, n)])
assert o == data_off0
data_entry_off = {}
o = data_off0
for k in sorted(leaves):
    data_entry_off[k] = o
    o += dat
assert o == blob_off
buf = bytearray(total)
buf[0:16] = dirhdr(ntypes)
for i, t in enumerate(types):
    buf[16+i*8:16+i*8+8] = struct.pack('<II', t, 0x80000000 | type_dir_off[t])
for t in types:
    off = type_dir_off[t]
    buf[off:off+16] = dirhdr(len(type_entries[t]))
    for i, n in enumerate(type_entries[t]):
        buf[off+16+i*8:off+16+i*8+8] = struct.pack('<II', n, 0x80000000 | name_dir_off[(t, n)])
    for n in type_entries[t]:
        noff = name_dir_off[(t, n)]
        langs = name_entries[(t, n)]
        buf[noff:noff+16] = dirhdr(len(langs))
        for i, lg in enumerate(langs):
            k = (t, n, lg)
            buf[noff+16+i*8:noff+16+i*8+8] = struct.pack('<II', lg, data_entry_off[k] & 0x7FFFFFFF)
for k in sorted(leaves):
    o = data_entry_off[k]
    buf[o:o+16] = struct.pack('<IIII', rsrc[1]+blob_pos[k], len(leaves[k]), 0, 0)
    buf[blob_pos[k]:blob_pos[k]+len(leaves[k])] = leaves[k]
assert len(buf) == total

# ---- splice into file ----
new_vs = total
new_rs = (total + filealign - 1) // filealign * filealign
new_file = bytes(d[:base]) + bytes(buf) + b'\0'*(new_rs - total)
# patch section header
b = rsrc[5]
new_file_b = bytearray(new_file)
struct.pack_into('<I', new_file_b, b+8, new_vs)   # VirtualSize
struct.pack_into('<I', new_file_b, b+16, new_rs)  # SizeOfRawData
d2 = new_file_b
# optional header SizeOfImage
new_last_va_end = rsrc[1] + new_vs
new_sizeimage = (new_last_va_end + secalign - 1)//secalign*secalign
struct.pack_into('<I', d2, opt+56, new_sizeimage)
# data directory size
struct.pack_into('<I', d2, dd+2*8+4, new_vs)
open(EXE, 'wb').write(bytes(d2))
print(f"icon embedded: {len(icons)} images, rsrc {rsrc[2]}->{new_vs} bytes, file {len(d)}->{len(d2)}, SizeOfImage {sizeimage:#x}->{new_sizeimage:#x}")
