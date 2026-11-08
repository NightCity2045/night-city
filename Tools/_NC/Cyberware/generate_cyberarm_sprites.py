#!/usr/bin/env python3
# SPDX-License-Identifier: CC0-1.0
"""Build SS14 cyberarm RSIs from original pixel masters and an ImageGen icon source.

World art is authored at native resolution; it is never a rotated inventory icon.
Direction order is south, north, east, west. Each state keeps the same wrist anchor.
"""
from pathlib import Path
import json
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parents[3]
ART = Path(__file__).resolve().parent / 'Art'
P = {'d':'#303034', 's':'#4d4e54', 'm':'#797c80', 'h':'#b4b5ae',
     'w':'#e2dfcc', 'a':'#bd8848', 'r':'#9c4541', 'c':'#658e91'}
MODULES = ['impact','claws','blade','mantis','monowire','pistol','launcher',
           'tool','medical','storage','socket']
ICONS = ['microdrive','guide','lock','damper','cooling','claws','blade','hinge',
         'reel','wire','pistol','launcher','tool','medical','storage','socket',
         'impact','cuff','shell','drive','mantis','monowire','civilian_left',
         'civilian_right','armored_left','armored_right']

# Small, beveled wrist collar. Rear view shows the access seam, not the bright top plate.
CUFF = ['.hm.', 'hmsd', 'msad', '.sd.']
BACK_CUFF = ['.ms.', 'smhd', 'dsmd', '.dd.']
# Front and side projections have distinct silhouettes and perspective shortening.
# d/s/m/h/w: contour, shadow, midtone, highlight, blade edge. Dots are transparent.
FRONT = {
 'pistol': ['.hh.....','smhh....','.dmmhs..','..dsmhh.','...dmmmd','....dssd','.....dd.'],
 'launcher': ['.hhm....','hmhms...','dmmhms..','.dsmhhs.','..dmmhmd','...dssmd','....ddd.'],
 'blade': ['..wm.','..wms','..wms','..whs','..whs','..wh.','..wm.','..w..'],
 'claws': ['h.m.h.','w.h.w.','w.w.w.','w.w.w.','m.w.m.','..m...'],
 'mantis': ['.hm.........','..hm........','..sam.......','...mh.h.....','..hwms.hm...','.hwms...hm..','.wms....whm.','.wm......wh.','..m.......w.'],
 'impact': ['.hhhm.','hmhmhm','hmmmsd','dmsmsd','.sddd.'],
 'tool': ['.h.m..','wh.wh.','m..m.h','s..s.m','...s.s'],
 'medical': ['.w...h','wm..hm','.m..ms','.s..sm','....m.'],
 'storage': ['.hhm.','hmsmd','smamd','.ddd.'],
 'socket': ['.hm.','hssh','mddm','.sd.'],
}
SIDE = {
 'pistol': ['.hhhhh....','hmmmsmhhs.','dsamsmmsdd','.ddssdddd.'],
 'launcher': ['..hhhmhhm..','.hmmhmmmsh.','dmamhmmmsmd','.dsshsssssd','..ddddddd..'],
 'blade': ['.......w','....wwhm','..wwhmms','hwhmmss.','.sddd...'],
 'claws': ['....hwww','..hm....','hmm.hwww','..hm....','....hwww'],
 'mantis': ['..hms........','...hmh.......','....amhh.....','....smhwwh...','..hwmmsswmh..','.hwmss...hwh.','.wms......wh.','..m........w.'],
 'impact': ['..hhhm..','.hmhmhm.','hmmmsmsd','.smsmsdd','..dddd..'],
 'tool': ['...hh..h','hmm..hh.','...hmm..','hmm...mh','...hhh.h'],
 'medical': ['...hwww.','hmm.....','...hmhhh','hmm...hm','...hmhhh'],
 'storage': ['.hhhm.','hmsmsd','dmsasd','.dddd.'],
 'socket': ['.hmhm.','hssmsd','.dddd.'],
}


def pixels(rows):
    image = Image.new('RGBA', (max(map(len, rows)), len(rows)))
    for y, row in enumerate(rows):
        for x, pixel in enumerate(row):
            if pixel in P:
                image.putpixel((x,y), tuple(bytes.fromhex(P[pixel][1:])) + (255,))
    return image


def stamp(canvas, rows, xy, mirror=False):
    image = pixels(rows)
    if mirror:
        image = image.transpose(Image.Transpose.FLIP_LEFT_RIGHT)
    canvas.alpha_composite(image, xy)


def world(kind, side, direction, phase, frame):
    canvas = Image.new('RGBA', (32,32))
    front = direction in (0,1)
    outward = (1 if (side == 'left') == (direction == 0) else -1) if front else (1 if direction == 2 else -1)
    if front:
        x = 22 if outward == 1 else 9
        y = 18
    else:
        far = (direction == 2 and side == 'left') or (direction == 3 and side == 'right')
        x = (20 if far else 14) if direction == 2 else (11 if far else 17)
        y = 19
    collar = BACK_CUFF if direction == 1 else CUFF
    # Far-side collar is behind the torso. Only the protruding mechanism is drawn.
    if front or not far:
        stamp(canvas, collar, (x-1,y-3), mirror=outward < 0)
    amount = ([0,.35,.7,1][frame] if phase == 'deploy' else
              [1,.7,.35,0][frame] if phase == 'retract' else 1)
    if amount == 0:
        return canvas
    if kind == 'monowire':
        stamp(canvas, ['.hh.','hmsh','dmsd','.sd.'],(x-1,y-1), mirror=outward < 0)
        d = ImageDraw.Draw(canvas)
        # A one-pixel filament with a short glint, not a solid steel hoop.
        length = max(2,round(7*amount))
        if front:
            points = [(x+outward,y+1),(x+3*outward,y+3),(x+3*outward,y+length),
                      (x+outward,y+length+2),(x-outward,y+length+1)]
        else:
            points = [(x+outward,y),(x+4*outward,y+1),(x+length*outward,y+3),
                      (x+(length-1)*outward,y+5)]
        if phase == 'fire':
            points = [(x,y),(x+outward*(5+frame),y-2),(x+outward*(8+frame),y+2)]
        d.line(points, fill=P['m'])
        d.point(points[1], fill=P['w'])
        return canvas
    rows = FRONT[kind] if front else SIDE[kind]
    image = pixels(rows)
    # Sliding mechanisms reveal their actual silhouette along the slide axis.
    if amount < 1:
        if kind == 'mantis':
            # Folded hinge lies along the forearm before the two blades separate.
            rows = ['.hm.','..hm','..am','..wm','..wm','..w.'] if front else ['.hmh..','..amhw','...ww.']
            image = pixels(rows)
        elif front:
            image = image.crop((0,0,image.width,max(1,round(image.height*amount))))
        else:
            image = image.crop((0,0,max(1,round(image.width*amount)),image.height))
    if outward < 0:
        image = image.transpose(Image.Transpose.FLIP_LEFT_RIGHT)
    ox = x-1 if outward > 0 else x+2-image.width
    oy = y-1 if front else y
    if phase == 'fire' and frame in (0,1):
        ox -= outward
        if kind in ('impact','claws','blade','mantis'):
            oy -= 1
    canvas.alpha_composite(image,(ox,oy))
    d = ImageDraw.Draw(canvas)
    if phase == 'empty':
        d.point((x,y-1),fill=P['r'])
    if phase == 'reload' and kind in ('pistol','launcher'):
        stamp(canvas,['.hh','mms','ds.'], (x-outward*(1+frame//2),y+3+frame%2), mirror=outward < 0)
        d.point((x,y-1),fill=P['a'])
    if phase == 'fire' and kind in ('pistol','launcher') and frame < 2:
        fx = ox+image.width if outward > 0 else ox-1
        fy = oy+image.height-2
        d.line([(fx,fy),(fx+2*outward,fy)],fill='#ead89c')
        d.point((fx+outward,fy-1),fill='#cc9656')
        d.point((fx+outward,fy+1),fill='#cc9656')
    return canvas


# Production parts use individual small silhouettes instead of a shared rectangle.
PARTS = {
 'microdrive':['....hhhh....','...hmmmms...','..hmsddsms..','..hmsadsmd..','..dmmssmd...','...dmmmd....','....hh......','....ms......','...hmmh.....','...dssd.....'],
 'guide':['..h......h..','..hm.....hm.','..hm.....hm.','..hmmmmmmhm.','..hmssssshm.','..hm.....hm.','..hm.....hm.','..d......d..'],
 'lock':['...hhhh....','..hmmssh...','..hm..sh...','..hm..sh...','.hmmmmmmh..','.hmssmsmd..','..dmsamsd..','...ddddd...'],
 'damper':['....hh....','....ms....','...hmmh...','..hsssmh..','...hmmh...','..hsssmh..','...hmmh...','....ms....','...hmmh...','...dssd...'],
 'cooling':['..hhhhhhhh..','.hmmmmmmmsd.','.hmhshshsmd.','.hmhshshsmd.','.hmhshshsmd.','.hmhshshsmd.','.dmmmmmmmsd.','..dddddddd..'],
 'hinge':['...hhh......','..hmssh.....','..hmamh.....','...dmmhh....','.....hmmh...','......hamh..','......dssd..'],
 'reel':['....hhhhh....','..hhmmssmh...','.hmmshhhsmh..','.hmshddhsmh..','.hmshadhsmh..','.hmshddhsmh..','..hmmssmmh...','...dmmmmd....'],
 'wire':['....hhhh....','...hmsdsh...','..hms.hmsh..','..hms.hmsh..','...dmmmmd...','.....m......','......m.....','.......hm...','.........m..'],
 'storage':['....hhhhh....','..hhmmmmsh...','.hmmsssssmd..','.hmshhhhsmh..','.hmsdddddmd..','..hmsadsmh...','...dmmmmd....','....dddd.....'],
 'socket':['...hhhhh...','..hmsssmh..','.hmsddsmh..','.hmsadsmh..','..hmsmmh...','...dddd....'],
 'cuff':['..hhhhhh..','.hmssssmh.','.hms..smh.','.hms..smh.','.dmssssmd.','..dmmmmd..','...dddd...'],
 'shell':['....hhhh...','...hmmmsh..','..hmmsssmh.','..hmssasmh.','..hmssssmd.','..hmmssmd..','...dmmmd...','....ddd....'],
 'drive':['..hhh....hhh..','.hmmsh..hmmsh.','.hmssh..hmssh.','..dms.hhsmd...','...hmmmmmh....','...dmmssmd....','....dmmmd.....','.....hh.......','....hmmh......','....dssd......'],
}


def source_icons():
    source = Image.open(ART/'arm-modules-source.png').convert('RGBA')
    result = {}
    names = ['pistol','launcher','blade','mantis','claws','impact','monowire','tool','medical']
    # The source is a regular 3x3 layout; keep transparent padding and fit each silhouette.
    for i,name in enumerate(names):
        x,y = i%3,i//3
        cell = source.crop((x*source.width//3,y*source.height//3,(x+1)*source.width//3,(y+1)*source.height//3))
        cell = cell.crop(cell.getbbox())
        cell.thumbnail((26,26), Image.Resampling.LANCZOS)
        alpha = cell.getchannel('A').point(lambda a: 255 if a >= 128 else 0)
        # Limited palette and binary alpha keep the final asset on a true one-pixel grid.
        pal = Image.new('P',(1,1))
        colors = [tuple(bytes.fromhex(c[1:])) for c in P.values()]
        pal.putpalette([v for c in colors for v in c] + [0]*(768-len(colors)*3))
        cell = cell.convert('RGB').quantize(palette=pal,dither=Image.Dither.NONE).convert('RGBA')
        cell.putalpha(alpha)
        image = Image.new('RGBA',(32,32))
        image.alpha_composite(cell,((32-cell.width)//2,(32-cell.height)//2))
        result[name] = image
    return result


def icon(kind, sources):
    if kind in sources:
        return sources[kind]
    image = Image.new('RGBA',(32,32))
    if kind.startswith(('civilian','armored')):
        rows = ['....hhhh....','...hmmmmh...','...hmssmmd..','...hmsmsmd..',
                '....hmsmd...','....hmsmd...','....hmmmd...','.....dmsd...',
                '.....hamh...','.....dmsd...','.....hmmh...','....hmmmmd..',
                '....hmssmd..','....hmcsmh..','....hmssmd..','.....hmsmd..',
                '.....hmsmd..','......hmmh..','......dmsd..','......hmmmh.',
                '......hmhmmd','.......msmsd','.......dddd.']
        if kind.startswith('armored'):
            rows = ['...hhhhhh...','..hmmmmmsh..','..hmsssssmh.','..hmmmsmsmd.',
                    '...hmmssmd..','...hmssmd...','....dmsmd...', '....hamh....',
                    '....dmsd....','...hmmmhh...','...hmsssmh..','...hmmmsmd..',
                    '...hmasmmd..','....hmssmd..','....hmssmd..','....hmmmd...',
                    '.....dmsd...','.....hmmh...','.....dmsd...','.....hmmmmh.',
                    '.....hmhmhmd','......msmsmd','......ddddd.']
        part = pixels(rows)
        if kind.endswith('right'):
            part = part.transpose(Image.Transpose.FLIP_LEFT_RIGHT)
    else:
        part = pixels(PARTS[kind])
    image.alpha_composite(part,((32-part.width)//2,(32-part.height)//2))
    return image


def save(path, states, draw):
    path.mkdir(parents=True,exist_ok=True)
    metadata = []
    for name, directions, frames, delay in states:
        # Direction-major ordering, as consumed by Robust's RSI loader.
        sheet = Image.new('RGBA',(32*frames,32*directions))
        for direction in range(directions):
            for frame in range(frames):
                sheet.alpha_composite(draw(name,direction,frame),(32*frame,32*direction))
        sheet.save(path/(name+'.png'))
        state = {'name':name,'directions':directions}
        if frames > 1:
            state['delays'] = [[delay]*frames for _ in range(directions)]
        metadata.append(state)
    (path/'meta.json').write_text(json.dumps({
        'version':1,'license':'CC0-1.0',
        'copyright':'Original Night City 2045 art. Inventory reference generated with OpenAI ImageGen; native on-body pixel masters authored separately. Sources and prompt: Tools/_NC/Cyberware/Art; build: Tools/_NC/Cyberware/generate_cyberarm_sprites.py.',
        'size':{'x':32,'y':32},'states':metadata},indent=4)+'\n')


def main():
    sources = source_icons()
    save(ROOT/'Resources/Textures/_NC/Objects/Cyberware/arms.rsi',
         [(n,1,1,0) for n in ICONS]+[('inhand-left',4,1,0),('inhand-right',4,1,0)],
         lambda n,d,f: Image.new('RGBA',(32,32)) if n.startswith('inhand') else icon(n,sources))
    states = []
    for kind in MODULES:
        for side in ('left','right'):
            for phase in ('deploy','ready','fire','empty','reload','retract'):
                animated = phase in ('deploy','fire','reload','retract')
                states.append((f'{kind}_{side}_{phase}',4,4 if animated else 1,.0625 if phase=='fire' else .15))
    save(ROOT/'Resources/Textures/_NC/Mobs/Cyberware/arms.rsi',states,
         lambda n,d,f: world(*n.split('_')[:2],d,n.split('_')[2],f))


if __name__ == '__main__':
    main()
