"""Draws the Android launcher icon: the desktop's blue pointer (desktop/assets/make-icon.py) on the
app's black, with the reply blue pooling in the bottom-right corner like the home screen.

Writes the adaptive icon layers (background, foreground, monochrome for themed icons) and the
legacy square icons into android/app/src/main/res. Run from mobile/: python tool/make_icon.py
"""
import importlib.util
import os
from PIL import Image, ImageChops, ImageDraw, ImageFilter

HERE = os.path.dirname(os.path.abspath(__file__))
RES = os.path.join(HERE, "..", "android", "app", "src", "main", "res")
spec = importlib.util.spec_from_file_location("desk", os.path.join(HERE, "..", "..", "desktop", "assets", "make-icon.py"))
desk = importlib.util.module_from_spec(spec)
spec.loader.exec_module(desk)

N = desk.CANVAS  # 1024: the 108 dp adaptive canvas
BACKGROUND = (0x07, 0x09, 0x0E)
GLOW = (0x33, 0x80, 0xFF)
POINTER = 0.46  # of the canvas; Android's safe zone is the middle 66/108


def pointer_mask():
    small = desk.mask.resize((round(N * POINTER),) * 2, Image.LANCZOS)
    mask = Image.new("L", (N, N), 0)
    offset = (N - small.width) // 2
    mask.paste(small, (offset + round(N * 0.02), offset + round(N * 0.02)))  # optical centre: the tip is top-left
    return mask


def background():
    image = Image.new("RGB", (N, N), BACKGROUND)
    glow = Image.new("L", (N, N), 0)
    ImageDraw.Draw(glow).ellipse((N * 0.45, N * 0.45, N * 1.45, N * 1.45), fill=150)
    glow = glow.filter(ImageFilter.GaussianBlur(N * 0.16))
    image.paste(Image.new("RGB", (N, N), GLOW), (0, 0), glow)
    return image


def foreground(mask):
    layer = Image.new("RGBA", (N, N), (0, 0, 0, 0))
    halo = mask.filter(ImageFilter.GaussianBlur(N * 0.035)).point(lambda v: v * 0.55)
    layer.paste(Image.new("RGBA", (N, N), (0x40, 0xBF, 0xFF, 255)), (0, 0), halo)
    gradient = Image.new("RGBA", (N, N))
    draw = ImageDraw.Draw(gradient)
    for y in range(N):
        t = y / (N - 1)
        draw.line([(0, y), (N, y)], fill=tuple(round(desk.TOP[i] + (desk.BOTTOM[i] - desk.TOP[i]) * t) for i in range(3)) + (255,))
    layer.paste(gradient, (0, 0), mask)
    return layer


def save(image, folder, name, size):
    os.makedirs(os.path.join(RES, folder), exist_ok=True)
    image.resize((size, size), Image.LANCZOS).save(os.path.join(RES, folder, name))


mask = pointer_mask()
back, front = background(), foreground(mask)
mono = Image.new("RGBA", (N, N), (255, 255, 255, 0))
mono.putalpha(mask)

# Legacy launchers: the whole icon in a rounded square.
square = back.convert("RGBA")
square.alpha_composite(front)
rounded = Image.new("L", (N, N), 0)
ImageDraw.Draw(rounded).rounded_rectangle((0, 0, N - 1, N - 1), radius=N * 0.22, fill=255)
square.putalpha(ImageChops.multiply(square.getchannel("A"), rounded))

for density, scale in {"mdpi": 1, "hdpi": 1.5, "xhdpi": 2, "xxhdpi": 3, "xxxhdpi": 4}.items():
    folder = f"mipmap-{density}"
    save(back, folder, "ic_launcher_background.png", round(108 * scale))
    save(front, folder, "ic_launcher_foreground.png", round(108 * scale))
    save(mono, folder, "ic_launcher_monochrome.png", round(108 * scale))
    save(square, folder, "ic_launcher.png", round(48 * scale))

os.makedirs(os.path.join(RES, "mipmap-anydpi-v26"), exist_ok=True)
with open(os.path.join(RES, "mipmap-anydpi-v26", "ic_launcher.xml"), "w", encoding="utf-8") as f:
    f.write("""<?xml version="1.0" encoding="utf-8"?>
<!-- Drawn by tool/make_icon.py -->
<adaptive-icon xmlns:android="http://schemas.android.com/apk/res/android">
    <background android:drawable="@mipmap/ic_launcher_background" />
    <foreground android:drawable="@mipmap/ic_launcher_foreground" />
    <monochrome android:drawable="@mipmap/ic_launcher_monochrome" />
</adaptive-icon>
""")
print("wrote launcher icons")
