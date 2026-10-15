import struct, re, sys

def read_us(path):
    data=open(path,'rb').read()
    sig=data.find(b'BSJB')
    ver_len=struct.unpack_from('<I',data,sig+12)[0]
    pos=sig+16+ver_len
    pos=(pos+3)&~3
    flags,nstreams=struct.unpack_from('<HH',data,pos); pos+=4
    us=None
    for i in range(nstreams):
        offset,size=struct.unpack_from('<II',data,pos); pos+=8
        nstart=pos; nend=data.index(b'\0',pos)
        name=data[nstart:nend].decode()
        pos=(nend+1+3)&~3
        if name=='#US': us=data[sig+offset: sig+offset+size]
    i=1; res=[]
    while i<len(us):
        b0=us[i]
        if b0==0: i+=1; continue
        if b0<0x80: ln=b0; i+=1
        elif b0<0xC0: ln=((b0&0x3F)<<8)|us[i+1]; i+=2
        else: ln=((b0&0x1F)<<24)|(us[i+1]<<16)|(us[i+2]<<8)|us[i+3]; i+=4
        if i+ln>len(us): break
        blob=us[i:i+ln]; i+=ln
        try: res.append(blob[:-1].decode('utf-16-le'))
        except: pass
    return res

def xbf_strings(pri_path, marker=b"B\x00U\x00I\x00L\x00D\x00 \x00S\x00T\x00E\x00P\x00S"):
    data=open(pri_path,'rb').read()
    locs=[m.start() for m in re.finditer(b'XBF\x00', data)]
    for off in locs:
        a,b=struct.unpack_from('<II', data, off+4)
        blob=data[off+12:off+12+a+b]
        if marker in blob:
            seqs={}
            for align in (0,1):
                bb=blob[align:]; i=0; cur=[]
                while i+1<len(bb):
                    c=struct.unpack_from('<H',bb,i)[0]
                    if 0x20<=c<0xD7FF: cur.append(chr(c)); i+=2
                    else:
                        if len(cur)>=2: seqs[''.join(cur)]=1
                        cur=[]; i+=2
                if len(cur)>=2: seqs[''.join(cur)]=1
            return list(seqs.keys()), blob
    return [], b""

if __name__=="__main__":
    base=sys.argv[1] if len(sys.argv)>1 else "x64/Tiny11Builder-0.1.1-win-x64"
    import json
    app=read_us(base+"/Tiny11Builder.dll")
    core=read_us(base+"/Tiny11Builder.Core.dll")
    xb,blob=xbf_strings(base+"/Tiny11Builder.pri")
    json.dump({"app":app,"core":core,"xbf":xb}, open("dump_"+base.split('/')[0]+".json","w",encoding="utf-8"), ensure_ascii=False)
    open("mainwindow.xbf","wb").write(blob)
    print("app:",len(app),"core:",len(core),"xbf-seqs:",len(xb),"xbf-blob:",len(blob))
