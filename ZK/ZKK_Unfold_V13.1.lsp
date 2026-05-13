;;; ============================================================
;;; ZKK V1.3  —  钣金截面展开 + 开缺
;;; 命令名: ZKK
;;; ============================================================
;;;
;;; ── 整体流程 ──
;;;   1. 选取闭合多段线（钣金截面）
;;;   2. 输入 板厚 / 扣减值
;;;   3. 分析截面外壁 → 得到展开段列
;;;   4. 选择分支:
;;;        Z(展开)  → 直接输出展开数据 + 开口刻线图
;;;        K(开缺)  → 绘制展开闭合矩形 → 选模板/方向/基点/行程线
;;;                   → 在矩形上下边画出缺口轮廓
;;;
;;; ── 开缺算法（V1.3 重构，依据 W/ZK/QQ20260422-052829-HD.mp4）──
;;;   位置: 由"基点 + 行径线"决定，沿行径线扫模板
;;;          - 行径线可多段拐角，逐段独立产出一个缺口区域
;;;          - 基点 = 模板上的锚点；行径线起点对应基点的世界位置
;;;          - 模板沿行径方向投影 → 得到该段缺口的"宽度跨度 + 深度剖面"
;;;   展开X: 缺口的世界端点投影到截面外壁最近段，再换算到 final-segments
;;;          累积长度 (跨过扣槽插入段)
;;;   扣减:  双边分摊，每端各扣 slot-width / 2
;;;   余量:  深度方向加 0.2，每个缺口只加一次（最深台阶上抬）
;;;          宽度方向不加余量
;;;   待澄清(留 TODO): 转角内边焦点垂直投影到外边的精细规则
;;;
;;; 无授权验证
;;; ============================================================

(vl-load-com)

;; ===================== 工具函数 =====================
;; 一些在全文中反复使用的小函数

;;; 判断实体是否存在（ent 非空 且能 entget）
(defun zkk:exists-p (ent) (and ent (entget ent)))

;;; 判断实体是否为曲线类型（LINE/ARC/LWPOLYLINE/POLYLINE）
(defun zkk:curve-p (ent / tp)
  (and (zkk:exists-p ent)
       (setq tp (cdr (assoc 0 (entget ent))))
       (member tp '("LINE" "ARC" "LWPOLYLINE" "POLYLINE"))))

;;; 判断是否为多段线
(defun zkk:pline-p (ent / tp)
  (and (zkk:exists-p ent)
       (setq tp (cdr (assoc 0 (entget ent))))
       (member tp '("LWPOLYLINE" "POLYLINE"))))

;;; 获取曲线总长度（利用 vlax-curve 函数）
;;; 返回长度数值，失败返回 nil
(defun zkk:clen (ent / ep)
  (if (and (zkk:curve-p ent)
           (not (vl-catch-all-error-p
                  (setq ep (vl-catch-all-apply 'vlax-curve-getEndParam (list ent)))))
           (numberp ep))
    (vlax-curve-getDistAtParam ent ep) nil))

;;; 批量删除实体列表
(defun zkk:del-ents (ents / e)
  (foreach e ents (if (zkk:exists-p e) (entdel e))))

;;; 将 VLA-VARIANT / SafeArray 转成普通 Lisp list（用于 BoundingBox 返回值）
(defun zkk:var->list (value / raw lv)
  (cond
    ((null value) nil) ((listp value) value)
    (T (setq raw (vl-catch-all-apply 'vlax-variant-value (list value)))
       (if (vl-catch-all-error-p raw) (setq raw value))
       (cond ((listp raw) raw)
             (T (setq lv (vl-catch-all-apply 'vlax-safearray->list (list raw)))
                (if (vl-catch-all-error-p lv) nil lv))))))

;;; 判断 v 是否为 2D 点 (x y)
(defun zkk:pt2d-p (v) (and (listp v) (numberp (car v)) (numberp (cadr v))))

;;; 强制转双精度 2D 点
(defun zkk:to2d (v)
  (if (zkk:pt2d-p v) (list (float (car v)) (float (cadr v))) nil))

;;; 安全的 acos，避免浮点超出 [-1,1] 时出错
(defun zkk:acos (x)
  (cond ((<= x -1.0) pi) ((>= x 1.0) 0.0)
        (T (atan (sqrt (- 1.0 (* x x))) x))))

;;; v1.1.63: 稳健「五舍六入」到 0.1
(defun zkk:round1 (v)
  (if (numberp v) (/ (fix (+ (* v 10.0) 0.5 1e-6)) 10.0) v))

;;; 实数 → 字符串（小数1位精度，去尾零，用于显示）
(defun zkk:r2s (value / text)
  (setq text (rtos (zkk:round1 value) 2 1))
  (while (and (vl-string-search "." text) (> (strlen text) 0)
              (= (substr text (strlen text) 1) "0"))
    (setq text (substr text 1 (1- (strlen text)))))
  (if (and (> (strlen text) 0) (= (substr text (strlen text) 1) "."))
    (setq text (substr text 1 (1- (strlen text)))))
  text)

;;; 实数 → 字符串（小数8位精度，去尾零，用于参数输入提示）
(defun zkk:r2sf (value / text)
  (setq text (rtos value 2 8))
  (while (and (vl-string-search "." text) (> (strlen text) 0)
              (= (substr text (strlen text) 1) "0"))
    (setq text (substr text 1 (1- (strlen text)))))
  (if (and (> (strlen text) 0) (= (substr text (strlen text) 1) "."))
    (setq text (substr text 1 (1- (strlen text)))))
  text)

;;; 提示用户输入实数，按回车取默认值
(defun zkk:ask-real (label default / s v)
  (setq s (getstring (strcat "\n" label " <" (zkk:r2sf default) ">: ")))
  (if (or (null s) (= s "")) default
    (progn (setq v (atof s)) (if (> v 0.0) v default))))

;;; 将数值列表用 sep 连接为字符串  如 (1 2 3) " " → "1 2 3"
(defun zkk:join-nums (vals sep / text first)
  (setq text "" first T)
  (foreach v vals
    (if (numberp v)
      (progn (if (not first) (setq text (strcat text sep)))
             (setq text (strcat text (zkk:r2s v))) (setq first nil))))
  text)

;;; 数值列表求和
(defun zkk:sum (vals / total)
  (setq total 0.0)
  (foreach v vals (if (numberp v) (setq total (+ total v)))) total)

;;; 复制一个实体，返回新实体 ename
(defun zkk:copy-ent (ent / co)
  (setq co (vl-catch-all-apply 'vla-copy (list (vlax-ename->vla-object ent))))
  (if (vl-catch-all-error-p co) nil (vlax-vla-object->ename co)))

;;; 获取 marker 之后新建的所有实体（用于追踪命令产生的新对象）
(defun zkk:ents-after (marker / ent result)
  (setq result nil ent (entnext marker))
  (while ent (setq result (cons ent result)) (setq ent (entnext ent)))
  (reverse result))

;;; 获取实体包围盒 → ((minX minY) (maxX maxY))，失败返回 nil
(defun zkk:ent-bbox (ent / obj mnv mxv mn mx)
  (setq obj (vlax-ename->vla-object ent))
  (vla-GetBoundingBox obj 'mnv 'mxv)
  (setq mn (zkk:var->list mnv) mx (zkk:var->list mxv))
  (if (and mn mx) (list mn mx) nil))

;; ===================== 配置参数 =====================
;; 默认配置表（alist）
;;
;; thickness     板厚 (mm)
;; slot-width    扣减值 (mm)，用于补偿折弯处的材料延展 / 挤压
;; short-threshold  “短边”判断阈值，<1.5mm 的短边被认为是板厚方向边
;; equal-tolerance  两短边“等长”判断容差
;; text-height   输出文字高度
;; ext-line-len  展开图的刷尺线长度（即矩形高度）
;; unfold-offset-y  展开图放在截面下方的 Y 偏移
;; offset-x      X 偏移
;; text-offset-y  文字相对截面底部的 Y 偏移

(defun zkk:def-cfg ()
  (list
    (cons 'thickness 0.76) (cons 'slot-width 0.7)
    (cons 'short-threshold 1.5) (cons 'equal-tolerance 0.01)
    (cons 'text-height 5.0) (cons 'ext-line-len 80.0)
    (cons 'unfold-offset-y -75.0)
    (cons 'offset-x -50.0) (cons 'text-offset-y -20.0)))

;;; 读取 cfg 中指定 key 的值
(defun zkk:cg (cfg key) (cdr (assoc key cfg)))

;;; 设置 cfg 中指定 key 的值（已有则替换，没有则追加）
(defun zkk:cs (cfg key val / pair)
  (setq pair (assoc key cfg))
  (if pair (subst (cons key val) pair cfg)
    (append cfg (list (cons key val)))))

;; ===================== 多段线点列 & 几何分析 =====================

;;; 提取 LWPOLYLINE 的所有 2D 顶点 → ((x1 y1 bulge1) (x2 y2 bulge2) ...)
;;; v1.1.52: bulge_i 表示从顶点 i 到顶点 i+1 那一段的凸度（无凸度=0=直线段）。
;;; DXF code 10 = 顶点坐标，紧跟其后的 code 42 = 该段 bulge（无 42 则默认 0）。
(defun zkk:lwpts (ent / data item pt pts last-pt)
  (setq pts nil data (entget ent))
  (foreach item data
    (cond
      ((= (car item) 10)
       (setq pt (zkk:to2d (cdr item)))
       (if pt (setq pts (append pts (list (list (car pt) (cadr pt) 0.0))))))
      ((= (car item) 42)
       ;; 把 bulge 写回最近一个顶点的第三元素
       (if pts
         (progn
           (setq last-pt (last pts))
           (setq pts (append (zkk:list-but-last pts)
                             (list (list (car last-pt) (cadr last-pt)
                                         (float (cdr item)))))))))))
  pts)

;;; 取出列表去掉最后一项
(defun zkk:list-but-last (lst / n res i)
  (setq n (length lst) i 0 res nil)
  (while (< i (1- n))
    (setq res (append res (list (nth i lst))))
    (setq i (1+ i)))
  res)

;;; 提取老式 POLYLINE（带 VERTEX 子实体）的顶点 → ((x y bulge) ...)
(defun zkk:legpts (ent / ne data pt b pts done)
  (setq pts nil done nil ne (entnext ent))
  (while (and ne (not done))
    (setq data (entget ne))
    (cond
      ((= (cdr (assoc 0 data)) "VERTEX")
       (setq pt (zkk:to2d (cdr (assoc 10 data))))
       (setq b (cdr (assoc 42 data)))
       (if (not (numberp b)) (setq b 0.0))
       (if pt (setq pts (append pts (list (list (car pt) (cadr pt) (float b)))))))
      ((= (cdr (assoc 0 data)) "SEQEND") (setq done T)))
    (if (not done) (setq ne (entnext ne))))
  pts)

;;; 统一接口：自动识别多段线类型并返回顶点列表
(defun zkk:getpts (ent / tp)
  (setq tp (cdr (assoc 0 (entget ent))))
  (cond ((= tp "LWPOLYLINE") (zkk:lwpts ent))
        ((= tp "POLYLINE") (zkk:legpts ent))
        (T nil)))

;;; 判断多段线是否闭合（宽松判断：标志位 / Closed属性 / 非零面积）
(defun zkk:closed-p (ent / data tp fl obj cp ar)
  (setq data (entget ent) tp (cdr (assoc 0 data)) fl (cdr (assoc 70 data)))
  (and (member tp '("LWPOLYLINE" "POLYLINE"))
       (or (and (numberp fl) (= 1 (logand fl 1)))
           (progn (setq obj (vlax-ename->vla-object ent))
                  (setq cp (vl-catch-all-apply 'vlax-get (list obj 'Closed)))
                  (and (not (vl-catch-all-error-p cp)) (/= cp :vlax-false)))
           (progn (setq obj (vlax-ename->vla-object ent))
                  (setq ar (vl-catch-all-apply 'vlax-get (list obj 'Area)))
                  (and (not (vl-catch-all-error-p ar)) (numberp ar) (> ar 1e-8))))))

;;; 严格判断多段线闭合（仅看标志位 / Closed属性，不看面积）
(defun zkk:strict-closed-p (ent / data tp fl obj cp)
  (setq data (entget ent) tp (cdr (assoc 0 data)) fl (cdr (assoc 70 data)))
  (and (member tp '("LWPOLYLINE" "POLYLINE"))
       (or (and (numberp fl) (= 1 (logand fl 1)))
           (progn (setq obj (vlax-ename->vla-object ent))
                  (setq cp (vl-catch-all-apply 'vlax-get (list obj 'Closed)))
                  (and (not (vl-catch-all-error-p cp)) (/= cp :vlax-false))))))

;;; 去除相邻重复点（距离 < 1e-6）
(defun zkk:dedup-pts (pts / res prev pt)
  (setq res nil prev nil)
  (foreach pt pts
    (if (or (null prev) (not (zkk:pt2d-p prev)) (not (zkk:pt2d-p pt))
            (> (distance prev pt) 1e-6))
      (setq res (append res (list pt))))
    (setq prev pt))
  res)

;;; 去除共线点（三点共线则去掉中间点）。cl = 是否闭合
;;; v1.1.52: 若 prev 或 curr 自带 bulge≠0（圆弧段），强制保留，避免吞掉弧。
(defun zkk:rm-collin (pts cl / res n i prev curr next cv s1 s2 bp bc)
  (setq n (length pts))
  (if (< n 3) pts
    (progn
      (setq res nil i 0)
      (while (< i n)
        (setq curr (nth i pts))
        (cond
          ((and (not cl) (or (= i 0) (= i (1- n))))
           (setq res (append res (list curr))))
          (T (if cl
               (progn (setq prev (nth (if (= i 0) (1- n) (1- i)) pts))
                      (setq next (nth (if (= i (1- n)) 0 (1+ i)) pts)))
               (progn (setq prev (nth (1- i) pts))
                      (setq next (nth (1+ i) pts))))
             (setq bp (caddr prev) bc (caddr curr))
             (setq cv (abs (- (* (- (car curr) (car prev)) (- (cadr next) (cadr prev)))
                              (* (- (cadr curr) (cadr prev)) (- (car next) (car prev))))))
             (setq s1 (distance prev curr) s2 (distance curr next))
             (if (or (<= s1 1e-8) (<= s2 1e-8) (> cv (* 0.001 s1 s2))
                     (and (numberp bp) (> (abs bp) 1e-9))   ; prev→curr 是圆弧
                     (and (numberp bc) (> (abs bc) 1e-9)))  ; curr→next 是圆弧
               (setq res (append res (list curr))))))
        (setq i (1+ i)))
      (if (< (length res) 3) pts res))))

;;; 计算多边形的有符号面积（shoelace 公式）
;;; 正值 = 逆时针(CCW)，负值 = 顺时针(CW)
(defun zkk:sarea (pts / area i ni p1 p2)
  (setq area 0.0 i 0)
  (while (< i (length pts))
    (setq p1 (nth i pts) ni (if (= (1+ i) (length pts)) 0 (1+ i)) p2 (nth ni pts))
    (if (and p1 p2)
      (setq area (+ area (- (* (car p1) (cadr p2)) (* (cadr p1) (car p2))))))
    (setq i (1+ i)))
  (/ area 2.0))

;;; 计算每段的长度列表。cl = 是否闭合
;;; v1.1.52: 若顶点带 bulge（第三元素，非 0），则按【弧长】计算；否则按弦长。
;;; 弧长公式：θ = 4·atan|bulge|；R = chord / (2·sin(θ/2))；arc = R·θ
(defun zkk:seglens (pts cl / n i ni cp np sl chord b theta segs)
  (setq n (length pts) i 0 segs nil)
  (while (< i n)
    (setq cp (nth i pts))
    (cond ((and cl (= i (1- n))) (setq ni 0))
          ((< (1+ i) n) (setq ni (1+ i)))
          (T (setq ni nil)))
    (setq np (if ni (nth ni pts) nil))
    (if (and (zkk:pt2d-p cp) (zkk:pt2d-p np))
      (progn
        (setq chord (distance cp np))
        (setq b (caddr cp))
        (if (and (numberp b) (> (abs b) 1e-9) (> chord 1e-9))
          (progn
            (setq theta (* 4.0 (atan (abs b))))
            (setq sl (/ (* chord theta) (* 2.0 (sin (/ theta 2.0))))))
          (setq sl chord)))
      (setq sl nil))
    (if (and (numberp sl) (> sl 1e-8))
      (setq segs (append segs (list sl))))
    (setq i (1+ i)))
  segs)

;;; 直接从实体获取段长度列表
(defun zkk:ent-seglens (ent)
  (zkk:seglens (zkk:getpts ent) (zkk:closed-p ent)))

;; ===================== 截面外壁提取 =====================
;; 核心思路：
;;   钣金截面是闭合多段线，其中有两条“短边”代表板厚方向。
;;   找到这两条短边后，将截面分成两个半弧：
;;   外壁（较长的那个）和内壁。取外壁作为展开数据。

;;; 在段长度列表中找一对“最短且近似等长”的边（即板厚方向的两条边）
;;; sthr = 短边阈值，etol = 等长容差
;;; 返回 ((idx1 len1) (idx2 len2)) 或 nil
(defun zkk:find-pair (segs sthr etol / indexed si pair i)
  (setq indexed nil i 0)
  (foreach sl segs (setq indexed (cons (list i sl) indexed)) (setq i (1+ i)))
  (setq si (vl-sort
    (vl-remove-if-not '(lambda (e) (and (numberp (cadr e)) (< (cadr e) sthr))) indexed)
    '(lambda (a b) (< (cadr a) (cadr b)))))
  (setq pair nil)
  (while (and (null pair) si (cdr si))
    (if (<= (abs (- (cadr (car si)) (cadr (cadr si)))) etol)
      (setq pair (list (car si) (cadr si))) (setq si (cdr si))))
  pair)

;;; 根据索引列表收集对应的段长度
(defun zkk:coll-segs (segs elist / res sl)
  (setq res nil)
  (foreach ei elist
    (setq sl (nth ei segs)) (if (numberp sl) (setq res (append res (list sl)))))
  res)

;;; 获取第 ei 条边的终点
(defun zkk:edge-ep (pts ei / n ni)
  (setq n (length pts) ni (1+ ei))
  (if (>= ni n) (setq ni 0)) (nth ni pts))

;;; 用边索引列表构建一条路径：点序 + 段长序
;;; 返回 (pts-list segs-list) 或 nil
(defun zkk:bld-path (pts segs elist / fe pp ps ep)
  (if (or (null elist) (null pts)) nil
    (progn
      (setq fe (car elist) pp nil)
      (if (zkk:pt2d-p (nth fe pts)) (setq pp (list (nth fe pts))))
      (setq ps (zkk:coll-segs segs elist))
      (foreach ei elist
        (setq ep (zkk:edge-ep pts ei))
        (if (and pp (zkk:pt2d-p ep)) (setq pp (append pp (list ep)))))
      (if (and pp ps (= (length pp) (1+ (length ps)))) (list pp ps) nil))))

;;; 生成 s..e 的连续索引列表 (s s+1 s+2 .. e)
(defun zkk:erange (s e / res i)
  (setq res nil)
  (if (<= s e) (progn (setq i s) (while (<= i e) (setq res (append res (list i))) (setq i (1+ i)))))
  res)

;;; 环形索引列表（从 s 到 e，在模 ec 下循环，不含 e）
(defun zkk:wrange (s e ec / res i)
  (setq res nil)
  (if (> ec 0)
    (progn
      (setq i (rem s ec)) (if (< i 0) (setq i (+ i ec)))
      (setq e (rem e ec)) (if (< e 0) (setq e (+ e ec)))
      (while (/= i e)
        (setq res (append res (list i)))
        (setq i (1+ i)) (if (>= i ec) (setq i 0)))))
  res)

;;; 两条路径取总长较大的那个（外壁总比内壁长）
(defun zkk:pick-outer (pa pb / la lb)
  (cond ((null pa) pb) ((null pb) pa)
    (T (setq la (zkk:sum (cadr pa)) lb (zkk:sum (cadr pb)))
       (if (>= la lb) pa pb))))

;;; 主分析入口：从闭合多段线中提取外壁路径
;;; 返回 (pts segs) —— 外壁点序 + 段长序
;;; 流程：
;;;   1. 提取点 → 去重 → 去共线 → 算段长
;;;   2. 找两条短边（板厚方向）
;;;   3. 以两短边为分割点，格外壁路径 (pts + segs)
(defun zkk:extract-wl (ent cfg /
  pts segs cl ec pair ia ib ea eb ec2 pa pb pc best)
  (setq pts (zkk:dedup-pts (zkk:getpts ent)))
  (setq cl (zkk:closed-p ent))
  (setq pts (zkk:rm-collin pts cl))
  (setq segs (zkk:seglens pts cl))
  (setq ec (length segs))
  (setq pair (zkk:find-pair segs (zkk:cg cfg 'short-threshold) (zkk:cg cfg 'equal-tolerance)))
  (if (or (null pair) (< (length pts) 2) (< ec 3)) nil
    (progn
      (setq ia (car (nth 0 pair)) ib (car (nth 1 pair)))
      (if (> ia ib)
        (progn (setq ia (+ ia ib)) (setq ib (- ia ib)) (setq ia (- ia ib))))
      (if cl
        (progn
          (setq ea (zkk:wrange (1+ ia) ib ec) eb (zkk:wrange (1+ ib) ia ec))
          (setq pa (zkk:bld-path pts segs ea) pb (zkk:bld-path pts segs eb))
          (setq best (zkk:pick-outer pa pb)))
        (progn
          (setq ea (zkk:erange 0 (1- ia))
                eb (zkk:erange (1+ ia) (1- ib))
                ec2 (zkk:erange (1+ ib) (1- ec)))
          (setq pa (zkk:bld-path pts segs ea)
                pb (zkk:bld-path pts segs eb)
                pc (zkk:bld-path pts segs ec2))
          (setq best (zkk:pick-outer pa pb))
          (setq best (zkk:pick-outer best pc))))
      best)))

;;; v1.1.72: 反转含 bulge 的（开放）点序列，保持 bulge 与段对齐。
;;; 原 pts[i].bulge 表示 pts[i]→pts[i+1] 段的 bulge；
;;; 反转后新段 new_pts[k]→new_pts[k+1] 对应原段 (n-2-k) 反向，bulge 应取负。
;;; 末点（k = n-1）无段，bulge 置 nil。
(defun zkk:reverse-pts-bulge (pts / n rp i curr nb new-pts)
  (setq n (length pts) rp (reverse pts) new-pts nil i 0)
  (while (< i n)
    (setq curr (nth i rp))
    (if (< i (1- n))
      (setq nb (caddr (nth (1+ i) rp)))
      (setq nb nil))
    (setq new-pts
      (append new-pts
        (list (list (car curr) (cadr curr)
                    (if (and (numberp nb) (> (abs nb) 1e-9)) (- nb) nil)))))
    (setq i (1+ i)))
  new-pts)

;;; 将点序调整为逆时针方向（CCW）
(defun zkk:norm-ccw (pts segs / area)
  (if (and (listp pts) (> (length pts) 2)
           (= (length pts) (1+ (length segs)))
           (not (vl-some '(lambda (p) (not (zkk:pt2d-p p))) pts)))
    (progn (setq area (zkk:sarea pts))
           (if (< area 0.0) (list (zkk:reverse-pts-bulge pts) (reverse segs)) (list pts segs)))
    (list pts segs)))

;;; 根据原始截面的绕向来决定外壁点序方向
;;; （保证外壁点序与原截面一致）
(defun zkk:norm-dir (src pts segs / sp sa)
  (if (zkk:closed-p src)
    (progn (setq sp (zkk:getpts src))
           (if (and sp (> (length sp) 2))
             (progn (setq sa (zkk:sarea sp))
                    (if (< sa 0.0) (list (zkk:reverse-pts-bulge pts) (reverse segs)) (list pts segs)))
             (zkk:norm-ccw pts segs)))
    (zkk:norm-ccw pts segs)))

;; ===================== 凸角判断 & 扣槽补偿 =====================
;; 这里的“槽”指钣金折弯处的工艺扣减/补加。
;; 凸角(convex) → 扣减半个槽宽
;; 凹角(concave) → 补加半个槽宽
;; 锐角 (<70°) 的凸角处额外插入一个槽宽段

;;; 判断角 pp→pc→pn 是否为凸角
;;; sa = 多边形有符号面积，用于确定"外侧"方向
(defun zkk:convex-p (sa pp pc pn / v1x v1y v2x v2y cp)
  (if (and (zkk:pt2d-p pp) (zkk:pt2d-p pc) (zkk:pt2d-p pn))
    (progn
      (setq v1x (- (car pc) (car pp)) v1y (- (cadr pc) (cadr pp))
            v2x (- (car pn) (car pc)) v2y (- (cadr pn) (cadr pc))
            cp (- (* v1x v2y) (* v1y v2x)))
      (> (* sa cp) 0.0001)) nil))

;;; v1.4.1 修订：判定截面"首末段邻接平行 + 间隙 < 1mm"
;;; pts 为外壁点序（长度 = sc+1，首点 pts[0]，末点 pts[sc]）
;;; 兼容两种几何拓扑：
;;;   A) 闭合截面 / 首末点重合：d(pts[0], pts[sc]) < 1mm
;;;      （例：方管、首末点同位）
;;;   B) 开口对接（Z 型 / C 型外壁互向）：d(pts[1], pts[sc-1]) < 1mm
;;;      （例：本图 Z 型 —— 顶部两段 13.9 的内端点相隔 0.2mm）
;;; 任一拓扑满足后，再校验首段 (pts[0]→pts[1]) 与末段 (pts[sc-1]→pts[sc])
;;; 方向近似平行：|cross| / (|v1|*|v2|) < 0.05 (≈ 2.9°)
;;; 返回 T / nil
(defun zkk:closed-parallel-p (pts / n p0 pe p1 pm dA dB v1x v1y v2x v2y l1 l2 cross ok)
  (setq ok nil n (length pts))
  (if (>= n 3)
    (progn
      (setq p0 (nth 0 pts)
            pe (nth (1- n) pts)
            p1 (nth 1 pts)
            pm (nth (- n 2) pts))
      (if (and (zkk:pt2d-p p0) (zkk:pt2d-p pe)
               (zkk:pt2d-p p1) (zkk:pt2d-p pm))
        (progn
          (setq dA (distance (list (car p0) (cadr p0))
                             (list (car pe) (cadr pe)))
                dB (distance (list (car p1) (cadr p1))
                             (list (car pm) (cadr pm))))
          (if (or (< dA 1.0) (< dB 1.0))
            (progn
              (setq v1x (- (car p1) (car p0)) v1y (- (cadr p1) (cadr p0))
                    v2x (- (car pe) (car pm)) v2y (- (cadr pe) (cadr pm))
                    l1 (sqrt (+ (* v1x v1x) (* v1y v1y)))
                    l2 (sqrt (+ (* v2x v2x) (* v2y v2y))))
              (if (and (> l1 1e-6) (> l2 1e-6))
                (progn
                  (setq cross (abs (- (* v1x v2y) (* v1y v2x))))
                  (if (< cross (* 0.05 l1 l2)) (setq ok T))))))))))
  ok)

;;; 计算角 pp→pc→pn 的折弯角度（度，0°=石线, 90°=直角）
(defun zkk:bangle (pp pc pn / v1x v1y v2x v2y dp m1 m2 ca)
  (if (and (zkk:pt2d-p pp) (zkk:pt2d-p pc) (zkk:pt2d-p pn))
    (progn
      (setq v1x (- (car pc) (car pp)) v1y (- (cadr pc) (cadr pp))
            v2x (- (car pn) (car pc)) v2y (- (cadr pn) (cadr pc))
            dp (+ (* v1x v2x) (* v1y v2y))
            m1 (distance '(0.0 0.0) (list v1x v1y))
            m2 (distance '(0.0 0.0) (list v2x v2y)))
      (if (> (* m1 m2) 0.0001)
        (progn (setq ca (/ dp (* m1 m2)))
               (if (> ca 1.0) (setq ca 1.0)) (if (< ca -1.0) (setq ca -1.0))
               (- 180.0 (* (zkk:acos ca) (/ 180.0 pi)))) nil)) nil))

;;; 对每个展开段进行扣槽补偿
;;; 输入: pts=外壁点序, segs=原始段长, cfg=配置
;;; 返回: 扣减/补加后的段长列表（可能比 segs 多，因为锐角会插入额外段）
;;;
;;; v1.4.0：当截面首末段"重合平行且间隙<1mm"（zkk:closed-parallel-p 为 T）时，
;;;          头段(i=0)和尾段(i=sc-1)按"接折弯一端"扣减一次 single（= slot-width/2），
;;;          凸凹判断分别用：
;;;            头段: (pts[0], pts[1], pts[2])
;;;            尾段: (pts[sc-2], pts[sc-1], pts[sc])
;;;          条件不满足时维持原逻辑（首末段不扣）。
(defun zkk:slot-deduct (pts segs cfg /
  sv single iv sa sc i sl av bs fs pp pc pn ba closed skip-end)
  (setq sv (zkk:cg cfg 'slot-width))
  (if (or (null sv) (<= sv 0.0)) segs
    (progn
      (setq single (/ sv 2.0) iv sv sa (zkk:sarea pts) sc (length segs) bs nil i 0
            closed (zkk:closed-parallel-p pts))
      (princ (strcat "\n[DBG] closed-parallel-p=" (if closed "T" "nil")))
      (while (< i sc)
        (setq sl (nth i segs))
        ;; 跳过条件：非数字 / 圆弧段 / (非闭合时的首末段)
        (setq skip-end (and (not closed) (or (= i 0) (= i (1- sc)))))
        (if (or (not (numberp sl)) skip-end
                (and (caddr (nth i pts))
                     (numberp (caddr (nth i pts)))
                     (> (abs (caddr (nth i pts))) 1e-6)))
          (setq bs (append bs (list sl)))
          (progn
            (setq av 0.0)
            (cond
              ;; 头段：闭合截面下只看 pts[0]->pts[1]->pts[2] 一端
              ((= i 0)
               (setq pp (nth i pts) pc (nth (1+ i) pts) pn (nth (+ i 2) pts))
               (if (zkk:convex-p sa pp pc pn)
                 (setq av (- av single)) (setq av (+ av single))))
              ;; 尾段：闭合截面下只看 pts[sc-2]->pts[sc-1]->pts[sc] 一端
              ((= i (1- sc))
               (setq pp (nth (1- i) pts) pc (nth i pts) pn (nth (1+ i) pts))
               (if (zkk:convex-p sa pp pc pn)
                 (setq av (- av single)) (setq av (+ av single))))
              ;; 中间段：两端各扣一次 single
              (T
               (setq pp (nth (1- i) pts) pc (nth i pts) pn (nth (1+ i) pts))
               (if (zkk:convex-p sa pp pc pn)
                 (setq av (- av single)) (setq av (+ av single)))
               (setq pp (nth i pts) pc (nth (1+ i) pts) pn (nth (+ i 2) pts))
               (if (zkk:convex-p sa pp pc pn)
                 (setq av (- av single)) (setq av (+ av single)))))
            (setq sl (+ sl av)) (if (< sl 0.0) (setq sl 0.0))
            (setq bs (append bs (list sl)))))
        (setq i (1+ i)))
      (setq fs nil i 0)
      (while (< i sc)
        (setq fs (append fs (list (nth i bs))))
        (if (< (1+ i) (1- (length pts)))
          (progn
            (setq pp (nth i pts) pc (nth (1+ i) pts) pn (nth (+ i 2) pts))
            (if (zkk:convex-p sa pp pc pn)
              (progn (setq ba (zkk:bangle pp pc pn))
                     (if (and (numberp ba) (< ba 70.0))
                       (setq fs (append fs (list iv))))))))
        (setq i (1+ i)))
      fs)))

;; ===================== 后处理（尾插入 + 方向判断） =====================
;;
;; 如果首尾段都 < 8mm，追加一个 8mm 补尾
;; 根据首尾段长度决定“头尾颜色”和“计算方向”
;;   头色 hc / 尾色 tc:  2=黄  3=绿
;;   calc-direction: HEAD_TO_TAIL 或 TAIL_TO_HEAD

(defun zkk:postproc (segs / fs nti sc f1 fl r1 rl hc tc cd)
  (setq fs segs nti nil sc (length fs))
  ;; v1.1.62: 8mm 阈值使用四舍五入到0.1后的值
  (if (>= sc 2)
    (progn (setq f1 (car fs) fl (nth (1- sc) fs))
           (if (and (numberp f1) (numberp fl))
             (progn (setq r1 (zkk:round1 f1) rl (zkk:round1 fl))
                    (if (and (< r1 8.0) (< rl 8.0))
                      (progn (setq fs (append fs (list 8.0))) (setq nti T)))))))
  (setq sc (length fs)
        f1 (if (> sc 0) (car fs) nil)
        fl (if (> sc 0) (nth (1- sc) fs) nil)
        r1 (if (numberp f1) (zkk:round1 f1) nil)
        rl (if (numberp fl) (zkk:round1 fl) nil))
  (cond
    ((and (numberp r1) (numberp rl) (>= r1 8.0) (>= rl 8.0))
     (setq hc 2 tc 3 cd 'TAIL_TO_HEAD))
    ((and (numberp r1) (numberp rl) (< r1 8.0) (>= rl 8.0))
     (setq hc 2 tc 3 cd 'TAIL_TO_HEAD))
    ((and (numberp r1) (numberp rl) (>= r1 8.0) (< rl 8.0))
     (setq hc 3 tc 2 cd 'HEAD_TO_TAIL))
    (T (setq hc 2 tc 3 cd 'TAIL_TO_HEAD)))
  (list (cons 'segments fs) (cons 'need-tail-insert nti)
        (cons 'head-color hc) (cons 'tail-color tc)
        (cons 'calc-direction cd)))

;; ===================== 文字输出 =====================
;;
;; 生成三行 MText:
;;   第1行: 板厚/总长
;;   第2行: 各段长度(空格分隔)
;;   第3行: 累计尺寸(+分隔)

(defun zkk:build-text (cfg segs pdata / th raw-total l1 l2 l3 ts s cd nti osegs n i v)
  (setq th (zkk:cg cfg 'thickness)
        cd (cdr (assoc 'calc-direction pdata))
        nti (cdr (assoc 'need-tail-insert pdata))
        ;; v1.1.60: l2/l3 始终从黄端开始输数
        osegs (if (eq cd 'HEAD_TO_TAIL) (reverse segs) segs)
        ;; v1.1.74: 总长与累加链都用原始未舍入的段长，保证与 unfold 几何总长一致
        raw-total (apply '+ (vl-remove-if-not 'numberp osegs)))
  (setq l1 (strcat (zkk:r2sf th) "/" (zkk:r2s raw-total)))
  (setq l2 (zkk:join-nums osegs "  "))
  ;; v1.1.61: 补尾 8mm 段附加「刨槽」标记
  (if nti (setq l2 (strcat l2 "刨槽")))
  ;; v1.1.74: l3 同序输出 raw-total - cum（原始段长累加，最后 r2s 显示）
  (setq n (length osegs) i 0 ts 0.0 l3 "")
  (foreach v osegs
    (setq i (1+ i))
    (if (numberp v)
      (progn (setq ts (+ ts v))
             (if (< i n)
               (progn (setq s (zkk:r2s (- raw-total ts)))
                      (if (= l3 "") (setq l3 s) (setq l3 (strcat l3 "+" s))))))))
  (strcat l1 "\\P" l2 "\\P" l3))

;;; 在截面的左下方绘制 MText
(defun zkk:draw-mtext (src content cfg / obj mnp mxp ip)
  (setq obj (vlax-ename->vla-object src))
  (vla-GetBoundingBox obj 'mnp 'mxp)
  (setq mnp (zkk:var->list mnp))
  (setq ip (list (+ (car mnp) (zkk:cg cfg 'offset-x))
                 (+ (cadr mnp) (zkk:cg cfg 'text-offset-y)) 0.0))
  (entmakex
    (list '(0 . "MTEXT") '(100 . "AcDbEntity") '(100 . "AcDbMText")
          (cons 10 ip) (cons 40 (zkk:cg cfg 'text-height))
          (cons 1 content) '(7 . "Standard") '(71 . 1) '(72 . 5) '(50 . 0.0))))

;; ===================== 绘图函数 =====================

;;; 创建闭合多段线（矩形等）
;;; v1.1.52: 只取 pt 前两元素，防止 bulge 误写入 DXF code 10 的 z 字段
(defun zkk:mk-closed-pl (pts / dd)
  (if (and pts (> (length pts) 2))
    (progn (setq dd (list '(0 . "LWPOLYLINE") '(100 . "AcDbEntity")
                          '(100 . "AcDbPolyline")
                          (cons 90 (length pts)) '(70 . 1)))
           (foreach pt pts
             (setq dd (append dd (list (cons 10 (list (car pt) (cadr pt)))))))
           (entmakex dd))))

;;; 创建开口多段线
(defun zkk:mk-open-pl (pts / dd)
  (if (and pts (> (length pts) 1))
    (progn (setq dd (list '(0 . "LWPOLYLINE") '(100 . "AcDbEntity")
                          '(100 . "AcDbPolyline")
                          (cons 90 (length pts)) '(70 . 0)))
           (foreach pt pts
             (setq dd (append dd (list (cons 10 (list (car pt) (cadr pt)))))))
           (entmakex dd))))

;; ---- Z 分支：展开图（开口线 + 刷尺线） ----
;; 从截面 BBox 左下向下偏移，画两条水平线（上缘 + 下缘）
;; 之间用竖线连接，颜色区分头/尾/中间
(defun zkk:draw-unfold-open (src segs cfg pdata /
  obj mnp mxp us tps bps lp np by ell nn ni pt pb lc hc tc)
  (if (null segs) nil
    (progn
      (setq obj (vlax-ename->vla-object src))
      (vla-GetBoundingBox obj 'mnp 'mxp)
      (setq mnp (zkk:var->list mnp))
      (setq us (list (+ (car mnp) (zkk:cg cfg 'offset-x))
                     (+ (cadr mnp) (zkk:cg cfg 'unfold-offset-y))))
      (setq tps (list us) lp us by (cadr us))
      (foreach sl segs
        (if (numberp sl)
          (progn (setq np (list (+ (car lp) sl) by))
                 (setq tps (append tps (list np))) (setq lp np))))
      (setq ell (zkk:cg cfg 'ext-line-len) bps nil)
      (foreach pt tps
        (setq bps (append bps (list (list (car pt) (- (cadr pt) ell))))))
      (zkk:mk-open-pl tps)
      (zkk:mk-open-pl bps)
      (setq hc (cdr (assoc 'head-color pdata)) tc (cdr (assoc 'tail-color pdata))
            nn (length tps) ni 0)
      (while (< ni nn)
        (setq pt (nth ni tps) pb (nth ni bps))
        (cond ((= ni 0) (setq lc hc)) ((= ni (1- nn)) (setq lc tc)) (T (setq lc 1)))
        (entmakex (list '(0 . "LINE") '(100 . "AcDbEntity") '(100 . "AcDbLine")
                        (cons 10 (list (car pt) (cadr pt) 0.0))
                        (cons 11 (list (car pb) (cadr pb) 0.0))
                        (cons 62 lc)))
        (setq ni (1+ ni))))))

;; ---- K 分支：展开图（闭合矩形 + 刷尺线） ----
;; 与 Z 分支类似，但画一个闭合矩形（而不是开口线）
;; 返回 rect-info: (rect-ent . ename) (top-y . Y) (bottom-y . Y) (left-x . X) (right-x . X)
;; 后续开缺流程会删除这个矩形，在原位置画带缺口的轮廓
(defun zkk:draw-unfold-closed (src segs cfg pdata /
  obj mnp mxp us tps bps lp np by ell nn ni pt pb lc hc tc rect-ent)
  (if (null segs) nil
    (progn
      (setq obj (vlax-ename->vla-object src))
      (vla-GetBoundingBox obj 'mnp 'mxp)
      (setq mnp (zkk:var->list mnp))
      (setq us (list (+ (car mnp) (zkk:cg cfg 'offset-x))
                     (+ (cadr mnp) (zkk:cg cfg 'unfold-offset-y))))
      (setq tps (list us) lp us by (cadr us))
      (foreach sl segs
        (if (numberp sl)
          (progn (setq np (list (+ (car lp) sl) by))
                 (setq tps (append tps (list np))) (setq lp np))))
      (setq ell (zkk:cg cfg 'ext-line-len) bps nil)
      (foreach pt tps
        (setq bps (append bps (list (list (car pt) (- (cadr pt) ell))))))
      ;; closed rect: TL -> TR -> BR -> BL
      (setq rect-ent
        (zkk:mk-closed-pl
          (list (car tps) (nth (1- (length tps)) tps)
                (nth (1- (length bps)) bps) (car bps))))
      ;; tick marks
      (setq hc (cdr (assoc 'head-color pdata)) tc (cdr (assoc 'tail-color pdata))
            nn (length tps) ni 0)
      (while (< ni nn)
        (setq pt (nth ni tps) pb (nth ni bps))
        (cond ((= ni 0) (setq lc hc)) ((= ni (1- nn)) (setq lc tc)) (T (setq lc 1)))
        (entmakex (list '(0 . "LINE") '(100 . "AcDbEntity") '(100 . "AcDbLine")
                        (cons 10 (list (car pt) (cadr pt) 0.0))
                        (cons 11 (list (car pb) (cadr pb) 0.0))
                        (cons 62 lc)))
        (setq ni (1+ ni)))
      (list (cons 'rect-ent rect-ent)
            (cons 'top-y by)
            (cons 'bottom-y (- by ell))
            (cons 'left-x (car (car tps)))
            (cons 'right-x (car (nth (1- (length tps)) tps)))))))

;; ===================== 截面分析 =====================
;; 串联上面所有分析步骤，返回一个 alist：
;;   points        — 外壁点序（已规范化方向）
;;   segments       — 原始外壁段长
;;   final-segments — 扣槽后段长（可能带插入段）
;;   paocao-data    — 后处理数据（头尾颜色/方向/补尾标记）

(defun zkk:analyze (src cfg / wd pts segs nd os pd fs)
  (setq wd (zkk:extract-wl src cfg))
  (if (null wd)
    (progn
      (princ "\n[ERROR] 无法提取外壁数据,请检查截面线")
      (princ (strcat "\n[INFO] " (zkk:join-nums (zkk:ent-seglens src) "  ")))
      nil)
    (progn
      (setq pts (car wd) segs (cadr wd))
      (if (null segs)
        (progn (princ "\n[ERROR] 段列为空") nil)
        (progn
          (setq nd (zkk:norm-dir src pts segs))
          (setq pts (car nd) segs (cadr nd))
          (setq os (zkk:slot-deduct pts segs cfg))
          (setq pd (zkk:postproc os))
          (setq fs (cdr (assoc 'segments pd)))
          (list (cons 'points pts) (cons 'segments segs)
                (cons 'final-segments fs) (cons 'paocao-data pd)))))))

;; ===================== Z 分支：展开 =====================
;; 绘制开口展开图 + 输出文字

(defun zkk:do-unfold (src cfg ana / fs pd mc)
  (setq fs (cdr (assoc 'final-segments ana))
        pd (cdr (assoc 'paocao-data ana)))
  (setq mc (zkk:build-text cfg fs pd))
  (zkk:draw-unfold-open src fs cfg pd)
  (zkk:draw-mtext src mc cfg)
  (princ (strcat "\n[OK] 展开总长: "
                 (zkk:r2s (zkk:sum fs)))))

;; ===================== K 分支：开缺 (V1.3 重构) =====================
;;
;; 算法依据 W/ZK/QQ20260422-052829-HD.mp4
;;
;; 核心模型：
;;   - 模板(开缺料): 任意闭合多段线，定义缺口几何剖面
;;   - 基点: 用户在模板上点选(语义=第一投影角)
;;   - 行径线: 用户画的多段线(可拐角)，决定模板沿什么方向、扫到哪里
;;   - 模式 S/X/Q: 上开缺 / 下开缺 / 上下开缺
;;
;; 数学模型：
;;   1. 模板用基点平移到原点 → tmpl-rel
;;   2. 行径线分解为线段序列 (p[i] → p[i+1])
;;   3. 对每段：
;;        dir = unit(p[i+1] - p[i])
;;        把 tmpl-rel 沿 (dir, normal) 投影
;;          → t-min, t-max  (沿行径方向跨度 = 缺口宽方向)
;;          → n-depth       (法向最大绝对值 = 缺口切入深度)
;;        模板扫过区域两端世界坐标:
;;          ws-start = p[i]   + t-min * dir
;;          ws-end   = p[i+1] + t-max * dir
;;        把 ws-start / ws-end 投影到截面外壁最近段
;;          → 展开X (跨过 slot-deduct 在锐凸角插入的肩段)
;;        扣减: 双边各扣 slot-width / 2
;;        余量: 该缺口深度 + 0.2 (一次)
;;   4. 在展开矩形上下边按缺口列表绘制
;;
;; 简化假设(留 TODO，等用户在 CAD 实测后再迭代)：
;;   - 当前每段行径线只产生一个矩形缺口(单一深度)，多台阶剖面不展开
;;   - 转角处"内边焦点垂直投影到外边"的精细规则未实现
;;     (依靠"投影到最近外壁段"间接近似，对正交转角通常已可用)

;; ---- 通用小工具 ----

;;; 在点集中找离 pick 最近的点
(defun zkk:nearest-pt (pts pick / best bestd p d)
  (setq best nil bestd nil)
  (foreach p pts
    (setq d (distance p pick))
    (if (or (null bestd) (< d bestd))
      (setq best p bestd d)))
  best)

;;; 提取行径线顶点 (支持 LINE / LWPOLYLINE / POLYLINE)
(defun zkk:path-vertices (ent / tp d)
  (setq d (entget ent) tp (cdr (assoc 0 d)))
  (cond
    ((= tp "LINE")
     (list (zkk:to2d (cdr (assoc 10 d)))
           (zkk:to2d (cdr (assoc 11 d)))))
    ((= tp "LWPOLYLINE") (zkk:lwpts ent))
    ((= tp "POLYLINE") (zkk:legpts ent))
    (T nil)))

;; ---- 外壁→内壁几何偏移 ----
;; 视频规则: "我们这种算法始终都是以内币为准" + "内币点作外币垂线, 到转角距离"
;; 实现: 每条外壁边沿内法向偏移板厚, 相邻偏移边求交得内壁顶点 (正交段等价于+wd)
;;       首末顶点相邻只有一条边, 直接法向偏 wd

;;; 多边形质心 (简单平均)
(defun zkk:centroid-pts (pts / sx sy n)
  (setq sx 0.0 sy 0.0 n (length pts))
  (foreach p pts (setq sx (+ sx (car p)) sy (+ sy (cadr p))))
  (if (> n 0) (list (/ sx n) (/ sy n)) '(0.0 0.0)))

;;; 外壁→内壁内法向方向 (+1 或 -1), 乘在 +90° 法向前
;;; zkk:norm-dir 已保证 outer-pts 方向与 src 一致, 标准 CAD CCW 源 → 材料 LEFT → 内法向 = +90° → sign=+1
;;; 若遇到 CW 源 norm-dir 已反转 → 仍是 CCW-outer → sign=+1
;;; 保留函数签名以便将来按需切换
(defun zkk:inner-sign (outer-pts) 1.0)

;;; 两条直线交点 (点A1方向d1 与 点A2方向d2); d 为单位向量
;;; 无交点(近似平行)时返回 nil
(defun zkk:line-intersect (a1 d1 a2 d2 / den t1 ix iy)
  (setq den (- (* (car d1) (cadr d2)) (* (cadr d1) (car d2))))
  (cond
    ((< (abs den) 1e-9) nil)
    (T
     (setq t1 (/ (- (* (- (car a2) (car a1)) (cadr d2))
                    (* (- (cadr a2) (cadr a1)) (car d2)))
                 den))
     (list (+ (car a1) (* t1 (car d1)))
           (+ (cadr a1) (* t1 (cadr d1)))))))

;;; 外壁顶点按 edge-offset 求交法生成内壁点序 (长度相同)
;;; 首末顶点: 只有一条相邻边, 直接法向偏 wd
(defun zkk:offset-inner-pts (outer-pts wd / n sign res i cur prev nxt
                                             e1 e1n ex1 ey1 l1 e2 e2n ex2 ey2 l2
                                             nrm1 nrm2 a1 a2 ip)
  (setq sign (zkk:inner-sign outer-pts)
        n (length outer-pts) i 0 res nil)
  (while (< i n)
    (setq cur (nth i outer-pts)
          prev (if (> i 0) (nth (1- i) outer-pts) nil)
          nxt  (if (< (1+ i) n) (nth (1+ i) outer-pts) nil))
    (cond
      ;; 首顶点: 仅向前一条边 (prev->cur 不存在), 用 cur->nxt 法向
      ((null prev)
       (setq ex2 (- (car nxt) (car cur)) ey2 (- (cadr nxt) (cadr cur))
             l2 (sqrt (+ (* ex2 ex2) (* ey2 ey2))))
       (setq nrm2 (list (* sign (/ (- ey2) l2)) (* sign (/ ex2 l2))))
       (setq res (append res (list
         (list (+ (car cur) (* (car nrm2) wd))
               (+ (cadr cur) (* (cadr nrm2) wd)))))))
      ;; 末顶点: 仅 prev->cur
      ((null nxt)
       (setq ex1 (- (car cur) (car prev)) ey1 (- (cadr cur) (cadr prev))
             l1 (sqrt (+ (* ex1 ex1) (* ey1 ey1))))
       (setq nrm1 (list (* sign (/ (- ey1) l1)) (* sign (/ ex1 l1))))
       (setq res (append res (list
         (list (+ (car cur) (* (car nrm1) wd))
               (+ (cadr cur) (* (cadr nrm1) wd)))))))
      ;; 中间顶点: 两条边偏移直线求交
      (T
       (setq ex1 (- (car cur) (car prev)) ey1 (- (cadr cur) (cadr prev))
             l1 (sqrt (+ (* ex1 ex1) (* ey1 ey1)))
             ex2 (- (car nxt) (car cur)) ey2 (- (cadr nxt) (cadr cur))
             l2 (sqrt (+ (* ex2 ex2) (* ey2 ey2))))
       (setq nrm1 (list (* sign (/ (- ey1) l1)) (* sign (/ ex1 l1)))
             nrm2 (list (* sign (/ (- ey2) l2)) (* sign (/ ex2 l2))))
       ;; prev->cur 偏移后通过点 cur + nrm1*wd, 方向 (ex1/l1, ey1/l1)
       (setq a1 (list (+ (car cur) (* (car nrm1) wd))
                      (+ (cadr cur) (* (cadr nrm1) wd)))
             a2 (list (+ (car cur) (* (car nrm2) wd))
                      (+ (cadr cur) (* (cadr nrm2) wd))))
       (setq ip (zkk:line-intersect a1 (list (/ ex1 l1) (/ ey1 l1))
                                    a2 (list (/ ex2 l2) (/ ey2 l2))))
       (if (null ip)
         ;; 平行: 取平均
         (setq ip (list (/ (+ (car a1) (car a2)) 2.0)
                        (/ (+ (cadr a1) (cadr a2)) 2.0))))
       (setq res (append res (list ip)))))
    (setq i (1+ i)))
  res)

;; ---- pts 边索引 → final-segments 累积偏移 ----
;; 与 zkk:slot-deduct 的插入规则保持一致:
;;   每经过一个内部锐凸角(bangle < 70°), 在该段后插入一段 slot-width
;; 返回 ((edge-start-x edge-len) ...) 长度 = (length pts) - 1
(defun zkk:edge-fs-map (pts fs cfg / sa nseg out cum k fs-k pp pc pn ba shoulder)
  (setq sa (zkk:sarea pts)
        nseg (1- (length pts))
        out nil cum 0.0 fs-k 0 k 0)
  (while (< k nseg)
    (setq out (append out (list (list cum (nth fs-k fs)))))
    (setq cum (+ cum (nth fs-k fs)) fs-k (1+ fs-k))
    ;; 是否在 edge k 后有插入肩段
    (setq shoulder nil)
    (if (< (1+ k) (1- (length pts)))
      (progn
        (setq pp (nth k pts) pc (nth (1+ k) pts) pn (nth (+ k 2) pts))
        (if (zkk:convex-p sa pp pc pn)
          (progn
            (setq ba (zkk:bangle pp pc pn))
            (if (and (numberp ba) (< ba 70.0)) (setq shoulder T))))))
    (if shoulder
      (setq cum (+ cum (nth fs-k fs)) fs-k (1+ fs-k)))
    (setq k (1+ k)))
  out)

;;; 把世界点 wp 投影到 pts 边 (p1, p2)
;;; 返回 (tv distance) , tv 已 clamp 到 [0,1]
(defun zkk:project-to-edge (wp p1 p2 / dx dy len2 tv prx pry)
  (setq dx (- (car p2) (car p1)) dy (- (cadr p2) (cadr p1))
        len2 (+ (* dx dx) (* dy dy)))
  (if (< len2 1e-9)
    (list 0.0 (distance wp p1))
    (progn
      (setq tv (/ (+ (* (- (car wp) (car p1)) dx)
                     (* (- (cadr wp) (cadr p1)) dy)) len2))
      (if (< tv 0.0) (setq tv 0.0))
      (if (> tv 1.0) (setq tv 1.0))
      (setq prx (+ (car p1) (* tv dx)) pry (+ (cadr p1) (* tv dy)))
      (list tv (distance wp (list prx pry))))))

;;; 把世界点 wp 投影到截面外壁，返回展开X
(defun zkk:wp-to-unfold-x (wp pts efmap /
  best-d best-i best-t k p1 p2 r d tv info)
  (setq best-d nil best-i 0 best-t 0.0 k 0)
  (while (< (1+ k) (length pts))
    (setq p1 (nth k pts) p2 (nth (1+ k) pts)
          r  (zkk:project-to-edge wp p1 p2)
          tv (car r) d (cadr r))
    (if (or (null best-d) (< d best-d))
      (setq best-d d best-i k best-t tv))
    (setq k (1+ k)))
  (if (null best-d) nil
    (progn
      (setq info (nth best-i efmap))
      (+ (car info) (* best-t (cadr info))))))

;;; 求多边形在水平切线 n = n-mid 上的 t 交点列表
;;; tn = 模板顶点 (t, n) 序列（闭合/开放均可，最后一条边自动闭合）
;;; 对每条边 (p_i, p_{i+1}): 若 n-mid 严格在两端点 n 值之间（或恰好等于一端，
;;; 视为交点），按参数 f = (n-mid - n_i) / (n_{i+1} - n_i) 算 t 交点。
;;; 返回排序去重后的 t 值列表。
(defun zkk:poly-intersect-n (tn n-mid / res i nseg a b na nb dn f tc tmpv sorted uniq prev)
  (setq nseg (length tn) res nil i 0)
  (while (< i nseg)
    (setq a (nth i tn)
          b (nth (rem (1+ i) nseg) tn))
    (setq na (cadr a) nb (cadr b) dn (- nb na))
    (cond
      ((< (abs dn) 1e-6)
       ;; 水平边 (n 方向无变化): 若恰好落在 n-mid 上, 其两端都算交点
       (if (< (abs (- n-mid na)) 1e-6)
         (setq res (append res (list (car a) (car b))))))
      (T
       (setq f (/ (- n-mid na) dn))
       (if (and (> f -1e-6) (< f (+ 1.0 1e-6)))
         (progn
           (setq tc (+ (car a) (* f (- (car b) (car a)))))
           (setq res (append res (list tc)))))))
    (setq i (1+ i)))
  ;; 排序并去除几乎相等的
  (setq sorted (vl-sort res '<) uniq nil prev nil)
  (foreach tmpv sorted
    (if (or (null prev) (> (abs (- tmpv prev)) 1e-4))
      (progn (setq uniq (append uniq (list tmpv))) (setq prev tmpv))))
  uniq)

;;; 把模板沿 (dir, n-hat) 投影分成多个 n 区间
;;; 返回 ((n-lo n-hi t-span) ...)  按 n-lo 升序
;;;   n-lo/n-hi = 该区间在法向的范围（以基点为原点）
;;;   t-span    = 该区间里模板沿 dir 方向的跨度 (= 缺口深度)
;;; 通用做法: 对每个 n 区间取中点 n-mid, 求模板多边形水平切线 t 交点,
;;;           t-span = 交点序列两两配对相减之和 (凸多边形 = 单对差)
(defun zkk:tmpl-zones (tmpl-rel dir n-hat /
  tn n-values n-breaks zones i n-lo n-hi n-mid ts j span t-min t-max p)
  (setq tn
    (mapcar '(lambda (p)
               (list (+ (* (car p) (car dir))   (* (cadr p) (cadr dir)))
                     (+ (* (car p) (car n-hat)) (* (cadr p) (cadr n-hat)))))
            tmpl-rel))
  (setq n-values (mapcar 'cadr tn))
  (setq n-breaks (zkk:uniq-sorted n-values 1e-4))
  (setq zones nil i 0)
  (while (< (1+ i) (length n-breaks))
    (setq n-lo (nth i n-breaks) n-hi (nth (1+ i) n-breaks))
    (setq n-mid (/ (+ n-lo n-hi) 2.0))
    (setq ts (zkk:poly-intersect-n tn n-mid))
    ;; t 跨度取最外侧两交点 (max-min); 同时记录 t-min / t-max
    ;; 用于多段语义: ws-start = p_a + t-min·dir, ws-end = p_b + t-max·dir
    (if (and ts (>= (length ts) 2))
      (progn
        (setq t-min (apply 'min ts) t-max (apply 'max ts)
              span (- t-max t-min))
        (if (> span 1e-4)
          (setq zones
            (append zones (list (list n-lo n-hi span t-min t-max)))))))
    (setq i (1+ i)))
  zones)

;;; 单个内壁边按 tmpl-zones 切分，返回 ((fa fb depth+allow) ...) fractions in [0,1] on inner edge
;;; n1, n2 = 内壁该段两端在法向的值
(defun zkk:edge-subzones (n1 n2 zones allow /
  res z n-lo n-hi depth a b fa fb tmpf dn is-h)
  (setq res nil dn (- n2 n1) is-h (< (abs dn) 1e-6))
  (foreach z zones
    (setq n-lo (car z) n-hi (cadr z) depth (caddr z))
    (setq fa nil fb nil)
    (cond
      (is-h
       ;; H 段: n 恒定. n1 严格落在 zone 内侧 (避免边界边被两个 zone 同时认领)
       (if (and (> n1 (+ n-lo 1e-4)) (< n1 (- n-hi 1e-4)))
         (setq fa 0.0 fb 1.0)))
      (T
       ;; 斜/竖段: 求 [n-lo,n-hi] ∩ [min(n1,n2),max(n1,n2)]
       (setq a (max n-lo (min n1 n2))
             b (min n-hi (max n1 n2)))
       (if (> (- b a) 1e-4)
         (progn
           (setq fa (/ (- a n1) dn)
                 fb (/ (- b n1) dn))
           (if (> fa fb) (progn (setq tmpf fa fa fb fb tmpf)))
           (if (< fa 0.0) (setq fa 0.0))
           (if (> fb 1.0) (setq fb 1.0))))))
    (if (and fa fb (> (- fb fa) 1e-6))
      (setq res (append res (list (list fa fb (+ depth allow)))))))
  res)

;;; 把 (fa, fb) 子段从 "内壁边 fraction" 映射到 "展开 u"
;;; 规则: 内壁 fraction f → 内壁点 → 垂直投影到外壁 → 外壁距离 d → u = cum + clamp(d - deduct, 0, fs-len)
;;; deduct = (outer_raw - fs_len) / 2  (视频: 单边扣减 0.35, 两端各一次)
;;; 端点吸附: fa≈0 → u1=cum (跑槽线对齐); fb≈1 → u2=cum+fs-len
(defun zkk:fraction-to-u (fa fb ip1 ip2 op1 op2 cum fs-len /
  olen odx ody odir pa pb da db deduct u1 u2)
  (setq odx (- (car op2) (car op1)) ody (- (cadr op2) (cadr op1))
        olen (sqrt (+ (* odx odx) (* ody ody))))
  (cond
    ((< olen 1e-9) (list cum cum))
    (T
     (setq odir (list (/ odx olen) (/ ody olen))
           deduct (/ (- olen fs-len) 2.0))
     (cond
       ((< fa 1e-6) (setq u1 cum))
       (T
        (setq pa (list (+ (car ip1) (* fa (- (car ip2) (car ip1))))
                       (+ (cadr ip1) (* fa (- (cadr ip2) (cadr ip1))))))
        (setq da (+ (* (- (car pa) (car op1)) (car odir))
                    (* (- (cadr pa) (cadr op1)) (cadr odir))))
        (setq u1 (+ cum (max 0.0 (min fs-len (- da deduct)))))))
     (cond
       ((> fb (- 1.0 1e-6)) (setq u2 (+ cum fs-len)))
       (T
        (setq pb (list (+ (car ip1) (* fb (- (car ip2) (car ip1))))
                       (+ (cadr ip1) (* fb (- (cadr ip2) (cadr ip1))))))
        (setq db (+ (* (- (car pb) (car op1)) (car odir))
                    (* (- (cadr pb) (cadr op1)) (cadr odir))))
        (setq u2 (+ cum (max 0.0 (min fs-len (- db deduct)))))))
     (if (> u1 u2) (list u2 u1) (list u1 u2)))))

;;; 根据基点 + 行径线 + 模板 + 截面, 计算缺口区域列表
;;; 规则 (视频 QQ20260422-052829-HD.mp4):
;;;   - 用 inner-pts (内壁) 的 n 坐标判定 zone 归属
;;;   - 用 outer 的 efmap (沿外壁累积距离 = 到转角距离) 给出 u 坐标
;;; 多段行径线: 逐段独立计算; 每段用自己的 p_j 作原点, 自己的 dir/n-hat
;;; 外壁边归属判定: 外壁边中点沿该段 dir 的投影在 [0, seg-len_j] 内
;;; 返回 ((u-start u-end depth) ...) 按 u-start 升序, 相邻同深度已合并
(defun zkk:compute-notches (path-pts base-pt tmpl-pts outer-pts inner-pts efmap cfg /
  dx dy slen dir n-hat p-a p-b tmpl-rel-j zones
  raw merged sorted cur nxt allow i n1 n2 cum fs-len seg-i n-segs
  edge-records ip1 ip2 op1 op2 rec fa fb dep uu mid tproj all-nil)
  (setq allow 0.2 raw nil n-segs (1- (length path-pts)) seg-i 0 all-nil T)
  (princ (strcat "\n[DBG] path segments=" (itoa n-segs)))
  (while (< seg-i n-segs)
    (setq p-a (nth seg-i path-pts) p-b (nth (1+ seg-i) path-pts)
          dx (- (car p-b) (car p-a)) dy (- (cadr p-b) (cadr p-a))
          slen (sqrt (+ (* dx dx) (* dy dy))))
    (cond
      ((< slen 1e-6)
       (princ (strcat "\n[DBG] seg#" (itoa seg-i) " 长度过小, 跳过")))
      (T
       (setq dir (list (/ dx slen) (/ dy slen))
             n-hat (list (- (cadr dir)) (car dir)))
       ;; 模板相对 base-pt (模板的锚点不变), 段起点 p-a 仅用于内壁定位
       (setq tmpl-rel-j
         (mapcar '(lambda (p)
                    (list (- (car p) (car base-pt))
                          (- (cadr p) (cadr base-pt))))
                 tmpl-pts))
       (setq zones (zkk:tmpl-zones tmpl-rel-j dir n-hat))
       (princ (strcat "\n[DBG] seg#" (itoa seg-i)
                      " dir=(" (zkk:r2s (car dir)) "," (zkk:r2s (cadr dir))
                      ") len=" (zkk:r2s slen)
                      " zones=" (itoa (length zones))))
       (foreach z zones
         (princ (strcat "\n[DBG]   zone n=[" (zkk:r2s (car z)) ","
                        (zkk:r2s (cadr z)) "] d=" (zkk:r2s (caddr z)))))
       (cond
         ((null zones)
          (princ (strcat "\n[DBG] seg#" (itoa seg-i) " 无 zone, 跳过")))
         (T
          (setq all-nil nil i 0)
          (while (< (1+ i) (length inner-pts))
            (setq ip1 (nth i inner-pts) ip2 (nth (1+ i) inner-pts)
                  op1 (nth i outer-pts) op2 (nth (1+ i) outer-pts))
            ;; 归属判定: 外壁边中点沿 dir 投影 (相对 p-a)
            (setq mid (list (/ (+ (car op1) (car op2)) 2.0)
                            (/ (+ (cadr op1) (cadr op2)) 2.0)))
            (setq tproj (+ (* (- (car mid) (car p-a)) (car dir))
                           (* (- (cadr mid) (cadr p-a)) (cadr dir))))
            (cond
              ;; 端帽过滤: 333 验证版 — 第一条和最后一条外壁边为弯折端 tab, 不参与开缺
              ((or (= i 0) (= i (- (length inner-pts) 2)))
               nil)
              ((or (< tproj (- 0.0 1e-3)) (> tproj (+ slen 1e-3)))
               nil)  ;; 该外壁边不属于本段, 跳过
              (T
               (setq n1 (+ (* (- (car ip1) (car p-a)) (car n-hat))
                           (* (- (cadr ip1) (cadr p-a)) (cadr n-hat))))
               (setq n2 (+ (* (- (car ip2) (car p-a)) (car n-hat))
                           (* (- (cadr ip2) (cadr p-a)) (cadr n-hat))))
               (setq cum (car (nth i efmap)) fs-len (cadr (nth i efmap)))
               (setq edge-records (zkk:edge-subzones n1 n2 zones allow))
               (if edge-records
                 (progn
                   (princ (strcat "\n[DBG] seg#" (itoa seg-i)
                                  " edge#" (itoa i)
                                  " n1=" (zkk:r2s n1) " n2=" (zkk:r2s n2)
                                  " recs=" (itoa (length edge-records))))
                   (foreach rec edge-records
                     (setq fa (car rec) fb (cadr rec) dep (caddr rec))
                     (setq uu (zkk:fraction-to-u fa fb ip1 ip2 op1 op2 cum fs-len))
                     (if (> (- (cadr uu) (car uu)) 1e-6)
                       (setq raw (append raw
                         (list (list (car uu) (cadr uu) dep))))))))))
            (setq i (1+ i)))))))
    (setq seg-i (1+ seg-i)))
  (cond
    (all-nil
     (princ "\n[ERROR] 所有段均未产生 zone (请检查基点和行径线方向)")
     nil)
    (T
     ;; 按 u-start 排序; 仅合并 "连续无间隙 + 同深度" 段, 保留 gap 形成独立缺口
     (setq sorted
       (vl-sort raw '(lambda (a b) (< (car a) (car b)))))
     (setq merged nil cur nil)
     (foreach nxt sorted
       (cond
         ((null cur) (setq cur nxt))
         ((and (< (abs (- (cadr cur) (car nxt))) 1e-3)
               (< (abs (- (caddr cur) (caddr nxt))) 1e-3))
          (setq cur (list (car cur) (cadr nxt) (caddr cur))))
         (T
          (setq merged (append merged (list cur)))
          (setq cur nxt))))
     (if cur (setq merged (append merged (list cur))))
     (princ (strcat "\n[DBG] notches=" (itoa (length merged))))
     (foreach z merged
       (princ (strcat "\n[DBG] notch u=[" (zkk:r2s (car z)) ","
                      (zkk:r2s (cadr z)) "] d=" (zkk:r2s (caddr z)))))
     merged)))

;;; 多段行径线: 老算法语义 (V1.3.lsp L693-720)
;;;   对每段 path-seg (p_a → p_b) × 每个 zone:
;;;     ws-start = p_a + t-min·dir
;;;     ws-end   = p_b + t-max·dir
;;;     缺口宽 = path-seg-len + zone-span
;;;     缺口深 = zone n-hi (n-hat 方向最大切入)
;;;     u 坐标 = ws-start/ws-end 投影到 section 外壁取展开 x
;;; 仅 n-hi > 0 的 zone 视为有效切入 (n-hat 反方向的 zone 是模板外露部分, 不切入)
(defun zkk:compute-notches-msg (path-pts base-pt tmpl-pts outer-pts efmap cfg /
  raw merged sorted cur nxt allow seg-i n-segs
  p-a p-b dx dy slen dir n-hat tmpl-rel zones
  z n-lo n-hi span t-min t-max depth ws-s ws-e us ue tmpv)
  (setq allow 0.2 raw nil n-segs (1- (length path-pts)) seg-i 0)
  (princ (strcat "\n[DBG-MSG] path segments=" (itoa n-segs)))
  (setq tmpl-rel
    (mapcar '(lambda (p)
               (list (- (car p) (car base-pt))
                     (- (cadr p) (cadr base-pt))))
            tmpl-pts))
  (while (< seg-i n-segs)
    (setq p-a (nth seg-i path-pts) p-b (nth (1+ seg-i) path-pts)
          dx (- (car p-b) (car p-a)) dy (- (cadr p-b) (cadr p-a))
          slen (sqrt (+ (* dx dx) (* dy dy))))
    (cond
      ((< slen 1e-6) nil)
      (T
       (setq dir (list (/ dx slen) (/ dy slen))
             n-hat (list (- (cadr dir)) (car dir)))
       (setq zones (zkk:tmpl-zones tmpl-rel dir n-hat))
       (princ (strcat "\n[DBG-MSG] seg#" (itoa seg-i)
                      " dir=(" (zkk:r2s (car dir)) "," (zkk:r2s (cadr dir))
                      ") len=" (zkk:r2s slen)
                      " zones=" (itoa (length zones))))
       (foreach z zones
         (setq n-lo (car z) n-hi (cadr z) span (caddr z)
               t-min (nth 3 z) t-max (nth 4 z))
         (cond
           ((<= n-hi 1e-4)
            (princ (strcat "\n[DBG-MSG]   zone n=[" (zkk:r2s n-lo)
                           "," (zkk:r2s n-hi) "] 法向无切入, 跳过")))
           (T
            (setq depth n-hi)
            (setq ws-s (list (+ (car p-a) (* t-min (car dir)))
                             (+ (cadr p-a) (* t-min (cadr dir)))))
            (setq ws-e (list (+ (car p-b) (* t-max (car dir)))
                             (+ (cadr p-b) (* t-max (cadr dir)))))
            (setq us (zkk:wp-to-unfold-x ws-s outer-pts efmap)
                  ue (zkk:wp-to-unfold-x ws-e outer-pts efmap))
            (cond
              ((or (null us) (null ue))
               (princ "\n[DBG-MSG]   投影失败"))
              (T
               (if (> us ue) (progn (setq tmpv us us ue ue tmpv)))
               (princ (strcat "\n[DBG-MSG]   zone n=[" (zkk:r2s n-lo)
                              "," (zkk:r2s n-hi) "] span=" (zkk:r2s span)
                              " → u=[" (zkk:r2s us) "," (zkk:r2s ue)
                              "] d=" (zkk:r2s depth)))
               (if (> (- ue us) 1e-6)
                 (setq raw (append raw
                   (list (list us ue (+ depth allow)))))))))))))
    (setq seg-i (1+ seg-i)))
  (cond
    ((null raw)
     (princ "\n[ERROR-MSG] 无有效缺口")
     nil)
    (T
     (setq sorted (vl-sort raw '(lambda (a b) (< (car a) (car b)))))
     ;; 不合并 — 多段路径每个 (seg, zone) 是独立缺口
     (princ (strcat "\n[DBG-MSG] notches=" (itoa (length sorted))))
     sorted)))

;; ---- 缺口轮廓绘制 (多台阶) ----

;;; 去除连续重复点
(defun zkk:dedupe-pts (pts / res prev p)
  (setq res nil prev nil)
  (foreach p pts
    (if (or (null prev)
            (> (distance prev p) 1e-6))
      (progn (setq res (append res (list p))) (setq prev p))))
  res)

;;; 沿一条边按缺口列表绘制 (多台阶连续折线)
;;; zones = 已排序 ((u1 u2 depth) ...), 绝对 X 坐标
;;; is-top: T=上边向下切, nil=下边向上切
(defun zkk:draw-edge-with-notches (left-x right-x edge-y zones is-top /
  sign pts last-y last-x z u1 u2 d target-y sorted cleaned)
  (setq sign (if is-top -1.0 1.0))
  (setq sorted (vl-sort zones '(lambda (a b) (< (car a) (car b)))))
  (setq pts (list (list left-x edge-y))
        last-y edge-y
        last-x left-x)
  (foreach z sorted
    (setq u1 (car z) u2 (cadr z) d (caddr z))
    (if (< u1 left-x) (setq u1 left-x))
    (if (> u2 right-x) (setq u2 right-x))
    (if (>= u1 (- u2 1e-6)) nil
      (progn
        (setq target-y (+ edge-y (* sign d)))
        (cond
          ((> (- u1 last-x) 1e-6)
           ;; 有间隔: 先回到边缘, 水平到 u1, 再下降
           (if (> (abs (- last-y edge-y)) 1e-6)
             (setq pts (append pts (list (list last-x edge-y)))))
           (setq pts (append pts (list (list u1 edge-y))))
           (setq pts (append pts (list (list u1 target-y)))))
          (T
           ;; 紧邻前一段: 原地阶梯变深度
           (setq pts (append pts (list (list last-x target-y))))))
        (setq pts (append pts (list (list u2 target-y))))
        (setq last-y target-y last-x u2))))
  (if (> (abs (- last-y edge-y)) 1e-6)
    (setq pts (append pts (list (list last-x edge-y)))))
  (if (> (- right-x last-x) 1e-6)
    (setq pts (append pts (list (list right-x edge-y)))))
  (setq cleaned (zkk:dedupe-pts pts))
  (if (>= (length cleaned) 2)
    (zkk:mk-open-pl cleaned)))

;;; 从旧代码复用: 排序去重 (容差 tol)
(defun zkk:uniq-sorted (vals tol / src out v keep lastv)
  (setq src (vl-sort vals '<) out nil lastv nil)
  (foreach v src
    (setq keep (or (null lastv) (> (abs (- v lastv)) tol)))
    (if keep (setq out (append out (list v)) lastv v)))
  out)

;;; 替换矩形为带缺口的轮廓
;;; 注意: zones 里的 u-start/u-end 是"相对展开图起点"(0..总长),
;;;       这里统一加上 left-x 偏移, 转为矩形的世界 X 坐标再绘制
(defun zkk:draw-notched-outline (rect-info zones side-str /
  rect-ent top-y bot-y left-x right-x abs-zones)
  (setq rect-ent (cdr (assoc 'rect-ent rect-info))
        top-y (cdr (assoc 'top-y rect-info))
        bot-y (cdr (assoc 'bottom-y rect-info))
        left-x (cdr (assoc 'left-x rect-info))
        right-x (cdr (assoc 'right-x rect-info)))
  (setq abs-zones
    (mapcar '(lambda (z)
               (list (+ (car z) left-x)
                     (+ (cadr z) left-x)
                     (caddr z)))
            zones))
  (if (and rect-ent (zkk:exists-p rect-ent)) (entdel rect-ent))
  (cond
    ((or (= side-str "S") (= side-str "Q"))
     (zkk:draw-edge-with-notches left-x right-x top-y abs-zones T))
    (T
     (zkk:mk-open-pl (list (list left-x top-y) (list right-x top-y)))))
  (cond
    ((or (= side-str "X") (= side-str "Q"))
     (zkk:draw-edge-with-notches left-x right-x bot-y abs-zones nil))
    (T
     (zkk:mk-open-pl (list (list left-x bot-y) (list right-x bot-y))))))

;; ---- K 分支主入口 ----

;;; 输出展开数据文字 (与 Z 分支一致的尺寸文字)
(defun zkk:emit-notch-text (src cfg ana / mc fs pd)
  (setq fs (cdr (assoc 'final-segments ana))
        pd (cdr (assoc 'paocao-data ana))
        mc (zkk:build-text cfg fs pd))
  (zkk:draw-mtext src mc cfg)
  (princ (strcat "\n[INFO] 展开总长: " (zkk:r2s (zkk:sum fs)))))

;;; Q 模式下若 2*max_d + 20 > 当前矩形高度 → 重新画矩形 (扩高), 而不是限深
;;; 返回更新后的 rect-info (若未扩则原样返回)
(defun zkk:expand-rect-q (rect-info zones /
  min-gap max-d cur-h need-h top-y bot-y left-x right-x rect-ent z d
  new-top new-bot new-rect)
  (setq min-gap 20.0 max-d 0.0)
  (foreach z zones
    (setq d (caddr z))
    (if (> d max-d) (setq max-d d)))
  (setq top-y (cdr (assoc 'top-y rect-info))
        bot-y (cdr (assoc 'bottom-y rect-info))
        left-x (cdr (assoc 'left-x rect-info))
        right-x (cdr (assoc 'right-x rect-info))
        rect-ent (cdr (assoc 'rect-ent rect-info))
        cur-h (- top-y bot-y)
        need-h (+ (* 2.0 max-d) min-gap))
  (cond
    ((<= need-h (+ cur-h 1e-3))
     (princ (strcat "\n[INFO] 矩形高度 " (zkk:r2s cur-h)
                    " 充足 (需 " (zkk:r2s need-h) ")"))
     rect-info)
    (T
     ;; 以矩形中心为基准上下对称扩展
     (setq new-top (+ (/ (+ top-y bot-y) 2.0) (/ need-h 2.0))
           new-bot (- (/ (+ top-y bot-y) 2.0) (/ need-h 2.0)))
     (princ (strcat "\n[INFO] Q模式扩展矩形: " (zkk:r2s cur-h)
                    " → " (zkk:r2s need-h) " (上下各深≤"
                    (zkk:r2s max-d) " + 20mm 间隔)"))
     ;; 删除旧矩形, 重画
     (if (and rect-ent (zkk:exists-p rect-ent)) (entdel rect-ent))
     (setq new-rect
       (zkk:mk-closed-pl
         (list (list left-x new-top) (list right-x new-top)
               (list right-x new-bot) (list left-x new-bot))))
     (list (cons 'rect-ent new-rect)
           (cons 'top-y new-top)
           (cons 'bottom-y new-bot)
           (cons 'left-x left-x)
           (cons 'right-x right-x)))))

;;; Q 模式: 上下开缺会重合时, 上下始终间隔 20mm
;;; 若 2*d > (rect-h - 20) → 限制 d = (rect-h - 20)/2
;;; rect-h = top-y - bot-y
(defun zkk:cap-depths-q (zones rect-h /
  min-gap d-max out z d)
  (setq min-gap 20.0 d-max (/ (- rect-h min-gap) 2.0) out nil)
  (cond
    ((<= d-max 0.0)
     (princ (strcat "\n[WARN] 展开矩形高度 " (zkk:r2s rect-h)
                    " mm 不足 20mm 间隔, 深度未限制"))
     zones)
    (T
     (foreach z zones
       (setq d (caddr z))
       (if (> d d-max)
         (progn
           (princ (strcat "\n[INFO] Q模式限深: u=[" (zkk:r2s (car z)) ","
                          (zkk:r2s (cadr z)) "] d " (zkk:r2s d)
                          " → " (zkk:r2s d-max) " (保留" (zkk:r2s min-gap) "mm间隔)"))
           (setq out (append out (list (list (car z) (cadr z) d-max)))))
         (setq out (append out (list z)))))
     out)))

;;; K 分支主流程
(defun zkk:do-notch (src cfg ana /
  fs pts pd rect-info inner-pts
  tmpl-ss tmpl-ent tmpl-pts side-str base-pt
  path-ss path-ent path-pts efmap zones)
  (setq fs (cdr (assoc 'final-segments ana))
        pts (cdr (assoc 'points ana))
        pd (cdr (assoc 'paocao-data ana)))
  (setq inner-pts (zkk:offset-inner-pts pts (zkk:cg cfg 'thickness)))
  (setq rect-info (zkk:draw-unfold-closed src fs cfg pd))
  (cond
    ((null rect-info)
     (princ "\n[ERROR] 展开矩形创建失败"))
    (T
     (princ "\n[OK] 展开矩形已绘制")
     (princ "\n选取开缺料(闭合多段线): ")
     (setq tmpl-ss (ssget "_:S" '((0 . "LWPOLYLINE,POLYLINE"))))
     (cond
       ((null tmpl-ss)
        (princ "\n[CANCEL] 未选开缺料, 仅输出展开数据")
        (zkk:emit-notch-text src cfg ana))
       (T
        (setq tmpl-ent (ssname tmpl-ss 0)
              tmpl-pts (zkk:getpts tmpl-ent))
        (cond
          ((or (null tmpl-pts) (< (length tmpl-pts) 3))
           (princ "\n[ERROR] 模板顶点数不足")
           (zkk:emit-notch-text src cfg ana))
          (T
           (setq base-pt (getpoint "\n选取开缺料上的基点(第一投影角): "))
           (cond
             ((null base-pt)
              (princ "\n[CANCEL] 未指定基点")
              (zkk:emit-notch-text src cfg ana))
             (T
              ;; snap base point to nearest template vertex
              (setq base-pt (zkk:nearest-pt tmpl-pts base-pt))
              (princ "\n选取开缺行径线: ")
              (setq path-ss (ssget "_:S"
                              '((0 . "LINE,LWPOLYLINE,POLYLINE"))))
              (cond
                ((null path-ss)
                 (princ "\n[CANCEL] 未选行径线")
                 (zkk:emit-notch-text src cfg ana))
                (T
                 (setq path-ent (ssname path-ss 0)
                       path-pts (zkk:path-vertices path-ent))
                 (cond
                   ((or (null path-pts) (< (length path-pts) 2))
                    (princ "\n[ERROR] 行径线顶点数不足")
                    (zkk:emit-notch-text src cfg ana))
                   (T
                    (initget "S X Q")
                    (setq side-str
                      (getkword
                        "\n指定开缺方向 [上开缺(S)/下开缺(X)/上下开缺(Q)]: "))
                    (if (null side-str) (setq side-str "X"))
                    (setq efmap (zkk:edge-fs-map pts fs cfg))
                    ;; 路由: 单段 path (顶点=2) 走老算法 (333 验证版);
                    ;;       多段 path (顶点>2) 走 ws-start/ws-end 投影语义
                    (cond
                      ((<= (length path-pts) 2)
                       (setq zones (zkk:compute-notches
                                     path-pts base-pt tmpl-pts pts inner-pts efmap cfg)))
                      (T
                       (princ "\n[INFO] 多段行径线 → 启用多段语义算法")
                       (setq zones (zkk:compute-notches-msg
                                     path-pts base-pt tmpl-pts pts efmap cfg))))
                    ;; Q 模式: 上下缺口可能重合 → 优先扩矩形保 20mm 间隔, 不削减深度
                    (if (and zones (= side-str "Q"))
                      (setq rect-info (zkk:expand-rect-q rect-info zones)))
                    (cond
                      ((null zones)
                       (princ "\n[ERROR] 未生成有效缺口")
                       (zkk:emit-notch-text src cfg ana))
                      (T
                       (zkk:draw-notched-outline rect-info zones side-str)
                       (princ (strcat "\n[OK] 已生成缺口: "
                                      (itoa (length zones)) " 处"))
                       (foreach z zones
                         (princ (strcat
                           "\n  宽=" (zkk:r2s (- (cadr z) (car z)))
                           " mm  深=" (zkk:r2s (caddr z))
                           " mm  (扣减+余量已计算)")))
                       (zkk:emit-notch-text src cfg ana))))))))))))))))

;; ===================== 主入口 =====================
;;
;; 整体流程：
;;   1. 用户选取闭合多段线 → 钣金截面
;;   2. 输入参数：板厚、扣减值
;;   3. 分析截面 → 提取外壁 → 扣槽补偿 → 后处理
;;   4. 选择 Z(展开) 或 K(开缺)
;;   5. Z: 画开口线 + 文字
;;      K: 画矩形 → 选模板 → 画缺口 + 文字

(defun zkk:main (/ *error* old-pe old-ce cfg src src-ss choice ana)
  (defun *error* (msg)
    (if old-pe (setvar "PEDITACCEPT" old-pe))
    (if old-ce (setvar "CMDECHO" old-ce))
    (if (and msg (/= msg "Function cancelled")
             (/= msg "quit / exit abort"))
      (princ (strcat "\n[ERROR] " msg)))
    (princ))

  (setq old-pe (getvar "PEDITACCEPT")
        old-ce (getvar "CMDECHO"))
  (setvar "PEDITACCEPT" 1)
  (setvar "CMDECHO" 0)

  (princ "\n>>>>>> ZKK V1.3 (开缺重构) <<<<<<")

  ;; 1. Select section
  (princ "\n选取钣金截面线(闭合多段线): ")
  (setq src-ss (ssget "_:S" '((0 . "LWPOLYLINE,POLYLINE"))))
  (if (null src-ss)
    (princ "\n[CANCEL]")
    (progn
      (setq src (ssname src-ss 0))
      (if (not (zkk:strict-closed-p src))
        (princ "\n[INFO] 未检测出闭合多段线")
        (progn
          ;; 2. Parameters
          (setq cfg (zkk:def-cfg))
          (setq cfg (zkk:cs cfg 'thickness
            (zkk:ask-real "板厚(mm)"
                          (zkk:cg cfg 'thickness))))
          (setq cfg (zkk:cs cfg 'slot-width
            (zkk:ask-real "扣减值(mm)"
                          (zkk:cg cfg 'slot-width))))

          ;; 3. Analyze
          (setq ana (zkk:analyze src cfg))
          (if (null ana) nil
            (progn
              ;; 4. Z / K choice
              (initget "Z K")
              (setq choice
                (getkword "\n指定操作 [展开(Z)/开缺(K)]: "))
              (if (null choice) (setq choice "Z"))
              (cond
                ((= choice "Z") (zkk:do-unfold src cfg ana))
                ((= choice "K") (zkk:do-notch src cfg ana)))))))))

  (setvar "PEDITACCEPT" old-pe)
  (setvar "CMDECHO" old-ce)
  (princ))

;;; 注册命令 ZKK
(defun c:ZKK () (zkk:main))

(princ "\nZKK V1.3 (开缺重构) 已加载. 命令: ZKK")
(princ)
