package com.dreambit.rider.ui;

import com.intellij.lang.documentation.AbstractDocumentationProvider;
import com.intellij.psi.PsiElement;
import org.jetbrains.annotations.Nullable;

import java.util.List;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

@SuppressWarnings("deprecation")
public final class DreambitDocumentationProvider extends AbstractDocumentationProvider {
    @Override
    public @Nullable String generateHoverDoc(PsiElement element, @Nullable PsiElement originalElement) {
        return generateDoc(element, originalElement);
    }

    @Override
    public @Nullable String generateDoc(PsiElement element, @Nullable PsiElement originalElement) {
        PsiElement target = originalElement != null ? originalElement : element;
        if (target == null || target.getContainingFile() == null || target.getContainingFile().getVirtualFile() == null) return null;

        String extension = target.getContainingFile().getVirtualFile().getExtension();
        if (extension == null) return null;

        String text = target.getContainingFile().getText();
        int offset = target.getTextRange().getStartOffset();
        DreambitWorkspaceIndex index = target.getProject().getService(DreambitWorkspaceIndex.class);
        DreambitWorkspaceIndex.Snapshot snapshot = index.get();

        if (extension.equalsIgnoreCase("uxml")) return uxmlDoc(text, offset, snapshot);
        if (extension.equalsIgnoreCase("ucss")) return ucssDoc(text, offset, snapshot);
        return null;
    }

    private static String uxmlDoc(String text, int offset, DreambitWorkspaceIndex.Snapshot snapshot) {
        Word word = wordAt(text, offset, true);
        if (word == null) return null;

        int open = text.lastIndexOf('<', Math.min(offset, text.length() - 1));
        int close = open >= 0 ? text.indexOf('>', open) : -1;
        if (open < 0 || close < 0 || offset > close) return null;
        String fragment = text.substring(open, close + 1);
        Matcher tagMatcher = Pattern.compile("^<\\s*/?\\s*([A-Za-z_][\\w.-]*)").matcher(fragment);
        if (!tagMatcher.find()) return null;
        String tagName = tagMatcher.group(1);
        int tagStart = open + tagMatcher.start(1);
        int tagEnd = open + tagMatcher.end(1);

        if (word.start >= tagStart && word.end <= tagEnd) {
            if (DreambitUiSupport.isElement(tagName, snapshot)) {
                return doc(tagName, "Dreambit UXML element", DreambitUiSupport.elementDescription(tagName, snapshot));
            }
            if (DreambitUiSupport.isBrush(tagName, snapshot)) {
                return doc(tagName, "Dreambit brush", DreambitUiSupport.brushDescription(tagName, snapshot));
            }
            if (tagName.contains(".")) {
                return doc(tagName, "Dreambit property element", "Assigns a brush or UI element to a writable property on the owning element.");
            }
            String structural = switch (tagName) {
                case "Ui" -> "Root element for a complete Dreambit UI layout.";
                case "UiComponent" -> "Root element for a reusable Dreambit UI component.";
                case "Include" -> "Includes and expands a reusable Dreambit .uxml component.";
                case "Component" -> "Declares a named reusable component alias and source path.";
                case "Ui.Components", "UiComponent.Components" -> "Contains named <Component> declarations.";
                default -> null;
            };
            if (structural != null) return doc(tagName, "Dreambit UXML", structural);
        }

        DreambitUiSupport.AttributeDefinition definition;
        if ((tagName.equals("Component") || tagName.equals("Include")) && word.value.equals("source")) {
            definition = DreambitUiSupport.sourceAttribute();
        } else if (word.value.equals("id-prefix")) {
            definition = DreambitUiSupport.idPrefixAttribute();
        } else if (tagName.equals("Component") && word.value.equals("name")) {
            definition = DreambitUiSupport.componentNameAttribute();
        } else {
            definition = DreambitUiSupport.attributeDefinition(tagName, word.value, snapshot);
        }
        if (definition == null) return null;
        return doc(definition.name(), definition.type() + " · Dreambit UXML attribute", definition.description(), definition.values());
    }

    private static String ucssDoc(String text, int offset, DreambitWorkspaceIndex.Snapshot snapshot) {
        Word word = wordAt(text, offset, false);
        if (word == null) return null;

        int previous = word.start - 1;
        while (previous >= 0 && Character.isWhitespace(text.charAt(previous))) previous--;
        if (previous >= 0 && text.charAt(previous) == '.') {
            return doc("." + word.value, "Dreambit UCSS class selector", "Matches elements whose class list contains '" + word.value + "'.");
        }

        int next = word.end;
        while (next < text.length() && Character.isWhitespace(text.charAt(next))) next++;
        if (next < text.length() && text.charAt(next) == ':') {
            DreambitUiSupport.UcssContext context = DreambitUiSupport.analyzeUcss(text, Math.min(next + 1, text.length()));
            DreambitUiSupport.AttributeDefinition definition =
                    DreambitUiSupport.cssPropertyDefinition(word.value, context.elementName(), snapshot);
            if (definition != null) {
                return doc(definition.name(), DreambitUiSupport.cssDetail(definition), definition.description(), definition.values());
            }
        }

        if (DreambitUiSupport.isElement(word.value, snapshot)) {
            return doc(word.value, "Dreambit UCSS element selector", DreambitUiSupport.elementDescription(word.value, snapshot));
        }
        return null;
    }

    private static String doc(String name, String kind, String description) {
        return doc(name, kind, description, List.of());
    }

    private static String doc(String name, String kind, String description, List<String> values) {
        StringBuilder html = new StringBuilder();
        html.append("<b>").append(escape(name)).append("</b><br>")
                .append("<span style='color:#888888'>").append(escape(kind)).append("</span><br><br>")
                .append(escape(description == null ? "" : description));
        if (values != null && !values.isEmpty()) {
            html.append("<br><br><b>Values:</b> ").append(escape(String.join(", ", values)));
        }
        return html.toString();
    }

    private static Word wordAt(String text, int offset, boolean dotAllowed) {
        if (text.isEmpty()) return null;
        int cursor = Math.max(0, Math.min(offset, text.length() - 1));
        if (!isWord(text.charAt(cursor), dotAllowed) && cursor > 0 && isWord(text.charAt(cursor - 1), dotAllowed)) cursor--;
        if (!isWord(text.charAt(cursor), dotAllowed)) return null;
        int start = cursor;
        int end = cursor + 1;
        while (start > 0 && isWord(text.charAt(start - 1), dotAllowed)) start--;
        while (end < text.length() && isWord(text.charAt(end), dotAllowed)) end++;
        return new Word(text.substring(start, end), start, end);
    }

    private static boolean isWord(char c, boolean dotAllowed) {
        return Character.isLetterOrDigit(c) || c == '_' || c == '-' || (dotAllowed && c == '.');
    }

    private static String escape(String value) {
        return value.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;").replace("\"", "&quot;");
    }

    private record Word(String value, int start, int end) {}
}
