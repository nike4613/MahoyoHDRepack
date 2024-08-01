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
UNI_RANGE_SIZE = 0x7F

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

identity = psMat.identity()
flipTransform = psMat.scale(-1,1)

shouldFlip = [
  False,
  False,
  True,
  True,
  True
]

# for some reason, normal forward italics is negative
italicAmount = [
  -13,
  13,
  None,
  -13,
  13,
]

print(italicAmount)
print([x for x in range(FNT_IT_F_IDX, FNT_FLIPPED_IT_R_IDX + 1)])

baseFnt = fontforge.open(baseFontFile, ("fstypepermitted", "hidewindow", "alltables"))

for cloneCp in range(UNI_MODPOINTS_MIN, UNI_MODPOINTS_MAX + 1):
  cpOffs = cloneCp - UNI_MODPOINTS_MIN
  glyph = baseFnt.createChar(cloneCp)

  for id in range(FNT_IT_F_IDX,FNT_FLIPPED_IT_R_IDX + 1):
    formCp = UNI_PRIVATEUSE + (id * UNI_RANGE_SIZE) + cpOffs

    newGlyph = baseFnt.createChar(formCp)
    # copy the glyph
    glyph.draw(newGlyph.glyphPen())
    newGlyph.left_side_bearing = int(glyph.left_side_bearing)
    newGlyph.right_side_bearing = int(glyph.right_side_bearing)
    newGlyph.width = glyph.width
    newGlyph.vwidth = glyph.vwidth

    # apply italicization as appropriate
    itAmt = italicAmount[id]
    if itAmt != None:
      baseFnt.selection.none()
      baseFnt.selection[formCp] = True
      baseFnt.italicize(italic_angle=itAmt)
      baseFnt.selection[formCp] = False

    # flip the glyph if needeed
    if shouldFlip[id]:
      newGlyph.transform(psMat.compose(flipTransform, psMat.translate(newGlyph.width, 0)))

baseFnt.generate(outFontFile)