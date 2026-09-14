# -*- coding: utf-8 -*-
"""生成应用图标（app.ico / tray.ico）。

设计：深蓝圆角底板 + 两条"隧道壁"横杠 + 中间一支向右的箭头，
表示本地端口 -> 远端 MySQL 的字节流隧道。小尺寸下也能辨认。

用法：
    python make_icon.py <输出目录>
"""
import sys
from pathlib import Path

from PIL import Image, ImageDraw

BG = (24, 95, 165, 255)      # 深蓝底板
WALL = (133, 183, 235, 255)  # 浅蓝隧道壁
ARROW = (250, 238, 218, 255)  # 琥珀箭头


def draw_icon(size: int) -> Image.Image:
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    radius = int(size * 0.22)
    d.rounded_rectangle([0, 0, size - 1, size - 1], radius=radius, fill=BG)

    # 隧道壁：上下两条横杠，居中偏上下
    wall_h = max(1, int(size * 0.075))
    wall_top = int(size * 0.27)
    wall_bottom = int(size * 0.73)
    wall_inset = int(size * 0.20)
    d.rounded_rectangle(
        [wall_inset, wall_top, size - wall_inset, wall_top + wall_h],
        radius=wall_h // 2, fill=WALL)
    d.rounded_rectangle(
        [wall_inset, wall_bottom - wall_h, size - wall_inset, wall_bottom],
        radius=wall_h // 2, fill=WALL)

    # 箭头：箭杆 + 三角箭头，指向右
    mid = size // 2
    shaft_h = max(1, int(size * 0.10))
    shaft_x0 = int(size * 0.24)
    shaft_x1 = int(size * 0.62)
    d.rectangle([shaft_x0, mid - shaft_h // 2, shaft_x1, mid + shaft_h // 2], fill=ARROW)
    d.polygon(
        [
            (shaft_x1, int(mid - size * 0.17)),
            (int(size * 0.80), mid),
            (shaft_x1, int(mid + size * 0.17)),
        ],
        fill=ARROW)

    return img


def main() -> None:
    if len(sys.argv) < 2:
        print("用法: python make_icon.py <输出目录>")
        sys.exit(1)
    out_dir = Path(sys.argv[1])
    out_dir.mkdir(parents=True, exist_ok=True)

    # 注意：PIL 保存 ICO 时，多尺寸只能靠 sizes 参数让它自动缩放；
    # 传 append_images 反而只会写入第一帧（实测 PIL 12.3 如此）。
    ico_sizes = [(16, 16), (20, 20), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)]
    base = draw_icon(256)

    for name in ("app.ico", "tray.ico"):
        base.save(out_dir / name, format="ICO", sizes=ico_sizes)
        print(f"已生成: {out_dir / name}")

    # 顺带出一张 PNG 方便在文档/界面里引用
    base.save(out_dir / "app.png", format="PNG")
    print(f"已生成: {out_dir / 'app.png'}")


if __name__ == "__main__":
    main()
