import sys
import math
import json
import fontforge
import psMat

if len(sys.argv) < 4:
  print("requires 3 args: <base font> <fontinfo.json> <out font>")
  exit()

baseFontFile = sys.argv[1]
fontinfoJson = sys.argv[2]
outFontFile = sys.argv[3]

fontinfo = None
with open(fontinfoJson) as f:
    fontinfo = json.load(f)

UNI_PRIVATEUSE = 0xE000
UNI_MODPOINTS_MIN = fontinfo['AutoMinCodepoint']
UNI_MODPOINTS_MAX = fontinfo['AutoMaxCodepoint']
UNI_RANGE_SIZE = fontinfo['RangeSize']

flipTransform = psMat.scale(-1,1)
vflipTransform = psMat.scale(1,-1)

baseFnt = fontforge.open(baseFontFile, ("fstypepermitted", "hidewindow", "alltables"))

allCodepoints = [*range(UNI_MODPOINTS_MIN, UNI_MODPOINTS_MAX + 1), *fontinfo['ExtraCodepoints']]

print(allCodepoints)

# first pass, find maximum glyph bounds
(gxmin, gymin, gxmax, gymax) = (math.inf, math.inf, -math.inf, -math.inf)
for cloneCp in allCodepoints:
  glyph = baseFnt.createChar(cloneCp)
  (xmin, ymin, xmax, ymax) = glyph.boundingBox()
  
  gxmin = min(gxmin, xmin)
  gymin = min(gymin, ymin)
  gxmax = max(gxmax, xmax)
  gymax = max(gymax, ymax)

# then we can do our work
for id,info in enumerate(fontinfo['FormatOptions']):
  baseOffs = UNI_PRIVATEUSE + (id * UNI_RANGE_SIZE)
  
  print("region",id,baseOffs,info)

  # first pass: copy glyphs
  for i,cp in enumerate(allCodepoints):
    glyph = baseFnt.createChar(cp)
    tgtGlyph = baseFnt.createChar(baseOffs + i)
    
    baseFnt.selection.select(glyph)
    baseFnt.copy()
    baseFnt.selection.select(tgtGlyph)
    baseFnt.paste()
    
  # second pass: italicize, if needed
  if info['ItalicAmt'] != None and info['ItalicAmt'] != 0:
    # select
    baseFnt.selection.none()
    for i in range(len(allCodepoints)):
      baseFnt.selection.select(("more",), baseFnt.createChar(baseOffs + i))
    # italicize
    baseFnt.italicize(italic_angle=info['ItalicAmt'])
  
  # third pass: flip and fixup
  for i in range(len(allCodepoints)):
    glyph = baseFnt.createChar(baseOffs + i)
    
    (xmin, ymin, xmax, ymax) = glyph.boundingBox()
    if info['HorizFlip']:
      glyph.transform(psMat.compose(flipTransform, psMat.translate(xmax + xmin, 0)))
    if info['VertFlip']:
      glyph.transform(psMat.compose(vflipTransform, psMat.translate(0, ymax + (gymax - ymax) + gymin)))
    glyph.correctDirection()

baseFnt.generate(outFontFile)