from PIL import Image

im = Image.open(r"D:\Project\smart-mzcmc\CommentatorApp\tools\reference.png").convert("RGB")
w, h = im.size
print("size", w, h)

# 第一张卡（正在）大约 y=80..280，扫水平边界
for y in [90, 110, 140, 160, 180, 200, 230, 250]:
    changes = []
    prev = im.getpixel((100, y))
    for x in range(101, w):
        cur = im.getpixel((x, y))
        if abs(prev[0]-cur[0])+abs(prev[1]-cur[1])+abs(prev[2]-cur[2]) > 40:
            changes.append((x, prev, cur))
        prev = cur
    print(f"y={y} n={len(changes)}")
    for c in changes[:10]:
        print("   ", c)

# 垂直扫 x=400（蓝条中）
print("--- vertical x=400 ---")
prev = im.getpixel((400, 70))
for y in range(71, 300):
    cur = im.getpixel((400, y))
    if abs(prev[0]-cur[0])+abs(prev[1]-cur[1])+abs(prev[2]-cur[2]) > 40:
        print("  ", y, prev, "->", cur)
    prev = cur

# 垂直扫 x=2050（竖条区）
print("--- vertical x=2050 ---")
prev = im.getpixel((2050, 70))
for y in range(71, 300):
    cur = im.getpixel((2050, y))
    if abs(prev[0]-cur[0])+abs(prev[1]-cur[1])+abs(prev[2]-cur[2]) > 40:
        print("  ", y, prev, "->", cur)
    prev = cur

print("--- fills ---")
for x, y in [(400, 110), (400, 170), (400, 230), (2050, 110), (2050, 230)]:
    print(f"  ({x},{y})={im.getpixel((x,y))}")
