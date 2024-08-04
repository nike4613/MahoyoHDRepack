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
fontMode = None
if len(sys.argv) >= 5:
  fontMode = int(sys.argv[4])
antiquaFontPath = None
if len(sys.argv) >= 6:
  antiquaFontPath = sys.argv[5]
antiquaGlyphScale = 1.0
if len(sys.argv) >= 7:
  antiquaGlyphScale = float(sys.argv[6])
antiquaWeightChange = 0
if len(sys.argv) >= 8:
  antiquaWeightChange = float(sys.argv[7])

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
antiqFnt = fontforge.open(antiquaFontPath, ("fstypepermitted", "hidewindow", "alltables")) if antiquaFontPath != None else None
if antiqFnt != None and antiquaWeightChange != 0:
  antiqFnt.changeWeight(antiquaWeightChange)

allCodepoints = [*range(UNI_MODPOINTS_MIN, UNI_MODPOINTS_MAX + 1), *fontinfo['ExtraCodepoints']]

print("mode", fontMode)


(gbxmin, gbymin, gbxmax, gbymax) = (math.inf, math.inf, -math.inf, -math.inf)
(gaxmin, gaymin, gaxmax, gaymax) = (math.inf, math.inf, -math.inf, -math.inf)
for i,cp in enumerate(allCodepoints):
  bglyph = baseFnt.createChar(cp)
  aglyph = antiqFnt.createChar(cp)
  
  (bxmin, bymin, bxmax, bymax) = bglyph.boundingBox()
  (axmin, aymin, axmax, aymax) = aglyph.boundingBox()
  
  gbxmin = min(gbxmin, bxmin)
  gbymin = min(gbymin, bymin)
  gbxmax = max(gbxmax, bxmax)
  gbymax = max(gbymax, bymax)
  
  gaxmin = min(gaxmin, axmin)
  gaymin = min(gaymin, aymin)
  gaxmax = max(gaxmax, axmax)
  gaymax = max(gaymax, aymax)

antiqScaleFac = max(gbxmax - gbxmin, gbymax - gbymin) / max(gaxmax - gaxmin, gaymax - gaymin);
antiqScaleFac *= antiquaGlyphScale;
print(antiqScaleFac)
antiqScale = psMat.scale(antiqScaleFac)

# then we can do our work
for id,fmtinfo in enumerate(fontinfo['FormatOptions']):
  baseOffs = UNI_PRIVATEUSE + (id * UNI_RANGE_SIZE)
  
  info = fmtinfo['Formats']
  modes = fmtinfo['FontModes']

  print("region",id,baseOffs,fmtinfo)
  
  if fontMode != None and fontMode not in modes:
    continue # a fontMode was specified, and this format isn't used in that font mode

  srcFnt = baseFnt
  glyScale = psMat.identity()
  if info['Antiqua']:
    if antiqFnt != None:
      srcFnt = antiqFnt
      glyScale = antiqScale
    else:
      print("WARNING: No Antiqua font provided, but one was needed! Using normal glyphs instead.")  
        
  # first pass, find maximum glyph bounds and copy glyphs
  (gxmin, gymin, gxmax, gymax) = (math.inf, math.inf, -math.inf, -math.inf)
  for i,cp in enumerate(allCodepoints):
    glyph = srcFnt.createChar(cp)
    tgtGlyph = baseFnt.createChar(baseOffs + i)
    
    srcFnt.selection.select(glyph)
    srcFnt.copy()
    baseFnt.selection.select(tgtGlyph)
    baseFnt.paste()
    
    # rescale the copied glyph to be more reasonable
    tgtGlyph.transform(glyScale)

    (xmin, ymin, xmax, ymax) = tgtGlyph.boundingBox()
    gxmin = min(gxmin, xmin)
    gymin = min(gymin, ymin)
    gxmax = max(gxmax, xmax)
    gymax = max(gymax, ymax)
    
  # second pass: italicize, if needed
  didItalic = False
  if info['ItalicAmt'] != None and info['ItalicAmt'] != 0:
    # select
    baseFnt.selection.none()
    didItalic = True
    for i in range(len(allCodepoints)):
      glyph = baseFnt.createChar(baseOffs + i)
    
      (xmin, ymin, xmax, ymax) = glyph.boundingBox()
      if info['HorizFlip']: # if we want to horizontal flip, do that BEFORE italicization to avoid kerning issues
        glyph.transform(psMat.compose(flipTransform, psMat.translate(xmax + xmin, 0)))
        
      baseFnt.selection.select(("more",), glyph)
    # italicize
    amt = info['ItalicAmt']
    if info['HorizFlip']:
      amt = -amt # if we're horizontal flipping, fix up the italic amount
    baseFnt.italicize(italic_angle=amt)
  
  # third pass: flip and fixup
  for i in range(len(allCodepoints)):
    glyph = baseFnt.createChar(baseOffs + i)
    
    (xmin, ymin, xmax, ymax) = glyph.boundingBox()
    if info['HorizFlip'] and not didItalic: # if we want to horizontal flip, do that BEFORE italicization to avoid kerning issues
      glyph.transform(psMat.compose(flipTransform, psMat.translate(xmax + xmin, 0)))
    if info['VertFlip']:
      glyph.transform(psMat.compose(vflipTransform, psMat.translate(0, ymax + (gymax - ymax) + gymin)))
    glyph.correctDirection()

baseFnt.generate(outFontFile)