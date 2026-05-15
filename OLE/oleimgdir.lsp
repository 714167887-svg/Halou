(vl-load-com)

(setq *oleimg-max-width* 2500.0)
(setq *oleimg-max-height* 2500.0)
(setq *oleimg-gap* 150.0)
(setq *oleimg-script-dir*
  (cond
    ((and (boundp '*load-truename*) *load-truename*)
      (vl-filename-directory *load-truename*)
    )
    ((and (boundp '*load-name*) *load-name* (findfile *load-name*))
      (vl-filename-directory (findfile *load-name*))
    )
    (T nil)
  )
)

(defun oleimg:message (text)
  (princ (strcat "\n[OLEIMGDIR] " text))
)

(defun oleimg:path-join (folder name / last-char)
  (setq last-char (substr folder (strlen folder) 1))
  (if (or (= last-char "\\") (= last-char "/"))
    (strcat folder name)
    (strcat folder "\\" name)
  )
)

(defun oleimg:script-dir ()
  (cond
    (*oleimg-script-dir* *oleimg-script-dir*)
    ((findfile "oleimgdir.lsp")
      (vl-filename-directory (findfile "oleimgdir.lsp"))
    )
    (T nil)
  )
)

(defun oleimg:helper-path (/ local-path drawing-path temp-path)
  (setq local-path
    (if (oleimg:script-dir)
      (oleimg:path-join (oleimg:script-dir) "oleimgdir-clipboard.ps1")
      nil
    )
  )
  (setq drawing-path
    (if (and (getvar "DWGPREFIX") (/= (getvar "DWGPREFIX") ""))
      (oleimg:path-join (getvar "DWGPREFIX") "oleimgdir-clipboard.ps1")
      nil
    )
  )
  (setq temp-path
    (if (getenv "TEMP")
      (oleimg:path-join (getenv "TEMP") "oleimgdir-clipboard.ps1")
      nil
    )
  )
  (cond
    ((and local-path (findfile local-path)) local-path)
    ((and temp-path (findfile temp-path)) temp-path)
    ((and drawing-path (findfile drawing-path)) drawing-path)
    (T nil)
  )
)

(defun oleimg:ps-quote (text / q)
  (setq q (chr 34))
  (strcat q (vl-string-subst (strcat q q) q text) q)
)

(defun oleimg:run-helper (action image-path / helper shell cmd code)
  (setq helper (oleimg:helper-path))
  (if (and helper (findfile helper))
    (progn
      (setq shell (vlax-create-object "WScript.Shell"))
      (setq cmd
        (strcat
          "powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File "
          (oleimg:ps-quote helper)
          " -Action "
          (oleimg:ps-quote action)
          (if image-path
            (strcat " -ImagePath " (oleimg:ps-quote image-path))
            ""
          )
        )
      )
      (setq code (vlax-invoke-method shell 'Run cmd 0 :vlax-true))
      (vlax-release-object shell)
      (= code 0)
    )
    (progn
      (oleimg:message "未找到辅助脚本，已检查 LSP 所在目录、图纸目录、e:\\111 和 TEMP。")
      nil
    )
  )
)

(defun oleimg:clipboard-save ()
  (oleimg:run-helper "save" nil)
)

(defun oleimg:clipboard-restore ()
  (oleimg:run-helper "restore" nil)
)

(defun oleimg:clipboard-clear-state ()
  (oleimg:run-helper "clearstate" nil)
)

(defun oleimg:set-clipboard-image (image-path)
  (oleimg:run-helper "setimage" image-path)
)

(defun oleimg:check-powershell (/ shell result)
  (setq shell (vlax-create-object "WScript.Shell"))
  (setq result (vl-catch-all-apply 'vlax-invoke-method (list shell 'Run "powershell.exe -NoProfile -Command exit" 0 :vlax-true)))
  (vlax-release-object shell)
  (not (vl-catch-all-error-p result))
)

(defun oleimg:check-image-folder-access (folder / files ok)
  (setq ok (not (vl-catch-all-error-p (vl-catch-all-apply 'vl-directory-files (list folder "*.*" 1)))))
  ok
)

(defun c:OLEIMGCHECK (/ helper folder)
  (setq helper (oleimg:helper-path))
  (oleimg:message "开始自检。")
  (if (and helper (findfile helper))
    (oleimg:message (strcat "辅助脚本正常: " helper))
    (oleimg:message "辅助脚本异常：在 LSP 所在目录、e:\\111 和 TEMP 中都没找到。"))
  (if (oleimg:check-powershell)
    (oleimg:message "PowerShell 可用。")
    (oleimg:message "PowerShell 异常：无法启动 powershell.exe。"))
  (setq folder (getvar "DWGPREFIX"))
  (if (and folder (/= folder ""))
    (if (oleimg:check-image-folder-access folder)
      (oleimg:message (strcat "图片目录可访问: " folder))
      (oleimg:message (strcat "图片目录无法访问: " folder)))
    (oleimg:message "跳过图片目录检查：当前图纸尚未保存。"))
  (oleimg:message "自检结束。")
  (princ)
)

(defun oleimg:get-image-files (folder / patterns names files)
  (setq patterns '("*.bmp" "*.dib" "*.png" "*.jpg" "*.jpeg" "*.gif" "*.tif" "*.tiff"))
  (setq files nil)
  (foreach pattern patterns
    (setq names (vl-directory-files folder pattern 1))
    (foreach name names
      (setq files (cons (oleimg:path-join folder name) files))
    )
  )
  (vl-sort files '(lambda (a b) (< (strcase a) (strcase b))))
)

(defun oleimg:to-point-list (value / maybe-variant)
  (setq maybe-variant (vl-catch-all-apply 'vlax-variant-value (list value)))
  (if (vl-catch-all-error-p maybe-variant)
    (vlax-safearray->list value)
    (vlax-safearray->list maybe-variant)
  )
)

(defun oleimg:get-bbox (ent / obj minpt maxpt)
  (if ent
    (progn
      (setq obj (vlax-ename->vla-object ent))
      (vla-GetBoundingBox obj 'minpt 'maxpt)
      (list
        (oleimg:to-point-list minpt)
        (oleimg:to-point-list maxpt)
      )
    )
    nil
  )
)

(defun oleimg:get-entity-size (ent / bbox minpt maxpt)
  (setq bbox (oleimg:get-bbox ent))
  (if bbox
    (progn
      (setq minpt (car bbox))
      (setq maxpt (cadr bbox))
      (list
        (abs (- (car maxpt) (car minpt)))
        (abs (- (cadr maxpt) (cadr minpt)))
      )
    )
    nil
  )
)

(defun oleimg:align-entity-lower-left (ent base-pt / bbox minpt obj delta)
  (setq bbox (oleimg:get-bbox ent))
  (if bbox
    (progn
      (setq minpt (car bbox))
      (setq obj (vlax-ename->vla-object ent))
      (setq delta
        (vlax-3d-point
          (list
            (- (car base-pt) (car minpt))
            (- (cadr base-pt) (cadr minpt))
            (- (if (caddr base-pt) (caddr base-pt) 0.0)
               (if (caddr minpt) (caddr minpt) 0.0))
          )
        )
      )
      (vla-Move obj (vlax-3d-point '(0.0 0.0 0.0)) delta)
    )
  )
  ent
)

(defun oleimg:fit-entity-inside-limit (ent base-pt max-width max-height / bbox minpt maxpt width height factor-x factor-y factor)
  (if ent
    (progn
      (setq bbox (oleimg:get-bbox ent))
      (if bbox
        (progn
          (setq minpt (car bbox))
          (setq maxpt (cadr bbox))
          (setq width (- (car maxpt) (car minpt)))
          (setq height (- (cadr maxpt) (cadr minpt)))
          (if (and (> width 1e-8) (> height 1e-8))
            (progn
              (setq factor-x (/ max-width width))
              (setq factor-y (/ max-height height))
              (setq factor (min factor-x factor-y 1.0))
              (if (< factor 0.999999)
                (command "_.SCALE" ent "" base-pt factor)
              )
            )
          )
        )
      )
      (oleimg:align-entity-lower-left ent base-pt)
    )
  )
  ent
)

(defun oleimg:delay-ms (milliseconds / start now)
  (setq start (getvar "MILLISECS"))
  (setq now start)
  (while (< (- now start) milliseconds)
    (setq now (getvar "MILLISECS"))
  )
)

(defun oleimg:paste-one (image-path insert-pt max-width max-height / before after)
  (if (not (oleimg:set-clipboard-image image-path))
    (progn
      (oleimg:message (strcat "写入剪贴板失败: " image-path))
      nil
    )
    (progn
      (setq before (entlast))
      (command "_.PASTECLIP" insert-pt)
      (oleimg:delay-ms 300)
      (setq after (entlast))
      (if (and after (/= after before))
        (progn
          (oleimg:fit-entity-inside-limit after insert-pt max-width max-height)
          after
        )
        (progn
          (oleimg:message (strcat "粘贴 OLE 图片失败: " image-path))
          nil
        )
      )
    )
  )
)

(defun oleimg:get-start-point (/ pt)
  (setq pt (getpoint "\n指定排布起点 <0,0>: "))
  (if pt pt '(0.0 0.0 0.0))
)

(defun oleimg:point-add (pt dx dy)
  (list (+ (car pt) dx) (+ (cadr pt) dy) (if (caddr pt) (caddr pt) 0.0))
)

(defun oleimg:import-file-list (files / total-count base-pt cursor-pt imported failed placed size width save-ok restored)
  (setq total-count (length files))
  (cond
    ((= total-count 0)
      (oleimg:message "没有可导入的图片。")
    )
    (T
      (setq save-ok (oleimg:clipboard-save))
      (if (not save-ok)
        (oleimg:message "警告：剪贴板快照保存失败，原剪贴板内容可能无法恢复。")
      )
      (setq base-pt (oleimg:get-start-point))
      (setq cursor-pt base-pt)
      (setq imported 0)
      (setq failed 0)
      (oleimg:message
        (strcat
          "开始导入，共 " (itoa total-count)
          " 张图片。每张图片最大不超过 "
          (rtos *oleimg-max-width* 2 2) " x " (rtos *oleimg-max-height* 2 2)
          " mm，横向间距为 " (rtos *oleimg-gap* 2 2) " mm。"
        )
      )
      (foreach file files
        (oleimg:message
          (strcat
            "正在处理 "
            (itoa (+ imported failed 1))
            "/"
            (itoa total-count)
            ": "
            (vl-filename-base file)
            (vl-filename-extension file)
          )
        )
        (setq placed (oleimg:paste-one file cursor-pt *oleimg-max-width* *oleimg-max-height*))
        (if placed
          (progn
            (setq imported (1+ imported))
            (setq size (oleimg:get-entity-size placed))
            (if (and size (> (car size) 1e-8))
              (setq width (car size))
              (setq width *oleimg-max-width*)
            )
            (setq cursor-pt (oleimg:point-add cursor-pt (+ width *oleimg-gap*) 0.0))
          )
          (progn
            (setq failed (1+ failed))
            (setq cursor-pt (oleimg:point-add cursor-pt (+ *oleimg-max-width* *oleimg-gap*) 0.0))
          )
        )
      )
      (setq restored (oleimg:clipboard-restore))
      (if (not restored)
        (oleimg:message "警告：导入结束后恢复剪贴板失败。")
      )
      (oleimg:clipboard-clear-state)
      (oleimg:message (strcat "导入完成。成功 " (itoa imported) " 张，失败 " (itoa failed) " 张。"))
    )
  )
)

(defun oleimg:import-folder (folder / files)
  (setq files (oleimg:get-image-files folder))
  (cond
    ((= (length files) 0)
      (oleimg:message (strcat "目录中未找到图片: " folder))
    )
    (T (oleimg:import-file-list files))
  )
)

(defun oleimg:read-droplist (path / fh line files)
  (setq files nil)
  (if (and path (findfile path))
    (progn
      (setq fh (open path "r"))
      (while (setq line (read-line fh))
        (if (and line (/= line "") (findfile line))
          (setq files (cons line files))
        )
      )
      (close fh)
      (vl-file-delete path)
    )
  )
  (reverse files)
)

(defun c:OLEDROP (/ listpath files)
  (setq listpath
    (if (getenv "TEMP")
      (oleimg:path-join (getenv "TEMP") "halou-oledrop.txt")
      nil
    )
  )
  (setq files (oleimg:read-droplist listpath))
  (cond
    ((or (null files) (= (length files) 0))
      (oleimg:message "未找到拖拽清单或清单为空。")
    )
    (T
      (oleimg:message (strcat "拖入 " (itoa (length files)) " 张图片，请点击排布起点。"))
      (oleimg:import-file-list files)
    )
  )
  (princ)
)

(defun c:OLEIMGDIR (/ folder)
  (setq folder (getvar "DWGPREFIX"))
  (if (or (null folder) (= folder ""))
    (setq folder (getstring T "\n当前图纸尚未保存，请输入图片目录: "))
  )
  (if (and folder (/= folder "") (vl-file-directory-p folder))
    (oleimg:import-folder folder)
    (oleimg:message "目录无效。")
  )
  (princ)
)

(defun c:OI () (c:OLEIMGDIR))
(defun c:OLE () (c:OLEIMGDIR))

(princ)
