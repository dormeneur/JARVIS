"""Draws jarvis.ico (and the grey jarvis-paused.ico for the tray while paused): a rounded pointer in the companion cursor's blue.

The on-screen cursor is an equilateral triangle, which at icon size reads as a "play" button,
so the icon is an arrowhead with a notched base, aimed at the top-left corner.

Run: python make-icon.py  (needs Pillow). Writes both next to this script.
"""
import math
import os
from PIL import Image, ImageDraw

CANVAS = 1024
ROTATION = math.radians(-45)  # pointing at the top-left corner
HALF_WIDTH = 0.62  # base half-width per unit of tip-to-base height
NOTCH = 0.30  # how far the base's middle is pulled toward the tip
CORNER = 0.12  # a little rounder than the overlay cursor (0.09) so small sizes stay soft
TOP, BOTTOM = (0x5C, 0x9B, 0xFF), (0x1F, 0x63, 0xE6)  # around CursorColor.Blue #3380FF


def triangle(side):
    local = [(0, -side), (side * HALF_WIDTH, 0), (0, -side * NOTCH), (-side * HALF_WIDTH, 0)]  # tip up, clockwise
    cos, sin = math.cos(ROTATION), math.sin(ROTATION)
    verts = [(x * cos - y * sin, x * sin + y * cos) for x, y in local]
    inset = side * CORNER
    points = []
    for i, c in enumerate(verts):
        def toward(v):
            dx, dy = v[0] - c[0], v[1] - c[1]
            n = math.hypot(dx, dy)
            return (c[0] + dx / n * inset, c[1] + dy / n * inset)
        p0, p2 = toward(verts[i - 1]), toward(verts[(i + 1) % len(verts)])
        for k in range(17):  # quadratic fillet through the corner
            t = k / 16
            points.append(tuple((1 - t) ** 2 * p0[j] + 2 * (1 - t) * t * c[j] + t * t * p2[j] for j in range(2)))
    # Centre the shape's bounding box on the canvas.
    xs, ys = [p[0] for p in points], [p[1] for p in points]
    ox, oy = CANVAS / 2 - (min(xs) + max(xs)) / 2, CANVAS / 2 - (min(ys) + max(ys)) / 2
    return [(x + ox, y + oy) for x, y in points], max(max(xs) - min(xs), max(ys) - min(ys))


points, extent = triangle(1.0)
scale = CANVAS * 0.94 / extent
points, _ = triangle(scale)

mask = Image.new("L", (CANVAS, CANVAS), 0)
ImageDraw.Draw(mask).polygon(points, fill=255)


def render(top, bottom, name):
    gradient = Image.new("RGBA", (CANVAS, CANVAS))
    draw = ImageDraw.Draw(gradient)
    for y in range(CANVAS):
        t = y / (CANVAS - 1)
        draw.line([(0, y), (CANVAS, y)], fill=tuple(round(top[i] + (bottom[i] - top[i]) * t) for i in range(3)) + (255,))
    icon = Image.new("RGBA", (CANVAS, CANVAS), (0, 0, 0, 0))
    icon.paste(gradient, (0, 0), mask)
    sizes = [16, 20, 24, 32, 40, 48, 64, 256]
    icon.save(os.path.join(os.path.dirname(os.path.abspath(__file__)), name), sizes=[(s, s) for s in sizes])
    print("wrote", name)


render(TOP, BOTTOM, "jarvis.ico")
render((0x9A, 0x9A, 0x9A), (0x76, 0x76, 0x76), "jarvis-paused.ico")  # tray while paused; mid-grey reads on light and dark
