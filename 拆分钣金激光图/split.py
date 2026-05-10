"""
钣金激光图拆分工具
==================
将本文件夹内的 .dxf 文件按青色矩形框拆分为独立 DXF。
每个源 DXF 的拆分结果放在输出目录下以源文件名命名的子文件夹中。

用法:
  1. 将待拆分的 .dxf 文件放到本脚本所在文件夹
  2. 修改下方 OUTPUT_DIR 为你想要的输出路径（或通过命令行 --output-dir 指定）
  3. 双击 拆分.bat 运行，或执行: python split.py

依赖: pip install ezdxf
前置: 需要安装 AutoCAD（用到 accoreconsole.exe 做原生导出）
"""
from __future__ import annotations

import os
import re
import shutil
import subprocess
import sys
import tempfile
import uuid
from pathlib import Path

import tkinter as tk
from tkinter import messagebox

import ezdxf
from ezdxf.lldxf.tagwriter import TagCollector

# ============================================================
# ★ 输出目录 — 修改这里即可更改默认输出位置 ★
# ============================================================
OUTPUT_DIR = Path(__file__).resolve().parent.parent / "jsq" / "拆分钣金激光图"

# ============================================================
# 内部常量
# ============================================================
SCRIPT_DIR = Path(__file__).resolve().parent
CYAN_COLOR = 4
BOX_TOLERANCE = 1e-3


# ─────────────────── 弹窗工具 ───────────────────


def _tk_root() -> tk.Tk:
    """获取隐藏的 Tk 根窗口（确保只创建一次）。"""
    root = tk.Tk()
    root.withdraw()
    root.attributes("-topmost", True)
    return root


def ask_batch_overwrite(conflict_names: list[str]) -> bool | None:
    """
    多个同名文件时弹窗询问批量策略。
    返回: True=全部覆盖, False=全部跳过, None=逐个询问
    """
    root = _tk_root()

    result: list[bool | None] = [None]  # 用列表传递值

    dlg = tk.Toplevel(root)
    dlg.title("同名文件冲突")
    dlg.resizable(False, False)
    dlg.attributes("-topmost", True)

    names_str = "\n".join(f"  • {n}" for n in conflict_names)
    msg = f"发现 {len(conflict_names)} 个同名文件:\n{names_str}\n\n请选择处理方式："
    tk.Label(dlg, text=msg, justify="left", padx=16, pady=12).pack()

    btn_frame = tk.Frame(dlg, pady=8)
    btn_frame.pack()

    def on_overwrite():
        result[0] = True
        dlg.destroy()

    def on_skip():
        result[0] = False
        dlg.destroy()

    def on_each():
        result[0] = None
        dlg.destroy()

    tk.Button(btn_frame, text="全部覆盖", width=10, command=on_overwrite).grid(row=0, column=0, padx=6)
    tk.Button(btn_frame, text="全部跳过", width=10, command=on_skip).grid(row=0, column=1, padx=6)
    tk.Button(btn_frame, text="逐个询问", width=10, command=on_each).grid(row=0, column=2, padx=6)

    dlg.protocol("WM_DELETE_WINDOW", on_each)  # 关窗口 = 逐个询问
    dlg.grab_set()
    root.wait_window(dlg)
    root.destroy()
    return result[0]


def ask_single_overwrite(filename: str) -> bool:
    """单个同名文件时弹窗询问是否覆盖，默认覆盖。"""
    root = _tk_root()
    ans = messagebox.askyesno(
        title="同名文件冲突",
        message=f"[{filename}] 已存在，是否覆盖？",
        default="yes",
        parent=root,
    )
    root.destroy()
    return ans


# ─────────────────── DXF 分析 ───────────────────


def collect_block_record_tags(entity, dxfversion: str):
    collector = TagCollector(dxfversion=dxfversion)
    entity.export_dxf(collector)
    return collector.tags


def find_true_name_in_tags(tags) -> str | None:
    for index, tag in enumerate(tags[:-1]):
        if tag == (1001, "AcDbDynamicBlockTrueName"):
            next_tag = tags[index + 1]
            if next_tag.code == 1000 and next_tag.value:
                return next_tag.value
    return None


def get_rep_source_record(doc, insert):
    if not insert.has_extension_dict:
        return None
    try:
        rep_dict = insert.get_extension_dict().dictionary["AcDbBlockRepresentation"]
        rep_data = rep_dict["AcDbRepData"]
    except Exception:
        return None
    tags = collect_block_record_tags(rep_data, doc.dxfversion)
    for index, tag in enumerate(tags[:-1]):
        if tag == (100, "AcDbBlockRepresentationData"):
            for next_tag in tags[index + 1:]:
                if next_tag.code == 340:
                    return doc.entitydb.get(next_tag.value)
    return None


def get_dynamic_true_name(doc, insert) -> str:
    block_layout = doc.blocks.get(insert.dxf.name)
    record = doc.entitydb.get(block_layout.block_record_handle)
    if record is None:
        return insert.dxf.name

    tags = collect_block_record_tags(record, doc.dxfversion)
    true_name = find_true_name_in_tags(tags)
    if true_name:
        return true_name

    rep_source_record = get_rep_source_record(doc, insert)
    if rep_source_record is not None:
        rep_tags = collect_block_record_tags(rep_source_record, doc.dxfversion)
        true_name = find_true_name_in_tags(rep_tags)
        if true_name:
            return true_name

    return insert.dxf.name


# ─────────────────── 几何工具 ───────────────────


def sanitize_filename(name: str) -> str:
    value = re.sub(r'[<>:"/\\|?*\x00-\x1F]', "_", name.strip())
    value = value.rstrip(". ")
    return value or "unnamed"


def is_cyan_rectangle(entity) -> bool:
    if entity.dxftype() != "LWPOLYLINE":
        return False
    if entity.dxf.get("color", 256) != CYAN_COLOR:
        return False
    if not (entity.dxf.get("flags", 0) & 1):
        return False
    points = list(entity.get_points(format="xy"))
    if len(points) != 4:
        return False
    xs = [p[0] for p in points]
    ys = [p[1] for p in points]
    xmin, xmax = min(xs), max(xs)
    ymin, ymax = min(ys), max(ys)
    if xmax - xmin < 10 or ymax - ymin < 10:
        return False
    # 每个顶点的 x 要么接近 xmin 要么接近 xmax，y 同理
    rect_tol = max(xmax - xmin, ymax - ymin) * 0.01
    for px, py in points:
        x_ok = abs(px - xmin) < rect_tol or abs(px - xmax) < rect_tol
        y_ok = abs(py - ymin) < rect_tol or abs(py - ymax) < rect_tol
        if not (x_ok and y_ok):
            return False
    return True


def get_box_bounds(entity) -> tuple[float, float, float, float]:
    points = list(entity.get_points(format="xy"))
    xs = [p[0] for p in points]
    ys = [p[1] for p in points]
    return min(xs), max(xs), min(ys), max(ys)


def point_in_box(x: float, y: float, bounds: tuple) -> bool:
    min_x, max_x, min_y, max_y = bounds
    return (
        min_x - BOX_TOLERANCE <= x <= max_x + BOX_TOLERANCE
        and min_y - BOX_TOLERANCE <= y <= max_y + BOX_TOLERANCE
    )


# ─────────────────── 任务构建 ───────────────────


def build_export_jobs(doc) -> list[dict]:
    msp = doc.modelspace()
    cyan_boxes = []
    for entity in msp:
        if is_cyan_rectangle(entity):
            bounds = get_box_bounds(entity)
            cyan_boxes.append({
                "entity": entity,
                "bounds": bounds,
                "area": (bounds[1] - bounds[0]) * (bounds[3] - bounds[2]),
            })

    selected_boxes = []
    for insert in msp.query("INSERT"):
        containing = [
            box for box in cyan_boxes
            if point_in_box(insert.dxf.insert.x, insert.dxf.insert.y, box["bounds"])
        ]
        if not containing:
            continue
        outer_box = max(containing, key=lambda b: b["area"])
        selected_boxes.append((outer_box, get_dynamic_true_name(doc, insert), insert.dxf.handle))

    jobs = []
    seen = set()
    used_names: dict[str, int] = {}
    for box, true_name, insert_handle in selected_boxes:
        box_handle = box["entity"].dxf.handle
        if box_handle in seen:
            continue
        seen.add(box_handle)

        base = sanitize_filename(true_name)
        if base in used_names:
            used_names[base] += 1
            filename = f"{base}_{used_names[base]}"
        else:
            used_names[base] = 1
            filename = base

        jobs.append({
            "filename": filename,
            "bounds": box["bounds"],
            "box_handle": box_handle,
            "insert_handle": insert_handle,
        })
    return jobs


# ─────────────────── accoreconsole ───────────────────


def decode_accore_output(raw) -> str:
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


def find_accoreconsole() -> str:
    env_path = os.environ.get("ACCORECONSOLE_PATH")
    if env_path and os.path.exists(env_path):
        return env_path
    for year in range(2026, 2017, -1):
        candidate = rf"C:\Program Files\Autodesk\AutoCAD {year}\accoreconsole.exe"
        if os.path.exists(candidate):
            return candidate
    raise FileNotFoundError(
        "未找到 accoreconsole.exe\n"
        "请安装 AutoCAD 或设置环境变量 ACCORECONSOLE_PATH 指向 accoreconsole.exe"
    )


def run_accoreconsole(accore: str, input_path: Path, script_path: Path, timeout: int = 300) -> str:
    isolate_id = uuid.uuid4().hex[:12]
    user_data_dir = script_path.parent / "accore_userdata"
    user_data_dir.mkdir(parents=True, exist_ok=True)

    cmd = [
        accore, "/i", str(input_path), "/s", str(script_path),
        "/l", "en-US", "/isolate", isolate_id, str(user_data_dir), "/readonly",
    ]
    result = subprocess.run(cmd, capture_output=True, timeout=timeout)
    stdout = decode_accore_output(result.stdout)
    stderr = decode_accore_output(result.stderr)
    if result.returncode != 0:
        raise RuntimeError(
            f"accoreconsole 退出码 {result.returncode}\n"
            f"STDOUT:\n{stdout[-2000:]}\nSTDERR:\n{stderr[-2000:]}"
        )
    return "\n".join(part for part in (stdout, stderr) if part)


# ─────────────────── 导出脚本生成 ───────────────────


def write_wblock_script(scr_path: Path, jobs: list[dict], export_dir: Path) -> None:
    lines = ["FILEDIA", "0", "CMDECHO", "0"]
    for job in jobs:
        min_x, max_x, min_y, max_y = job["bounds"]
        out_path = (export_dir / f"{job['filename']}.dwg").as_posix()
        lines.extend([
            "_.-WBLOCK",
            f'"{out_path}"',
            "",                                    # 接受"定义新图形"默认
            f"{min_x:.6f},{min_y:.6f},0",          # 基点
            "_C",                                  # Crossing 选择
            f"{min_x:.6f},{min_y:.6f}",            # 角点1
            f"{max_x:.6f},{max_y:.6f}",            # 角点2
            "",                                    # 结束选择
        ])
    lines.extend(["QUIT", "_Y"])
    scr_path.write_text("\n".join(lines) + "\n", encoding="ascii", newline="\n")


def write_dxfout_script(scr_path: Path, output_dxf: Path) -> None:
    scr_path.write_text(
        f'_.DXFOUT\n"{output_dxf.as_posix()}"\n16\nQUIT\n_Y\n',
        encoding="ascii", newline="\n",
    )


def convert_dwg_to_dxf(accore: str, dwg_path: Path, output_dxf: Path) -> None:
    with tempfile.TemporaryDirectory(prefix="split_dxfout_") as td:
        tmp = Path(td)
        dxfout_path = tmp / output_dxf.name
        script_path = tmp / "dxfout.scr"
        write_dxfout_script(script_path, dxfout_path)
        log = run_accoreconsole(accore, dwg_path, script_path)
        if not dxfout_path.exists():
            raise FileNotFoundError(f"DXFOUT 失败: {dwg_path.name}\n{log[-2000:]}")
        try:
            shutil.copy2(dxfout_path, output_dxf)
        except PermissionError:
            raise PermissionError(f"{output_dxf.name} 被其他程序占用，请关闭后重试")


# ─────────────────── 主导出流程 ───────────────────


def export_jobs_natively(
    source_path: Path, output_dir: Path, jobs: list[dict]
) -> list[str]:
    accore = find_accoreconsole()

    with tempfile.TemporaryDirectory(prefix="split_wblock_") as td:
        tmp = Path(td)
        source_copy = tmp / source_path.name
        shutil.copy2(source_path, source_copy)

        export_dir = tmp / "wblock_exports"
        export_dir.mkdir()

        scr_path = tmp / "export_jobs.scr"
        write_wblock_script(scr_path, jobs, export_dir)
        export_log = run_accoreconsole(accore, source_copy, scr_path, timeout=600)

        # 先统计有多少同名文件，超过1个时提供批量选项
        conflicts = [
            output_dir / f"{job['filename']}.dxf"
            for job in jobs
            if (output_dir / f"{job['filename']}.dxf").exists()
        ]
        batch_overwrite: bool | None = None   # True=全覆盖 False=全跳过 None=逐个询问
        if len(conflicts) > 1:
            batch_overwrite = ask_batch_overwrite([p.name for p in conflicts])
            label = {True: "全部覆盖", False: "全部跳过", None: "逐个询问"}[batch_overwrite]
            print(f"  → {label}")

        created: list[str] = []
        for job in jobs:
            dwg_path = export_dir / f"{job['filename']}.dwg"
            if not dwg_path.exists():
                raise FileNotFoundError(
                    f"WBLOCK 失败: {dwg_path.name}\n{export_log[-4000:]}"
                )
            out_dxf = output_dir / f"{job['filename']}.dxf"
            if out_dxf.exists():
                if batch_overwrite is True:
                    pass  # 直接覆盖
                elif batch_overwrite is False:
                    print(f"  [跳过] {out_dxf.name}")
                    continue
                else:
                    # 逐个询问
                    if not ask_single_overwrite(out_dxf.name):
                        print(f"  [跳过] {out_dxf.name}")
                        continue
            convert_dwg_to_dxf(accore, dwg_path, out_dxf)
            created.append(out_dxf.name)

    return created


def process_one_dxf(source_path: Path, output_dir: Path) -> list[str]:
    """处理单个 DXF 文件，返回生成的文件名列表。"""
    doc = ezdxf.readfile(source_path)
    jobs = build_export_jobs(doc)
    if not jobs:
        print(f"  [跳过] 未找到青色矩形框内的块引用")
        return []
    print(f"  发现 {len(jobs)} 个钣金图: {', '.join(j['filename'] for j in jobs)}")
    created = export_jobs_natively(source_path, output_dir, jobs)
    return created


# ─────────────────── 入口 ───────────────────


def main() -> None:
    import argparse
    parser = argparse.ArgumentParser(description="钣金激光图拆分工具")
    parser.add_argument(
        "--output-dir", type=Path, default=OUTPUT_DIR,
        help=f"输出目录 (默认: {OUTPUT_DIR})",
    )
    args = parser.parse_args()
    output_dir: Path = args.output_dir.expanduser().resolve()

    # 扫描脚本所在目录的 .dxf 文件
    dxf_files = sorted(SCRIPT_DIR.glob("*.dxf"))
    if not dxf_files:
        print(f"未在 {SCRIPT_DIR} 下找到任何 .dxf 文件")
        print("请将待拆分的 DXF 文件放到本脚本所在文件夹后重新运行")
        sys.exit(1)

    print(f"输出目录: {output_dir}")
    print(f"找到 {len(dxf_files)} 个 DXF 文件\n")

    total_created = 0
    for dxf_file in dxf_files:
        print(f"处理: {dxf_file.name}")

        # 多个源文件时每个源文件建子文件夹；单文件直接放输出目录
        if len(dxf_files) == 1:
            dest = output_dir
        else:
            dest = output_dir / dxf_file.stem
        dest.mkdir(parents=True, exist_ok=True)

        created = process_one_dxf(dxf_file, dest)
        total_created += len(created)
        for name in created:
            print(f"  ✓ {name}")
        print()

    print(f"完成，共生成 {total_created} 个 DXF 文件 → {output_dir}")


if __name__ == "__main__":
    main()
