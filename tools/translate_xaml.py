import json, re, sys, html
m=json.load(open(sys.argv[3],encoding="utf-8"))
attrs=["Text","Content","Header","PlaceholderText","Title","Message","OnContent","OffContent",
       "AutomationProperties.Name","AutomationProperties.HelpText","AutomationProperties.FullDescription"]
src=open(sys.argv[1],encoding="utf-8").read()
cnt=[0]
def rep(mm):
    val=html.unescape(mm.group(2))
    if val in m:
        cnt[0]+=1
        ru=m[val].replace("&","&amp;").replace("<","&lt;").replace(">","&gt;")
        return mm.group(1)+'="'+ru+'"'
    return mm.group(0)
pat=re.compile('((?:^|[\s])(?:'+"|".join(attrs)+'))="([^"]*)"')
out=pat.sub(rep, src)
open(sys.argv[2],"w",encoding="utf-8").write(out)
print(f"{sys.argv[1]}: {cnt[0]} attrs translated")
