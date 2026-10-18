import json
import os

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TPL = os.path.join(ROOT, "tools", "menu-v2.template.html")
DATA = os.path.join(ROOT, "data", "aquamai-ui.json")
ZONES = os.path.join(ROOT, "data", "traced.json")
OUT = os.path.join(ROOT, "src", "MaimaiLauncher", "web", "menu-v2.html")

tpl = open(TPL, encoding="utf-8").read()
data = json.load(open(DATA, encoding="utf-8"))
zones = json.load(open(ZONES, encoding="utf-8"))

payload = json.dumps(data, ensure_ascii=False, separators=(",", ":"))
zpayload = json.dumps(zones, ensure_ascii=False, separators=(",", ":"))

t1 = "/*__AQUAMAI_DATA__*/{groups:[]}"
t2 = "/*__ZONES_DATA__*/{}"
if t1 not in tpl or t2 not in tpl:
    raise SystemExit("token not found")

out = tpl.replace(t1, "/*__AQUAMAI_DATA__*/" + payload).replace(t2, "/*__ZONES_DATA__*/" + zpayload)
os.makedirs(os.path.dirname(OUT), exist_ok=True)
open(OUT, "w", encoding="utf-8", newline="\n").write(out)
print("wrote", OUT, len(out), "chars")
