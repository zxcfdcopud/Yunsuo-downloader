"""生成 Fluent Downloader 的应用图标（多尺寸 .ico）。

用 DSH 自带 Python（含 Pillow）运行：
    python tools/../installer/make_icon.py

产出：
    installer/app.ico                     安装包与主程序图标
    src/FluentDownloader.App/Resources/app.ico   程序内置图标（托盘也会用它）
"""
import os
import math
from PIL import Image, ImageDraw, ImageFilter

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT_ICO = os.path.join(ROOT, "installer", "app.ico")
OUT_APP = os.path.join(ROOT, "src", "FluentDownloader.App", "Resources", "app.ico")

S = 1024          # 超采样尺寸
SS = 4            # 超采样倍数（先画大图再缩小，边缘更干净）
W = S * SS

# ---------------------------------------------------------------- 颜色
TOP = (86, 156, 255)      # 浅一点的 Fluent 蓝
BOTTOM = (0, 95, 204)     # 深一点的 Fluent 蓝
WHITE = (255, 255, 255, 255)


def rounded_mask(size, radius):
    m = Image.new("L", (size, size), 0)
    d = ImageDraw.Draw(m)
    d.rounded_rectangle([0, 0, size - 1, size - 1], radius=radius, fill=255)
    return m


def make_base():
    # 竖向渐变
    grad = Image.new("RGBA", (W, W))
    gd = ImageDraw.Draw(grad)
    for y in range(W):
        t = y / (W - 1)
        r = int(TOP[0] + (BOTTOM[0] - TOP[0]) * t)
        g = int(TOP[1] + (BOTTOM[1] - TOP[1]) * t)
        b = int(TOP[2] + (BOTTOM[2] - TOP[2]) * t)
        gd.line([(0, y), (W, y)], fill=(r, g, b, 255))

    # 左上角加一点高光，接近 Fluent 的亚克力质感
    glow = Image.new("L", (W, W), 0)
    ImageDraw.Draw(glow).ellipse([-W * 0.35, -W * 0.55, W * 0.95, W * 0.45], fill=110)
    glow = glow.filter(ImageFilter.GaussianBlur(W // 12))
    grad = Image.composite(Image.new("RGBA", (W, W), (255, 255, 255, 255)), grad, glow)

    # 圆角
    base = Image.new("RGBA", (W, W), (0, 0, 0, 0))
    base.paste(grad, (0, 0), rounded_mask(W, int(W * 0.22)))
    return base


def make_glyph(size):
    """下载箭头 + 托盘横线（白色）。"""
    layer = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)

    w = size
    cx = w / 2
    shaft_w = w * 0.135
    shaft_top = w * 0.215
    shaft_bottom = w * 0.545
    head_half = w * 0.20
    head_top = w * 0.455
    head_bottom = w * 0.735

    # 箭头竖杆
    d.rounded_rectangle(
        [cx - shaft_w / 2, shaft_top, cx + shaft_w / 2, shaft_bottom],
        radius=shaft_w / 2, fill=WHITE)
    # 箭头三角
    d.polygon([(cx - head_half, head_top), (cx + head_half, head_top), (cx, head_bottom)], fill=WHITE)
    # 托盘横线
    bar_w = w * 0.52
    bar_h = w * 0.115
    bar_top = w * 0.775
    d.rounded_rectangle(
        [cx - bar_w / 2, bar_top, cx + bar_w / 2, bar_top + bar_h],
        radius=bar_h / 2, fill=WHITE)
    return layer


def build():
    base = make_base()
    glyph = make_glyph(W)
    img = Image.alpha_composite(base, glyph)
    return img.resize((S, S), Image.LANCZOS)


def main():
    icon = build()

    os.makedirs(os.path.dirname(OUT_ICO), exist_ok=True)
    os.makedirs(os.path.dirname(OUT_APP), exist_ok=True)

    sizes = [(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)]
    icon.save(OUT_ICO, format="ICO", sizes=sizes)
    icon.save(OUT_APP, format="ICO", sizes=sizes)
    icon.resize((256, 256), Image.LANCZOS).save(os.path.join(os.path.dirname(OUT_ICO), "app.png"), format="PNG")

    for p in (OUT_ICO, OUT_APP):
        print(f"{p}  {os.path.getsize(p)} bytes")
    print("sizes:", ", ".join(f"{w}x{h}" for w, h in sizes))


if __name__ == "__main__":
    main()
