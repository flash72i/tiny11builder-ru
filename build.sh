#!/usr/bin/env bash
# Reproducible build of the Russian edition from the OFFICIAL upstream release.
# Prereqs: .NET SDK 8 (any OS, EnableWindowsTargeting), python3, network.
# Upstream binaries are NOT redistributed here: they are downloaded from the
# official GitHub release and transformed locally (see docs/REPORT_RU.md).
set -euo pipefail
cd "$(dirname "$0")"
V=0.1.1
mkdir -p out dl
for A in x64 arm64; do
  Z="dl/Tiny11Builder-$V-win-$A.zip"
  [ -f "$Z" ] || curl -fL -o "$Z" "https://github.com/ntdevlabs/tiny11builder/releases/download/winui-$V/Tiny11Builder-$V-win-$A.zip"
  rm -rf "out/$A"; mkdir -p "out/$A"
  python3 -c "import zipfile,sys; zipfile.ZipFile('$Z').extractall('out/$A')"
  D="out/$A/Tiny11Builder-$V-win-$A"
  # 1) localization runtime layer
  dotnet build overlay/Tiny11Builder.Ru.csproj -c Release -o out/ru
  cp out/ru/Tiny11Builder.Ru.dll "$D/"
  # 2) IL weaving (strings, hook, MapStage preludes, OEM encoding, bind-check)
  dotnet build tools/Weaver/Weaver.csproj -c Release -o out/weaver
  python3 tools/strip_rsrc.py "$D/Tiny11Builder.dll" "$D/Tiny11Builder.Core.dll"
  dotnet out/weaver/Weaver.dll "$D" dict/map_app.json dict/map_core.json "$D/Tiny11Builder.Ru.dll" dict/stage_pairs.json
  python3 tools/patch_deps.py "$D/Tiny11Builder.deps.json"
  # 3) tricolor icon into the apphost PE resources (x64 only, as shipped)
  [ "$A" = x64 ] && python3 tools/peicon.py "$D/Tiny11Builder.exe" icon/Tiny11Builder-RU.ico
  cp docs/README_RU.txt "$D/"
  # 4) pack + checksum
  python3 - "$A" "$D" <<'PYEOF'
import zipfile, hashlib, os, sys
arch, srcdir = sys.argv[1], sys.argv[2]
orig = f"dl/Tiny11Builder-0.1.1-win-{arch}.zip"
out = f"out/Tiny11Builder-0.1.1-ru-win-{arch}.zip"
zo = zipfile.ZipFile(orig)
with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as zn:
    have = set()
    for i in zo.infolist():
        if i.filename.endswith("/"): continue
        have.add(i.filename)
        zn.writestr(i.filename, open(os.path.join(srcdir, i.filename.split("/",1)[1]), "rb").read())
    top = next(iter(have)).split("/")[0]
    for root, _, files in os.walk(srcdir):
        for fn in sorted(files):
            rel = top + "/" + os.path.relpath(os.path.join(root, fn), srcdir).replace(os.sep, "/")
            if rel not in have: zn.writestr(rel, open(os.path.join(root, fn), "rb").read())
h = hashlib.sha256(open(out, "rb").read()).hexdigest().upper()
open(out + ".sha256", "w").write(h + "  " + os.path.basename(out) + "\n")
print(out, h)
PYEOF
done
echo "DONE: out/Tiny11Builder-$V-ru-win-*.zip"
