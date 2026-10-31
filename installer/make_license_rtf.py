"""Converts ../LICENSE to License.rtf for the installer's licence page. Run from this folder."""
import os

here = os.path.dirname(os.path.abspath(__file__))
text = open(os.path.join(here, "..", "LICENSE"), encoding="utf-8").read().strip()
body = text.replace("\\", "\\\\").replace("{", "\\{").replace("}", "\\}")
paragraphs = [p.replace("\n", " ") for p in body.split("\n\n")]
rtf = "{\\rtf1\\ansi\\deff0{\\fonttbl{\\f0 Segoe UI;}}\\f0\\fs18 " + "\\par\\par ".join(paragraphs) + "}"
open(os.path.join(here, "License.rtf"), "w", encoding="ascii").write(rtf)
