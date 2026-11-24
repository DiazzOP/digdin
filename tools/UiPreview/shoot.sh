#!/bin/bash
# usage: shoot.sh <tag> <WxH> <dpi>   — runs the preview under Wine and saves screenshots to /root/shots/<tag>-*.png
TAG=$1; GEOM=$2; DPI=${3:-96}
export WINEPREFIX=/root/.wine WINEDEBUG=-all XDG_RUNTIME_DIR=/run/user/0 WINEDLLOVERRIDES="mshtml="
OUT=/root/shots/$TAG; rm -rf $OUT; mkdir -p $OUT
LP=$(printf '%08x' $DPI)
cat > /root/dpi.reg <<R
REGEDIT4

[HKEY_CURRENT_USER\\Control Panel\\Desktop]
"LogPixels"=dword:$LP

[HKEY_CURRENT_USER\\Software\\Wine\\Fonts]
"LogPixels"=dword:$LP
R
xvfb-run -n 78 -f /root/xauth78 wine regedit /S 'Z:\root\dpi.reg' >/dev/null 2>&1
xvfb-run -n 77 -f /root/xauth77 -s "-screen 0 ${GEOM}x24" wine /root/rbxprev/RbxPreview.exe "Z:\\root\\shots\\$TAG" $TAG > $OUT/run.log 2>&1 &
sleep 3
for i in $(seq 1 600); do
  for r in $OUT/ready-*; do
    [ -e "$r" ] || continue
    n=${r##*/ready-}
    [ -e "$OUT/done-$n" ] && continue
    sleep 0.4
    XAUTHORITY=/root/xauth77 import -display :77 -window root "$OUT/$n.png" 2>/dev/null
    touch "$OUT/done-$n"
  done
  pgrep -f RbxPreview.exe >/dev/null || break
  sleep 0.5
done
ls $OUT/*.png | wc -l
