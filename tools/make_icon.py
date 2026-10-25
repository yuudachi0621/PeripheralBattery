# -*- coding: utf-8 -*-
"""
生成 app.ico —— 程序图标。

和托盘图标（TrayIcon.RenderIcon）是同一套视觉语言：
  · 圆角胶囊电池外框 + 右侧正极触点 + 内部电量填充；
  · 陶土橙 #D97757 作为品牌主色（Claude 主题的强调色）；
  · 纸白 #FAF9F5 作背景圆角底，保证在深色/浅色任务栏和资源管理器里都看得清。

和托盘图标的区别：托盘图标是"单色线条 + 电量填充"，因为它要在 16×16 里
跟一堆系统图标挤在一起；而程序图标（资源管理器、开始菜单、任务栏大图）
需要更强的识别度，所以做成"实心橙色底 + 白色电池"。

尺寸覆盖 Windows 实际会用到的全部档位：
  16  任务栏小图标 / 列表视图
  20  某些缩放比例下的任务栏
  24  小图标 + 125% 缩放
  32  桌面、开始菜单
  40  125% 缩放的 32
  48  中图标
  64  大图标
  128 超大图标
  256 详情窗格、Jumbo 视图（PNG 压缩存储）
"""
from PIL import Image, ImageDraw
import os

# ---- 调色板（与 UI/Theme.cs 一致）----
ACCENT = (0xD9, 0x77, 0x57)          # 陶土橙
ACCENT_DARK = (0xC4, 0x66, 0x48)     # 底部略深，做一点体积感
PAPER = (0xFA, 0xF9, 0xF5)           # 纸白
INK = (0x1F, 0x1E, 0x1B)             # 暖黑（仅用于极小的浅色描边）

SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]

# 每个尺寸都单独按比例重画（而不是把 256 缩小），
# 因为缩小会让 16×16 的电池边框糊成一片灰，直接重画才够干净。
SS = 8   # 超采样倍数：先按 8 倍画再缩，边缘不会有锯齿


def draw_icon(size: int) -> Image.Image:
    """按目标尺寸画一张正方形 RGBA 图。"""
    s = size * SS
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    # ---- 1. 背景：陶土橙圆角方块 ----
    # 圆角半径取边长的 22%，接近 Windows 11 的应用图标圆角观感
    radius = int(s * 0.22)
    d.rounded_rectangle([0, 0, s - 1, s - 1], radius=radius, fill=ACCENT)

    # ---- 2. 电池主体：实心白块 + 挖出橙色内腔 ----
    # 用"实心 + 挖空"而不是"描边"，是因为描边宽度在小尺寸下会被抗锯齿吃掉一半，
    # 16×16 时线条会发灰发虚；实心块缩小后依然是干净的纯白。
    bw = s * 0.52          # 电池主体宽
    bh = s * 0.34          # 电池主体高
    bx = (s - bw) / 2 - s * 0.048   # 左移，给右边触点腾位置
    by = (s - bh) / 2
    shell = max(SS * 1.1, s * 0.058)   # 壳厚
    box_r = int(bh * 0.26)

    d.rounded_rectangle([bx, by, bx + bw, by + bh], radius=box_r, fill=PAPER)

    # 内腔（橙色），四周留出 shell 的厚度
    d.rounded_rectangle(
        [bx + shell, by + shell, bx + bw - shell, by + bh - shell],
        radius=max(1, int(s * 0.020)),
        fill=ACCENT,
    )

    # ---- 3. 正极触点（和主体分离，不要连成一条线）----
    nub_w = max(SS * 1.0, s * 0.055)
    nub_h = bh * 0.42
    nub_x = bx + bw + s * 0.028
    d.rounded_rectangle(
        [nub_x, by + (bh - nub_h) / 2, nub_x + nub_w, by + (bh - nub_h) / 2 + nub_h],
        radius=max(1, int(nub_w * 0.35)),
        fill=PAPER,
    )

    # ---- 4. 电量填充 ----
    # 三个刻意为之的细节，都是为了让 16×16 还认得出这是电池：
    #   a) 填充块**左、上、下都贴住内腔边缘**，只有右侧留一大块橙底 —— 这样读起来是
    #      "电池左半边充满了"，而不是"内腔里飘着一个小方块"（飘着的那版小尺寸下像双层框）；
    #   b) 填充比例 60%，右侧橙底足够宽，缩到 16px 也不会被抗锯齿抹平；
    #   c) 填充块只在右侧做圆角，左侧跟内腔左壁严丝合缝。
    inner_x0 = bx + shell
    inner_x1 = bx + bw - shell
    inner_y0 = by + shell
    inner_y1 = by + bh - shell
    fill_x1 = inner_x0 + (inner_x1 - inner_x0) * 0.60
    if fill_x1 - inner_x0 > 1 and inner_y1 - inner_y0 > 1:
        d.rounded_rectangle(
            [inner_x0 - shell * 0.4, inner_y0, fill_x1, inner_y1],
            radius=max(1, int(s * 0.014)),
            fill=PAPER,
        )
        # 把左侧被圆角切掉的部分补回来，让它和内腔左壁完全连续
        d.rectangle([inner_x0 - shell * 0.4, inner_y0, inner_x0 + s * 0.012, inner_y1], fill=PAPER)

    return img.resize((size, size), Image.LANCZOS)


def main():
    out_dir = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    out_path = os.path.join(out_dir, "app.ico")

    # 关键：每档都单独按比例重画（不是把 256 缩下来），小图才不会糊。
    #
    # 存 ICO 的顺序有讲究：Pillow 会把"第一张"当作基准尺寸，
    # 后面 append_images 里的图按 ICO 头里的 sizes 逐个塞进去。
    # 这里从大到小传（256 → 16），并在 sizes 里给出完整档位表，
    # Windows 才会在任务栏(16/20/24)、桌面(32/48)、大图标(128/256) 各取所需。
    sizes_desc = sorted(SIZES, reverse=True)
    frames = [draw_icon(n) for n in sizes_desc]

    frames[0].save(
        out_path,
        format="ICO",
        sizes=[(n, n) for n in sizes_desc],
        append_images=frames[1:],
    )

    # 顺便导出一张 256 的 PNG，方便肉眼检查效果
    png_path = os.path.join(out_dir, "tools", "app-preview.png")
    draw_icon(256).save(png_path, format="PNG")

    print(f"已生成 {out_path}  ({os.path.getsize(out_path)} 字节)")
    print(f"预览图 {png_path}")


if __name__ == "__main__":
    main()
