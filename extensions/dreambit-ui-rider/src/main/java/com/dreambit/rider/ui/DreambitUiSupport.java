package com.dreambit.rider.ui;

import java.util.*;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

public final class DreambitUiSupport {
    private DreambitUiSupport() {}

    public enum UxmlKind { NONE, CONTENT, TAG_NAME, CLOSING_TAG, ATTRIBUTE_NAME, ATTRIBUTE_VALUE }
    public enum UcssKind { SELECTOR, PROPERTY_NAME, PROPERTY_VALUE }

    public record AttributeDefinition(String name, String type, String description, List<String> values) {}
    public record ElementDefinition(String description, List<String> attributes, List<String> propertyNodes) {}
    public record BrushDefinition(String description, List<String> attributes) {}
    public record UxmlContext(
            UxmlKind kind,
            String parentName,
            String tagName,
            String attributeName,
            String prefix,
            Set<String> existingAttributes) {}
    public record UcssContext(UcssKind kind, String elementName, String propertyName, String prefix) {}

    private static final List<String> ANCHORS = List.of(
            "TopLeft", "TopCenter", "TopRight",
            "CenterLeft", "Center", "CenterRight",
            "BottomLeft", "BottomCenter", "BottomRight");

    private static final Map<String, AttributeDefinition> DEFINITIONS = new LinkedHashMap<>();
    private static final Map<String, ElementDefinition> ELEMENTS = new LinkedHashMap<>();
    private static final Map<String, BrushDefinition> BRUSHES = new LinkedHashMap<>();
    private static final Map<String, String> CSS_ALIASES = Map.of(
            "font-family", "font",
            "color", "text-color",
            "z-index", "z");
    private static final Map<String, String> XML_TO_CSS = Map.of(
            "font", "font-family",
            "text-color", "color",
            "z", "z-index");
    private static final Set<String> STRUCTURAL = Set.of("id", "class", "source", "id-prefix");
    private static final List<String> COMMON_ATTRIBUTES = List.of(
            "id", "class", "x", "y", "width", "height", "anchor", "origin", "z",
            "grid-row", "grid-column", "grid-row-span", "grid-column-span",
            "is-visible", "is-enabled", "is-hit-test-visible", "is-focusable",
            "captures-keyboard-input", "clip-to-bounds");
    private static final Set<String> NON_CONTAINERS = Set.of("Text", "Texture", "Spacer");

    static {
        defineAttributes();
        defineElements();
        defineBrushes();
    }

    private static void defineAttributes() {
        put(a("id", "string", "Unique identifier used by UiLayout.Find. XML only."));
        put(a("class", "class-list", "Whitespace-separated style classes. XML only."));
        put(a("x", "length", "Horizontal position: pixels or a percentage."));
        put(a("y", "length", "Vertical position: pixels or a percentage."));
        put(a("width", "size", "Element width: pixels, percentage, or auto."));
        put(a("height", "size", "Element height: pixels, percentage, or auto."));
        put(a("anchor", "enum", "Reference point on the parent.", ANCHORS));
        put(a("origin", "enum", "Reference point on this element.", ANCHORS));
        put(a("z", "integer", "Draw and hit-test order within the parent."));
        put(a("grid-row", "integer", "Zero-based grid row."));
        put(a("grid-column", "integer", "Zero-based grid column."));
        put(a("grid-row-span", "integer", "Number of grid rows occupied."));
        put(a("grid-column-span", "integer", "Number of grid columns occupied."));
        put(bool("is-visible", "Whether the element participates in layout and drawing."));
        put(bool("is-enabled", "Whether the element accepts interaction."));
        put(bool("is-hit-test-visible", "Whether pointer hit testing can target the element."));
        put(bool("is-focusable", "Whether keyboard/gamepad focus can target the element."));
        put(bool("captures-keyboard-input", "Whether the element consumes keyboard input."));
        put(bool("clip-to-bounds", "Whether descendants are clipped to the element bounds."));
        put(color("background-color", "Creates a solid background brush with this tint."));
        put(color("background-tint", "Tint supplied to the background brush."));
        put(a("padding", "thickness", "Insets in left, top, right, bottom order in UXML; UCSS accepts 1–4 px values."));
        put(a("content-alignment", "enum", "Alignment of content inside a content control.", ANCHORS));
        put(color("hover-tint", "Tint multiplier while the pointer is over the control."));
        put(color("pressed-tint", "Tint multiplier while the control is pressed."));
        put(color("focused-tint", "Tint multiplier while the control has focus."));
        put(color("disabled-tint", "Tint multiplier while the control is disabled."));
        put(color("checked-tint", "Tint multiplier while a toggle control is checked."));
        put(color("selected-tint", "Tint multiplier while the control is selected."));
        put(color("open-tint", "Tint multiplier while the control is open."));
        put(a("text", "string", "Displayed or editable text."));
        put(a("placeholder", "string", "Text shown when a text box is empty."));
        put(a("font", "asset", "Dreambit font asset path. Use font-family in UCSS."));
        put(a("font-size", "number", "Font size in pixels."));
        put(color("text-color", "Text color. The UCSS color alias maps here."));
        put(a("horizontal-alignment", "enum", "Horizontal text alignment.", List.of("Left", "Center", "Right")));
        put(bool("multi-line", "Whether text wraps over multiple lines."));
        put(bool("auto-resize-height", "Whether text changes its own height to fit wrapped lines."));
        put(a("max-length", "integer", "Maximum editable character count; zero means unlimited."));
        put(a("password-character", "character", "Optional character used to mask text input."));
        put(color("placeholder-color", "Text box placeholder color."));
        put(color("selection-color", "Text box selection highlight color."));
        put(color("caret-color", "Text box caret color."));
        put(a("sprite", "asset", "Sprite asset path."));
        put(color("tint", "Texture tint."));
        put(a("orientation", "enum", "Layout or fill axis.", List.of("Vertical", "Horizontal")));
        put(a("spacing", "integer", "Space between adjacent children."));
        put(a("line-spacing", "integer", "Space between wrapped lines."));
        put(a("column-spacing", "integer", "Space between uniform-grid columns."));
        put(a("row-spacing", "integer", "Space between uniform-grid rows."));
        put(a("cross-alignment", "enum", "Child alignment on the cross axis.", List.of("Start", "Center", "End")));
        put(a("grow-direction", "enum", "Placement of the complete stack on its primary axis.", List.of("Start", "Top", "Left", "Center", "End", "Bottom", "Right")));
        put(a("row-definitions", "grid-definitions", "Comma-separated Grid lengths, for example *, 2*, 40."));
        put(a("column-definitions", "grid-definitions", "Comma-separated Grid lengths, for example *, 2*, 120."));
        put(a("rows", "integer", "Explicit uniform-grid row count; zero is automatic."));
        put(a("columns", "integer", "Explicit uniform-grid column count; zero is automatic."));
        put(bool("blocks-input", "Whether the overlay blocks input to content behind it."));
        put(bool("is-checked", "Initial checked state."));
        put(a("group", "string", "Radio-button group name."));
        put(a("minimum", "number", "Minimum range value."));
        put(a("maximum", "number", "Maximum range value."));
        put(a("step", "number", "Range increment."));
        put(a("value", "number", "Current range value."));
        put(a("track-thickness", "integer", "Slider track thickness in pixels."));
        put(a("thumb-size", "integer", "Slider thumb size in pixels."));
        put(color("track-tint", "Track brush tint."));
        put(color("fill-tint", "Filled-range brush tint."));
        put(color("thumb-tint", "Thumb brush tint."));
        put(a("viewport-size", "number", "Visible portion represented by a scrollbar thumb."));
        put(a("large-change", "number", "Scrollbar page increment."));
        put(a("minimum-thumb-size", "integer", "Smallest scrollbar thumb size in pixels."));
        put(a("indicator-size", "integer", "Checkbox indicator size in pixels."));
        put(a("indicator-spacing", "integer", "Space between indicator and content."));
        put(color("indicator-tint", "Checkbox indicator brush tint."));
        put(color("mark-tint", "Checkbox mark brush tint."));
        put(a("item-height", "integer", "Combo-box popup item height."));
        put(a("items", "csv", "Comma-separated combo-box items."));
        put(a("selected-index", "integer", "Initially selected item index; -1 means no selection."));
        put(color("popup-tint", "Combo-box popup tint."));
        put(bool("is-open", "Whether a popup is initially open."));
        put(bool("stays-open", "Whether a popup remains open after outside interaction."));
        put(a("placement-target", "id-reference", "ID of the element used to place a popup."));
        put(a("horizontal-offset", "integer", "Popup horizontal offset in pixels."));
        put(a("vertical-offset", "integer", "Popup vertical offset in pixels."));
        put(a("placement", "enum", "Popup placement relative to its target.", List.of("Bottom", "Top", "Left", "Right", "Center", "Absolute")));
        put(a("delay", "number", "Tooltip delay in seconds."));
        put(a("stretch", "enum", "How viewbox content fits its bounds.", List.of("None", "Fill", "Uniform", "UniformToFill")));

        // Brush-only authored attributes.
        put(a("slice", "thickness", "Default nine-slice edge thickness."));
        put(a("slice-left", "integer", "Nine-slice left edge thickness."));
        put(a("slice-top", "integer", "Nine-slice top edge thickness."));
        put(a("slice-right", "integer", "Nine-slice right edge thickness."));
        put(a("slice-bottom", "integer", "Nine-slice bottom edge thickness."));
        put(a("thickness", "thickness", "Default outline thickness."));
        put(a("left", "integer", "Outline left thickness."));
        put(a("top", "integer", "Outline top thickness."));
        put(a("right", "integer", "Outline right thickness."));
        put(a("bottom", "integer", "Outline bottom thickness."));
    }

    private static void defineElements() {
        List<String> content = List.of("background-color", "padding", "content-alignment", "background-tint");
        List<String> control = List.of("hover-tint", "pressed-tint", "focused-tint", "disabled-tint", "checked-tint", "selected-tint", "open-tint");
        List<String> range = List.of("minimum", "maximum", "step", "value");
        List<String> slider = List.of("orientation", "track-thickness", "thumb-size", "track-tint", "fill-tint", "thumb-tint");
        List<String> stack = List.of("padding", "spacing", "cross-alignment", "grow-direction");

        element("Container", "General-purpose retained UI container.");
        element("Panel", "Basic container for freely positioned children.");
        element("Canvas", "Panel intended for explicit child positioning.");
        element("ContentControl", "Container with one content child and a brush background.", content, List.of("Background"));
        element("Control", "Interactive single-content control with focus and pointer state tints.", concat(content, control), List.of("Background"));
        element("Border", "Content control used to provide a background or border brush.", content, List.of("Background"));
        element("Overlay", "Content control that can block interaction behind it.", concat(content, List.of("blocks-input")), List.of("Background"));
        element("Button", "Focusable clickable content control.", concat(content, control), List.of("Background"));
        element("ToggleButton", "Button with a persistent checked state.", concat(content, control, List.of("is-checked")), List.of("Background"));
        element("CheckBox", "Toggle button with indicator and mark brushes.", concat(content, control, List.of("is-checked", "indicator-size", "indicator-spacing", "indicator-tint", "mark-tint")), List.of("Background", "IndicatorBrush", "MarkBrush"));
        element("RadioButton", "Checkbox that is mutually exclusive within a named group.", concat(content, control, List.of("is-checked", "indicator-size", "indicator-spacing", "indicator-tint", "mark-tint", "group")), List.of("Background", "IndicatorBrush", "MarkBrush"));
        element("ComboBox", "Focusable drop-down selector populated from a comma-separated item list.", concat(content, control, List.of("font", "font-size", "item-height", "items", "selected-index", "text-color", "popup-tint")), List.of("Background"));
        element("Popup", "Floating content control placed relative to another element.", concat(content, control, List.of("is-open", "stays-open", "placement-target", "horizontal-offset", "vertical-offset", "placement")), List.of("Background"));
        element("Tooltip", "Popup shown for another element after a configurable delay.", concat(content, control, List.of("is-open", "stays-open", "placement-target", "horizontal-offset", "vertical-offset", "placement", "delay")), List.of("Background"));
        element("Grid", "Container with authored row and column definitions.", List.of("rows", "columns", "row-definitions", "column-definitions", "padding"), List.of());
        element("UniformGrid", "Container that gives every cell the same size.", List.of("rows", "columns", "spacing", "column-spacing", "row-spacing", "padding"), List.of());
        element("WrapPanel", "Container that wraps children onto additional lines.", List.of("orientation", "spacing", "line-spacing", "cross-alignment", "padding"), List.of());
        element("VerticalStackPanel", "Container that arranges children vertically.", stack, List.of());
        element("HorizontalStackPanel", "Container that arranges children horizontally.", stack, List.of());
        element("StackPanel", "Legacy configurable-axis stack panel.", concat(stack, List.of("orientation")), List.of());
        element("ItemsControl", "Stack panel with an item-oriented API.", concat(stack, List.of("orientation")), List.of());
        element("ListBox", "Selectable stack of child items.", concat(stack, List.of("orientation", "selected-index", "background-tint")), List.of("Background"));
        element("Text", "Renders text with a Dreambit font asset.", List.of("text", "font-size", "font", "multi-line", "auto-resize-height", "horizontal-alignment", "text-color"), List.of());
        element("TextBox", "Focusable single-line text editor.", concat(content, control, List.of("text", "placeholder", "font", "font-size", "max-length", "password-character", "text-color", "placeholder-color", "selection-color", "caret-color")), List.of("Background"));
        element("Texture", "Renders a sprite asset.", List.of("sprite", "tint"), List.of());
        element("Slider", "Interactive numeric range control.", concat(content, control, range, slider), List.of("Background", "TrackBrush", "FillBrush", "ThumbBrush"));
        element("ScrollBar", "Slider whose thumb represents a viewport.", concat(content, control, range, slider, List.of("viewport-size", "large-change", "minimum-thumb-size")), List.of("Background", "TrackBrush", "FillBrush", "ThumbBrush"));
        element("ProgressBar", "Non-interactive visual representation of a numeric range.", concat(content, control, range, List.of("orientation", "track-tint", "fill-tint")), List.of("Background", "TrackBrush", "FillBrush"));
        element("Spacer", "Empty layout element used to reserve space.");
        element("Viewbox", "Scales one content child to fit its available bounds.", concat(content, List.of("stretch")), List.of("Background"));
    }

    private static void defineBrushes() {
        brush("SolidColorBrush", "Fills the owner bounds using its tint.");
        brush("SpriteBrush", "Stretches one sprite over the owner bounds.", List.of("sprite"));
        brush("TiledSpriteBrush", "Tiles one sprite at native size.", List.of("sprite"));
        brush("NineSliceBrush", "Draws a sprite with independently scalable nine-slice regions.", List.of("sprite", "slice", "slice-left", "slice-top", "slice-right", "slice-bottom"));
        brush("OutlineBrush", "Draws a solid outline using the supplied tint.", List.of("thickness", "left", "top", "right", "bottom"));
        brush("LayeredBrush", "Draws child brushes in back-to-front order.");
    }

    public static List<String> allElementNames(DreambitWorkspaceIndex.Snapshot snapshot) {
        TreeSet<String> result = new TreeSet<>(ELEMENTS.keySet());
        result.addAll(snapshot.customElements().keySet());
        return List.copyOf(result);
    }

    public static List<String> allBrushNames(DreambitWorkspaceIndex.Snapshot snapshot) {
        TreeSet<String> result = new TreeSet<>(BRUSHES.keySet());
        result.addAll(snapshot.customBrushes().keySet());
        return List.copyOf(result);
    }

    public static boolean isElement(String name, DreambitWorkspaceIndex.Snapshot snapshot) {
        return ELEMENTS.containsKey(name) || snapshot.customElements().containsKey(name);
    }

    public static boolean isBrush(String name, DreambitWorkspaceIndex.Snapshot snapshot) {
        return BRUSHES.containsKey(name) || snapshot.customBrushes().containsKey(name);
    }

    public static String elementDescription(String name, DreambitWorkspaceIndex.Snapshot snapshot) {
        ElementDefinition definition = ELEMENTS.get(name);
        if (definition != null) return definition.description();
        DreambitWorkspaceIndex.DynamicType dynamic = snapshot.customElements().get(name);
        return dynamic == null ? null : "Project-defined Dreambit UI element (" + dynamic.clrName() + ").";
    }

    public static String brushDescription(String name, DreambitWorkspaceIndex.Snapshot snapshot) {
        BrushDefinition definition = BRUSHES.get(name);
        if (definition != null) return definition.description();
        DreambitWorkspaceIndex.DynamicType dynamic = snapshot.customBrushes().get(name);
        return dynamic == null ? null : "Project-defined Dreambit UI brush (" + dynamic.clrName() + ").";
    }

    public static List<AttributeDefinition> elementAttributes(String name, DreambitWorkspaceIndex.Snapshot snapshot) {
        LinkedHashSet<String> names = new LinkedHashSet<>(COMMON_ATTRIBUTES);
        ElementDefinition builtIn = ELEMENTS.get(name);
        if (builtIn != null) names.addAll(builtIn.attributes());
        DreambitWorkspaceIndex.DynamicType dynamic = snapshot.customElements().get(name);
        if (dynamic != null) names.addAll(dynamic.attributes());

        List<AttributeDefinition> result = new ArrayList<>();
        for (String attribute : names) {
            AttributeDefinition definition = overrideDefinition(name, attribute);
            result.add(definition != null ? definition : generic(attribute));
        }
        return result;
    }

    public static List<AttributeDefinition> brushAttributes(String name, DreambitWorkspaceIndex.Snapshot snapshot) {
        LinkedHashSet<String> names = new LinkedHashSet<>();
        BrushDefinition builtIn = BRUSHES.get(name);
        if (builtIn != null) names.addAll(builtIn.attributes());
        DreambitWorkspaceIndex.DynamicType dynamic = snapshot.customBrushes().get(name);
        if (dynamic != null) names.addAll(dynamic.attributes());

        List<AttributeDefinition> result = new ArrayList<>();
        for (String attribute : names) result.add(DEFINITIONS.getOrDefault(attribute, generic(attribute)));
        return result;
    }

    public static List<String> propertyNodes(String elementName, DreambitWorkspaceIndex.Snapshot snapshot) {
        LinkedHashSet<String> names = new LinkedHashSet<>();
        names.add("Tooltip");
        ElementDefinition builtIn = ELEMENTS.get(elementName);
        if (builtIn != null) names.addAll(builtIn.propertyNodes());
        DreambitWorkspaceIndex.DynamicType dynamic = snapshot.customElements().get(elementName);
        if (dynamic != null) names.addAll(dynamic.propertyNodes());
        return names.stream().map(name -> elementName + "." + name).toList();
    }

    public static AttributeDefinition attributeDefinition(String tagName, String attributeName, DreambitWorkspaceIndex.Snapshot snapshot) {
        List<AttributeDefinition> candidates = isBrush(tagName, snapshot)
                ? brushAttributes(tagName, snapshot)
                : elementAttributes(tagName, snapshot);
        for (AttributeDefinition candidate : candidates) {
            if (candidate.name().equals(attributeName)) return candidate;
        }
        return null;
    }

    public static List<AttributeDefinition> cssProperties(String elementName, DreambitWorkspaceIndex.Snapshot snapshot) {
        List<AttributeDefinition> candidates;
        if (elementName != null && isElement(elementName, snapshot)) {
            candidates = elementAttributes(elementName, snapshot);
        } else {
            LinkedHashMap<String, AttributeDefinition> all = new LinkedHashMap<>(DEFINITIONS);
            for (DreambitWorkspaceIndex.DynamicType type : snapshot.customElements().values()) {
                for (String attribute : type.attributes()) all.putIfAbsent(attribute, generic(attribute));
            }
            candidates = new ArrayList<>(all.values());
        }

        TreeMap<String, AttributeDefinition> result = new TreeMap<>();
        for (AttributeDefinition candidate : candidates) {
            if (STRUCTURAL.contains(candidate.name())) continue;
            String cssName = XML_TO_CSS.getOrDefault(candidate.name(), candidate.name());
            result.putIfAbsent(cssName, new AttributeDefinition(cssName, candidate.type(), candidate.description(), candidate.values()));
        }
        return List.copyOf(result.values());
    }

    public static AttributeDefinition cssPropertyDefinition(String propertyName, String elementName, DreambitWorkspaceIndex.Snapshot snapshot) {
        if (propertyName == null) return null;
        String normalized = propertyName.toLowerCase(Locale.ROOT);
        String xmlName = CSS_ALIASES.getOrDefault(normalized, normalized);
        for (AttributeDefinition definition : cssProperties(elementName, snapshot)) {
            String candidateXml = CSS_ALIASES.getOrDefault(definition.name(), definition.name());
            if (definition.name().equals(normalized) || candidateXml.equals(xmlName)) return definition;
        }
        return null;
    }

    public static List<String> valueSuggestions(
            AttributeDefinition definition,
            DreambitWorkspaceIndex.Snapshot snapshot,
            Collection<String> ids,
            boolean css) {
        if (definition == null) return List.of();
        if (definition.type().equals("class-list")) return List.copyOf(snapshot.classes());
        if (definition.type().equals("uxml-path")) return snapshot.uxmlPaths();
        if (definition.type().equals("id-reference")) return ids.stream().sorted().toList();
        if (!definition.values().isEmpty()) return definition.values();

        return switch (definition.type()) {
            case "length" -> css ? List.of("0", "8px", "50%", "-8px") : List.of("0", "8", "50%", "-8");
            case "size" -> css ? List.of("auto", "0", "48px", "100%") : List.of("*", "0", "48", "100%");
            case "number" -> css && definition.name().equals("font-size")
                    ? List.of("0", "16px", "24px", "32px")
                    : List.of("0", "1", "10", "0.5");
            case "integer" -> List.of("0", "1", "2", "-1");
            case "color" -> List.of("#FFFFFF", "#000000", "#FFFFFF80");
            case "thickness" -> css
                    ? List.of("0", "8px", "8px 16px", "4px 8px 12px 16px")
                    : List.of("0", "8", "8,16", "4,8,12,16");
            case "grid-definitions" -> css
                    ? List.of("\"*\"", "\"*,*\"", "\"Auto,*\"", "\"80,2*\"")
                    : List.of("*", "*,*", "Auto,*", "80,2*");
            case "asset" -> definition.name().equals("font") || definition.name().equals("font-family")
                    ? (css ? List.of("\"monogram\"") : List.of("monogram")) : List.of();
            default -> List.of();
        };
    }

    public static UxmlContext analyzeUxml(String text, int offset) {
        String before = text.substring(0, Math.max(0, Math.min(offset, text.length())));
        String parentName = findOpenParent(before);
        if (before.lastIndexOf("<!--") > before.lastIndexOf("-->") ||
                before.lastIndexOf("<![CDATA[") > before.lastIndexOf("]]>") ) {
            return new UxmlContext(UxmlKind.NONE, parentName, null, null, "", Set.of());
        }

        int lastOpen = before.lastIndexOf('<');
        int lastClose = before.lastIndexOf('>');
        if (lastOpen <= lastClose) return new UxmlContext(UxmlKind.CONTENT, parentName, null, null, "", Set.of());

        String fragment = before.substring(lastOpen);
        if (fragment.matches("^<\\s*[!?].*")) return new UxmlContext(UxmlKind.NONE, parentName, null, null, "", Set.of());
        if (fragment.matches("^<\\s*/.*")) {
            String prefix = fragment.replaceFirst("^<\\s*/\\s*", "");
            return new UxmlContext(UxmlKind.CLOSING_TAG, parentName, null, null, prefix, Set.of());
        }

        Matcher tagMatcher = Pattern.compile("^<\\s*([A-Za-z_][\\w.-]*)?").matcher(fragment);
        boolean hasTagMatch = tagMatcher.find();
        String tagName = hasTagMatch && tagMatcher.group(1) != null ? tagMatcher.group(1) : "";
        String matched = hasTagMatch ? tagMatcher.group(0) : "";
        if (tagName.isEmpty() || !fragment.substring(Math.min(matched.length(), fragment.length())).matches("(?s).*\\s.*")) {
            String prefix = fragment.replaceFirst("^<\\s*", "");
            return new UxmlContext(UxmlKind.TAG_NAME, parentName, tagName, null, prefix, Set.of());
        }

        Matcher valueMatcher = Pattern.compile("([A-Za-z_][\\w.-]*)\\s*=\\s*([\"'])([^\"']*)$").matcher(fragment);
        if (valueMatcher.find()) {
            String valuePrefix = valueMatcher.group(3);
            if (valueMatcher.group(1).equals("class")) {
                int space = Math.max(valuePrefix.lastIndexOf(' '), valuePrefix.lastIndexOf('\t'));
                valuePrefix = valuePrefix.substring(space + 1);
            }
            return new UxmlContext(
                    UxmlKind.ATTRIBUTE_VALUE,
                    parentName,
                    tagName,
                    valueMatcher.group(1),
                    valuePrefix,
                    extractAttributes(fragment));
        }

        Matcher prefixMatcher = Pattern.compile("([A-Za-z_][\\w.-]*)$").matcher(fragment);
        String prefix = prefixMatcher.find() ? prefixMatcher.group(1) : "";
        return new UxmlContext(UxmlKind.ATTRIBUTE_NAME, parentName, tagName, null, prefix, extractAttributes(fragment));
    }

    public static UcssContext analyzeUcss(String text, int offset) {
        String sanitized = sanitizeCss(text.substring(0, Math.max(0, Math.min(offset, text.length()))));
        int depth = 0;
        int lastOpen = -1;
        for (int i = 0; i < sanitized.length(); i++) {
            char c = sanitized.charAt(i);
            if (c == '{') {
                depth++;
                if (depth == 1) lastOpen = i;
            } else if (c == '}') {
                depth = Math.max(0, depth - 1);
                if (depth == 0) lastOpen = -1;
            }
        }

        if (depth == 0 || lastOpen < 0) {
            int previousClose = sanitized.lastIndexOf('}');
            String prefix = sanitized.substring(previousClose < 0 ? 0 : previousClose + 1).trim();
            return new UcssContext(UcssKind.SELECTOR, null, null, prefix);
        }

        int previousClose = sanitized.lastIndexOf('}', lastOpen - 1);
        int selectorStart = previousClose < 0 ? 0 : previousClose + 1;
        String selector = sanitized.substring(selectorStart, lastOpen).trim();
        Matcher elementMatcher = Pattern.compile("^([A-Za-z_][\\w-]*)").matcher(selector);
        String elementName = elementMatcher.find() ? elementMatcher.group(1) : null;

        String block = sanitized.substring(lastOpen + 1);
        int declarationStart = Math.max(block.lastIndexOf(';'), block.lastIndexOf('{')) + 1;
        String declaration = block.substring(declarationStart);
        int colon = declaration.indexOf(':');
        if (colon >= 0) {
            return new UcssContext(
                    UcssKind.PROPERTY_VALUE,
                    elementName,
                    declaration.substring(0, colon).trim(),
                    declaration.substring(colon + 1).trim());
        }
        return new UcssContext(UcssKind.PROPERTY_NAME, elementName, null, declaration.trim());
    }

    public static String sanitizeCss(String text) {
        StringBuilder result = new StringBuilder(text.length());
        int state = 0; // 0 normal, 1 comment, 2 double string, 3 single string
        for (int i = 0; i < text.length(); i++) {
            char c = text.charAt(i);
            char next = i + 1 < text.length() ? text.charAt(i + 1) : '\0';
            if (state == 1) {
                if (c == '*' && next == '/') { result.append("  "); i++; state = 0; }
                else result.append(c == '\n' ? '\n' : ' ');
                continue;
            }
            if (state == 2 || state == 3) {
                char quote = state == 2 ? '"' : '\'';
                if (c == '\\') {
                    result.append(' ');
                    if (i + 1 < text.length()) { result.append(' '); i++; }
                } else if (c == quote) { result.append(' '); state = 0; }
                else result.append(c == '\n' ? '\n' : ' ');
                continue;
            }
            if (c == '/' && next == '*') { result.append("  "); i++; state = 1; }
            else if (c == '"') { result.append(' '); state = 2; }
            else if (c == '\'') { result.append(' '); state = 3; }
            else result.append(c);
        }
        return result.toString();
    }

    public static String findOpenParent(String text) {
        ArrayDeque<String> stack = new ArrayDeque<>();
        int cursor = 0;
        while (cursor < text.length()) {
            int open = text.indexOf('<', cursor);
            if (open < 0) break;
            if (text.startsWith("<!--", open)) {
                int end = text.indexOf("-->", open + 4);
                cursor = end < 0 ? text.length() : end + 3;
                continue;
            }
            if (text.startsWith("<![CDATA[", open)) {
                int end = text.indexOf("]]>", open + 9);
                cursor = end < 0 ? text.length() : end + 3;
                continue;
            }
            int end = findXmlTagEnd(text, open + 1);
            if (end < 0) break;
            String body = text.substring(open + 1, end).trim();
            cursor = end + 1;
            if (body.isEmpty() || body.startsWith("!") || body.startsWith("?")) continue;
            boolean closing = body.startsWith("/");
            Matcher matcher = Pattern.compile("^/?\\s*([A-Za-z_][\\w.-]*)").matcher(body);
            if (!matcher.find()) continue;
            String name = matcher.group(1);
            boolean selfClosing = body.endsWith("/");
            if (closing) {
                while (!stack.isEmpty()) {
                    String popped = stack.removeLast();
                    if (popped.equals(name)) break;
                }
            } else if (!selfClosing) {
                stack.addLast(name);
            }
        }
        return stack.peekLast();
    }

    public static List<String> extractComponentNames(String text) {
        LinkedHashSet<String> result = new LinkedHashSet<>();
        Matcher matcher = Pattern.compile("<\\s*Component\\b[^>]*\\bname\\s*=\\s*([\"'])([^\"']+)\\1", Pattern.CASE_INSENSITIVE)
                .matcher(stripXmlComments(text));
        while (matcher.find()) {
            String value = matcher.group(2).trim();
            if (!value.isEmpty()) result.add(value);
        }
        return List.copyOf(result);
    }

    public static Set<String> extractIds(String text) {
        TreeSet<String> result = new TreeSet<>();
        Matcher matcher = Pattern.compile("\\bid\\s*=\\s*([\"'])([^\"']+)\\1", Pattern.CASE_INSENSITIVE)
                .matcher(stripXmlComments(text));
        while (matcher.find()) result.add(matcher.group(2).trim());
        return result;
    }

    public static boolean canContainElements(String parentName) {
        return parentName != null && !NON_CONTAINERS.contains(parentName);
    }

    public static AttributeDefinition sourceAttribute() {
        return a("source", "uxml-path", "Path to a reusable Dreambit .uxml component.");
    }

    public static AttributeDefinition componentNameAttribute() {
        return a("name", "identifier", "Name used as the component element in this document.");
    }

    public static AttributeDefinition idPrefixAttribute() {
        return a("id-prefix", "string", "Prefix applied to IDs inside this component instance.");
    }

    public static String cssDetail(AttributeDefinition definition) {
        String xmlName = CSS_ALIASES.get(definition.name());
        return definition.type() + " · Dreambit UCSS" + (xmlName == null ? "" : " → " + xmlName);
    }

    private static AttributeDefinition overrideDefinition(String elementName, String attribute) {
        if (elementName.equals("Grid") && attribute.equals("rows"))
            return a("rows", "grid-definitions", "Preferred alias for Grid row-definitions.");
        if (elementName.equals("Grid") && attribute.equals("columns"))
            return a("columns", "grid-definitions", "Preferred alias for Grid column-definitions.");
        return DEFINITIONS.get(attribute);
    }

    private static Set<String> extractAttributes(String fragment) {
        LinkedHashSet<String> result = new LinkedHashSet<>();
        Matcher matcher = Pattern.compile("\\s([A-Za-z_][\\w.-]*)\\s*=").matcher(fragment);
        while (matcher.find()) result.add(matcher.group(1));
        return result;
    }

    private static int findXmlTagEnd(String text, int start) {
        char quote = 0;
        for (int i = start; i < text.length(); i++) {
            char c = text.charAt(i);
            if (quote != 0) {
                if (c == quote) quote = 0;
            } else if (c == '"' || c == '\'') quote = c;
            else if (c == '>') return i;
        }
        return -1;
    }

    private static String stripXmlComments(String text) {
        return text.replaceAll("(?s)<!--[\\s\\S]*?-->", " ");
    }

    private static void put(AttributeDefinition definition) { DEFINITIONS.put(definition.name(), definition); }
    private static AttributeDefinition generic(String name) { return a(name, "value", "Project-defined Dreambit property."); }
    private static AttributeDefinition bool(String name, String description) { return a(name, "boolean", description, List.of("true", "false")); }
    private static AttributeDefinition color(String name, String description) { return a(name, "color", description); }
    private static AttributeDefinition a(String name, String type, String description) { return a(name, type, description, List.of()); }
    private static AttributeDefinition a(String name, String type, String description, List<String> values) { return new AttributeDefinition(name, type, description, List.copyOf(values)); }

    private static void element(String name, String description) { element(name, description, List.of(), List.of()); }
    private static void element(String name, String description, List<String> attrs, List<String> props) {
        ELEMENTS.put(name, new ElementDefinition(description, List.copyOf(attrs), List.copyOf(props)));
    }
    private static void brush(String name, String description) { brush(name, description, List.of()); }
    private static void brush(String name, String description, List<String> attrs) {
        BRUSHES.put(name, new BrushDefinition(description, List.copyOf(attrs)));
    }

    @SafeVarargs
    private static <T> List<T> concat(List<T>... lists) {
        ArrayList<T> result = new ArrayList<>();
        for (List<T> list : lists) result.addAll(list);
        return result;
    }
}
