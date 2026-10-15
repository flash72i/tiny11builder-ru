import struct, sys
def strip(path):
    d=bytearray(open(path,'rb').read())
    e_lfanew=struct.unpack_from('<I',d,0x3C)[0]
    assert d[e_lfanew:e_lfanew+4]==b'PE\0\0', "not PE"
    magic=struct.unpack_from('<H',d,e_lfanew+24)[0]
    dd_off=e_lfanew+24+(96 if magic==0x10b else 112)
    rva,size=struct.unpack_from('<II',d,dd_off+2*8)
    print(path, "magic=%x"%magic, "rsrc rva=%x size=%x"%(rva,size))
    if size:
        struct.pack_into('<II',d,dd_off+2*8,0,0)
        open(path,'wb').write(d)
        print("  stripped")
for p in sys.argv[1:]: strip(p)
