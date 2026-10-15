import zipfile, hashlib, os, sys
arch=sys.argv[1]           # x64 | arm64
orig=f"dl/Tiny11Builder-0.1.1-win-{arch}.zip"
srcdir=f"{arch}/Tiny11Builder-0.1.1-win-{arch}"
outname=f"Tiny11Builder-0.1.1-ru-win-{arch}.zip"
outpath=sys.argv[2] if len(sys.argv)>2 else outname
zo=zipfile.ZipFile(orig)
names=[i.filename for i in zo.infolist()]
changed=[]; added=[]
with zipfile.ZipFile(outpath,"w",zipfile.ZIP_DEFLATED,compresslevel=6) as zn:
    for n in names:
        if n.endswith("/"): 
            continue
        local=os.path.join(srcdir, n.split("/",1)[1])
        data=open(local,"rb").read()
        origdata=zo.read(n)
        if data!=origdata: changed.append(n.split("/",1)[1])
        zn.writestr(n, data)
    top=names[0].split("/")[0]
    have=set(names)
    for root,_,files in os.walk(srcdir):
        for fn in sorted(files):
            full=os.path.join(root,fn)
            rel=top+"/"+os.path.relpath(full,srcdir).replace(os.sep,"/")
            if rel in have: continue
            data=open(full,"rb").read()
            zn.writestr(rel,data); added.append(rel.split("/",1)[1])
h=hashlib.sha256(open(outpath,"rb").read()).hexdigest().upper()
open(outpath+".sha256","w").write(h+"  "+outname+"\n")
print(f"packed {outpath} size={os.path.getsize(outpath)} sha256={h}")
print("changed:", changed)
print("added:", added)
