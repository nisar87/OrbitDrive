#!/usr/bin/env python3
"""Renders docs/branding/CONCEPT_BOARD.png from docs/branding/concepts (run logos.py first)."""
import subprocess
from PIL import Image, ImageDraw, ImageFont
C = ['A-connected-m', 'B-personal-portal', 'C-connected-orbit']
def r(svg, w=None, h=None, out='/tmp/_b.png'):
    a = ['rsvg-convert', '-o', out] + (['-w', str(w)] if w else []) + (['-h', str(h)] if h else [])
    subprocess.run(a + [svg], check=True); return Image.open(out).convert('RGBA')
img = Image.new('RGB', (1800, 1300), '#F7F8F5'); d = ImageDraw.Draw(img)
F = ImageFont.truetype('/usr/share/fonts/opentype/inter/Inter-SemiBold.otf', 26)
for i, c in enumerate(C):
    x0 = 40 + i * 590; d.text((x0, 24), c, fill='#142C43', font=F)
    ic = r(f'docs/branding/concepts/{c}/icon.svg', w=300); img.paste(ic, (x0, 70), ic)
    xs = x0 + 330
    for s in (96, 48, 32, 16):
        im = r(f'docs/branding/concepts/{c}/icon.svg', w=s); img.paste(im, (xs, 70), im); xs += s + 14
    g = r(f'docs/branding/concepts/{c}/icon.svg', w=128).convert('L').convert('RGBA'); img.paste(g, (x0 + 330, 230), g)
    hl = r(f'docs/branding/concepts/{c}/horizontal-light.svg', h=80); img.paste(hl, (x0, 420), hl)
    d.rectangle((x0 - 10, 520, x0 + 560, 640), fill='#142C43')
    hd = r(f'docs/branding/concepts/{c}/horizontal-dark.svg', h=80); img.paste(hd, (x0, 540), hd)
    st = r(f'docs/branding/concepts/{c}/stacked-light.svg', h=200); img.paste(st, (x0, 680), st)
    d.rectangle((x0 + 330, 680, x0 + 560, 880), fill='#146F70')
    m = r(f'docs/branding/concepts/{c}/mono-white.svg', w=130); img.paste(m, (x0 + 380, 715), m)
    xs = x0
    for s in (64, 32, 24, 16):
        im = r(f'docs/branding/concepts/{c}/mark-light.svg', w=s); img.paste(im, (xs, 930), im); xs += s + 20
    d.rectangle((x0, 1050, x0 + 560, 1210), fill='#D9E3DD')
    t = r(f'docs/branding/concepts/{c}/horizontal-light.svg', h=60); img.paste(t, (x0 + 20, 1100), t)
img.save('docs/branding/CONCEPT_BOARD.png')
