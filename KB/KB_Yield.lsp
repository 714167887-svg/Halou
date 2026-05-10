;;; KB_Yield_v1.2.lsp
;;; Command: KB
;;; FUNCTION:
;;;   1. 选闭合钣金截面多段线
;;;   2. 复用 ZKK_Unfold_V13.1 的核心算法：
;;;      a) 找一对“近似等长的最短边”作为板厚两端
;;;      b) 取较长一侧路径为外壁
;;;      c) 对外壁逐折角做扣减/补加（凸 -slot/2，凹 +slot/2，锐角额外插段）
;;;   3. 提示输入扣减值 slot-width（mm，默认 0.7）；输入 0 即纯外壁周长
;;;   4. 最终展开长度 = sum(扣减后段长)
;;;   5. 与原料宽 1219 计算可开支数
;;;
;;; 与 ZKK V13.1 算法等价（相同 slot-width 应得相同展开长度）。

(vl-load-com)

;;; ===== 工具 =====
(defun kb:pt2d-p (v) (and (listp v) (numberp (car v)) (numberp (cadr v))))
(defun kb:to2d (v)
  (if (kb:pt2d-p v) (list (float (car v)) (float (cadr v))) nil))
(defun kb:acos (x)
  (cond ((<= x -1.0) pi) ((>= x 1.0) 0.0)
        (T (atan (sqrt (- 1.0 (* x x))) x))))
(defun kb:sum (vs / s) (setq s 0.0)
  (foreach v vs (if (numberp v) (setq s (+ s v)))) s)

;;; LWPOLYLINE / POLYLINE 顶点提取
(defun kb:lwpts (ent / data item pt pts)
  (setq pts nil data (entget ent))
  (foreach item data
    (if (= (car item) 10)
      (progn (setq pt (kb:to2d (cdr item)))
             (if pt (setq pts (append pts (list pt)))))))
  pts)
(defun kb:legpts (ent / ne data pt pts done)
  (setq pts nil done nil ne (entnext ent))
  (while (and ne (not done))
    (setq data (entget ne))
    (cond
      ((= (cdr (assoc 0 data)) "VERTEX")
       (setq pt (kb:to2d (cdr (assoc 10 data))))
       (if pt (setq pts (append pts (list pt)))))
      ((= (cdr (assoc 0 data)) "SEQEND") (setq done T)))
    (if (not done) (setq ne (entnext ne))))
  pts)
(defun kb:getpts (ent / tp)
  (setq tp (cdr (assoc 0 (entget ent))))
  (cond ((= tp "LWPOLYLINE") (kb:lwpts ent))
        ((= tp "POLYLINE")   (kb:legpts ent))
        (T nil)))

(defun kb:closed-p (ent / data tp fl obj cp ar)
  (setq data (entget ent) tp (cdr (assoc 0 data)) fl (cdr (assoc 70 data)))
  (and (member tp '("LWPOLYLINE" "POLYLINE"))
       (or (and (numberp fl) (= 1 (logand fl 1)))
           (progn (setq obj (vlax-ename->vla-object ent))
                  (setq cp (vl-catch-all-apply 'vlax-get (list obj 'Closed)))
                  (and (not (vl-catch-all-error-p cp)) (/= cp :vlax-false)))
           (progn (setq obj (vlax-ename->vla-object ent))
                  (setq ar (vl-catch-all-apply 'vlax-get (list obj 'Area)))
                  (and (not (vl-catch-all-error-p ar)) (numberp ar) (> ar 1e-8))))))

;;; 严格闭合判定（与 ZKK 一致）：仅看 70 标志位与 Closed 属性，不以 Area 兑底。
;;; 防止「首尾几乎重合但未闭合」的多段线被误认。
(defun kb:strict-closed-p (ent / data tp fl obj cp)
  (setq data (entget ent) tp (cdr (assoc 0 data)) fl (cdr (assoc 70 data)))
  (and (member tp '("LWPOLYLINE" "POLYLINE"))
       (or (and (numberp fl) (= 1 (logand fl 1)))
           (progn (setq obj (vlax-ename->vla-object ent))
                  (setq cp (vl-catch-all-apply 'vlax-get (list obj 'Closed)))
                  (and (not (vl-catch-all-error-p cp)) (/= cp :vlax-false))))))

;;; 去重 / 去共线 / 段长 / 有符号面积
(defun kb:dedup-pts (pts / res prev pt)
  (setq res nil prev nil)
  (foreach pt pts
    (if (or (null prev) (not (kb:pt2d-p prev)) (not (kb:pt2d-p pt))
            (> (distance prev pt) 1e-6))
      (setq res (append res (list pt))))
    (setq prev pt))
  res)

(defun kb:rm-collin (pts cl / res n i prev curr next cv s1 s2)
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
             (setq cv (abs (- (* (- (car curr) (car prev)) (- (cadr next) (cadr prev)))
                              (* (- (cadr curr) (cadr prev)) (- (car next) (car prev))))))
             (setq s1 (distance prev curr) s2 (distance curr next))
             (if (or (<= s1 1e-8) (<= s2 1e-8) (> cv (* 0.001 s1 s2)))
               (setq res (append res (list curr))))))
        (setq i (1+ i)))
      (if (< (length res) 3) pts res))))

(defun kb:sarea (pts / area i ni p1 p2)
  (setq area 0.0 i 0)
  (while (< i (length pts))
    (setq p1 (nth i pts) ni (if (= (1+ i) (length pts)) 0 (1+ i)) p2 (nth ni pts))
    (if (and p1 p2)
      (setq area (+ area (- (* (car p1) (cadr p2)) (* (cadr p1) (car p2))))))
    (setq i (1+ i)))
  (/ area 2.0))

(defun kb:seglens (pts cl / n i ni cp np sl segs)
  (setq n (length pts) i 0 segs nil)
  (while (< i n)
    (setq cp (nth i pts))
    (cond ((and cl (= i (1- n))) (setq ni 0))
          ((< (1+ i) n) (setq ni (1+ i)))
          (T (setq ni nil)))
    (setq np (if ni (nth ni pts) nil))
    (if (and (kb:pt2d-p cp) (kb:pt2d-p np))
      (setq sl (distance cp np)) (setq sl nil))
    (if (and (numberp sl) (> sl 1e-8))
      (setq segs (append segs (list sl))))
    (setq i (1+ i)))
  segs)

;;; ===== 板厚短边对 + 外壁路径 (对齐 ZKK extract-wl) =====
(defun kb:find-pair (segs sthr etol / indexed si pair i)
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

(defun kb:coll-segs (segs el / r) (setq r nil)
  (foreach e el (setq r (append r (list (nth e segs))))) r)
(defun kb:edge-ep (pts ei / n ni)
  (setq n (length pts) ni (1+ ei))
  (if (>= ni n) (setq ni 0)) (nth ni pts))
(defun kb:bld-path (pts segs el / fe pp ps ep)
  (if (or (null el) (null pts)) nil
    (progn
      (setq fe (car el) pp nil)
      (if (kb:pt2d-p (nth fe pts)) (setq pp (list (nth fe pts))))
      (setq ps (kb:coll-segs segs el))
      (foreach ei el
        (setq ep (kb:edge-ep pts ei))
        (if (and pp (kb:pt2d-p ep)) (setq pp (append pp (list ep)))))
      (if (and pp ps (= (length pp) (1+ (length ps)))) (list pp ps) nil))))
(defun kb:erange (s e / r i)
  (setq r nil)
  (if (<= s e) (progn (setq i s)
    (while (<= i e) (setq r (append r (list i))) (setq i (1+ i))))) r)
(defun kb:wrange (s e ec / r i)
  (setq r nil)
  (if (> ec 0)
    (progn
      (setq i (rem s ec)) (if (< i 0) (setq i (+ i ec)))
      (setq e (rem e ec)) (if (< e 0) (setq e (+ e ec)))
      (while (/= i e)
        (setq r (append r (list i)))
        (setq i (1+ i)) (if (>= i ec) (setq i 0))))) r)
(defun kb:pick-outer (pa pb / la lb)
  (cond ((null pa) pb) ((null pb) pa)
    (T (setq la (kb:sum (cadr pa)) lb (kb:sum (cadr pb)))
       (if (>= la lb) pa pb))))

(defun kb:extract-wl (ent sthr etol /
  pts segs cl ec pair ia ib ea eb ec2 pa pb pc best)
  (setq pts (kb:dedup-pts (kb:getpts ent)))
  (setq cl (kb:closed-p ent))
  (setq pts (kb:rm-collin pts cl))
  (setq segs (kb:seglens pts cl))
  (setq ec (length segs))
  (setq pair (kb:find-pair segs sthr etol))
  (if (or (null pair) (< (length pts) 2) (< ec 3)) nil
    (progn
      (setq ia (car (nth 0 pair)) ib (car (nth 1 pair)))
      (if (> ia ib)
        (progn (setq ia (+ ia ib)) (setq ib (- ia ib)) (setq ia (- ia ib))))
      (if cl
        (progn
          (setq ea (kb:wrange (1+ ia) ib ec) eb (kb:wrange (1+ ib) ia ec))
          (setq pa (kb:bld-path pts segs ea) pb (kb:bld-path pts segs eb))
          (setq best (kb:pick-outer pa pb)))
        (progn
          (setq ea (kb:erange 0 (1- ia))
                eb (kb:erange (1+ ia) (1- ib))
                ec2 (kb:erange (1+ ib) (1- ec)))
          (setq pa (kb:bld-path pts segs ea)
                pb (kb:bld-path pts segs eb)
                pc (kb:bld-path pts segs ec2))
          (setq best (kb:pick-outer pa pb))
          (setq best (kb:pick-outer best pc))))
      best)))

;;; 把外壁点序规范成与原截面绕向一致（沿用 ZKK 思路）
(defun kb:norm-ccw (pts segs / area)
  (if (and (listp pts) (> (length pts) 2)
           (= (length pts) (1+ (length segs)))
           (not (vl-some '(lambda (p) (not (kb:pt2d-p p))) pts)))
    (progn (setq area (kb:sarea pts))
           (if (< area 0.0) (list (reverse pts) (reverse segs)) (list pts segs)))
    (list pts segs)))

(defun kb:norm-dir (src pts segs / sp sa)
  (if (kb:closed-p src)
    (progn (setq sp (kb:getpts src))
           (if (and sp (> (length sp) 2))
             (progn (setq sa (kb:sarea sp))
                    (if (< sa 0.0) (list (reverse pts) (reverse segs)) (list pts segs)))
             (kb:norm-ccw pts segs)))
    (kb:norm-ccw pts segs)))

;;; ===== 凸角判断 / 折角 / 扣减 (对齐 ZKK slot-deduct) =====
(defun kb:convex-p (sa pp pc pn / v1x v1y v2x v2y cp)
  (if (and (kb:pt2d-p pp) (kb:pt2d-p pc) (kb:pt2d-p pn))
    (progn
      (setq v1x (- (car pc) (car pp)) v1y (- (cadr pc) (cadr pp))
            v2x (- (car pn) (car pc)) v2y (- (cadr pn) (cadr pc))
            cp (- (* v1x v2y) (* v1y v2x)))
      (> (* sa cp) 0.0001)) nil))

(defun kb:bangle (pp pc pn / v1x v1y v2x v2y dp m1 m2 ca)
  (if (and (kb:pt2d-p pp) (kb:pt2d-p pc) (kb:pt2d-p pn))
    (progn
      (setq v1x (- (car pc) (car pp)) v1y (- (cadr pc) (cadr pp))
            v2x (- (car pn) (car pc)) v2y (- (cadr pn) (cadr pc))
            dp (+ (* v1x v2x) (* v1y v2y))
            m1 (distance '(0.0 0.0) (list v1x v1y))
            m2 (distance '(0.0 0.0) (list v2x v2y)))
      (if (> (* m1 m2) 0.0001)
        (progn (setq ca (/ dp (* m1 m2)))
               (if (> ca 1.0) (setq ca 1.0)) (if (< ca -1.0) (setq ca -1.0))
               (- 180.0 (* (kb:acos ca) (/ 180.0 pi)))) nil)) nil))

;;; 对外壁每个内部段两端做扣减/补加；锐角凸角处插入 slot-width
(defun kb:slot-deduct (pts segs sv /
  single iv sa sc i sl av bs fs pp pc pn ba)
  (if (or (null sv) (<= sv 0.0)) segs
    (progn
      (setq single (/ sv 2.0) iv sv sa (kb:sarea pts) sc (length segs) bs nil i 0)
      (while (< i sc)
        (setq sl (nth i segs))
        (if (or (not (numberp sl)) (= i 0) (= i (1- sc)))
          (setq bs (append bs (list sl)))
          (progn
            (setq av 0.0
                  pp (nth (1- i) pts) pc (nth i pts) pn (nth (1+ i) pts))
            (if (kb:convex-p sa pp pc pn)
              (setq av (- av single)) (setq av (+ av single)))
            (setq pp (nth i pts) pc (nth (1+ i) pts) pn (nth (+ i 2) pts))
            (if (kb:convex-p sa pp pc pn)
              (setq av (- av single)) (setq av (+ av single)))
            (setq sl (+ sl av)) (if (< sl 0.0) (setq sl 0.0))
            (setq bs (append bs (list sl)))))
        (setq i (1+ i)))
      (setq fs nil i 0)
      (while (< i sc)
        (setq fs (append fs (list (nth i bs))))
        (if (< (1+ i) (1- (length pts)))
          (progn
            (setq pp (nth i pts) pc (nth (1+ i) pts) pn (nth (+ i 2) pts))
            (if (kb:convex-p sa pp pc pn)
              (progn (setq ba (kb:bangle pp pc pn))
                     (if (and (numberp ba) (< ba 70.0))
                       (setq fs (append fs (list iv))))))))
        (setq i (1+ i)))
      fs)))

;;; ===== 主命令 =====
(defun c:KB ( / ss n i ent wl pts segs fs outer_raw final_len
              slot s_in r_in raw_width yield_count remainder_len
              SHORT_THR EQ_TOL succ skip )

  (princ "\n>>> KB 钣金出材率计算器 v1.4 (多选 + 可调原料宽 + ZKK 算法对齐) <<<")

  ;; 1. 选多段线（支持多选）
  (princ "\n请选择一个或多个钣金截面多段线: ")
  (setq ss (ssget '((0 . "LWPOLYLINE,POLYLINE"))))
  (if (null ss) (progn (princ "\n[错误] 未选中多段线，命令取消。") (exit)))

  ;; 2. 输入扣减值 slot-width（默认 0.7，对所有钣金生效）
  (initget 4)
  (setq s_in (getreal "\n请输入扣减值 slot-width <0.7>: "))
  (setq slot (if (null s_in) 0.7 s_in))

  ;; 2b. 输入原料宽度（默认 1210mm）
  (initget 6)  ;; 禁 0、禁负
  (setq r_in (getreal "\n请输入原料宽度 (mm) <1210>: "))
  (setq raw_width (if (null r_in) 1210.0 r_in))

  (setq SHORT_THR 1.5  EQ_TOL 0.01)
  (setq n (sslength ss)  i 0  succ 0  skip 0)
  (princ (strcat "\n>>> 共 " (itoa n) " 个钣金，开始处理 ----------------------------------------"))

  (while (< i n)
    (setq ent (ssname ss i))
    (princ (strcat "\n\n[#" (itoa (1+ i)) "/" (itoa n) "]"))
    (setq outer_raw nil  final_len nil)

    ;; 0. 严格闭合检查（与 ZKK 一致）：未闭合直接跳过
    (cond
      ((not (kb:strict-closed-p ent))
       (princ "\n   [跳过] 多段线未闭合（需 70 标志位=1 或 Closed=T）")
       (setq skip (1+ skip)))
      (T
    ;; 3. 外壁提取
    (setq wl (kb:extract-wl ent SHORT_THR EQ_TOL))
    (cond
      ((null wl)
       (princ "\n   [ERROR] 无法提取外壁数据，请检查截面线 (跳过)")
       (setq skip (1+ skip)))
      (T
       (setq pts (car wl) segs (cadr wl))
       (setq wl (kb:norm-dir ent pts segs))
       (setq pts (car wl) segs (cadr wl))
       (setq outer_raw (kb:sum segs))
       (setq fs (kb:slot-deduct pts segs slot))
       (setq final_len (kb:sum fs))
       (princ (strcat "\n   外壁原长: " (rtos outer_raw 2 1)
                      " mm   扣减后: " (rtos final_len 2 1)
                      " mm   (slot=" (rtos slot 2 3) ")"))

       ;; 4. 计算 + 标注 + 汇总（仅当外壁提取成功时执行）
       (cond
         ((or (null final_len) (<= final_len 0.0))
          (princ "\n   [跳过] 展开长度 <= 0")
          (setq skip (1+ skip)))
         ((> final_len raw_width)
          (princ (strcat "\n   [跳过] 展开长度 " (rtos final_len 2 1)
                         " > 原料宽 " (rtos raw_width 2 0)))
          (setq skip (1+ skip)))
         (T
          (setq yield_count   (fix (/ raw_width final_len)))
          (setq remainder_len (- raw_width (* yield_count final_len)))
          (princ (strcat "\n   >>> 可开: " (itoa yield_count) " 支"
                         "   余料: " (rtos remainder_len 2 1) " mm"))
          (if (> yield_count 0)
            (progn (kb:write-label ent final_len yield_count)
                   (setq succ (1+ succ)))
            (setq skip (1+ skip)))))))      ))    (setq i (1+ i)))

  (princ (strcat "\n\n>>> 完成：已标注 " (itoa succ)
                 " / 共 " (itoa n) " 个"
                 (if (> skip 0) (strcat "，跳过 " (itoa skip) " 个") "")
                 " ----------------------------------------"))
  (princ))

;;; ===== 在多段线下方写带颜色的 MTEXT =====
;;; 文字格式: 钣金数：<红>final_len</红>  开<红>yield_count</红>
(defun kb:write-label (ent final_len yield_count /
                       obj minP maxP xL yL xR yT w h th ix iy mtxt)
  (setq obj (vlax-ename->vla-object ent))
  (vla-getboundingbox obj 'minP 'maxP)
  (setq minP (vlax-safearray->list minP)
        maxP (vlax-safearray->list maxP))
  (setq xL (car minP) yL (cadr minP)
        xR (car maxP) yT (cadr maxP))
  (setq w  (- xR xL) h (- yT yL))
  ;; 字高：取截面高度 8%，但限制在 [5, 30] mm
  (setq th (max 5.0 (min 30.0 (* h 0.08))))
  ;; 插入点：水平居中于截面，顶部位于截面下方 (1.5*字高 + 10mm) 处
  (setq ix (* 0.5 (+ xL xR))
        iy (- yL (* th 1.5) 10.0))
  (setq mtxt (strcat
               "钣金数："
               "\\C1;" (rtos final_len 2 1) "\\C7;"
               "  开"
               "\\C1;" (itoa yield_count) "\\C7;"))
  (entmake (list
             '(0 . "MTEXT")
             '(100 . "AcDbEntity")
             '(100 . "AcDbMText")
             '(8 . "0")                       ;; 图层
             (cons 10 (list ix iy 0.0))       ;; 插入点
             (cons 40 th)                     ;; 字高
             (cons 41 (* w 2.0))              ;; 参考宽度
             '(71 . 2)                        ;; 2=顶部居中附着
             '(7 . "Standard")                ;; 文字样式
             (cons 1 mtxt)))
  (princ))

(princ "\n【已加载】KB 钣金出材率计算器 v1.4 (多选 + 可调原料宽) - 命令: KB")
(princ)
