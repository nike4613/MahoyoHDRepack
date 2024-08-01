import sys
import math
import fontforge
import psMat

if len(sys.argv) < 3:
  print("requires 2 args: <base font> <out font>")
  exit()

baseFontFile = sys.argv[1]
outFontFile = sys.argv[2]

UNI_PRIVATEUSE = 0xE000
UNI_MODPOINTS_MIN = 0x21
UNI_MODPOINTS_MAX = 0x7E
UNI_RANGE_SIZE = 0x80

# forward italics
FNT_IT_F_IDX = 0
# reverse italics
FNT_IT_R_IDX = 1
# flipped
FNT_FLIPPED_IDX = 2
# flipped forward italics
FNT_FLIPPED_IT_F_IDX = 3
# flipped reverse italics
FNT_FLIPPED_IT_R_IDX = 4
# vertical flip
FNT_VFLIPPED_IDX = 5

identity = psMat.identity()
flipTransform = psMat.scale(-1,1)
vflipTransform = psMat.scale(1,-1)

shouldFlip = [
  False,
  False,
  True,
  True,
  True,
  False,
]

shouldVFlip = [
  False,
  False,
  False,
  False,
  False,
  True,
]

# for some reason, normal forward italics is negative
italicAmount = [
  -13,
  13,
  None,
  -13,
  13,
  None,
]

print(italicAmount)
print([x for x in range(FNT_IT_F_IDX, FNT_VFLIPPED_IDX + 1)])

baseFnt = fontforge.open(baseFontFile, ("fstypepermitted", "hidewindow", "alltables"))

# first pass, find maximum glyph bounds
(gxmin, gymin, gxmax, gymax) = (math.inf, math.inf, -math.inf, -math.inf)
for cloneCp in range(UNI_MODPOINTS_MIN, UNI_MODPOINTS_MAX + 1):
  glyph = baseFnt.createChar(cloneCp)
  (xmin, ymin, xmax, ymax) = glyph.boundingBox()
  
  gxmin = min(gxmin, xmin)
  gymin = min(gymin, ymin)
  gxmax = max(gxmax, xmax)
  gymax = max(gymax, ymax)

# then we can do our work
for cloneCp in range(UNI_MODPOINTS_MIN, UNI_MODPOINTS_MAX + 1):
  cpOffs = cloneCp - UNI_MODPOINTS_MIN
  glyph = baseFnt.createChar(cloneCp)
  
  baseFnt.selection.none()
  baseFnt.selection.select(glyph)
  baseFnt.copy()

  for id in range(FNT_IT_F_IDX,FNT_VFLIPPED_IDX + 1):
    formCp = UNI_PRIVATEUSE + (id * UNI_RANGE_SIZE) + cpOffs

    newGlyph = baseFnt.createChar(formCp)
    baseFnt.selection.none()
    baseFnt.selection.select(newGlyph)
    
    # copy the glyph
    baseFnt.paste()

    # apply italicization as appropriate
    itAmt = italicAmount[id]
    if itAmt != None:
      baseFnt.italicize(italic_angle=itAmt)

    (xmin, ymin, xmax, ymax) = newGlyph.boundingBox()

    # flip the glyph if needeed
    if shouldFlip[id]:
      newGlyph.transform(psMat.compose(flipTransform, psMat.translate(xmax + xmin, 0)))
      
    if shouldVFlip[id]:
      newGlyph.transform(psMat.compose(vflipTransform, psMat.translate(0, ymax + (gymax - ymax))))
      
    newGlyph.correctDirection()
    

baseFnt.generate(outFontFile)