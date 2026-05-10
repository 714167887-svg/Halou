#!/usr/bin/env python
"""Small GUI wrapper for the JSQCAD angle generator."""

from __future__ import annotations

import json
import tkinter as tk
from tkinter import filedialog
from tkinter import messagebox
from tkinter import ttk

from generate_sheet_jsqcad import DEFAULT_MODEL_NAME
from generate_sheet_jsqcad import RIGHT_PROFILE_ANGLES
from generate_sheet_jsqcad import _mirror_payload
from generate_sheet_jsqcad import build_pair_jsqcad
from generate_sheet_jsqcad import build_jsqcad
from generate_sheet_jsqcad import get_model_range


WINDOW_TITLE = "JSQCAD Generator"


class GeneratorApp:
    def __init__(self, root: tk.Tk) -> None:
        self.root = root
        self.root.title(WINDOW_TITLE)
        self.root.geometry("920x720")
        self.root.minsize(760, 560)

        self.left_angle_var = tk.StringVar(value="129")
        self.right_angle_var = tk.StringVar(value="135")
        self.gap_var = tk.StringVar(value="50")
        self.axis_var = tk.StringVar(value="左轴")
        self.base_x_var = tk.StringVar(value="0")
        self.base_y_var = tk.StringVar(value="0")
        left_min_angle, left_max_angle = get_model_range(DEFAULT_MODEL_NAME)
        self.status_var = tk.StringVar(
            value=(
                f"转轴中柱 {left_min_angle:.0f}-{left_max_angle:.0f} deg, "
                f"挡水中柱 {RIGHT_PROFILE_ANGLES[0]:.0f}-{RIGHT_PROFILE_ANGLES[-1]:.0f} deg."
            )
        )

        self._build_ui()
        self.text.focus_set()
        self.root.bind("<Return>", self.on_generate)
        self.root.bind("<Control-c>", self.on_copy)

    def _build_ui(self) -> None:
        shell = ttk.Frame(self.root, padding=14)
        shell.pack(fill="both", expand=True)

        controls = ttk.Frame(shell)
        controls.pack(fill="x")

        ttk.Label(controls, text="转轴中柱角度").grid(row=0, column=0, sticky="w")
        ttk.Entry(controls, textvariable=self.left_angle_var, width=12).grid(
            row=1, column=0, sticky="ew", padx=(0, 10)
        )

        ttk.Label(controls, text="挡水中柱角度").grid(row=0, column=1, sticky="w")
        ttk.Entry(controls, textvariable=self.right_angle_var, width=12).grid(
            row=1, column=1, sticky="ew", padx=(0, 10)
        )

        ttk.Label(controls, text="Gap").grid(row=0, column=2, sticky="w")
        ttk.Entry(controls, textvariable=self.gap_var, width=12).grid(
            row=1, column=2, sticky="ew", padx=(0, 10)
        )

        ttk.Label(controls, text="Base X").grid(row=0, column=3, sticky="w")
        ttk.Entry(controls, textvariable=self.base_x_var, width=12).grid(
            row=1, column=3, sticky="ew", padx=(0, 10)
        )

        ttk.Label(controls, text="Base Y").grid(row=0, column=4, sticky="w")
        ttk.Entry(controls, textvariable=self.base_y_var, width=12).grid(
            row=1, column=4, sticky="ew", padx=(0, 10)
        )

        ttk.Button(controls, text="Generate", command=self.on_generate).grid(
            row=1, column=5, sticky="ew", padx=(10, 8)
        )
        ttk.Button(controls, text="Copy", command=self.on_copy).grid(
            row=1, column=6, sticky="ew", padx=(0, 8)
        )
        ttk.Button(controls, text="Save", command=self.on_save).grid(
            row=1, column=7, sticky="ew", padx=(0, 8)
        )
        ttk.Button(controls, text="Clear", command=self.on_clear).grid(
            row=1, column=8, sticky="ew"
        )

        for index in range(9):
            controls.columnconfigure(index, weight=1 if index < 5 else 0)

        axis_frame = ttk.Frame(shell)
        axis_frame.pack(fill="x", pady=(8, 0))
        ttk.Label(axis_frame, text="开门方向:").pack(side="left")
        ttk.Radiobutton(axis_frame, text="左轴", variable=self.axis_var, value="左轴").pack(side="left", padx=(6, 4))
        ttk.Radiobutton(axis_frame, text="右轴", variable=self.axis_var, value="右轴").pack(side="left", padx=(0, 12))
        ttk.Button(axis_frame, text="旋转已有", command=self.on_rotate).pack(side="left", padx=(0, 8))

        ttk.Label(shell, textvariable=self.status_var).pack(fill="x", pady=(10, 10))

        text_frame = ttk.Frame(shell)
        text_frame.pack(fill="both", expand=True)

        scrollbar = ttk.Scrollbar(text_frame, orient="vertical")
        scrollbar.pack(side="right", fill="y")

        self.text = tk.Text(
            text_frame,
            wrap="none",
            undo=True,
            font=("Consolas", 10),
            yscrollcommand=scrollbar.set,
        )
        self.text.pack(side="left", fill="both", expand=True)
        scrollbar.config(command=self.text.yview)

    def _build_text(self) -> str:
        left_angle = float(self.left_angle_var.get().strip())
        right_angle_raw = self.right_angle_var.get().strip()
        gap = float(self.gap_var.get().strip())
        base_x = float(self.base_x_var.get().strip())
        base_y = float(self.base_y_var.get().strip())
        if right_angle_raw:
            payload = build_pair_jsqcad(left_angle, float(right_angle_raw), gap, base_x, base_y)
        else:
            payload = build_jsqcad(left_angle, base_x, base_y, DEFAULT_MODEL_NAME)
        if self.axis_var.get() == "右轴":
            payload = _mirror_payload(payload)
        return "JSQCAD:" + json.dumps(payload, ensure_ascii=False, indent=2)

    def on_generate(self, event: object | None = None) -> None:
        del event
        try:
            text = self._build_text()
        except Exception as exc:
            self.status_var.set(str(exc))
            messagebox.showerror(WINDOW_TITLE, str(exc))
            return

        self.text.delete("1.0", "end")
        self.text.insert("1.0", text)
        axis = self.axis_var.get()
        self.status_var.set(f"已生成 8317 中柱 JSQCAD（{axis}）。可直接复制。")

    def on_copy(self, event: object | None = None) -> None:
        del event
        content = self.text.get("1.0", "end-1c")
        if not content.strip():
            try:
                content = self._build_text()
            except Exception as exc:
                self.status_var.set(str(exc))
                messagebox.showerror(WINDOW_TITLE, str(exc))
                return
            self.text.delete("1.0", "end")
            self.text.insert("1.0", content)

        self.root.clipboard_clear()
        self.root.clipboard_append(content)
        self.root.update()
        self.status_var.set("已复制 JSQCAD 到剪贴板。")

    def on_save(self) -> None:
        content = self.text.get("1.0", "end-1c").strip()
        if not content:
            try:
                content = self._build_text()
            except Exception as exc:
                self.status_var.set(str(exc))
                messagebox.showerror(WINDOW_TITLE, str(exc))
                return
            self.text.delete("1.0", "end")
            self.text.insert("1.0", content)

        path = filedialog.asksaveasfilename(
            title="Save JSQCAD",
            defaultextension=".txt",
            filetypes=[("Text Files", "*.txt"), ("All Files", "*.*")],
        )
        if not path:
            return

        with open(path, "w", encoding="utf-8") as handle:
            handle.write(content)
            handle.write("\n")
        self.status_var.set(f"Saved to {path}")

    def on_clear(self) -> None:
        self.text.delete("1.0", "end")
        left_min_angle, left_max_angle = get_model_range(DEFAULT_MODEL_NAME)
        self.status_var.set(
            f"转轴中柱 {left_min_angle:.0f}-{left_max_angle:.0f} deg, "
            f"挡水中柱 {RIGHT_PROFILE_ANGLES[0]:.0f}-{RIGHT_PROFILE_ANGLES[-1]:.0f} deg."
        )

    def on_rotate(self) -> None:
        """Mirror/rotate the current JSQCAD in the text box."""
        content = self.text.get("1.0", "end-1c").strip()
        if not content:
            self.status_var.set("文本为空，请先生成 JSQCAD。")
            return
        try:
            json_str = content
            if json_str.startswith("JSQCAD:"):
                json_str = json_str[7:]
            payload = json.loads(json_str)
            was_mirrored = payload.get("meta", {}).get("mirrored", False)
            payload = _mirror_payload(payload)
            if "meta" in payload:
                payload["meta"]["mirrored"] = not was_mirrored
            text = "JSQCAD:" + json.dumps(payload, ensure_ascii=False, indent=2)
            self.text.delete("1.0", "end")
            self.text.insert("1.0", text)
            if self.axis_var.get() == "左轴":
                self.axis_var.set("右轴")
            else:
                self.axis_var.set("左轴")
            self.status_var.set("已旋转 JSQCAD（左右镜像翻转）。")
        except Exception as exc:
            self.status_var.set(f"旋转失败: {exc}")
            messagebox.showerror(WINDOW_TITLE, f"旋转失败: {exc}")


def main() -> int:
    root = tk.Tk()
    try:
        root.iconname(WINDOW_TITLE)
    except Exception:
        pass
    style = ttk.Style(root)
    try:
        style.theme_use("vista")
    except Exception:
        pass
    GeneratorApp(root)
    root.mainloop()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
