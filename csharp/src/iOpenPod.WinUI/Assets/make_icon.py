"""生成 iOpenPod 的应用图标（ICO，含多个尺寸）。仅使用标准库。"""
from __future__ import annotations

import math
import struct
from pathlib import Path

SIZES = (16, 24, 32, 48, 64, 128, 256)


def lerp(a: float, b: float, t: float) -> float:
    return a + (b - a) * t


def rounded_rect_alpha(x: float, y: float, size: float, radius: float) -> float:
    """返回 0..1 的圆角矩形覆盖度（带 1 像素抗锯齿）。"""
    cx = min(max(x, radius), size - radius)
    cy = min(max(y, radius), size - radius)
    dx = x - cx
    dy = y - cy
    distance = math.hypot(dx, dy)
    if cx - radius <= x <= size - radius + 0 and cy - radius <= y <= size - radius and distance <= radius:
        pass
    # 到圆角矩形边界的有符号距离
    qx = abs(x - size / 2) - (size / 2 - radius)
    qy = abs(y - size / 2) - (size / 2 - radius)
    outside = math.hypot(max(qx, 0.0), max(qy, 0.0))
    inside = min(max(qx, qy), 0.0)
    sd = outside + inside - radius
    return max(0.0, min(1.0, 0.5 - sd))


def note_coverage(x: float, y: float, size: float) -> float:
    """白色八分音符的覆盖度。"""
    s = size
    # 符头：椭圆
    head_cx, head_cy = 0.385 * s, 0.700 * s
    head_rx, head_ry = 0.165 * s, 0.128 * s
    ellipse = ((x - head_cx) / head_rx) ** 2 + ((y - head_cy) / head_ry) ** 2
    if ellipse <= 1.0:
        return 1.0

    # 符干
    stem_left, stem_right = 0.512 * s, 0.585 * s
    stem_top, stem_bottom = 0.175 * s, 0.700 * s
    if stem_left <= x <= stem_right and stem_top <= y <= stem_bottom:
        return 1.0

    # 符尾：一段弧
    flag_cx, flag_cy = 0.548 * s, 0.315 * s
    rx, ry = 0.215 * s, 0.185 * s
    angle = math.atan2((y - flag_cy) / ry, (x - flag_cx) / rx)
    radius = math.hypot((x - flag_cx) / rx, (y - flag_cy) / ry)
    if 0.55 <= radius <= 1.0 and math.radians(-120) <= angle <= math.radians(60):
        return 1.0

    return 0.0


def render(size: int) -> bytes:
    """生成 BGRA 像素（自下而上）。"""
    supersample = 3
    rows = []
    for py in range(size):
        row = bytearray()
        for px in range(size):
            r = g = b = a = 0.0
            for sy in range(supersample):
                for sx in range(supersample):
                    x = px + (sx + 0.5) / supersample
                    y = py + (sy + 0.5) / supersample
                    coverage = rounded_rect_alpha(x, y, size, size * 0.22)
                    if coverage <= 0:
                        continue
                    t = (x + y) / (2 * size)
                    # 紫 → 蓝 渐变
                    bg_r = lerp(124, 46, t)
                    bg_g = lerp(58, 134, t)
                    bg_b = lerp(237, 222, t)
                    note = note_coverage(x, y, size)
                    cr = lerp(bg_r, 255, note)
                    cg = lerp(bg_g, 255, note)
                    cb = lerp(bg_b, 255, note)
                    r += cr * coverage
                    g += cg * coverage
                    b += cb * coverage
                    a += 255 * coverage
            samples = supersample * supersample
            row += bytes((int(b / samples), int(g / samples), int(r / samples), int(a / samples)))
        rows.append(bytes(row))

    rows.reverse()  # BMP 自下而上
    pixels = b"".join(rows)

    header = struct.pack("<IiiHHIIiiII", 40, size, size * 2, 1, 32, 0, len(pixels), 0, 0, 0, 0)
    mask_stride = ((size + 31) // 32) * 4
    mask = bytes(mask_stride * size)
    return header + pixels + mask


def main() -> None:
    images = [(size, render(size)) for size in SIZES]
    out = Path(__file__).with_name("iopenpod.ico")
    directory = struct.pack("<HHH", 0, 1, len(images))
    offset = 6 + 16 * len(images)
    entries = b""
    payload = b""
    for size, data in images:
        entries += struct.pack("<BBBBHHII", size if size < 256 else 0, size if size < 256 else 0,
                               0, 0, 1, 32, len(data), offset)
        payload += data
        offset += len(data)
    out.write_bytes(directory + entries + payload)
    print(f"wrote {out} ({out.stat().st_size} bytes)")


if __name__ == "__main__":
    main()
