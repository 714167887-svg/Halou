"""
PB Extract: 扫描 11/ 目录下所有 DWG/DXF 文件，提取青色框内洋红线以下的钣金激光图。
DWG 文件会自动通过 AutoCAD accoreconsole 转换。
"""

import os
import sys
import math
import uuid
import shutil
import tempfile
import subprocess

import ezdxf


def find_cyan_boxes_with_magenta(msp):
    """
    严格匹配：
    1. 找所有青色闭合4点矩形框
    2. 区分大框和小框（小框完全在大框内部）
    3. 大框内必须有对应的小框和洋红线段
    4. 提取区域 = 大框范围内、洋红线以下
    """
    cyan_boxes = []
    for e in msp:
        if e.dxftype() == "LWPOLYLINE" and e.dxf.get("color", 256) == 4:
            flags = e.dxf.get("flags", 0)
            if flags & 1:
                pts = list(e.get_points(format="xy"))
                if len(pts) == 4:
                    xs = [p[0] for p in pts]
                    ys = [p[1] for p in pts]
                    min_x, max_x = min(xs), max(xs)
                    min_y, max_y = min(ys), max(ys)
                    w = max_x - min_x
                    h = max_y - min_y
                    if w < 50 or h < 50:
                        continue
                    rect_tol = max(w, h) * 0.01
                    is_rect = all(
                        (abs(px - min_x) < rect_tol or abs(px - max_x) < rect_tol)
                        and (abs(py - min_y) < rect_tol or abs(py - max_y) < rect_tol)
                        for px, py in pts
                    )
                    if is_rect:
                        cyan_boxes.append(
                            {
                                "entity": e,
                                "min_x": min_x,
                                "max_x": max_x,
                                "min_y": min_y,
                                "max_y": max_y,
                                "w": w,
                                "h": h,
                            }
                        )

    magenta_lines = []
    for e in msp:
        c = e.dxf.get("color", 256)
        if c == 6 and e.dxftype() == "LINE":
            magenta_lines.append(e)

    zones = []
    tol = 2.0
    for big in cyan_boxes:
        inner_smalls = []
        for small in cyan_boxes:
            if small is big:
                continue
            if (
                big["min_x"] + tol <= small["min_x"]
                and small["max_x"] <= big["max_x"] - tol
                and big["min_y"] + tol <= small["min_y"]
                and small["max_y"] <= big["max_y"] - tol
            ):
                inner_smalls.append(small)

        if not inner_smalls:
            continue

        inside_mag = []
        for ml in magenta_lines:
            sx, sy = ml.dxf.start.x, ml.dxf.start.y
            ex, ey = ml.dxf.end.x, ml.dxf.end.y
            if (
                big["min_x"] - tol <= sx <= big["max_x"] + tol
                and big["min_x"] - tol <= ex <= big["max_x"] + tol
                and big["min_y"] - tol <= sy <= big["max_y"] + tol
                and big["min_y"] - tol <= ey <= big["max_y"] + tol
            ):
                inside_mag.append(ml)

        if not inside_mag:
            continue

        mag_y = inside_mag[0].dxf.start.y

        small_above = False
        for small in inner_smalls:
            if small["min_y"] >= mag_y - tol:
                small_above = True
                break
        if not small_above:
            continue

        zones.append(
            {
                "box": big,
                "mag_y": mag_y,
                "extract_min_x": big["min_x"],
                "extract_max_x": big["max_x"],
                "extract_min_y": big["min_y"],
                "extract_max_y": mag_y,
            }
        )
        print(
            f"  Matched: big box {big['entity'].dxf.handle} ({big['w']:.0f}x{big['h']:.0f}), "
            f"small box(es)={len(inner_smalls)}, magenta lines={len(inside_mag)}, extract y < {mag_y:.2f}"
        )

    return zones


def entity_in_zone(entity, zone, tol=2.0):
    """判断实体是否在提取区域内（青色框内、洋红线以下）"""
    zx1 = zone["extract_min_x"] - tol
    zx2 = zone["extract_max_x"] + tol
    zy1 = zone["extract_min_y"] - tol
    zy2 = zone["extract_max_y"] + tol

    c = entity.dxf.get("color", 256)
    if c == 4 or c == 6:
        return False

    etype = entity.dxftype()
    if etype in (
        "DIMENSION",
        "MTEXT",
        "TEXT",
        "LEADER",
        "MULTILEADER",
        "TOLERANCE",
        "ARCALIGNEDTEXT",
    ):
        return False

    try:
        if etype == "LINE":
            sx, sy = entity.dxf.start.x, entity.dxf.start.y
            ex, ey = entity.dxf.end.x, entity.dxf.end.y
            return zx1 <= sx <= zx2 and zx1 <= ex <= zx2 and zy1 <= sy <= zy2 and zy1 <= ey <= zy2

        if etype == "LWPOLYLINE":
            pts = list(entity.get_points(format="xy"))
            if not pts:
                return False
            xs = [p[0] for p in pts]
            ys = [p[1] for p in pts]
            return zx1 <= min(xs) and max(xs) <= zx2 and zy1 <= min(ys) and max(ys) <= zy2

        if etype in ("CIRCLE", "ARC"):
            cx, cy = entity.dxf.center.x, entity.dxf.center.y
            r = entity.dxf.radius
            return zx1 <= cx - r and cx + r <= zx2 and zy1 <= cy - r and cy + r <= zy2

        if etype == "INSERT":
            ix = entity.dxf.insert.x
            iy = entity.dxf.insert.y
            return zx1 <= ix <= zx2 and zy1 <= iy <= zy2

        if etype == "HATCH":
            for path in entity.paths:
                if hasattr(path, "vertices"):
                    xs = [v[0] for v in path.vertices]
                    ys = [v[1] for v in path.vertices]
                    if xs and ys:
                        return zx1 <= min(xs) and max(xs) <= zx2 and zy1 <= min(ys) and max(ys) <= zy2
            return False

    except Exception:
        return False

    return False


def decode_accore_output(raw):
    if raw is None:
        return ""
    if isinstance(raw, bytes):
        for enc in ("utf-8", "gbk", "cp936", "latin1"):
            try:
                return raw.decode(enc, errors="replace")
            except Exception:
                continue
        return raw.decode(errors="replace")
    return str(raw)


def find_accoreconsole():
    env = os.environ.get("ACCORECONSOLE_PATH")
    if env and os.path.exists(env):
        return env

    candidates = [
        r"C:\Program Files\Autodesk\AutoCAD 2024\accoreconsole.exe",
        r"C:\Program Files\Autodesk\AutoCAD 2023\accoreconsole.exe",
        r"C:\Program Files\Autodesk\AutoCAD 2022\accoreconsole.exe",
        r"C:\Program Files\Autodesk\AutoCAD 2021\accoreconsole.exe",
        r"C:\Program Files\Autodesk\AutoCAD 2020\accoreconsole.exe",
        r"C:\Program Files\Autodesk\AutoCAD 2019\accoreconsole.exe",
        r"C:\Program Files\Autodesk\AutoCAD 2018\accoreconsole.exe",
    ]
    for p in candidates:
        if os.path.exists(p):
            return p

    raise FileNotFoundError("未找到 accoreconsole.exe，请设置环境变量 ACCORECONSOLE_PATH")


def convert_dwg_to_dxf(dwg_path):
    if not os.path.exists(dwg_path):
        raise FileNotFoundError(f"输入文件不存在: {dwg_path}")

    accore = find_accoreconsole()
    tmp_dir = tempfile.mkdtemp(prefix="pb_extract_")

    try:
        src_copy = os.path.join(tmp_dir, "input.dwg")
        shutil.copy2(dwg_path, src_copy)

        dest_path = os.path.join(tmp_dir, "output.dxf")
        isolate_id = str(uuid.uuid4())
        user_data_dir = os.path.join(tmp_dir, "accore_userdata")
        os.makedirs(user_data_dir, exist_ok=True)

        scr_path = os.path.join(tmp_dir, "convert.scr")
        with open(scr_path, "w", encoding="ascii", newline="\n") as f:
            f.write(f'_.DXFOUT\n"{dest_path}"\n16\n')

        print(f"  Converting {os.path.basename(dwg_path)} -> DXF ...")
        cmd = [
            accore,
            "/i",
            src_copy,
            "/s",
            scr_path,
            "/l",
            "en-US",
            "/isolate",
            isolate_id,
            user_data_dir,
            "/readonly",
        ]

        try:
            result = subprocess.run(cmd, capture_output=True, timeout=300)
        except subprocess.TimeoutExpired as ex:
            if os.path.exists(dest_path):
                print("  Conversion timed out after DXF was created; using generated DXF.")
                return dest_path, tmp_dir

            stdout = decode_accore_output(ex.stdout)
            stderr = decode_accore_output(ex.stderr)
            if stdout:
                print(f"  accoreconsole stdout: {stdout[-1000:]}")
            if stderr:
                print(f"  accoreconsole stderr: {stderr[-1000:]}")
            raise TimeoutError(f"转换超时：{os.path.basename(dwg_path)}")

        if not os.path.exists(dest_path):
            stdout = decode_accore_output(result.stdout)
            stderr = decode_accore_output(result.stderr)
            if stdout:
                print(f"  accoreconsole stdout: {stdout[-1000:]}")
            if stderr:
                print(f"  accoreconsole stderr: {stderr[-1000:]}")
            raise FileNotFoundError(f"转换失败：{dest_path} 不存在")

        print(f"  Conversion OK: {dest_path}")
        return dest_path, tmp_dir

    except:
        shutil.rmtree(tmp_dir, ignore_errors=True)
        raise


def _entity_bbox(entity):
    try:
        etype = entity.dxftype()
        if etype == "LINE":
            x1, y1 = entity.dxf.start.x, entity.dxf.start.y
            x2, y2 = entity.dxf.end.x, entity.dxf.end.y
            return min(x1, x2), min(y1, y2), max(x1, x2), max(y1, y2)

        if etype == "LWPOLYLINE":
            pts = list(entity.get_points(format="xy"))
            if not pts:
                return None
            xs = [p[0] for p in pts]
            ys = [p[1] for p in pts]
            return min(xs), min(ys), max(xs), max(ys)

        if etype in ("CIRCLE", "ARC"):
            cx, cy = entity.dxf.center.x, entity.dxf.center.y
            r = entity.dxf.radius
            return cx - r, cy - r, cx + r, cy + r

        if etype == "INSERT":
            ix, iy = entity.dxf.insert.x, entity.dxf.insert.y
            return ix, iy, ix, iy
    except Exception:
        return None
    return None


def append_extractions_to_doc(input_path, output_doc, current_x, spacing=50):
    """读取单个 DXF，将提取出的激光图追加到汇总 DXF 中。"""
    print(f"Reading: {input_path}")
    doc = ezdxf.readfile(input_path)
    msp = doc.modelspace()

    zones = find_cyan_boxes_with_magenta(msp)
    print(f"Found {len(zones)} extraction zones")
    if not zones:
        print("No zones found in this file.")
        return current_x, 0, 0

    from ezdxf.addons import Importer

    importer = Importer(doc, output_doc)
    importer.import_tables()
    new_msp = output_doc.modelspace()

    zone_count = 0
    total_extracted = 0

    for zone in zones:
        entities = [e for e in msp if entity_in_zone(e, zone)]
        if not entities:
            continue

        bbs = [_entity_bbox(e) for e in entities]
        bbs = [b for b in bbs if b is not None]
        if not bbs:
            continue

        min_x = min(b[0] for b in bbs)
        min_y = min(b[1] for b in bbs)
        max_y = max(b[3] for b in bbs)
        max_x = max(b[2] for b in bbs)
        w = max_x - min_x
        h = max_y - min_y
        is_landscape = w > h

        from ezdxf.math import Matrix44

        for src in entities:
            try:
                copied = src.copy()
                if is_landscape:
                    # 横料打竖：先移到原点，再旋转 90 度，再平移到当前排版位置
                    copied.transform(Matrix44.translate(-min_x, -min_y, 0))
                    copied.transform(Matrix44.z_rotate(math.pi / 2))
                    copied.transform(Matrix44.translate(h, 0, 0))
                    copied.transform(Matrix44.translate(current_x, 0, 0))
                else:
                    copied.transform(Matrix44.translate(current_x - min_x, -min_y, 0))
                new_msp.add_entity(copied)
            except Exception:
                continue

        zone_count += 1
        total_extracted += len(entities)
        zone_w = h if is_landscape else w
        current_x += zone_w + spacing

    importer.finalize()
    return current_x, zone_count, total_extracted


def main():
    base_dir = os.path.dirname(os.path.abspath(__file__))
    input_dir = os.path.join(base_dir, "11")
    output_path = os.path.join(base_dir, "PB_merged.dxf")

    cad_files = sorted(
        f for f in os.listdir(input_dir)
        if f.lower().endswith((".dwg", ".dxf"))
    )
    if not cad_files:
        print(f"在 {input_dir} 中未找到 DWG/DXF 文件")
        sys.exit(1)

    print(f"找到 {len(cad_files)} 个 CAD 文件：{cad_files}")

    merged_doc = ezdxf.new(dxfversion="R2010")
    current_x = 0
    total_files = 0
    total_zones = 0
    total_entities = 0

    for cad_name in cad_files:
        input_path = os.path.join(input_dir, cad_name)
        print(f"\n{'=' * 60}")
        tmp_dir = None
        try:
            if cad_name.lower().endswith(".dwg"):
                dxf_path, tmp_dir = convert_dwg_to_dxf(input_path)
            else:
                dxf_path = input_path

            current_x, zone_count, entity_count = append_extractions_to_doc(
                dxf_path, merged_doc, current_x
            )
            if zone_count:
                total_files += 1
                total_zones += zone_count
                current_x += 300
            total_entities += entity_count
        finally:
            if tmp_dir and os.path.exists(tmp_dir):
                shutil.rmtree(tmp_dir, ignore_errors=True)

    if total_entities == 0:
        print("\nNo entities extracted from any input file. Exiting without saving.")
        sys.exit(1)

    print(f"\nMerged {total_zones} zone(s) from {total_files} file(s)")
    print(f"Total extracted: {total_entities} entities")
    print(f"Saving merged DXF: {output_path}")
    merged_doc.saveas(output_path)
    print("Done!")


if __name__ == "__main__":
    main()
