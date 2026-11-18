"""Convert a supplied transparent PNG into the Windows icon and embedded header image."""
import sys
from pathlib import Path
from PIL import Image

source = Image.open(sys.argv[1]).convert("RGBA")
assets = Path(__file__).resolve().parents[1] / "src" / "AudioFlow.App" / "Assets"
source.resize((256, 256), Image.Resampling.LANCZOS).save(assets / "app-icon.png")
source.resize((256, 256), Image.Resampling.LANCZOS).save(
    assets / "app.ico", sizes=[(s, s) for s in (16, 20, 24, 32, 40, 48, 64, 128, 256)]
)
with Image.open(assets / "app.ico") as icon:
    print("Windows icon sizes:", sorted(icon.ico.sizes()))
