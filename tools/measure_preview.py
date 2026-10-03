"""量 tools/title-preview.png 里那张卡片的几何，换算成设计单位打印出来。

⚠️ **这张图才是本项目的样式参照物。**
   同目录下的 tools/reference.png（央视原始截图）**不是**——它的卡片是 4.92:1，
   这张是 3.563:1，比例对不上。此前有人按 reference.png 改过 TitleWindow.xaml.cs，
   把竖条做成「上下都比蓝条高出一截的凸出标签」，代码从此编译不过；量那张图的
   tools/measure_edges.py 也一并删掉了，免得下一个人拿它去"修正"本来就对的实现。

量法：沿扫描线切「同色连续段」，段与段的边界就是元素边界；文字高度量墨迹
（纯白 / 纯黑像素，不含抗锯齿过渡色）。

换算：预览图里那张卡片正好 620 × 174 像素 = 310 × 87 设计单位（那张卡片是
2 倍超采样画完没缩回来的产物），所以 1 设计单位 = 2 像素。本脚本不硬编码这个
倍率，而是从量出来的卡片外框反推，x / y 两轴分别校验。

只依赖 PIL。

用法：python tools/measure_preview.py
"""

import io
import os
import sys

from PIL import Image

# ── 设计网格 ──────────────────────────────────────────────────────
#
# 与 TitleWindow.xaml.cs 里的 DesignW / DesignH 一致；310 这个宽**包含右侧竖条**。
DESIGN_W = 310.0
DESIGN_H = 87.0

# ── 预览图里的固定色 ──────────────────────────────────────────────
#
# 键色在预览图里不是真的键色：PIL 画布用深灰 (28,32,38) 当底，代表窗口底色。
# 本窗口运行时那里是 config.KeyColor（默认纯绿 #00FF00），由采集端抠掉。
CANVAS_BG = (28, 32, 38)
GRAY = (218, 224, 229)  # 灰底
EDGE = (242, 245, 247)  # 描边 #F2F5F7
BAR = (0, 84, 183)  # 蓝条 #0054B7
ACCENT = (0, 70, 165)  # 竖条 #0046A5
CHIP_ON_AIR = (15, 85, 136)  # 色块·正在播送 #0F5588（曾为青绿 #48C4A0）
CHIP_PENDING = (220, 55, 55)  # 色块·即将播送 #DC3737

TOL = 8  # 色段内的逐通道抖动
BG_TOL = 12  # 判「是不是底色」的容差
BOX_TOL = 60  # 找色块外框的容差（色块有抗锯齿过渡像素）
INK_ALPHA = 0.85  # 墨迹覆盖度阈值，见 ink_box()
INK_RATIO = 0.86  # 汉字墨迹高约占 em 的比例（黑体/雅黑粗体）
WHITE = (255, 255, 255)
BLACK = (20, 20, 20)


def load():
    path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "title-preview.png")
    return Image.open(path).convert("RGB")


def near(c, t, tol=TOL):
    return sum(abs(a - b) for a, b in zip(c, t)) <= tol


def first_run(vals):
    """布尔序列里第一段连续 True 的 (起, 止)，闭区间。"""
    start = None
    for i, v in enumerate(vals + [False]):
        if v and start is None:
            start = i
        elif not v and start is not None:
            return start, i - 1
    return None


def find_card(px, w, h):
    """找第一张卡片的外框，返回 (x0, y0, x1, y1)，像素闭区间。

    两个坑：
    1. 预览图左边那列状态名文字（"正在播送"）也是非底色，不能算进卡片。所以按
       **整行的非底色像素数**找卡片行，而不是取极值。
    2. 判非底色也不能太严：黑色机位名笔画边缘的抗锯齿像素里，会混进与底色
       差值 < 12 的点。用"最长连续段"找行会被这些点从中间切断，短到看不出
       那是张卡片。
    """
    rows = []
    for y in range(h):
        n = sum(1 for x in range(w) if not near(px[x, y], CANVAS_BG, BG_TOL))
        if n > w * 0.4:
            rows.append(y)
    band = first_run([y in rows for y in range(h)])
    y0, y1 = band

    # 左沿取「整段高度都非底色」的第一列——卡片左描边从顶贯到底，一定满足。
    x0 = next(
        x
        for x in range(w)
        if all(not near(px[x, y], CANVAS_BG, BG_TOL) for y in range(y0, y1 + 1))
    )
    # 右沿从 x0 起向右连续延伸，竖条那半截只有上半段非底色，所以不能要求整高。
    x1 = max(
        x
        for x in range(x0, w)
        if any(not near(px[x, y], CANVAS_BG, BG_TOL) for y in range(y0, y1 + 1))
    )
    return x0, y0, x1, y1


class Ruler:
    """像素坐标 -> 设计单位。

    像素段 [a, b] 对应设计区间 [ux(a), ux(b + 1)]：边界落在像素之间，右端要 +1。
    x / y 两轴倍率分别算（正常情况下都是 2）。
    """

    def __init__(self, box):
        self.x0, self.y0, self.x1, self.y1 = box
        self.kx = (self.x1 - self.x0 + 1) / DESIGN_W
        self.ky = (self.y1 - self.y0 + 1) / DESIGN_H

    def ux(self, p):
        return (p - self.x0) / self.kx

    def uy(self, p):
        return (p - self.y0) / self.ky

    def uw(self, n):
        return n / self.kx

    def uh(self, n):
        return n / self.ky

    def hsegs(self, px, y):
        """沿 y 这一行切色段，返回 [(x_start, x_end, color)]，像素闭区间。"""
        out, s, prev = [], self.x0, px[self.x0, y]
        for x in range(self.x0 + 1, self.x1 + 1):
            c = px[x, y]
            if sum(abs(a - b) for a, b in zip(c, prev)) > TOL:
                out.append((s, x - 1, prev))
                s, prev = x, c
        out.append((s, self.x1, prev))
        return out

    def vsegs(self, px, x):
        """沿 x 这一列切色段，返回 [(y_start, y_end, color)]，像素闭区间。"""
        out, s, prev = [], self.y0, px[x, self.y0]
        for y in range(self.y0 + 1, self.y1 + 1):
            c = px[x, y]
            if sum(abs(a - b) for a, b in zip(c, prev)) > TOL:
                out.append((s, y - 1, prev))
                s, prev = y, c
        out.append((s, self.y1, prev))
        return out


def show(segs, r, horizontal):
    for a, b, c in segs:
        if horizontal:
            print(f"    {r.ux(a):7.2f}..{r.ux(b + 1):7.2f}  宽 {r.uw(b - a + 1):5.2f}  {c}")
        else:
            print(f"    {r.uy(a):7.2f}..{r.uy(b + 1):7.2f}  高 {r.uh(b - a + 1):5.2f}  {c}")


def pick(segs, color, which, horizontal, r):
    """取第 which 段最接近 color 的那一段，返回设计单位下的 (起点, 终点)。

    两条约束：
    - 匹配容差必须收紧到 TOL。蓝条 (0,84,183) 与竖条 (0,70,165) 只差 32，放宽
      容差会把蓝条当竖条读出来——竖条的宽度会变成 282.5 这种一眼就荒谬的数。
    - 一条扫描线上同一种颜色常出现多次（描边就有左、右、下三条），所以调用点要
      显式说清取第几段，否则读到的是"最近的那一段"，改扫描线就悄悄读错位置。
    """
    hits = [(a, b) for a, b, c in segs if sum(abs(p - q) for p, q in zip(c, color)) <= TOL]
    if len(hits) <= which:
        raise SystemExit(f"扫描线上找不到第 {which} 段接近 {color} 的色段")
    a, b = hits[which][0], hits[which][1] + 1
    return (r.ux(a), r.ux(b)) if horizontal else (r.uy(a), r.uy(b))


def color_box(px, box, color, tol):
    """按颜色找一个矩形色块的外框，返回像素闭区间 (x0, y0, x1, y1)。"""
    hit = None
    for y in range(box[1], box[3] + 1):
        for x in range(box[0], box[2] + 1):
            if near(px[x, y], color, tol):
                hit = (
                    min(hit[0], x) if hit else x,
                    min(hit[1], y) if hit else y,
                    max(hit[2], x) if hit else x,
                    max(hit[3], y) if hit else y,
                )
    if hit is None:
        raise SystemExit(f"卡片里找不到接近 {color} 的色块")
    return hit


def ink_box(px, x0, y0, x1, y1, fg, bg):
    """墨迹外框（像素闭区间）；只算**覆盖度** >= INK_ALPHA 的笔画像素。

    覆盖度 α = (像素 - 底色) / (字色 - 底色)，取三通道里 |字色-底色| 最大的那一路
    （本图三处都是红通道）。用覆盖率而不是"纯白/纯黑"来卡阈值，是因为纯白纯黑
    的像素一个都没有，全靠阈值切在抗锯齿边上：赛事名按 α>=0.85 卡量到 22.0，
    放宽到 α>=0.80 就变成 22.5——那 0.5 全是笔画边缘的半覆盖像素，算进字号
    会把字撑大一号。0.85 ~ 1.0 之间三个高度都不跳，是这段阈值的平台区。
    """
    ch = max(range(3), key=lambda i: abs(fg[i] - bg[i]))
    span = abs(fg[ch] - bg[ch])
    sign = 1 if fg[ch] > bg[ch] else -1
    hit = []
    for y in range(y0, y1):
        for x in range(x0, x1):
            if (px[x, y][ch] - bg[ch]) * sign >= span * INK_ALPHA:
                hit.append((x, y))
    if not hit:
        raise SystemExit("范围内没找到墨迹，检查扫描范围或 INK_ALPHA")
    return (
        min(p[0] for p in hit),
        min(p[1] for p in hit),
        max(p[0] for p in hit),
        max(p[1] for p in hit),
    )


def main():
    sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")

    im = load()
    px = im.load()
    w, h = im.size
    box = find_card(px, w, h)
    r = Ruler(box)

    # 四条扫描线全部避开文字，色段干净、段界就是元素边界：
    #   y=设计 6   在蓝条描边（0..3）与赛事名墨迹（11..33）之间
    #   y=设计 46  蓝条下沿 45.5 与色块上沿 46 之间那一行，顺带量出色块左沿
    #   x=设计 250 在赛事名墨迹（64..224）与色块（止于 74）右侧，一条切齐五条横带
    #   x=设计 299 竖条列
    def yline(v):
        return box[1] + int(round(v * r.ky))

    def xline(v):
        return box[0] + int(round(v * r.kx))

    Y_BAR, Y_ROW = yline(6.0), yline(46.0)
    X_BODY, X_ACCENT = xline(250.0), xline(299.0)

    hb = r.hsegs(px, Y_BAR)
    hg = r.hsegs(px, Y_ROW)
    vb = r.vsegs(px, X_BODY)
    va = r.vsegs(px, X_ACCENT)

    print(f"图片 {w} x {h}")
    print(
        f"第一张卡片 像素 x {box[0]}..{box[2]}  y {box[1]}..{box[3]}"
        f"   {box[2] - box[0] + 1} x {box[3] - box[1] + 1} 像素"
    )
    print(
        f"换算倍率 1 设计单位 = {r.kx:.3f} x {r.ky:.3f} 像素"
        f"   （设计网格 {DESIGN_W:.0f} x {DESIGN_H:.0f}）"
    )
    print(f"卡片整体 x 0.00..{r.ux(box[2] + 1):.2f}   y 0.00..{r.uy(box[3] + 1):.2f}\n")

    print("── 横向扫描（避开文字）──")
    print(f"  [设计 y = 6]  蓝条行")
    show(hb, r, True)
    print(f"  [设计 y = 46] 蓝条下沿与色块之间那一行")
    show(hg, r, True)
    print("\n── 纵向扫描（避开文字）──")
    print("  [设计 x = 250] 主体列")
    show(vb, r, False)
    print("  [设计 x = 299] 竖条列")
    show(va, r, False)

    # 蓝条行：描边 | 蓝 | 描边 | 竖条
    left_edge = pick(hb, EDGE, 0, True, r)
    bar_fill = pick(hb, BAR, 0, True, r)
    bar_outer_r = pick(hb, EDGE, 1, True, r)
    accent_x = pick(hb, ACCENT, 0, True, r)
    # 底行：描边 | 灰(1 像素) | 色块 | 灰 | 描边 | 底色
    chip_x = pick(hg, CHIP_ON_AIR, 0, True, r)
    gray_fill = pick(hg, GRAY, 1, True, r)
    gray_outer_r = pick(hg, EDGE, 1, True, r)
    # 主体列：描边 | 蓝 | 描边 | 灰 | 描边 | 底色
    top_edge = pick(vb, EDGE, 0, False, r)
    bar_v = pick(vb, BAR, 0, False, r)
    mid_edge = pick(vb, EDGE, 1, False, r)
    gray_v = pick(vb, GRAY, 0, False, r)
    bottom_edge = pick(vb, EDGE, 2, False, r)
    # 竖条列：竖条 | 底色
    accent_y = pick(va, ACCENT, 0, False, r)

    # 只在**灰底那条带**里找色块，不能拿整张卡片去找。
    #
    # 找外框用的是 near()，判据是三通道差值**求和** <= BOX_TOL(60)，不是取最大，
    # 所以容差比看上去宽得多也紧得多（要三个通道一起接近）。色块曾是青绿
    # #48C4A0 时它与蓝条差 15+112+23=150、竖条差 72+126+5=203，都碰不上，整卡搜索
    # 一直没出问题。改成蓝 #0F5588 之后：与蓝条差 63（差一点，仍然不匹配），
    # 与竖条 (0,70,165) 差 **59 —— 匹配上了**，竖条被并进外框。
    #
    # 而且带子的上沿必须是**灰底上沿**（45.5），不能用蓝条下沿（42.5）：后者正好切进
    # 竖条最后 3 个设计单位，一并被算进来。
    #
    # 靠缩容差治不了：色块颜色还会再变，颜色本身不该成为「它不会跟别的元素撞」的
    # 隐含前提。用位置限定搜索范围，换多少次颜色都不影响。
    chip_band = (box[0], yline(gray_v[0]), box[2], yline(bottom_edge[0]))
    chip = color_box(px, chip_band, CHIP_ON_AIR, BOX_TOL)
    # 色块不可能占满整条灰底带。真的占满了，说明上面的定位条件被某个改动破坏了，
    # 而外框算错只会静悄悄地把后面的字号也带偏——这里宁可停下来。
    if chip[1] <= chip_band[1] and chip[3] >= chip_band[3]:
        raise SystemExit(
            f"色块外框吃满了整条灰底带（像素 y {chip_band[1]}..{chip_band[3]}），"
            "多半是搜索范围没把别的元素排除干净，而不是色块真的这么高"
        )
    chip_ink = ink_box(px, chip[0], chip[1], chip[2] + 1, chip[3] + 1, WHITE, CHIP_ON_AIR)
    # 赛事名：在蓝条**填充**范围内取墨迹，边界各缩进 1 个单位，免得描边混进来。
    event_ink = ink_box(
        px,
        xline(bar_fill[0] + 1),
        yline(bar_v[0] + 1),
        xline(bar_fill[1]),
        yline(bar_v[1]),
        WHITE,
        BAR,
    )
    # 机位名：色块右侧到灰底填充右沿之间。
    prog_ink = ink_box(
        px,
        xline(chip_x[1]),
        yline(gray_v[0]),
        xline(gray_fill[1]),
        yline(gray_v[1]),
        BLACK,
        GRAY,
    )

    border = gray_outer_r[1] - gray_outer_r[0]
    chip_h = r.uh(chip[3] - chip[1] + 1)
    chip_ink_h = r.uh(chip_ink[3] - chip_ink[1] + 1)
    card_w = r.ux(box[2] + 1)
    card_h = r.uy(box[3] + 1)

    print("\n=== 实测结果（设计单位） ===")
    print(f"  卡片整体       x 0.00..{card_w:7.2f}   y 0.00..{card_h:7.2f}")
    print(
        f"  描边宽         左 {left_edge[1] - left_edge[0]:5.2f}   上 {top_edge[1] - top_edge[0]:5.2f}"
        f"   右 {border:5.2f}   下 {bottom_edge[1] - bottom_edge[0]:5.2f}"
    )
    print(
        f"  蓝条           x 0.00..{bar_outer_r[1]:7.2f}   y 0.00..{mid_edge[1]:7.2f}"
        f"   (填充 x {bar_fill[0]:.2f}..{bar_fill[1]:.2f}  y {bar_v[0]:.2f}..{bar_v[1]:.2f})"
    )
    print(
        f"  竖条           x {accent_x[0]:7.2f}..{accent_x[1]:7.2f}   y 0.00..{accent_y[1]:7.2f}"
        f"   (宽 {accent_x[1] - accent_x[0]:.2f})"
    )
    print(
        f"  灰底           x 0.00..{gray_outer_r[1]:7.2f}   y {gray_v[0]:7.2f}..{bottom_edge[1]:7.2f}"
        f"   (高 {bottom_edge[1] - gray_v[0]:.2f})"
    )
    print(f"  蓝条填充右沿   {bar_fill[1]:7.2f}   = 蓝条外框 - 描边 {border:.2f}")
    print(f"  灰底填充右沿   {gray_fill[1]:7.2f}   = 灰底外框 - 描边 {border:.2f}")
    print(f"  色块           x {chip_x[0]:7.2f} 起   高 {chip_h:6.2f}   (灰底高 {bottom_edge[1] - gray_v[0]:.2f})")
    print(
        f"  色块内边距     左 {r.ux(chip_ink[0]) - r.ux(chip[0]):5.2f}"
        f"   右 {r.ux(chip[2] + 1) - r.ux(chip_ink[2] + 1):5.2f}"
        f"   （两侧不等：预览图按墨迹而非字宽居中，恒差半个像素）"
    )
    print(
        f"  色块右沿       {r.ux(chip[2] + 1):7.2f}   机位名墨迹左沿 {r.ux(prog_ink[0]):7.2f}"
        f"   墨迹间距 {r.ux(prog_ink[0]) - r.ux(chip[2] + 1):6.2f}"
    )
    print("                 ↑ 机位名是居中的，墨迹间距比排版间距大，不能当间距用")
    print()
    for label, ib in (
        ("赛事名白字", event_ink),
        ("机位名黑字", prog_ink),
        ("色块内白字", chip_ink),
    ):
        ink = r.uh(ib[3] - ib[1] + 1)
        print(f"  {label}墨迹   高 {ink:6.2f}  ->  字号 {ink / INK_RATIO:6.2f}   (墨迹/em ≈ {INK_RATIO})")
    print(f"  色块字号按行高给的比例   {chip_ink_h / INK_RATIO / chip_h:.4f}   （= {chip_ink_h / INK_RATIO:.1f} / {chip_h:.0f}）")

    step_x = max(bar_outer_r[1], accent_x[0], gray_outer_r[1])
    step_y = max(accent_y[1], gray_v[0])
    print("\n=== 阶梯轮廓：没有元素覆盖、只有底色（= 键色）的区域 ===")
    print(
        f"  x {step_x:.2f}..{card_w:.2f}   y {step_y:.2f}..{card_h:.2f}"
        f"   {card_w - step_x:.2f} x {card_h - step_y:.2f}"
        f"   —— 就这一块，右下角与竖条下方是同一块"
    )
    print(
        f"  竖条与蓝条之间没有缝：{bar_fill[1]:.2f}..{bar_outer_r[1]:.2f}"
        f" 是蓝条自己的右描边，不是留出来的空隙"
    )
    print(
        f"  蓝条与灰底之间没有缝：{mid_edge[0]:.2f}..{mid_edge[1]:.2f}"
        f" 是蓝条自己的下描边，不是留出来的空隙"
    )


if __name__ == "__main__":
    main()