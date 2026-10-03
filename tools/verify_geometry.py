"""核对 TitleWindow.xaml.cs 的几何常量与 tools/title-preview.png 的实测值。

⚠️ 实测值一律量自 tools/title-preview.png——**那张图才是本项目的样式参照物**。
   tools/reference.png（央视原始截图）不是：它的卡片是 4.92:1，参照图是 3.563:1，
   比例对不上。量它的 tools/measure_edges.py 已删除。

用法：
    python tools/measure_preview.py     # 先重量，看实际量到什么
    python tools/verify_geometry.py     # 再核对代码是否与之一致

单位统一为「设计单位」，设计网格 310 x 87（310 这个宽**包含右侧竖条**）。
源码里的常数是除以 DesignW / DesignH 的比例，这里换算回设计单位再比。
任何一项偏差超过容差就 exit 1。

改动流程：改常量 -> 同步改这里 MEASURED -> 跑本脚本 -> dotnet build -c Release。
"""

import io
import os
import re
import sys

# ── 实测值（tools/measure_preview.py 的输出，设计单位） ───────────────
#
# 量自 title-preview.png 第一张卡片（正在播送态），四条扫描线全部避开文字，
# 详细推导见 measure_preview.py 的输出。
MEASURED = {
    # 描边宽（四边都是）
    "border": 3.0,
    # 蓝条外框 x0,y0,x1,y1（不含键色）
    "bar": (0.0, 0.0, 288.5, 45.5),
    # 竖条外框 x0,y0,x1,y1
    "accent": (288.5, 0.0, 310.0, 45.5),
    # 灰底外框 x0,y0,x1,y1
    "gray": (0.0, 45.5, 288.5, 87.0),
    # 描边内沿，也就是填充的右沿 = 外框 - 描边
    "bar_fill_r": 285.5,
    "gray_fill_r": 285.5,
    # 色块
    "chip_x": 3.5,
    "chip_h": 38.0,
    # 色块内白字距色块左沿的留白（右侧量到 6.00，两侧差半像素，见 measure_preview.py）
    "chip_pad": 5.5,
    # 字号：由墨迹高 / 0.86 反推
    "event_font": 22.0 / 0.86,
    "prog_font": 18.0 / 0.86,
    "chip_font": 14.5 / 0.86,
}

# 量不出来、只作为排版常量的（机位名是居中的，图上量到的 42.5 是墨迹间距，
# 比排版间距大，不能拿来核对）
LAYOUT_ONLY = {"ChipGap": 6.0}

TOL = 0.6  # 设计单位，约 1.2 预览图像素

SRC = os.path.join(
    os.path.dirname(os.path.abspath(__file__)), "..", "TitleWindow.xaml.cs"
)

# 只允许常量表达式里出现的东西；eval 前先过这一关，不给源码里的字符串留口子。
SAFE = re.compile(r"^[0-9A-Za-z_.\s()*/+-]+$")
CONST = re.compile(r"private const double (\w+)\s*=\s*([^;]+);")


def read_constants(path):
    """按源码顺序抽出 private const double，缺一个就报错。"""
    env = {}
    with io.open(path, encoding="utf-8") as f:
        for name, expr in CONST.findall(f.read()):
            expr = expr.strip()
            if not SAFE.match(expr):
                raise SystemExit(f"常量 {name} 的表达式 {expr!r} 里有非常量成分")
            env[name] = eval(expr, {"__builtins__": {}}, env)  # noqa: S307
    return env


def main():
    sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")

    if not os.path.isfile(SRC):
        raise SystemExit(f"找不到 {SRC}")
    k = read_constants(SRC)

    need = [
        "DesignW",
        "DesignH",
        "BorderU",
        "GrayW",
        "GrayY",
        "BarW",
        "BarH",
        "AccentX",
        "AccentW",
        "AccentH",
        "ChipX",
        "ChipPad",
        "ChipGap",
        "RowH",
        "EventFontPx",
        "ProgFontPx",
        "ChipFontRatio",
    ]
    missing = [n for n in need if n not in k]
    if missing:
        # 这正是上一轮把代码打坏时的症状：常量被删了，Layout() 还在引用它。
        raise SystemExit("源码里少了这些常量：" + "、".join(missing))

    W, H = k["DesignW"], k["DesignH"]
    gray_top = k["GrayY"] * H
    gray_h = H - gray_top
    row_h = gray_h * k["RowH"]

    checks = [
        ("描边宽", MEASURED["border"], k["BorderU"] * W),
        ("蓝条 左边", MEASURED["bar"][0], 0.0),
        ("蓝条 上边", MEASURED["bar"][1], 0.0),
        ("蓝条 右边", MEASURED["bar"][2], k["BarW"] * W),
        ("蓝条 下边", MEASURED["bar"][3], k["BarH"] * H),
        ("竖条 左边", MEASURED["accent"][0], k["AccentX"] * W),
        ("竖条 上边", MEASURED["accent"][1], 0.0),
        ("竖条 右边", MEASURED["accent"][2], (k["AccentX"] + k["AccentW"]) * W),
        ("竖条 下边", MEASURED["accent"][3], k["AccentH"] * H),
        ("灰底 左边", MEASURED["gray"][0], 0.0),
        ("灰底 上边", MEASURED["gray"][1], gray_top),
        ("灰底 右边", MEASURED["gray"][2], k["GrayW"] * W),
        ("灰底 下边", MEASURED["gray"][3], H),
        ("蓝条填充右沿", MEASURED["bar_fill_r"], k["BarW"] * W - k["BorderU"] * W),
        ("灰底填充右沿", MEASURED["gray_fill_r"], k["GrayW"] * W - k["BorderU"] * W),
        ("色块 左沿", MEASURED["chip_x"], k["ChipX"] * W),
        ("色块 高", MEASURED["chip_h"], row_h),
        ("色块内边距", MEASURED["chip_pad"], k["ChipPad"] * W),
        ("赛事名字号", MEASURED["event_font"], k["EventFontPx"]),
        ("机位名字号", MEASURED["prog_font"], k["ProgFontPx"]),
        ("色块内字号", MEASURED["chip_font"], k["ChipFontRatio"] * row_h),
    ]

    # 代码自身的结构不变量：这几条一旦被改坏，成品就会退化成"凸出标签 + 带缝"的
    # 卡片，而每一项单独看都还在容差里，所以要单独钉死。
    invariants = [
        (
            "竖条与蓝条齐平（高度差）",
            0.0,
            (k["AccentH"] - k["BarH"]) * H,
        ),
        (
            "竖条右沿贴卡片右沿（差）",
            0.0,
            W - (k["AccentX"] + k["AccentW"]) * W,
        ),
        (
            "灰底右沿与蓝条右沿成一条边（差）",
            0.0,
            (k["GrayW"] - k["BarW"]) * W,
        ),
        (
            "竖条与蓝条之间的缝（宽）",
            0.0,
            (k["AccentX"] - k["BarW"]) * W,
        ),
        (
            "蓝条与灰底之间的缝（高）",
            0.0,
            gray_top - k["BarH"] * H,
        ),
    ]

    print(f"设计网格 {W:.0f} x {H:.0f}，容差 ±{TOL} 设计单位")
    print("（源码里的常数是比例，这里换算回设计单位再比）")
    print("实测值量自 tools/title-preview.png —— 那张图才是样式参照物\n")

    worst = 0.0
    bad = []
    print(f"{'项目':<26}{'实测':>9}{'实现':>10}{'偏差':>8}")
    for name, meas, got in checks:
        d = got - meas
        worst = max(worst, abs(d))
        ok = abs(d) <= TOL
        if not ok:
            bad.append(name)
        print(f"{name:<26}{meas:9.2f}{got:10.2f}{d:8.2f}  {'OK' if ok else '偏差过大'}")

    print("\n=== 结构不变量（代码自身必须自洽）===")
    for name, meas, got in invariants:
        d = got - meas
        worst = max(worst, abs(d))
        ok = abs(d) <= TOL
        if not ok:
            bad.append(name)
        print(f"{name:<26}{meas:9.2f}{got:10.2f}{d:8.2f}  {'OK' if ok else '偏差过大'}")

    bar_r = k["BarW"] * W
    gray_r = k["GrayW"] * W
    acc_l = k["AccentX"] * W
    gray_t = gray_top
    acc_b = k["AccentH"] * H
    bar_b = k["BarH"] * H

    print("\n=== 键色区域：卡片没覆盖的地方会露出窗口底色，由采集端抠掉 ===")
    gap_x0 = max(bar_r, acc_l, gray_r)
    gap_y0 = max(bar_b, acc_b, gray_t)
    print(
        f"  右下角 + 竖条下方（同属一块）  x {gap_x0:.2f}..{W:.2f}"
        f"   y {gap_y0:.2f}..{H:.2f}   {W - gap_x0:.2f} x {H - gap_y0:.2f}"
    )
    print(
        f"  竖条与蓝条之间没有缝：{bar_r - k['BorderU'] * W:.2f}..{bar_r:.2f}"
        f"  那 {k['BorderU'] * W:.2f} 是蓝条自己的右描边"
    )
    print(
        f"  蓝条与灰底之间没有缝：{bar_b - k['BorderU'] * W:.2f}..{bar_b:.2f}"
        f"  那 {k['BorderU'] * W:.2f} 是蓝条自己的下描边"
    )

    print("\n=== 量不出来、只作排版用的常量（不参与比对）===")
    for name, v in LAYOUT_ONLY.items():
        print(f"  {name:<26}{v:9.2f}{k[name] * W:10.2f}   （机位名居中，图上量不到）")

    print(f"\n最大偏差 {worst:.2f} 设计单位")
    if bad:
        print("未对齐：" + "、".join(bad))
        raise SystemExit(1)
    print("全部对齐")


if __name__ == "__main__":
    main()