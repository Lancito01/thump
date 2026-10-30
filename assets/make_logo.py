"""Renders the Thump logo (a kick-drum pulse) to PNG + ICO. Run: python assets/make_logo.py"""
from PIL import Image, ImageDraw, ImageFilter, ImageChops
import os

S = 1024 * 2  # supersample
PINK, ORANGE = (255, 25, 120), (255, 176, 32)
here = os.path.dirname(os.path.abspath(__file__))


def gradient(size, c1, c2):
    """Diagonal gradient c1 (top-left) -> c2 (bottom-right)."""
    w, h = size
    g = Image.new("RGB", (256, 1))
    for x in range(256):
        t = x / 255
        g.putpixel((x, 0), tuple(round(a + (b - a) * t) for a, b in zip(c1, c2)))
    g = g.resize((w * 2, h * 2)).rotate(45, resample=Image.BICUBIC, expand=False)
    return g.crop((w // 2, h // 2, w // 2 + w, h // 2 + h))


def ring(r, width):
    m = Image.new("L", (S, S), 0)
    d = ImageDraw.Draw(m)
    c = S // 2
    d.ellipse((c - r, c - r, c + r, c + r), fill=255)
    d.ellipse((c - r + width, c - r + width, c + r - width, c + r - width), fill=0)
    return m


def disc(r):
    m = Image.new("L", (S, S), 0)
    c = S // 2
    ImageDraw.Draw(m).ellipse((c - r, c - r, c + r, c + r), fill=255)
    return m


def scale_alpha(mask, a):
    return mask.point(lambda v: int(v * a))


# Background tile
tile = Image.new("L", (S, S), 0)
ImageDraw.Draw(tile).rounded_rectangle((0, 0, S - 1, S - 1), radius=int(S * 0.22), fill=255)
bg = Image.new("RGB", (S, S))
top, bottom = (31, 30, 44), (12, 12, 18)
for y in range(S):
    t = y / (S - 1)
    ImageDraw.Draw(bg).line([(0, y), (S, y)], fill=tuple(round(a + (b - a) * t) for a, b in zip(top, bottom)))
img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
img.paste(bg, (0, 0), tile)

grad = gradient((S, S), PINK, ORANGE)
u = S / 1024

# Glow behind the core
glow = disc(int(250 * u)).filter(ImageFilter.GaussianBlur(110 * u))
img.paste(grad, (0, 0), ImageChops.multiply(scale_alpha(glow, 0.75), tile))

# Shock-wave rings, fading outward
for r, w, a in [(400, 22, 0.35), (310, 30, 0.70)]:
    img.paste(grad, (0, 0), scale_alpha(ring(int(r * u), int(w * u)), a))

# The kick
img.paste(grad, (0, 0), disc(int(185 * u)))
# Subtle highlight on the core
hl = disc(int(185 * u)).filter(ImageFilter.GaussianBlur(4 * u))
shine = Image.new("L", (S, S), 0)
c = S // 2
ImageDraw.Draw(shine).ellipse((c - 150 * u, c - 165 * u, c + 70 * u, c + 10 * u), fill=255)
shine = ImageChops.multiply(shine.filter(ImageFilter.GaussianBlur(40 * u)), hl)
img.paste((255, 255, 255), (0, 0), scale_alpha(shine, 0.22))

logo = img.resize((1024, 1024), Image.LANCZOS)
logo.save(os.path.join(here, "logo.png"))
logo.resize((256, 256), Image.LANCZOS).save(os.path.join(here, "..", "src", "Thump", "Assets", "logo.png"))
logo.save(os.path.join(here, "..", "src", "Thump", "Assets", "thump.ico"),
          sizes=[(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)])
print("ok")
