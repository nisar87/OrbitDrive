#!/usr/bin/env python3
"""Builds the provisional (Concept A) asset kit. Usage: python3 docs/branding/tools/kit.py docs/branding/kit-provisional"""
import os, subprocess, sys, json
sys.path.insert(0, os.path.dirname(__file__))
import logos as L
from PIL import Image, ImageDraw, ImageFont

out = sys.argv[1] if len(sys.argv) > 1 else 'docs/branding/kit-provisional'
for sub in ('svg', 'png', 'pwa', 'windows', 'social'): os.makedirs(f'{out}/{sub}', exist_ok=True)
A = 'docs/branding/concepts/A-connected-m'
L.build('docs/branding/concepts')

def raster(svg, png, w=None, h=None):
    a = ['rsvg-convert', '-o', png] + (['-w', str(w)] if w else []) + (['-h', str(h)] if h else []) + [svg]
    subprocess.run(a, check=True)

# SVG masters
import shutil
for f in os.listdir(A):
    shutil.copy(f'{A}/{f}', f'{out}/svg/{f}')
shutil.copy('docs/branding/concepts/wordmark-light.svg', f'{out}/svg/wordmark-light.svg')

# Full-bleed icon (maskable / app tiles): navy square, mark inside the 80% safe zone
def fullbleed(scale):
    inner = L.mark_A(dict(bg=None, fg=L.PEARL, acc=L.MINT))
    t = (64 - 64 * scale) / 2
    return L.svg(64, 64, f'<rect width="64" height="64" fill="{L.NAVY}"/><g transform="translate({t} {t}) scale({scale})">{inner.replace(L.MINT, L.MINT)}</g>', 'MyOrbitDrive maskable icon')
# the node ring colour for tile-less marks falls back to the fg colour; on the navy field it must be navy
def fullbleed_fixed(scale):
    s = fullbleed(scale)
    return s.replace(f'stroke="{L.PEARL}" stroke-width="2.2"', f'stroke="{L.NAVY}" stroke-width="2.2"')
open(f'{out}/svg/icon-maskable.svg', 'w').write(fullbleed_fixed(0.62))
open(f'{out}/svg/icon-fullbleed.svg', 'w').write(fullbleed_fixed(0.74))

# PNGs
for s in (16, 24, 32, 48, 64, 96, 128, 180, 192, 256, 512):
    raster(f'{A}/icon.svg', f'{out}/png/icon-{s}.png', w=s)
raster(f'{A}/horizontal-light.svg', f'{out}/png/horizontal-light.png', h=256)
raster(f'{A}/horizontal-dark.svg', f'{out}/png/horizontal-dark.png', h=256)
raster(f'{A}/stacked-light.svg', f'{out}/png/stacked-light.png', h=512)

# PWA set
raster(f'{A}/icon.svg', f'{out}/pwa/icon-192.png', w=192)
raster(f'{A}/icon.svg', f'{out}/pwa/icon-512.png', w=512)
raster(f'{out}/svg/icon-maskable.svg', f'{out}/pwa/icon-maskable-512.png', w=512)
raster(f'{out}/svg/icon-fullbleed.svg', f'{out}/pwa/apple-touch-icon.png', w=180)
manifest = {"name": "MyOrbitDrive", "short_name": "MyOrbitDrive", "theme_color": L.NAVY, "background_color": L.PEARL,
            "icons": [{"src": "icon-192.png", "sizes": "192x192", "type": "image/png"},
                      {"src": "icon-512.png", "sizes": "512x512", "type": "image/png"},
                      {"src": "icon-maskable-512.png", "sizes": "512x512", "type": "image/png", "purpose": "maskable"}]}
json.dump(manifest, open(f'{out}/pwa/manifest-icons-snippet.json', 'w'), indent=2)

# favicon.ico (16/32/48) and Windows multi-size ico
def ico(path, sizes):
    ims = [Image.open(f'{out}/png/icon-{s}.png').convert('RGBA') for s in sizes]
    ims[-1].save(path, format='ICO', sizes=[(s, s) for s in sizes], append_images=ims[:-1])
ico(f'{out}/favicon.ico', (16, 32, 48))
ico(f'{out}/windows/OrbitDrive-app.ico', (16, 24, 32, 48, 64, 128, 256))
# Windows tile/store assets matching the existing shell extension filenames
for name, w, h in (('Square44x44Logo.png', 44, 44), ('Square150x150Logo.png', 150, 150), ('StoreLogo.png', 50, 50), ('Square310x310Logo.png', 310, 310)):
    raster(f'{out}/svg/icon-fullbleed.svg', f'{out}/windows/{name}', w=w, h=h)
raster(f'{out}/svg/icon-fullbleed.svg', f'{out}/windows/Square44x44Logo.targetsize-256.png', w=256)

# Social share image 1200x630 (raster text from Inter; SVG lockup embedded as PNG)
img = Image.new('RGB', (1200, 630), L.PEARL); d = ImageDraw.Draw(img)
d.rectangle((0, 0, 1200, 10), fill=L.TEAL)
raster(f'{A}/horizontal-light.svg', '/tmp/_h.png', h=84); hl = Image.open('/tmp/_h.png').convert('RGBA'); img.paste(hl, (80, 80), hl)
H1 = ImageFont.truetype('/usr/share/fonts/opentype/inter/Inter-Bold.otf', 76)
H2 = ImageFont.truetype('/usr/share/fonts/opentype/inter/Inter-Medium.otf', 34)
d.text((80, 250), 'Your PC files.', fill=L.NAVY, font=H1)
d.text((80, 340), 'Wherever you are.', fill=L.TEAL, font=H1)
d.text((80, 470), 'Open the folders you choose from your phone or any browser.', fill=L.NAVY, font=H2)
d.text((80, 520), 'Read-only by default. You approve every folder and device.', fill='#3A5169', font=H2)
raster(f'{A}/icon.svg', '/tmp/_i.png', w=230); ic = Image.open('/tmp/_i.png').convert('RGBA'); img.paste(ic, (930, 70), ic)
img.save(f'{out}/social/og-image-1200x630.png', optimize=True)
print('kit built')
