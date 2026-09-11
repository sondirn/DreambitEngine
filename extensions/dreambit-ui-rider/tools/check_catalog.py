from pathlib import Path
import re
import sys

root = Path(__file__).resolve().parents[1]
source = (root / "src/main/java/com/dreambit/rider/ui/DreambitUiSupport.java").read_text(encoding="utf-8")

expected_elements = [
    "Container", "Panel", "Canvas", "ContentControl", "Control", "Border", "Overlay", "Button",
    "ToggleButton", "CheckBox", "RadioButton", "ComboBox", "Popup", "Tooltip", "Grid", "UniformGrid",
    "WrapPanel", "VerticalStackPanel", "HorizontalStackPanel", "StackPanel", "ItemsControl", "ListBox",
    "Text", "TextBox", "Texture", "Slider", "ScrollBar", "ProgressBar", "Spacer", "Viewbox",
]
expected_brushes = [
    "SolidColorBrush", "SpriteBrush", "TiledSpriteBrush", "NineSliceBrush", "OutlineBrush", "LayeredBrush",
]

element_block = source[source.index("private static void defineElements()") : source.index("private static void defineBrushes()")]
brush_block = source[source.index("private static void defineBrushes()") : source.index("public static List<String> allElementNames")]

elements = re.findall(r'\belement\("([^"]+)"', element_block)
brushes = re.findall(r'\bbrush\("([^"]+)"', brush_block)

ok = True
if elements != expected_elements:
    ok = False
    print("Element catalog mismatch", file=sys.stderr)
    print("Expected:", expected_elements, file=sys.stderr)
    print("Actual:  ", elements, file=sys.stderr)
if brushes != expected_brushes:
    ok = False
    print("Brush catalog mismatch", file=sys.stderr)
    print("Expected:", expected_brushes, file=sys.stderr)
    print("Actual:  ", brushes, file=sys.stderr)

print(f"Elements: {len(elements)}")
print(f"Brushes:  {len(brushes)}")
if not ok:
    raise SystemExit(1)
print("Catalog check passed.")
