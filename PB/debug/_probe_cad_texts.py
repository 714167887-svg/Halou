import os
import shutil
import ezdxf
import pb_extract

base = r"e:\halou wode\W\PB\11"
files = sorted([f for f in os.listdir(base) if f.lower().endswith('.dwg')])
print("FILES", files)

for name in files:
    p = os.path.join(base, name)
    print(f"\n=== {name} ===")
    dxf_path, tmp_dir = pb_extract.convert_dwg_to_dxf(p)
    try:
        doc = ezdxf.readfile(dxf_path)
        msp = doc.modelspace()
        texts = []
        for e in msp:
            if e.dxftype() == "TEXT":
                s = (e.dxf.text or "").strip()
                if s:
                    texts.append(s)
            elif e.dxftype() == "MTEXT":
                s = (e.plain_text() or "").strip()
                if s:
                    texts.append(s)

        uniq = []
        seen = set()
        for s in texts:
            if s not in seen:
                seen.add(s)
                uniq.append(s)

        print("TEXT_COUNT", len(texts), "UNIQ", len(uniq))
        for s in uniq[:80]:
            print("  ", s)
    finally:
        if tmp_dir and os.path.exists(tmp_dir):
            shutil.rmtree(tmp_dir, ignore_errors=True)
