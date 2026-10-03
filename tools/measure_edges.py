"""一次性测量脚本：钉死 tools/reference.png 里卡片各条边的位置。

用法：python tools/measure_edges.py
"""

from PIL import Image

im = Image.open(r"tools/reference.png").convert("RGB")
px = im.load()
W, H = im.size

# 设计网格：设计宽固定 310，注释里同时给出参考图原始像素。
BBOX = (14, 129, 2157, 565)  # x0, y0, x1, y1（含）
K = 310.0 / (BBOX[2] - BBOX[0] + 1)


def u(px_x, px_y):
    """参考图像素坐标 -> 设计单位。"""
    return (px_x - BBOX[0]) * K, (px_y - BBOX[1]) * K


def hscan(y, x0, x1, label):
    print(f"[{label}] y={y}")
    prev = px[x0, y]
    start = x0
    for x in range(x0 + 1, x1):
        c = px[x, y]
        if sum(abs(a - b) for a, b in zip(c, prev)) > 6:
            print(f"    x {start:>5}..{x - 1:<5} {prev}")
            start, prev = x, c
    print(f"    x {start:>5}..{x1:<5} {prev}")


def vscan(x, y0, y1, label):
    print(f"[{label}] x={x}")
    prev = px[x, y0]
    start = y0
    for y in range(y0 + 1, y1):
        c = px[x, y]
        if sum(abs(a - b) for a, b in zip(c, prev)) > 6:
            print(f"    y {start:>5}..{y - 1:<5} {prev}")
            start, prev = y, c
    print(f"    y {start:>5}..{y1:<5} {prev}")


print(f"图片 {W}x{H}")
print(f"内容区 {BBOX[2]-BBOX[0]+1} x {BBOX[3]-BBOX[1]+1} 像素 -> 设计单位 310 x {(BBOX[3]-BBOX[1]+1)*K:.1f}\n")

hscan(133, 0, 45, "蓝条左端")
print()
hscan(133, 2030, 2172, "蓝条右端/缝隙/竖条")
print()
vscan(600, 120, 150, "蓝条上沿")
print()
vscan(600, 340, 380, "蓝条下沿到灰底")
print()
hscan(563, 2055, 2172, "灰底右端")
print()
vscan(2120, 120, 145, "竖条上沿")
print()
vscan(2120, 350, 380, "竖条下沿")

print("\n=== 换算成设计单位 ===")
for name, box in {
    "蓝条填充": (21, 139, 2047, 348),
    "竖条": (2079, 129, 2156, 358),
    "灰底": (14, 361, 2075, 564),
}.items():
    x0, y0 = u(box[0], box[1])
    x1, y1 = u(box[2] + 1, box[3] + 1)
    print(f"  {name:<8} x {x0:6.2f}..{x1:6.2f}  y {y0:6.2f}..{y1:6.2f}")