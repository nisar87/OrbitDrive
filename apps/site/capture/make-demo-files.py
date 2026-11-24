#!/usr/bin/env python3
"""Creates the disposable MyOrbitDrive demo folder (synthetic images and documents only; no real data).
Usage: python3 make-demo-files.py <empty-dir>"""
import os, sys, random
from PIL import Image, ImageDraw, ImageFont
root = sys.argv[1]
INTER = '/usr/share/fonts/opentype/inter/Inter-SemiBold.otf'
def font(n): return ImageFont.truetype(INTER, n)
def mk(*p): d = os.path.join(root, *p); os.makedirs(d, exist_ok=True); return d

def landscape(path, seed, sky, hill1, hill2, sun):
    rnd = random.Random(seed); W, H = 1600, 1067
    im = Image.new('RGB', (W, H)); d = ImageDraw.Draw(im)
    for y in range(H):
        t = y / H; d.line((0, y, W, y), fill=tuple(int(sky[0][i] * (1 - t) + sky[1][i] * t) for i in range(3)))
    cx, cy = rnd.randint(300, 1300), rnd.randint(200, 420)
    d.ellipse((cx - 90, cy - 90, cx + 90, cy + 90), fill=sun)
    for base, col, amp in ((700, hill1, 140), (840, hill2, 100)):
        pts = [(0, H)]
        for x in range(0, W + 40, 40): pts.append((x, base - rnd.randint(0, amp)))
        pts.append((W, H)); d.polygon(pts, fill=col)
    im.save(path, 'JPEG', quality=88)

pics = mk('Photos', 'Trip to the north')
landscape(f'{pics}/Sunrise over the valley.jpg', 1, ((255, 214, 170), (120, 170, 210)), (40, 92, 98), (22, 52, 70), (255, 244, 214))
landscape(f'{pics}/Lake at noon.jpg', 2, ((150, 205, 240), (230, 244, 250)), (30, 110, 120), (20, 70, 90), (255, 255, 255))
landscape(f'{pics}/Camp at dusk.jpg', 3, ((60, 70, 130), (240, 150, 120)), (30, 40, 70), (18, 26, 48), (255, 220, 160))
landscape(f'{mk("Photos", "Family")}/Garden in spring.jpg', 4, ((190, 230, 255), (240, 250, 235)), (60, 140, 90), (30, 96, 70), (255, 250, 220))

def pdf(path, title, lines):
    pages = []
    for _ in range(2):
        im = Image.new('RGB', (1240, 1754), 'white'); d = ImageDraw.Draw(im)
        d.rectangle((0, 0, 1240, 14), fill='#146F70'); d.text((90, 110), title, fill='#142C43', font=font(54))
        y = 260
        for ln in lines:
            d.text((90, y), ln, fill='#3A5169', font=ImageFont.truetype('/usr/share/fonts/opentype/inter/Inter-Medium.otf', 30)); y += 64
        d.text((90, 1650), 'Sample document created for the MyOrbitDrive demo. Not a real record.', fill='#7A8A99', font=ImageFont.truetype('/usr/share/fonts/opentype/inter/Inter-Medium.otf', 24))
        pages.append(im)
    pages[0].save(path, 'PDF', save_all=True, append_images=pages[1:], resolution=150)

inv = mk('Documents', 'Invoices')
pdf(f'{inv}/Invoice 1042.pdf', 'Invoice 1042 (sample)', ['Item: Design consultation   1 x 120.00', 'Item: Printing   3 x 15.00', 'Total due: 165.00', 'Payment terms: 14 days'])
pdf(f'{inv}/Invoice 1043.pdf', 'Invoice 1043 (sample)', ['Item: Hosting   12 x 8.00', 'Total due: 96.00'])
pdf(f'{mk("Documents")}/Travel plan.pdf', 'Travel plan (sample)', ['Day 1: Drive north', 'Day 2: Lake and trail', 'Day 3: Return'])
open(f'{mk("Documents")}/Meeting notes.txt', 'w').write('Sample notes.\n- Review the budget\n- Plan the trip\n')
open(f'{mk("Projects")}/Budget 2026.csv', 'w').write('Month,Income,Spending\nJan,3200,2100\nFeb,3200,1980\nMar,3250,2230\n')
open(f'{mk("Projects")}/Ideas.md', 'w').write('# Ideas\n\nSample project notes.\n')
with open(f'{mk("Projects")}/export.bin', 'wb') as f: f.write(os.urandom(2_400_000))
print('demo files ready')
