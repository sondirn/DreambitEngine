package com.dreambit.rider.ui;

import com.intellij.openapi.components.Service;
import com.intellij.openapi.project.Project;
import com.intellij.openapi.roots.ProjectFileIndex;
import com.intellij.openapi.vfs.VfsUtilCore;
import com.intellij.openapi.vfs.VirtualFile;
import com.intellij.psi.util.PsiModificationTracker;

import java.io.IOException;
import java.util.*;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

@Service(Service.Level.PROJECT)
public final class DreambitWorkspaceIndex {
    private static final int MAX_UI_FILES = 1000;
    private static final int MAX_CS_FILES = 5000;

    private static final Pattern UXML_CLASS = Pattern.compile("\\bclass\\s*=\\s*([\"'])([^\"']*)\\1", Pattern.CASE_INSENSITIVE);
    private static final Pattern UCSS_CLASS = Pattern.compile("(?:^|[}\\s])(?:[A-Za-z_][\\w-]*)?\\.([-_a-zA-Z][-_a-zA-Z0-9]*)\\s*\\{", Pattern.MULTILINE);
    private static final Pattern CS_CLASS = Pattern.compile(
            "(?s)((?:\\[[^]]*]\\s*)*)" +
            "((?:(?:public|internal|private|protected|sealed|abstract|partial|static|new)\\s+)*)" +
            "class\\s+([A-Za-z_][A-Za-z0-9_]*)" +
            "(?:\\s*<[^>{}]*>)?\\s*" +
            "(?:\\:\\s*([^\\{]+))?\\{");
    private static final Pattern XML_NAME = Pattern.compile("UiXmlName(?:Attribute)?\\s*\\(\\s*\"([^\"]+)\"");
    private static final Pattern PARSED_ATTRIBUTE = Pattern.compile(
            "UiXmlParser\\.[A-Za-z0-9_<>]+\\s*\\(\\s*node\\s*,\\s*\"([^\"]+)\"");
    private static final Pattern WRITABLE_PROPERTY = Pattern.compile(
            "public\\s+([A-Za-z_][A-Za-z0-9_.<>]*)\\s+([A-Za-z_][A-Za-z0-9_]*)\\s*\\{(?=[^}]*\\bset\\b)",
            Pattern.DOTALL);

    private final Project project;
    private volatile long cachedModificationCount = Long.MIN_VALUE;
    private volatile Snapshot cached = Snapshot.empty();

    public DreambitWorkspaceIndex(Project project) {
        this.project = project;
    }

    public Snapshot get() {
        long modificationCount = PsiModificationTracker.getInstance(project).getModificationCount();
        Snapshot current = cached;
        if (modificationCount == cachedModificationCount) {
            return current;
        }

        synchronized (this) {
            modificationCount = PsiModificationTracker.getInstance(project).getModificationCount();
            if (modificationCount == cachedModificationCount) {
                return cached;
            }

            cached = scan();
            cachedModificationCount = modificationCount;
            return cached;
        }
    }

    private Snapshot scan() {
        Set<String> classes = new TreeSet<>();
        Map<String, VirtualFile> uxmlFiles = new LinkedHashMap<>();
        List<CsType> csTypes = new ArrayList<>();
        int[] uiFileCount = {0};
        int[] csFileCount = {0};

        ProjectFileIndex.getInstance(project).iterateContent(file -> {
            if (file.isDirectory()) {
                return true;
            }

            String ext = file.getExtension();
            if (ext == null) {
                return true;
            }

            try {
                if ((ext.equalsIgnoreCase("uxml") || ext.equalsIgnoreCase("ucss")) && uiFileCount[0] < MAX_UI_FILES) {
                    uiFileCount[0]++;
                    String text = VfsUtilCore.loadText(file);
                    if (ext.equalsIgnoreCase("uxml")) {
                        collectUxmlClasses(text, classes);
                        addUxmlAliases(file, uxmlFiles);
                    } else {
                        collectUcssClasses(text, classes);
                    }
                } else if (ext.equalsIgnoreCase("cs") && csFileCount[0] < MAX_CS_FILES) {
                    csFileCount[0]++;
                    collectCsTypes(VfsUtilCore.loadText(file), csTypes);
                }
            } catch (IOException ignored) {
                // A file can disappear or become unreadable during an IDE refresh.
            }
            return true;
        });

        DynamicCatalog dynamic = classifyTypes(csTypes);
        List<String> paths = new ArrayList<>(uxmlFiles.keySet());
        paths.removeIf(path -> path.startsWith("~/"));
        paths = paths.stream().distinct().sorted().toList();

        return new Snapshot(
                Collections.unmodifiableSet(classes),
                Collections.unmodifiableList(paths),
                Collections.unmodifiableMap(uxmlFiles),
                Collections.unmodifiableMap(dynamic.elements),
                Collections.unmodifiableMap(dynamic.brushes));
    }

    private static void collectUxmlClasses(String text, Set<String> classes) {
        Matcher matcher = UXML_CLASS.matcher(stripXmlComments(text));
        while (matcher.find()) {
            for (String value : matcher.group(2).trim().split("\\s+")) {
                if (!value.isBlank()) classes.add(value);
            }
        }
    }

    private static void collectUcssClasses(String text, Set<String> classes) {
        Matcher matcher = UCSS_CLASS.matcher(DreambitUiSupport.sanitizeCss(text));
        while (matcher.find()) {
            classes.add(matcher.group(1));
        }
    }

    private void addUxmlAliases(VirtualFile file, Map<String, VirtualFile> files) {
        String path = normalize(file.getPath());
        String basePath = project.getBasePath();
        if (basePath != null) {
            String base = normalize(basePath);
            if (path.startsWith(base + "/")) {
                addAlias(files, path.substring(base.length() + 1), file);
            }
        }

        int assets = path.lastIndexOf("/Assets/");
        if (assets >= 0) {
            String assetPath = path.substring(assets + "/Assets/".length());
            addAlias(files, assetPath, file);
            addAlias(files, "~/" + assetPath, file);
        }

        addAlias(files, file.getName(), file);
    }

    private static void addAlias(Map<String, VirtualFile> files, String alias, VirtualFile file) {
        String normalized = normalize(alias);
        files.putIfAbsent(normalized, file);
    }

    public VirtualFile resolveUxml(VirtualFile currentFile, String authoredPath) {
        if (authoredPath == null || authoredPath.isBlank()) return null;
        Snapshot snapshot = get();
        String source = normalize(authoredPath.trim());

        VirtualFile exact = snapshot.uxmlFiles.get(source);
        if (exact != null) return exact;

        if (source.startsWith("~/")) {
            exact = snapshot.uxmlFiles.get(source.substring(2));
            if (exact != null) return exact;
        }

        VirtualFile parent = currentFile == null ? null : currentFile.getParent();
        if (parent != null && !source.startsWith("~/")) {
            VirtualFile relative = VfsUtilCore.findRelativeFile(source, parent);
            if (relative != null && !relative.isDirectory()) return relative;
        }

        String suffix = source.startsWith("~/") ? source.substring(2) : source;
        VirtualFile match = null;
        for (Map.Entry<String, VirtualFile> entry : snapshot.uxmlFiles.entrySet()) {
            String alias = entry.getKey();
            if (alias.equals(suffix) || alias.endsWith("/" + suffix)) {
                if (match != null && !match.equals(entry.getValue())) return null;
                match = entry.getValue();
            }
        }
        return match;
    }

    private static void collectCsTypes(String source, List<CsType> output) {
        String stripped = stripCsComments(source);
        Matcher matcher = CS_CLASS.matcher(stripped);
        while (matcher.find()) {
            String attributes = matcher.group(1) == null ? "" : matcher.group(1);
            String modifiers = matcher.group(2) == null ? "" : matcher.group(2);
            String name = matcher.group(3);
            String baseText = matcher.group(4) == null ? "" : matcher.group(4);
            boolean isAbstract = Arrays.asList(modifiers.trim().split("\\s+")).contains("abstract");

            List<String> bases = new ArrayList<>();
            for (String base : baseText.split(",")) {
                String normalized = simpleTypeName(base);
                if (!normalized.isBlank()) bases.add(normalized);
            }

            String explicitXmlName = null;
            Matcher nameMatcher = XML_NAME.matcher(attributes);
            if (nameMatcher.find()) explicitXmlName = nameMatcher.group(1);

            int bodyStart = matcher.end() - 1;
            int bodyEnd = findMatchingBrace(stripped, bodyStart);
            String body = bodyEnd > bodyStart ? stripped.substring(bodyStart + 1, bodyEnd) : "";

            Set<String> parsedAttributes = new LinkedHashSet<>();
            Matcher attrMatcher = PARSED_ATTRIBUTE.matcher(body);
            while (attrMatcher.find()) parsedAttributes.add(attrMatcher.group(1));

            Set<String> propertyNodes = new LinkedHashSet<>();
            Matcher propertyMatcher = WRITABLE_PROPERTY.matcher(body);
            while (propertyMatcher.find()) {
                String propertyType = simpleTypeName(propertyMatcher.group(1));
                if (propertyType.equals("IUiBrush") || propertyType.endsWith("Brush") || propertyType.startsWith("Ui"))
                    propertyNodes.add(propertyMatcher.group(2));
            }

            output.add(new CsType(name, bases, isAbstract, explicitXmlName, parsedAttributes, propertyNodes));
        }
    }

    private static DynamicCatalog classifyTypes(List<CsType> types) {
        Map<String, CsType> byName = new LinkedHashMap<>();
        for (CsType type : types) byName.put(type.name, type);

        Set<String> elementTypes = new HashSet<>(List.of(
                "UiElement", "UiContainer", "UiPanel", "UiCanvas", "UiContentControl", "UiControl",
                "UiBorder", "UiOverlay", "UiButton", "UiToggleButton", "UiCheckBox", "UiRadioButton",
                "UiComboBox", "UiPopup", "UiTooltip", "UiGrid", "UiUniformGrid", "UiWrapPanel",
                "UiStackPanelBase", "UiVerticalStackPanel", "UiHorizontalStackPanel", "UiStackPanel",
                "UiItemsControl", "UiSelector", "UiListBox", "UiText", "UiTextBox", "UiTexture",
                "UiRangeBase", "UiSlider", "UiScrollBar", "UiProgressBar", "UiSpacer", "UiViewbox"));
        Set<String> brushTypes = new HashSet<>(List.of(
                "IUiBrush", "UiBrush", "SolidColorBrush", "SpriteBrush", "TiledSpriteBrush",
                "NineSliceBrush", "OutlineBrush", "LayeredBrush"));

        boolean changed;
        do {
            changed = false;
            for (CsType type : types) {
                if (!elementTypes.contains(type.name) && type.bases.stream().anyMatch(elementTypes::contains)) {
                    changed |= elementTypes.add(type.name);
                }
                if (!brushTypes.contains(type.name) && type.bases.stream().anyMatch(brushTypes::contains)) {
                    changed |= brushTypes.add(type.name);
                }
            }
        } while (changed);

        Map<String, DynamicType> elements = new TreeMap<>();
        Map<String, DynamicType> brushes = new TreeMap<>();
        for (CsType type : types) {
            if (type.isAbstract) continue;
            if (elementTypes.contains(type.name)) {
                String xmlName = type.explicitXmlName != null
                        ? type.explicitXmlName
                        : type.name.startsWith("Ui") && type.name.length() > 2 ? type.name.substring(2) : type.name;
                elements.put(xmlName, dynamicType(type, byName, elementTypes));
            }
            if (brushTypes.contains(type.name)) {
                String xmlName = type.explicitXmlName != null ? type.explicitXmlName : type.name;
                brushes.put(xmlName, dynamicType(type, byName, brushTypes));
            }
        }
        return new DynamicCatalog(elements, brushes);
    }

    private static DynamicType dynamicType(CsType type, Map<String, CsType> byName, Set<String> family) {
        LinkedHashSet<String> attrs = new LinkedHashSet<>();
        LinkedHashSet<String> propertyNodes = new LinkedHashSet<>();
        collectInherited(type, byName, family, attrs, propertyNodes, new HashSet<>());
        return new DynamicType(type.name, attrs, propertyNodes);
    }

    private static void collectInherited(
            CsType type,
            Map<String, CsType> byName,
            Set<String> family,
            Set<String> attrs,
            Set<String> properties,
            Set<String> seen) {
        if (!seen.add(type.name)) return;
        for (String base : type.bases) {
            if (!family.contains(base)) continue;
            CsType baseType = byName.get(base);
            if (baseType != null) collectInherited(baseType, byName, family, attrs, properties, seen);
        }
        attrs.addAll(type.parsedAttributes);
        properties.addAll(type.propertyNodes);
    }

    private static int findMatchingBrace(String text, int openBrace) {
        int depth = 0;
        boolean inString = false;
        char quote = 0;
        for (int i = openBrace; i < text.length(); i++) {
            char c = text.charAt(i);
            if (inString) {
                if (c == '\\') {
                    i++;
                } else if (c == quote) {
                    inString = false;
                }
                continue;
            }
            if (c == '"' || c == '\'') {
                inString = true;
                quote = c;
                continue;
            }
            if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return i;
        }
        return -1;
    }

    private static String simpleTypeName(String value) {
        String result = value.trim();
        int generic = result.indexOf('<');
        if (generic >= 0) result = result.substring(0, generic);
        int dot = result.lastIndexOf('.');
        if (dot >= 0) result = result.substring(dot + 1);
        return result.trim();
    }

    private static String stripXmlComments(String text) {
        return text.replaceAll("(?s)<!--[\\s\\S]*?-->", " ");
    }

    private static String stripCsComments(String text) {
        StringBuilder out = new StringBuilder(text.length());
        boolean line = false, block = false, string = false, verbatim = false;
        char quote = 0;
        for (int i = 0; i < text.length(); i++) {
            char c = text.charAt(i);
            char n = i + 1 < text.length() ? text.charAt(i + 1) : '\0';
            if (line) {
                if (c == '\n') { line = false; out.append(c); } else out.append(' ');
                continue;
            }
            if (block) {
                if (c == '*' && n == '/') { out.append("  "); i++; block = false; }
                else out.append(c == '\n' ? '\n' : ' ');
                continue;
            }
            if (string) {
                out.append(c);
                if (verbatim) {
                    if (c == '"' && n == '"') { out.append(n); i++; }
                    else if (c == '"') string = false;
                } else if (c == '\\') {
                    if (i + 1 < text.length()) { out.append(n); i++; }
                } else if (c == quote) string = false;
                continue;
            }
            if (c == '/' && n == '/') { out.append("  "); i++; line = true; continue; }
            if (c == '/' && n == '*') { out.append("  "); i++; block = true; continue; }
            if (c == '@' && n == '"') { out.append("@\""); i++; string = true; verbatim = true; quote = '"'; continue; }
            if (c == '"' || c == '\'') { out.append(c); string = true; verbatim = false; quote = c; continue; }
            out.append(c);
        }
        return out.toString();
    }

    private static String normalize(String value) {
        String result = value.replace('\\', '/');
        while (result.startsWith("./")) result = result.substring(2);
        while (result.contains("//")) result = result.replace("//", "/");
        return result;
    }

    private record CsType(
            String name,
            List<String> bases,
            boolean isAbstract,
            String explicitXmlName,
            Set<String> parsedAttributes,
            Set<String> propertyNodes) {}

    private record DynamicCatalog(Map<String, DynamicType> elements, Map<String, DynamicType> brushes) {}

    public record DynamicType(String clrName, Set<String> attributes, Set<String> propertyNodes) {}

    public record Snapshot(
            Set<String> classes,
            List<String> uxmlPaths,
            Map<String, VirtualFile> uxmlFiles,
            Map<String, DynamicType> customElements,
            Map<String, DynamicType> customBrushes) {
        static Snapshot empty() {
            return new Snapshot(Set.of(), List.of(), Map.of(), Map.of(), Map.of());
        }
    }
}
