"""One-time asset generation; ordinary builds use the checked-in .ttf.gz files.

Requires fonttools==4.60.1. Input: unmodified Google Fonts NotoSansSC[wght].ttf
at revision a85815a42757630ce188fdad368c2dfc444d4773. See assets/fonts/README.md.
"""
import gzip
import hashlib
import io
import pathlib
import sys

from fontTools import subset
from fontTools.ttLib import TTFont
from fontTools.varLib.instancer import instantiateVariableFont

root = pathlib.Path(__file__).resolve().parents[1]
source = pathlib.Path(sys.argv[1])
assert hashlib.sha256(source.read_bytes()).hexdigest() == "a3041811a78c361b1de50f953c805e0244951c21c5bd412f7232ef0d899af0da"
characters = set(range(0x20, 0x100))
for high in range(0xA1, 0xF8):
    for low in range(0xA1, 0xFF):
        try:
            characters.update(map(ord, bytes([high, low]).decode("gb2312")))
        except UnicodeDecodeError:
            pass
characters.update(map(ord, (root / "src/DeepSeekHarnessControlPanel.cs").read_text(encoding="utf-8-sig")))
font = TTFont(source, recalcTimestamp=False)
options = subset.Options()
options.name_IDs = [0, 1, 2, 3, 4, 5, 6, 13, 14, 16, 17]
subsetter = subset.Subsetter(options=options)
subsetter.populate(unicodes=characters)
subsetter.subset(font)
for weight in (400, 500, 600, 700):
    instance = instantiateVariableFont(font, {"wght": weight}, inplace=False)
    # No variable axes remain; STAT would make Windows append Medium/SemiBold
    # to these deliberately independent family names.
    if "STAT" in instance:
        del instance["STAT"]
    family = "DSH Tray UI " + str(weight)
    # Independent families avoid GDI+'s Regular/Bold-only style linking. Outlines
    # are genuine static instances at the requested weight, not synthetic bold.
    names = instance["name"]
    for name_id in (1, 2, 3, 4, 6, 16, 17):
        names.removeNames(nameID=name_id)
    for name_id, value in {1: family, 2: "Regular", 3: family + " 1.0", 4: family,
                           6: "DSHTrayUI" + str(weight), 16: family, 17: "Regular"}.items():
        names.setName(value, name_id, 3, 1, 0x409)
    instance["OS/2"].usWeightClass = weight
    instance["OS/2"].fsSelection = (instance["OS/2"].fsSelection & ~0x21) | 0x40
    instance["head"].macStyle &= ~3
    output = io.BytesIO()
    instance.save(output)
    target = root / ("assets/fonts/TrayUI" + str(weight) + ".ttf.gz")
    target.write_bytes(gzip.compress(output.getvalue(), compresslevel=9, mtime=0))
    print(f"{target.name}: {len(output.getvalue())} bytes, compressed {target.stat().st_size}, weight {weight}", flush=True)
