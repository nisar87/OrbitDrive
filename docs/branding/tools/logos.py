#!/usr/bin/env python3
"""Generates the MyOrbitDrive logo concept SVG masters (reproducible).
Usage: python3 docs/branding/tools/logos.py <outdir>
Wordmarks are converted to outlines from Inter (SIL OFL 1.1), so the SVGs need no font."""
import sys, os
from fontTools.ttLib import TTFont
from fontTools.pens.svgPathPen import SVGPathPen
from fontTools.pens.transformPen import TransformPen

NAVY, TEAL, MINT, PEARL = '#142C43', '#146F70', '#A4E2CE', '#F7F8F5'
FONT = '/usr/share/fonts/opentype/inter/Inter-SemiBold.otf'
FONT_MED = '/usr/share/fonts/opentype/inter/Inter-Medium.otf'

def text_path(text, font_path, size, x, y, tracking=0.0):
    f = TTFont(font_path); gs = f.getGlyphSet(); cmap = f.getBestCmap()
    upm = f['head'].unitsPerEm; s = size / upm; cur = x; parts = []
    for ch in text:
        g = cmap[ord(ch)]; pen = SVGPathPen(gs)
        tp = TransformPen(pen, (s, 0, 0, -s, cur, y)); gs[g].draw(tp)
        parts.append(pen.getCommands()); cur += gs[g].width * s + tracking * size
    return ' '.join(parts), cur - x

# Marks are drawn on a 64x64 grid. c = dict(bg, fg, acc); bg None => no tile.
def tile(c):
    return f'<rect width="64" height="64" rx="14" fill="{c["bg"]}"/>' if c['bg'] else ''

def mark_A(c):  # Connected M: an M whose centre vertex is a node
    return (tile(c) +
      f'<path d="M16 47V19L32 37L48 19V47" fill="none" stroke="{c["fg"]}" stroke-width="6.5" stroke-linecap="round" stroke-linejoin="round"/>'
      f'<circle cx="32" cy="37" r="5.2" fill="{c["acc"]}" stroke="{c["bg"] or c["fg"]}" stroke-width="2.2"/>')

def mark_B(c):  # Personal Portal: an arched doorway with a file stepping through
    return (tile(c) +
      f'<path d="M18 49V30a14 14 0 0 1 28 0V49" fill="none" stroke="{c["fg"]}" stroke-width="6" stroke-linecap="round" stroke-linejoin="round"/>'
      f'<rect x="26.5" y="28" width="11" height="14" rx="2.6" fill="{c["acc"]}"/>'
      f'<path d="M12 49H52" stroke="{c["fg"]}" stroke-width="6" stroke-linecap="round"/>')

def mark_C(c):  # Connected Orbit: tilted orbit with a satellite around a folder
    return (tile(c) +
      f'<g transform="rotate(-28 32 32)"><ellipse cx="32" cy="32" rx="22" ry="11" fill="none" stroke="{c["fg"]}" stroke-width="4.6"/></g>'
      f'<path d="M24 26.5a2.5 2.5 0 0 1 2.5-2.5h4l2.6 3H38a2.5 2.5 0 0 1 2.5 2.5V37a2.5 2.5 0 0 1-2.5 2.5H26.5A2.5 2.5 0 0 1 24 37Z" fill="{c["acc"]}"/>'
      f'<circle cx="50.4" cy="24.2" r="5" fill="{c["acc"]}" stroke="{c["bg"] or "#fff"}" stroke-width="2.4"/>')

def mark_D(c):  # Orbit Gap: an open orbit ring whose satellite sits in the gap, around a solid core
    ring = c['fg']; core = c['fg']; sat = c['acc']
    return (tile(c) +
      f'<path d="M50.35 27.08A19 19 0 1 1 36.92 13.65" fill="none" stroke="{ring}" stroke-width="6.4" stroke-linecap="round"/>'
      f'<circle cx="32" cy="32" r="8.2" fill="{core}"/>'
      f'<circle cx="46.2" cy="17.8" r="5.4" fill="{sat}"/>')

def mark_E(c):  # Folder Orbit: a solid folder at the core, wrapped by a tilted orbit with its satellite
    ring = c['fg']; core = c['acc']; sat = c['fg']
    return (tile(c) +
      '<g transform="rotate(-32 32 32)">'
      f'<path d="M10.2 32a21.8 11.4 0 0 0 43.6 0" fill="none" stroke="{ring}" stroke-width="5.6" stroke-linecap="round"/></g>'
      f'<path d="M21 25.4a3 3 0 0 1 3-3h6.2l3.4 3.8H40a3 3 0 0 1 3 3V38a3 3 0 0 1-3 3H24a3 3 0 0 1-3-3Z" fill="{core}"/>'
      '<g transform="rotate(-32 32 32)">'
      f'<path d="M53.8 32a21.8 11.4 0 0 0-8.6-9" fill="none" stroke="{ring}" stroke-width="5.6" stroke-linecap="round"/></g>'
      f'<circle cx="46.9" cy="17.6" r="4.6" fill="{sat}"/>')

def mark_F(c):  # Folder Orbit v2: an upright folder at the core, an orbit passing behind (top) and in front (bottom), a satellite
    import math
    ring = c['fg']; core = c['acc']; sat = c['acc']
    rx, ry = 25.0, 13.0
    ang = math.radians(-18)
    sx, sy = 32 + rx * math.cos(ang), 32 + ry * math.sin(ang)
    return (tile(c) +
      '<g transform="rotate(-28 32 32)">'
      f'<path d="M{32-rx} 32A{rx} {ry} 0 0 1 {32+rx} 32" fill="none" stroke="{ring}" stroke-width="5.2" stroke-linecap="round"/></g>'
      f'<path d="M20.5 24.6a3.2 3.2 0 0 1 3.2-3.2h6.6l3.6 4H40.3a3.2 3.2 0 0 1 3.2 3.2V39a3.2 3.2 0 0 1-3.2 3.2H23.7a3.2 3.2 0 0 1-3.2-3.2Z" fill="{core}"/>'
      '<g transform="rotate(-28 32 32)">'
      f'<path d="M{32-rx} 32A{rx} {ry} 0 0 0 {32+rx} 32" fill="none" stroke="{ring}" stroke-width="5.2" stroke-linecap="round"/>'
      f'<circle cx="{sx:.2f}" cy="{sy:.2f}" r="0" fill="{sat}"/></g>')

MARKS = {'A-connected-m': mark_A, 'B-personal-portal': mark_B, 'C-connected-orbit': mark_C, 'D-orbit-gap': mark_D, 'E-folder-orbit': mark_E, 'F-folder-orbit-v2': mark_F}
SCHEMES = {  # tile icon colours / on-light / on-dark / mono
  'icon':       dict(bg=NAVY,  fg=PEARL, acc=MINT),
  'mark-light': dict(bg=None,  fg=NAVY,  acc=TEAL),
  'mark-dark':  dict(bg=None,  fg=PEARL, acc=MINT),
  'mono-navy':  dict(bg=None,  fg=NAVY,  acc=NAVY),
  'mono-white': dict(bg=None,  fg='#FFFFFF', acc='#FFFFFF'),
}

def svg(w, h, body, title):
    return (f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {w} {h}" width="{w}" height="{h}" role="img" aria-label="{title}">'
            f'<title>{title}</title>{body}</svg>\n')

def wordmark(color_my, color_rest, size=34, x=0, y=0):
    d1, w1 = text_path('My', FONT_MED, size, x, y, -0.01)
    d2, w2 = text_path('OrbitDrive', FONT, size, x + w1, y, -0.01)
    return f'<path d="{d1}" fill="{color_my}"/><path d="{d2}" fill="{color_rest}"/>', w1 + w2

def build(out):
    os.makedirs(out, exist_ok=True)
    for name, fn in MARKS.items():
        d = os.path.join(out, name); os.makedirs(d, exist_ok=True)
        for sk, c in SCHEMES.items():
            open(f'{d}/{sk}.svg', 'w').write(svg(64, 64, fn(c), f'MyOrbitDrive ({name}) {sk}'))
        # horizontal lockups: 64px mark + wordmark; light background and dark background
        for tag, mk, my, rest in (('light', SCHEMES['mark-light'], TEAL, NAVY), ('dark', SCHEMES['mark-dark'], MINT, PEARL), ('mono-navy', SCHEMES['mono-navy'], NAVY, NAVY), ('mono-white', SCHEMES['mono-white'], '#FFFFFF', '#FFFFFF')):
            wm, ww = wordmark(my, rest, 34, 64, 42)
            W = int(64 + ww + 4)
            open(f'{d}/horizontal-{tag}.svg', 'w').write(svg(W, 64, f'<g>{fn(mk)}</g>{wm}', f'MyOrbitDrive horizontal {tag}'))
            # stacked: mark centred above wordmark
            wm2, ww2 = wordmark(my, rest, 34, 0, 0)
            W2 = int(ww2 + 16); sx = (W2 - 64) / 2; tx = 8
            wm3, _ = wordmark(my, rest, 34, tx, 108)
            open(f'{d}/stacked-{tag}.svg', 'w').write(svg(W2, 124, f'<g transform="translate({sx:.1f} 0)">{fn(mk)}</g>{wm3}', f'MyOrbitDrive stacked {tag}'))
        wm, ww = wordmark(MINT, PEARL, 34, 80, 42)
    # wordmark only
    wm, ww = wordmark(TEAL, NAVY, 40, 0, 40)
    open(f'{out}/wordmark-light.svg', 'w').write(svg(int(ww + 4), 52, wm, 'MyOrbitDrive wordmark'))

if __name__ == '__main__':
    build(sys.argv[1] if len(sys.argv) > 1 else 'docs/branding/concepts')
