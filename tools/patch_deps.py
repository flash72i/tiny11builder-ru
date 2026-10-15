import json, sys
path=sys.argv[1]
d=json.load(open(path, encoding="utf-8-sig"))
tk=[k for k in d["targets"] if "/" in k][0]   # RID-specific target
t=d["targets"][tk]
t["Tiny11Builder.Ru/0.1.1"]={"runtime":{"Tiny11Builder.Ru.dll":{}}}
t["Tiny11Builder/0.1.1"].setdefault("dependencies",{})["Tiny11Builder.Ru"]="0.1.1"
d["libraries"]["Tiny11Builder.Ru/0.1.1"]={"type":"project","serviceable":False,"sha512":""}
json.dump(d, open(path,"w",encoding="utf-8"), indent=2)
print("deps patched:", path, "| target:", tk)
