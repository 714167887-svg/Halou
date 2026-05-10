import ezdxf

doc = ezdxf.readfile(r'e:\halou wode\W\PB\11\111.dxf')
msp = doc.modelspace()

# Find all cyan closed rectangular LWPOLYLINE (color=4, closed, 4 points)
cyan_boxes = []
for e in msp:
    if e.dxftype() == 'LWPOLYLINE' and e.dxf.get('color', 256) == 4:
        flags = e.dxf.get('flags', 0)
        if flags & 1:  # closed
            pts = list(e.get_points(format='xy'))
            if len(pts) == 4:
                xs = [p[0] for p in pts]
                ys = [p[1] for p in pts]
                min_x, max_x = min(xs), max(xs)
                min_y, max_y = min(ys), max(ys)
                w = max_x - min_x
                h = max_y - min_y
                if w > 10 and h > 10:  # filter out tiny ones
                    cyan_boxes.append({
                        'handle': e.dxf.handle,
                        'min_x': min_x, 'max_x': max_x,
                        'min_y': min_y, 'max_y': max_y,
                        'w': w, 'h': h
                    })

print(f'=== Cyan rectangular boxes: {len(cyan_boxes)} ===')
for b in cyan_boxes:
    print(f"  handle={b['handle']} x=[{b['min_x']:.2f}, {b['max_x']:.2f}] y=[{b['min_y']:.2f}, {b['max_y']:.2f}] size={b['w']:.1f}x{b['h']:.1f}")

# Find magenta lines and match them to cyan boxes
magenta_lines = []
for e in msp:
    c = e.dxf.get('color', 256)
    if c == 6 and e.dxftype() == 'LINE':
        sx, sy = e.dxf.start.x, e.dxf.start.y
        ex, ey = e.dxf.end.x, e.dxf.end.y
        magenta_lines.append({'sx': sx, 'sy': sy, 'ex': ex, 'ey': ey, 'handle': e.dxf.handle})

print(f'\n=== Magenta lines: {len(magenta_lines)} ===')

# For each cyan box, find magenta lines inside it
for b in cyan_boxes:
    inside_mag = []
    for ml in magenta_lines:
        if (b['min_x'] <= ml['sx'] <= b['max_x'] and b['min_x'] <= ml['ex'] <= b['max_x'] and
            b['min_y'] <= ml['sy'] <= b['max_y'] and b['min_y'] <= ml['ey'] <= b['max_y']):
            inside_mag.append(ml)
    if inside_mag:
        print(f"\n  Box {b['handle']} ({b['w']:.0f}x{b['h']:.0f}):")
        for ml in inside_mag:
            print(f"    magenta line y={ml['sy']:.2f} x=[{ml['sx']:.2f}, {ml['ex']:.2f}]")
        # Find entities below the magenta line(s) within the box
        mag_y = min(ml['sy'] for ml in inside_mag)  # lowest magenta line
        print(f"    -> Extract zone: y < {mag_y:.2f} (below magenta)")
        
        # Count entities in the extract zone
        count = 0
        for e2 in msp:
            if e2.dxf.get('color', 256) == 4:
                continue  # skip cyan box itself
            if e2.dxf.get('color', 256) == 6:
                continue  # skip magenta lines
            # Check if entity is inside the box and below magenta
            try:
                if e2.dxftype() == 'LINE':
                    sy2 = min(e2.dxf.start.y, e2.dxf.end.y)
                    sx2 = min(e2.dxf.start.x, e2.dxf.end.x)
                    ex2 = max(e2.dxf.start.x, e2.dxf.end.x)
                    ey2 = max(e2.dxf.start.y, e2.dxf.end.y)
                    if (b['min_x'] <= sx2 and ex2 <= b['max_x'] and
                        b['min_y'] <= sy2 and ey2 <= mag_y + 1):
                        count += 1
                elif e2.dxftype() == 'LWPOLYLINE':
                    pts = list(e2.get_points(format='xy'))
                    xs = [p[0] for p in pts]
                    ys = [p[1] for p in pts]
                    if (b['min_x'] <= min(xs) and max(xs) <= b['max_x'] and
                        b['min_y'] <= min(ys) and max(ys) <= mag_y + 1):
                        count += 1
                elif e2.dxftype() in ('MTEXT', 'TEXT'):
                    ix = e2.dxf.insert.x
                    iy = e2.dxf.insert.y
                    if (b['min_x'] <= ix <= b['max_x'] and b['min_y'] <= iy <= mag_y + 1):
                        count += 1
            except:
                pass
        print(f"    -> Entities below magenta: {count}")
