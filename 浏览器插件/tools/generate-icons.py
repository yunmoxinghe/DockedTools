#!/usr/bin/env python3
"""
生成扩展图标：src/icons/icon-{16,32,48,128}.png

【为什么必须有真实 PNG】
Chrome 商店提交强制要求 16/32/48/128 四档 PNG，
manifest 里引用不存在的文件、或者四个尺寸共用一张图，都会在加载/审核阶段出问题。
manifest 里现在声明了 icons，所以这些文件**必须真实存在且尺寸精确**。

【图形语义】
左侧三条横条 = 网页条目，右侧竖条 = 边栏 —— "把网页存进边栏助手"。

【为什么要 4 倍超采样再缩小】
16px 下直接画圆角矩形，抗锯齿边缘会糊成一团。
先按 4 倍尺寸画再用 LANCZOS 缩小，小尺寸也能保持边缘干净。

用法：
    python 浏览器插件/tools/generate-icons.py
"""
import os
import sys

from PIL import Image, ImageDraw

OUT_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'src', 'icons')
SIZES = [16, 32, 48, 128]
SUPERSAMPLE = 4

BG = (76, 110, 245, 255)
FG = (255, 255, 255, 255)


def draw_icon(size: int) -> Image.Image:
    s = size * SUPERSAMPLE
    img = Image.new('RGBA', (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    m = round(s * 0.03)
    d.rounded_rectangle([m, m, s - m, s - m], radius=round(s * 0.22), fill=BG)

    d.rounded_rectangle(
        [round(s * 0.63), round(s * 0.17), round(s * 0.86), round(s * 0.83)],
        radius=round(s * 0.05),
        fill=FG,
    )

    for i in range(3):
        y = round(s * (0.28 + i * 0.20))
        d.rounded_rectangle(
            [round(s * 0.16), y, round(s * 0.50), y + round(s * 0.07)],
            radius=round(s * 0.03),
            fill=FG,
        )

    return img.resize((size, size), Image.LANCZOS)


def main() -> int:
    out = os.path.normpath(OUT_DIR)
    os.makedirs(out, exist_ok=True)

    for size in SIZES:
        img = draw_icon(size)
        if img.size != (size, size):
            print(f'尺寸不对：{img.size}，应为 {(size, size)}', file=sys.stderr)
            return 1
        path = os.path.join(out, f'icon-{size}.png')
        img.save(path, 'PNG')
        print(f'  {path}  {size}x{size}')

    return 0


if __name__ == '__main__':
    raise SystemExit(main())
