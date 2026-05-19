;;; JT_Snapshot.lsp
;;; 命令: JT  ——  方框截图 + 剪贴板 + 导出 PNG
;;;
;;; 功能：
;;;   1. 选一个矩形/多段线方框
;;;   2. 自动框选方框内所有对象（含块引用）
;;;   3. 调 _COPYCLIP，把对象放进 Windows 剪贴板（同时含 CAD 实体 + 位图两种格式）
;;;      → 粘贴到微信/Word = 看到图片；粘贴到 CAD = 看到实体
;;;   4. 同时把方框区域导出为 PNG，保存到 E:\halou wode\W\JT\时间戳.png
;;;
;;; 用法：JT → 选方框 → 完成
;;;
;;; 依赖：AutoCAD 自带 COPYCLIP / VIEW / ZOOM / vla-Export

(vl-load-com)

;;; v1.1.39: 临时把模型空间背景切到白色（让 PNGOUT 截图变白底；CAD 会自动把
;;; 白色绘图反转为黑色，所以图纸内容仍清晰）。返回原背景颜色（int），失败返回 nil。
(defun jt:bg-set-white ( / dp old)
  (vl-catch-all-apply
    '(lambda ()
       (setq dp (vla-get-display
                  (vla-get-preferences (vlax-get-acad-object))))
       (setq old (vla-get-graphicswinmodelbackgrndcolor dp))
       (vla-put-graphicswinmodelbackgrndcolor dp 16777215))) ;; 0x00FFFFFF 白
  old)

(defun jt:bg-restore (old / dp)
  (if old
    (vl-catch-all-apply
      '(lambda ()
         (setq dp (vla-get-display
                    (vla-get-preferences (vlax-get-acad-object))))
         (vla-put-graphicswinmodelbackgrndcolor dp old)))))

;;; v2.0.53: 删除 v2.0.49 引入的 CLEANSCREEN / VIEWRES bump：
;;;   v2.0.52 起白底与原底都走 jt-plot-png (PLOT API)，按 window 精确出图，
;;;   分辨率由 media 决定（1600x1280），与视口面积无关，VIEWRES 也无关。
;;;   保留 cleanscreen 反而会把用户 CAD 功能区隐藏，体验糟糕。
(defun jt:cleanscreen-on ( ) 0)
(defun jt:cleanscreen-restore (cs-old) nil)

;;; 时间戳：yyyyMMdd_HHmmss
(defun jt:timestamp ( / d)
  (setq d (rtos (getvar "CDATE") 2 6))
  (strcat (substr d 1 8) "_" (substr d 10 6)))

;;; 取实体包围盒 → ((xmin ymin) (xmax ymax))
(defun jt:bbox (ent / obj minP maxP)
  (setq obj (vlax-ename->vla-object ent))
  (vla-getboundingbox obj 'minP 'maxP)
  (list (vlax-safearray->list minP)
        (vlax-safearray->list maxP)))

;;; 从选择集中剔除指定实体
(defun jt:ss-remove (ss target / new i n e)
  (setq new (ssadd) n (sslength ss) i 0)
  (while (< i n)
    (setq e (ssname ss i))
    (if (not (eq e target)) (ssadd e new))
    (setq i (1+ i)))
  new)

;;; 确保目录存在；递归创建上级目录
(defun jt:ensure-dir (path / pos parent)
  (if (not (vl-file-directory-p path))
    (progn
      ;; 去掉末尾的 \\
      (if (and (> (strlen path) 0)
               (= (substr path (strlen path) 1) "\\"))
        (setq path (substr path 1 (1- (strlen path)))))
      (setq pos (vl-string-position 92 path nil T)) ;; 92 = '\'
      (if (and pos (> pos 2))
        (progn
          (setq parent (substr path 1 pos))
          (if (not (vl-file-directory-p parent))
            (jt:ensure-dir parent))))
      (vl-mkdir path))))

;;; -PLOT 出图 PNG（PublishToWeb PNG.pc3 + Window + Fit）。返回 T/Nil 表示是否调用成功。
;;; 注：不同 AutoCAD 版本的提示数可能略有差异；失败时主命令会回退到 PNGOUT。
(defun jt:plot-png (full pp1 pp2 / err)
  (setq err
    (vl-catch-all-apply
      'vl-cmdf
      (list "._-PLOT"
        "_Y"                                       ; 详细打印配置? 是
        ""                                         ; 布局：当前(Model)
        "PublishToWeb PNG.pc3"                     ; 打印设备
        "Sun Hi-Res (1600.00 x 1280.00 Pixels)"   ; 图纸尺寸
        "_M"                                        ; 单位：毫米
        "_L"                                        ; 方向：横向
        "_N"                                        ; 反向打印? 否
        "_W" pp1 pp2                                ; 打印区域 = 窗口 + 两点
        "_F"                                        ; 布满图纸
        "_C"                                        ; 居中打印
        "_Y"                                        ; 使用打印样式表? 是
        "acad.ctb"                                  ; 样式表
        "_Y"                                        ; 打印线宽
        "_N"                                        ; 打印透明度
        "_N"                                        ; 打印图纸空间最后
        "_N"                                        ; 隐藏图纸空间
        full                                        ; 输出文件
        "_N"                                        ; 保存对布局的修改? 否
        "_Y"                                       ; 继续打印? 是
      )))
  (not (vl-catch-all-error-p err)))

(defun c:JT ( / frames frame ent tp bb p1 p2 ss sub i j n
              dir full padX padY pp1 pp2 fd-old
              tmp-pngs single-png all-ents hl-ss bg-old vt-old ti-old
              bg-mode kw cs-old vr-old plot-ok plot-media)

  (princ "\n>>> JT 方框截图：剪贴板 + PNG 导出 <<<")
  (princ "\n  支持选多个方框：每选一个回车继续，全部选完再回车结束")
  (princ "\n  输入 R 可切换截图底色（白底/原底）")

  ;; v2.0.49: 默认沿用原 JT 行为（白底）
  (setq bg-mode "White")

  ;; 1. 循环选择多个方框（已选的方框会高亮+夹点显示，不会忘记）
  ;;    v2.0.49: 加 R 关键字切换底色
  (setq frames '()  hl-ss (ssadd))
  (while
    (progn
      (initget "R")
      (setq frame (entsel (strcat "\n请选择方框 [R=底色("
                                  (if (eq bg-mode "White") "白底" "原底")
                                  ")] ["
                                  (itoa (length frames))
                                  " 个已选，回车结束]: ")))
      ;; frame 取值：list(选中实体) / "R"(切换底色) / nil(回车结束)
      (cond
        ((eq frame "R")
         (initget "W O")
         (setq kw (getkword
                    (strcat "\n选择截图底色 [白底(W)/原底(O)] <"
                            (if (eq bg-mode "White") "白底" "原底")
                            ">: ")))
         (cond ((eq kw "O") (setq bg-mode "Original"))
               ((eq kw "W") (setq bg-mode "White")))
         (princ (strcat "\n  → 当前底色: "
                        (if (eq bg-mode "White") "白底（PNG 黑线白底）"
                                                 "原底（跟随 CAD 当前底色）")))
         T)                        ;; 继续循环再选方框
        (frame T)                  ;; 选中了实体 → 继续 body
        (T nil)))                  ;; 回车 → 退出循环
    ;; body: 只有 frame 是 entsel 列表时才处理
    (if (listp frame)
      (progn
        (setq frames (cons (car frame) frames))
        ;; 视觉反馈：高亮虚线 + 加入夹点选择集
        (redraw (car frame) 3)
        (ssadd (car frame) hl-ss)
        (sssetfirst nil hl-ss))))
  (setq frames (reverse frames))
  (if (null frames)
    (progn (princ "\n[取消] 未选任何方框") (sssetfirst nil nil) (exit)))
  (princ (strcat "\n共 " (itoa (length frames)) " 个方框（已高亮显示）"
                 "，底色: "
                 (if (eq bg-mode "White") "白底" "原底")))

  ;; v2.0.55: 不论 White / Original 都走"切白底 → PLOT/PNGOUT 出白底图 → crop-white"链路，
  ;;   原底模式末尾再调 (jt-plot-png ... "invert-only") 把整张 PNG 反色为黑底白线。
  ;;   理由：客户机若 PLOT 不可用会 fallback 到 PNGOUT 视口截屏，原底直接截屏=带视口黑边；
  ;;        改走白底链路后 crop-white 能精确贴框，再统一反色，原底也保证贴框无留黑。
  (setq bg-old (jt:bg-set-white))

  ;; v1.1.39: 临时关闭 ZOOM/VIEW 过渡动画，减少“闪屏”观感。
  (setq vt-old (getvar "VTENABLE"))
  (setvar "VTENABLE" 0)

  ;; v1.1.48: 临时关闭表格行列指示标尺（A/B/1/2…），避免被截进 PNG。
  (setq ti-old (vl-catch-all-apply 'getvar (list "TABLEINDICATOR")))
  (if (vl-catch-all-error-p ti-old) (setq ti-old nil))
  (vl-catch-all-apply 'setvar (list "TABLEINDICATOR" 0))
  ;; 同时清掉当前选择集（被选中的表格才会显示行列条）
  (sssetfirst nil nil)

  ;; v2.0.53: 不再调 CLEANSCREEN/VIEWRES（PLOT API 不依赖视口面积，避免隐藏功能区）
  (setq cs-old nil)
  (setq vr-old nil)

  ;; 2. 准备目录：放到系统 TEMP 下避免污染工作目录；命令开始清掉上一轮残留
  (setq dir (strcat (vl-string-right-trim "\\/" (getenv "TEMP")) "\\halou-jt\\"))
  (jt:ensure-dir dir)
  ;; 清掉上一轮 JT_*.png（不能在命令结束就删——剪贴板 CF_HDROP 是路径引用，
  ;; 微信/QQ 粘贴时才异步读，若立即删文件会粘贴失败）
  (foreach f (vl-directory-files dir "JT*.png" 1)
    (vl-catch-all-apply 'vl-file-delete (list (strcat dir f))))
  (setq full (strcat dir "JT.png"))
  (if (findfile full) (vl-file-delete full))

  ;; 3. 对每个方框：取 bbox → ssget → ZOOM → PNGOUT → 裁白 → 临时文件
  (setq tmp-pngs '() all-ents (ssadd) i 0 n (length frames))
  (foreach ent frames
    (setq i (1+ i))
    (setq tp (cdr (assoc 0 (entget ent))))
    (setq bb (vl-catch-all-apply 'jt:bbox (list ent)))
    (if (or (vl-catch-all-error-p bb) (null bb))
      (princ (strcat "\n[跳过] 第 " (itoa i) " 个 (" tp ") 取不到包围盒"))
      (progn
        (setq p1 (car bb) p2 (cadr bb))
        (if (or (<= (- (car p2) (car p1)) 1e-6)
                (<= (- (cadr p2) (cadr p1)) 1e-6))
          (princ (strcat "\n[跳过] 第 " (itoa i) " 个面积为 0"))
          (progn
            ;; v1.1.50: 先 ZOOM 到目标区域，再 ssget。
            ;; 原因：AutoCAD 的 ssget "_C" 对【不在当前视图内】的复杂实体（dimension、
            ;; leader、含属性块）有概率漏选，导致 PNGOUT 出图缺线/缺标注。
            ;; v1.1.51: 选区窗口紧贴 bbox（不再外扩），避免把相邻方框图纸的内容也吃进来。
            (setq padX (* 0.05 (- (car p2) (car p1)))
                  padY (* 0.05 (- (cadr p2) (cadr p1))))
            (setq pp1 (list (- (car p1) padX) (- (cadr p1) padY))
                  pp2 (list (+ (car p2) padX) (+ (cadr p2) padY)))
            ;; ★ 先 ZOOM_W：保证目标区域在当前视图内，让 ssget 不漏选
            (vl-catch-all-apply 'vl-cmdf (list "._ZOOM" "_W" pp1 pp2))
            ;; ssget 选区严格 = 方框 bbox（紧贴），避免越界吃到相邻图
            (setq ss (ssget "_C" p1 p2))
            (if (null ss)
              (progn
                (vl-catch-all-apply 'vl-cmdf (list "._ZOOM" "_P"))
                (princ (strcat "\n[跳过] 第 " (itoa i) " 个内部无对象")))
              (progn
                (setq sub (jt:ss-remove ss ent))
                (if (zerop (sslength sub))
                  (progn
                    (vl-catch-all-apply 'vl-cmdf (list "._ZOOM" "_P"))
                    (princ (strcat "\n[跳过] 第 " (itoa i) " 个除自身外无对象")))
                  (progn
                    ;; 单图临时文件
                    (setq single-png (strcat dir "JT_" (itoa i) ".png"))
                    (if (findfile single-png) (vl-file-delete single-png))
                    ;; v2.0.56: 固定走"白底链路"，PLOT 优先（精确），失败回退 PNGOUT (视口截屏+crop-white)。
                    (setq plot-ok nil)
                    (setq plot-media "Sun Hi-Res (1600.00 x 1280.00 Pixels)")
                    (setq plot-ok
                      (vl-catch-all-apply 'jt-plot-png
                        (list single-png
                              (car p1) (cadr p1) (car p2) (cadr p2)
                              plot-media)))
                    (if (vl-catch-all-error-p plot-ok) (setq plot-ok nil))
                    (if (not plot-ok)
                      (progn
                        ;; fallback: PNGOUT 视口截屏（白底，便于后续 crop-white）
                        (setq fd-old (getvar "FILEDIA"))
                        (setvar "FILEDIA" 0)
                        (vl-catch-all-apply 'vl-cmdf (list "._PNGOUT" single-png ss ""))
                        (setvar "FILEDIA" fd-old)))
                    (vl-catch-all-apply 'vl-cmdf (list "._ZOOM" "_P"))
                    (if (findfile single-png)
                      (progn
                        ;; PLOT 已精确；PNGOUT fallback 时需 crop-white 去掉视口横向白边
                        (if (not plot-ok) (jt-crop-white single-png))
                        (setq tmp-pngs (cons single-png tmp-pngs))
                        (princ (strcat "\n   √ 第 " (itoa i) "/" (itoa n) " 个出图完成 "
                                       (if plot-ok "(PLOT高清)" "(PNGOUT+crop)"))))
                      (princ (strcat "\n   × 第 " (itoa i) " 个出图失败"))))))))))))
  (setq tmp-pngs (reverse tmp-pngs))

  (if (null tmp-pngs)
    (progn (princ "\n[错误] 没有任何方框成功出图")
           (jt:bg-restore bg-old)
           (if vt-old (setvar "VTENABLE" vt-old))
           (if ti-old (vl-catch-all-apply 'setvar (list "TABLEINDICATOR" ti-old)))
           ;; v2.0.49: 恢复 cleanscreen / viewres
           (jt:cleanscreen-restore cs-old)
           (if (and vr-old (numberp vr-old))
             (vl-catch-all-apply 'vl-cmdf (list "._VIEWRES" "_Y" vr-old)))
           (exit)))

  ;; 4. 把所有 PNG 文件路径写入剪贴板（CF_HDROP，微信粘贴=分别多张图）
  (princ "\n[diag] 写入剪贴板（多文件 CF_HDROP）...")
  (if (apply 'jt-pngs-to-clipboard tmp-pngs)
    (princ (strcat "\n   √ 剪贴板已写入 " (itoa (length tmp-pngs)) " 张 PNG（分别）"))
    (princ "\n   × jt-pngs-to-clipboard 失败"))

  ;; v1.1.34: 不再嵌入 DWG（不需要 CAD 实体兼容），完全跳过 -WBLOCK，
  ;; 杜绝 v1.1.22~v1.1.32 那种"误删原图实体"的隐患。

  (princ "\n========================================")
  (princ (strcat "\n  共 " (itoa (length tmp-pngs)) " 张 PNG 已进剪贴板（分别）"))
  (princ "\n  Ctrl+V 到微信/QQ → 一次发送多张独立高清图")
  (princ "\n  PNG 文件:")
  (foreach p tmp-pngs (princ (strcat "\n    " p)))
  (princ "\n========================================")

  ;; 清除选中方框的高亮和夹点
  (foreach e frames (redraw e 4))
  (sssetfirst nil nil)

  ;; v1.1.39: 恢复原背景色 + ZOOM 过渡动画设置
  (jt:bg-restore bg-old)
  (if vt-old (setvar "VTENABLE" vt-old))
  ;; v1.1.48: 恢复表格行列指示标尺
  (if ti-old (vl-catch-all-apply 'setvar (list "TABLEINDICATOR" ti-old)))
  ;; v2.0.49: 恢复 cleanscreen 与 VIEWRES
  (jt:cleanscreen-restore cs-old)
  (if (and vr-old (numberp vr-old))
    (vl-catch-all-apply 'vl-cmdf (list "._VIEWRES" "_Y" vr-old)))

  (princ))

;;; 把选择集 ss 写到临时 .dwg 并嵌入到 PNG 的 tEXt chunk
;;; 注意：-WBLOCK 会从当前图删除选中实体，必须紧跟 OOPS 恢复
(defun jt:embed-entities (pngFull ss / tmpDwg fd-old ok)
  (setq tmpDwg (strcat (getvar "TEMPPREFIX") "jt_embed_" (jt:timestamp) ".dwg"))
  (if (= 0 (strlen (getvar "TEMPPREFIX")))
    (setq tmpDwg (strcat (getenv "TEMP") "\\jt_embed_" (jt:timestamp) ".dwg")))
  (if (findfile tmpDwg) (vl-file-delete tmpDwg))
  (setq fd-old (getvar "FILEDIA"))
  (setvar "FILEDIA" 0)
  ;; -WBLOCK: 文件名 → 默认 Objects（回车）→ 插入基点 0,0,0 → 选择集 → 结束
  (vl-catch-all-apply 'vl-cmdf (list "._-WBLOCK" tmpDwg "" "0,0,0" ss ""))
  ;; ★ 关键：WBLOCK 默认删除选中实体，立即 OOPS 恢复
  (vl-catch-all-apply 'vl-cmdf (list "._OOPS"))
  (setvar "FILEDIA" fd-old)
  (if (findfile tmpDwg)
    (progn
      (setq ok (jt-embed-dwg pngFull tmpDwg))
      (if ok
        (princ "\n   √ DWG 实体已嵌入 PNG（原图实体已通过 OOPS 恢复）")
        (princ "\n   × DWG 嵌入失败 (jt-embed-dwg 返回 nil)"))
      (vl-file-delete tmpDwg))
    (princ "\n   × WBLOCK 临时 DWG 创建失败，跳过实体嵌入")))

(princ "\n【已加载】JT 方框截图 v1.29 (PLOT高清/不隐藏功能区) - 命令: JT")
(princ)
