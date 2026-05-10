import ezdxf
from collections import Counter

doc = ezdxf.readfile(r'e:\halou wode\W\PB\11\111.dxf')
msp = doc.modelspace()

type_count = Counter()
color_count = Counter()
cyan_entities = []
magenta_entities = []

for e in msp:
    type_count[e.dxftype()] += 1
    c = e.dxf.get('color', 256)
    color_count[c] += 1
    if c == 4:
        cyan_entities.append(e)
    if c == 6:
        magenta_entities.append(e)

print('=== Entity types ===')
for t, n in type_count.most_common():
    print(f'  {t}: {n}')

print(f'\n=== Colors ===')
for c, n in color_count.most_common(10):
    print(f'  color {c}: {n}')

print(f'\n=== Cyan (4) entities: {len(cyan_entities)} ===')
for e in cyan_entities[:20]:
    info = f'  {e.dxftype()} handle={e.dxf.handle}'
    if e.dxftype() == 'LWPOLYLINE':
        pts = list(e.get_points(format='xy'))
        closed = e.dxf.get('flags', 0) & 1
        info += f' closed={closed} pts={len(pts)}'
        if len(pts) <= 6:
            info += f' coords={[(round(x,2),round(y,2)) for x,y in pts]}'
    print(info)

print(f'\n=== Magenta (6) entities: {len(magenta_entities)} ===')
for e in magenta_entities[:20]:
    info = f'  {e.dxftype()} handle={e.dxf.handle}'
    if e.dxftype() == 'LINE':
        info += f' start=({e.dxf.start.x:.2f},{e.dxf.start.y:.2f}) end=({e.dxf.end.x:.2f},{e.dxf.end.y:.2f})'
    elif e.dxftype() == 'LWPOLYLINE':
        pts = list(e.get_points(format='xy'))
        info += f' pts={len(pts)}'
    print(info)
