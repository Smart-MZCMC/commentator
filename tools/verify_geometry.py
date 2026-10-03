"""核对 TitleWindow.xaml.cs 的几何常量与 tools/measure_edges.py 的实测值。

单位统一为「设计单位」，设计网格 310 x 63。

用法：python tools/verify_geometry.py
"""

# tools/measure_edges.py 的实测换算结果，(x0, x1, y0, y1)，单位设计单位
MEASURED = {
    "bar":    (1.01, 294.10, 1.45, 31.81),
    "accent": (298.58, 309.86, 0.00, 33.26),
    "gray":   (0.00, 298.14, 33.54, 63.04),
}

# TitleWindow.xaml.cs 里的常量：(BarX, BarX*DW + BarW*DW, BarY, BarY*DH + BarH*DH) 等
DW, DH = 310.0, 63.0
BAR_X, BAR_Y, BAR_W, BAR_H = 1.0, 1.5, 293.1, 30.4
ACC_X, ACC_Y, ACC_W, ACC_H = 298.6, 0.0, 11.3, 33.3
GRAY_W, GRAY_Y = 298.1, 33.5

CS = {
    "bar":    (BAR_X, BAR_X + BAR_W, BAR_Y, BAR_Y + BAR_H),
    "accent": (ACC_X, ACC_X + ACC_W, ACC_Y, ACC_Y + ACC_H),
    "gray":   (0.0, GRAY_W, GRAY_Y, DH),
}

TOL = 0.6  # 设计单位，约 0.4 像素

print(f"设计网格 {DW} x {DH}，容差 ±{TOL} 设计单位")
print("(BW/BH 等常量在源码里是除以 DW/DH 的比例，这里还原成设计单位)\n")

worst = 0.0
bad = []
for name in ("bar", "accent", "gray"):
    m = MEASURED[name]
    c = CS[name]
    deltas = [c[i] - m[i] for i in range(4)]
    worst_here = max(abs(d) for d in deltas)
    worst = max(worst, worst_here)
    ok = worst_here <= TOL
    if not ok:
        bad.append(name)
    print(f"{name:<7} 实测 x{m[0]:7.2f}..{m[1]:7.2f}  y{m[2]:6.2f}..{m[3]:6.2f}")
    print(f"{'':<7} 实现 x{c[0]:7.2f}..{c[1]:7.2f}  y{c[2]:6.2f}..{c[3]:6.2f}   "
          f"最大偏差 {worst_here:.2f}  {'OK' if ok else '偏差过大'}")

print("\n=== 键色区域：卡片没覆盖的地方会露出窗口底色，由采集端抠掉 ===")
bar_r = CS["bar"][1]
acc_l = CS["accent"][0]
bar_b = CS["bar"][3]
gray_t = CS["gray"][2]
gray_r = CS["gray"][1]
print(f"  蓝条上/左留边   x 0..{CS['bar'][0]:.2f}   y 0..{CS['bar'][2]:.2f}")
print(f"  蓝条/竖条 之间的缝  x {bar_r:.2f}..{acc_l:.2f}   宽 {acc_l-bar_r:.2f}")
print(f"  蓝条/灰底 之间的缝  y {bar_b:.2f}..{gray_t:.2f}   高 {gray_t-bar_b:.2f}")
print(f"  灰底右下方缺口       x {gray_r:.2f}..{DW:.2f}  y {gray_t:.2f}..{DH:.2f}"
      f"   {DW-gray_r:.2f} x {DH-gray_t:.2f}")

print(f"\n最大偏差 {worst:.2f} 设计单位")
if bad:
    print("未对齐：" + "、".join(bad))
    raise SystemExit(1)
print("全部对齐")