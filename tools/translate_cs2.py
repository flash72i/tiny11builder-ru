# -*- coding: utf-8 -*-
"""Full C# string-literal translator: handles regular, verbatim, interpolated (nested) literals.
Translates literal CHUNKS exactly like the IL-level ldstr patching does for interpolated strings."""
import json, sys

MAP = json.load(open(sys.argv[3], encoding="utf-8"))

def decode(cs):
    out=[]; i=0
    while i < len(cs):
        c=cs[i]
        if c=='\\' and i+1<len(cs):
            n=cs[i+1]
            simple={'n':'\n','r':'\r','t':'\t','0':'\0','\\':'\\','"':'"',"'":"'","a":"\a","b":"\b","f":"\f","v":"\v"}
            if n in simple: out.append(simple[n]); i+=2
            elif n=='u' and i+6<=len(cs): out.append(chr(int(cs[i+2:i+6],16))); i+=6
            else: out.append(n); i+=2
        else: out.append(c); i+=1
    return ''.join(out)

def encode(s):
    out=[]
    for ch in s:
        if ch=='\\': out.append('\\\\')
        elif ch=='"': out.append('\\"')
        elif ch=='\n': out.append('\\n')
        elif ch=='\r': out.append('\\r')
        elif ch=='\t': out.append('\\t')
        else: out.append(ch)
    return ''.join(out)

stats=[0]
def tr_text(chunk):
    """Translate one literal chunk (raw source text between holes, escapes intact)."""
    if not chunk: return chunk
    try: dec=decode(chunk)
    except Exception: return chunk
    if dec in MAP:
        stats[0]+=1
        return encode(MAP[dec])
    return chunk

def scan(src):
    out=[]; i=0; n=len(src)
    in_line_comment=in_block_comment=False
    while i<n:
        c=src[i]
        # comments
        if not in_line_comment and not in_block_comment and c=='/' and i+1<n:
            if src[i+1]=='/': in_line_comment=True; out.append(c); i+=1; continue
            if src[i+1]=='*': in_block_comment=True; out.append(c); i+=1; continue
        if in_line_comment:
            out.append(c); i+=1
            if c=='\n': in_line_comment=False
            continue
        if in_block_comment:
            out.append(c); i+=1
            if c=='*' and i+1<n and src[i+1]=='/': out.append('/'); i+=2; in_block_comment=False
            continue
        # char literal
        if c=="'":
            j=i+1
            while j<n:
                if src[j]=='\\': j+=2; continue
                if src[j]=="'": j+=1; break
                j+=1
            out.append(src[i:j]); i=j; continue
        # string literal (with optional $ / @ prefixes)
        pre=''
        j=i
        while j<n and src[j] in '$@':
            pre+=src[j]; j+=1
        if j<n and src[j]=='"' and (pre in ('$','@','$@','@$','$$','')):
            interp = '$' in pre
            verb = '@' in pre
            # scan literal
            k=j+1; buf=[]; holes=[]
            while k<n:
                ch=src[k]
                if interp and ch=='{':
                    if k+1<n and src[k+1]=='{': buf.append('{{'); k+=2; continue
                    # find matching close with nesting and strings
                    depth=0; m=k
                    while m<n:
                        cm=src[m]
                        if cm=='"':
                            m+=1
                            while m<n and src[m]!='"':
                                if src[m]=='\\' and '@' not in pre: m+=1
                                m+=1
                        elif cm=='{': depth+=1
                        elif cm=='}':
                            depth-=1
                            if depth==0: break
                        m+=1
                    holes.append((len(''.join(buf)), src[k:m+1]))
                    buf.append('\x00HOLE%d\x00'%(len(holes)-1))
                    k=m+1; continue
                if interp and ch=='}':
                    if k+1<n and src[k+1]=='}': buf.append('}}'); k+=2; continue
                if ch=='\\' and not verb:
                    buf.append(src[k:k+2]); k+=2; continue
                if ch=='"':
                    if verb and k+1<n and src[k+1]=='"': buf.append('""'); k+=2; continue
                    k+=1; break
                buf.append(ch); k+=1
            body=''.join(buf)
            # translate literal segments between hole markers
            import re as _re
            parts=_re.split(r'(\x00HOLE\d+\x00)', body)
            newparts=[]
            for p in parts:
                if p.startswith('\x00HOLE'): newparts.append(p)
                else: newparts.append(tr_text(p) if not verb else tr_verbatim(p))
            newbody=''.join(newparts)
            for idx,(pos,htext) in enumerate(holes):
                newbody=newbody.replace('\x00HOLE%d\x00'%idx, htext)
            out.append(pre+'"'+newbody+'"')
            i=k; continue
        out.append(c); i+=1
    return ''.join(out)

def tr_verbatim(chunk):
    # chunk between doubled quotes already normalized; decode "" -> " for matching
    raw=chunk.replace('""','"')
    if raw in MAP:
        stats[0]+=1
        return MAP[raw].replace('"','""')
    return chunk

src=open(sys.argv[1],encoding="utf-8").read()
res=scan(src)
open(sys.argv[2],"w",encoding="utf-8").write(res)
print(f"{sys.argv[1]}: {stats[0]} chunks translated")
