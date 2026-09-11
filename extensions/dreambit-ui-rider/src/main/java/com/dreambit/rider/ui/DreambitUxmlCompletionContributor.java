package com.dreambit.rider.ui;

import com.intellij.codeInsight.completion.*;
import com.intellij.codeInsight.lookup.LookupElementBuilder;
import com.intellij.openapi.project.DumbAware;
import com.intellij.patterns.PlatformPatterns;
import com.intellij.psi.PsiElement;
import com.intellij.util.ProcessingContext;
import org.jetbrains.annotations.NotNull;

import java.util.*;

public final class DreambitUxmlCompletionContributor extends CompletionContributor implements DumbAware {
    public DreambitUxmlCompletionContributor() {
        extend(CompletionType.BASIC, PlatformPatterns.psiElement(), new Provider());
    }

    private static final class Provider extends CompletionProvider<CompletionParameters> {
        @Override
        protected void addCompletions(
                @NotNull CompletionParameters parameters,
                @NotNull ProcessingContext processingContext,
                @NotNull CompletionResultSet result) {
            if (!"uxml".equalsIgnoreCase(parameters.getOriginalFile().getVirtualFile().getExtension())) return;

            String text = parameters.getOriginalFile().getText();
            DreambitUiSupport.UxmlContext context = DreambitUiSupport.analyzeUxml(text, parameters.getOffset());
            DreambitWorkspaceIndex index = parameters.getPosition().getProject().getService(DreambitWorkspaceIndex.class);
            DreambitWorkspaceIndex.Snapshot snapshot = index.get();
            List<String> componentNames = DreambitUiSupport.extractComponentNames(text);
            Set<String> ids = DreambitUiSupport.extractIds(text);

            CompletionResultSet scoped = result.withPrefixMatcher(context.prefix() == null ? "" : context.prefix());

            switch (context.kind()) {
                case CLOSING_TAG -> addClosing(scoped, context.parentName());
                case TAG_NAME -> addTags(scoped, context.parentName(), componentNames, snapshot);
                case ATTRIBUTE_NAME -> addAttributes(scoped, context.tagName(), context.existingAttributes(), componentNames, snapshot);
                case ATTRIBUTE_VALUE -> addValues(scoped, context, componentNames, ids, snapshot);
                default -> { }
            }

            // UXML is intentionally a Dreambit dialect. Suppress generic XML completion noise.
            result.stopHere();
        }
    }

    private static void addClosing(CompletionResultSet result, String parentName) {
        if (parentName != null && !parentName.isBlank()) result.addElement(DreambitLookup.closingTag(parentName));
    }

    private static void addTags(
            CompletionResultSet result,
            String parentName,
            List<String> componentNames,
            DreambitWorkspaceIndex.Snapshot snapshot) {
        if (parentName == null) {
            result.addElement(DreambitLookup.simple("Ui", "Dreambit document root", "Complete retained UI layout root."));
            result.addElement(DreambitLookup.simple("UiComponent", "Dreambit component root", "Reusable Dreambit component document root."));
            return;
        }

        if (parentName.equals("Ui.Components") || parentName.equals("UiComponent.Components")) {
            result.addElement(DreambitLookup.simple("Component", "Dreambit component declaration", "Declares a reusable component name and source."));
            return;
        }

        if (parentName.endsWith(".Tooltip")) {
            result.addElement(DreambitLookup.simple("Tooltip", "Dreambit element", DreambitUiSupport.elementDescription("Tooltip", snapshot)));
            return;
        }

        if (parentName.contains(".") || DreambitUiSupport.isBrush(parentName, snapshot)) {
            for (String brush : DreambitUiSupport.allBrushNames(snapshot)) {
                result.addElement(DreambitLookup.simple(brush, "Dreambit brush", DreambitUiSupport.brushDescription(brush, snapshot)));
            }
            return;
        }

        if (DreambitUiSupport.canContainElements(parentName)) {
            for (String element : DreambitUiSupport.allElementNames(snapshot)) {
                result.addElement(DreambitLookup.simple(element, "Dreambit element", DreambitUiSupport.elementDescription(element, snapshot)));
            }
            for (String component : componentNames) {
                result.addElement(DreambitLookup.simple(component, "Dreambit named component", "Expands the matching <Component> declaration."));
            }
            result.addElement(DreambitLookup.simple("Include", "Dreambit component include", "Includes a reusable UXML component by source path."));
        }

        if (parentName.equals("Ui")) {
            result.addElement(DreambitLookup.simple("Ui.Components", "Dreambit component declarations", "Declares reusable component aliases for this layout."));
        } else if (parentName.equals("UiComponent")) {
            result.addElement(DreambitLookup.simple("UiComponent.Components", "Dreambit component declarations", "Declares nested component aliases for this component."));
        }

        if (DreambitUiSupport.isElement(parentName, snapshot)) {
            for (String propertyNode : DreambitUiSupport.propertyNodes(parentName, snapshot)) {
                result.addElement(DreambitLookup.simple(
                        propertyNode,
                        "Dreambit property element",
                        propertyNode.endsWith(".Tooltip")
                                ? "Assigns a Tooltip element to this element."
                                : "Assigns a brush or UI element to a writable property."));
            }
        }
    }

    private static void addAttributes(
            CompletionResultSet result,
            String tagName,
            Set<String> existing,
            List<String> componentNames,
            DreambitWorkspaceIndex.Snapshot snapshot) {
        List<DreambitUiSupport.AttributeDefinition> definitions = new ArrayList<>();
        if ("Component".equals(tagName)) {
            definitions.add(DreambitUiSupport.componentNameAttribute());
            definitions.add(DreambitUiSupport.sourceAttribute());
        } else if ("Include".equals(tagName)) {
            definitions.add(DreambitUiSupport.sourceAttribute());
            definitions.add(DreambitUiSupport.idPrefixAttribute());
            definitions.addAll(DreambitUiSupport.elementAttributes("", snapshot));
        } else if (componentNames.contains(tagName)) {
            definitions.add(DreambitUiSupport.idPrefixAttribute());
            definitions.addAll(DreambitUiSupport.elementAttributes("", snapshot));
        } else if (DreambitUiSupport.isBrush(tagName, snapshot)) {
            definitions.addAll(DreambitUiSupport.brushAttributes(tagName, snapshot));
        } else {
            definitions.addAll(DreambitUiSupport.elementAttributes(tagName, snapshot));
        }

        LinkedHashMap<String, DreambitUiSupport.AttributeDefinition> unique = new LinkedHashMap<>();
        for (DreambitUiSupport.AttributeDefinition definition : definitions) unique.putIfAbsent(definition.name(), definition);
        for (DreambitUiSupport.AttributeDefinition definition : unique.values()) {
            if (!existing.contains(definition.name())) result.addElement(DreambitLookup.attribute(definition));
        }
    }

    private static void addValues(
            CompletionResultSet result,
            DreambitUiSupport.UxmlContext context,
            List<String> componentNames,
            Set<String> ids,
            DreambitWorkspaceIndex.Snapshot snapshot) {
        DreambitUiSupport.AttributeDefinition definition;
        if (("Component".equals(context.tagName()) || "Include".equals(context.tagName())) && "source".equals(context.attributeName())) {
            definition = DreambitUiSupport.sourceAttribute();
        } else if (("Include".equals(context.tagName()) || componentNames.contains(context.tagName())) && "id-prefix".equals(context.attributeName())) {
            definition = DreambitUiSupport.idPrefixAttribute();
        } else if ("Component".equals(context.tagName()) && "name".equals(context.attributeName())) {
            definition = DreambitUiSupport.componentNameAttribute();
        } else {
            definition = DreambitUiSupport.attributeDefinition(context.tagName(), context.attributeName(), snapshot);
        }

        for (String value : DreambitUiSupport.valueSuggestions(definition, snapshot, ids, false)) {
            LookupElementBuilder item = DreambitLookup.simple(value, "Dreambit value", definition == null ? "" : definition.description());
            result.addElement(item);
        }
    }
}
