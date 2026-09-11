package com.dreambit.rider.ui;

import com.intellij.codeInsight.completion.*;
import com.intellij.openapi.project.DumbAware;
import com.intellij.patterns.PlatformPatterns;
import com.intellij.util.ProcessingContext;
import org.jetbrains.annotations.NotNull;

import java.util.Set;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

public final class DreambitUcssCompletionContributor extends CompletionContributor implements DumbAware {
    public DreambitUcssCompletionContributor() {
        extend(CompletionType.BASIC, PlatformPatterns.psiElement(), new Provider());
    }

    private static final class Provider extends CompletionProvider<CompletionParameters> {
        @Override
        protected void addCompletions(
                @NotNull CompletionParameters parameters,
                @NotNull ProcessingContext processingContext,
                @NotNull CompletionResultSet result) {
            if (!"ucss".equalsIgnoreCase(parameters.getOriginalFile().getVirtualFile().getExtension())) return;

            String text = parameters.getOriginalFile().getText();
            DreambitUiSupport.UcssContext context = DreambitUiSupport.analyzeUcss(text, parameters.getOffset());
            DreambitWorkspaceIndex index = parameters.getPosition().getProject().getService(DreambitWorkspaceIndex.class);
            DreambitWorkspaceIndex.Snapshot snapshot = index.get();
            CompletionResultSet scoped = result.withPrefixMatcher(context.prefix() == null ? "" : context.prefix());

            switch (context.kind()) {
                case SELECTOR -> addSelectors(scoped, context.prefix(), snapshot);
                case PROPERTY_NAME -> addProperties(scoped, context.elementName(), snapshot);
                case PROPERTY_VALUE -> addValues(scoped, context, snapshot);
            }

            // Dreambit intentionally supports only element, .class and element.class selectors.
            result.stopHere();
        }
    }

    private static void addSelectors(
            CompletionResultSet result,
            String prefix,
            DreambitWorkspaceIndex.Snapshot snapshot) {
        Matcher combined = Pattern.compile("^([A-Za-z_][\\w-]*)\\.([-_a-zA-Z0-9]*)$").matcher(prefix == null ? "" : prefix);
        if (combined.matches()) {
            CompletionResultSet classResult = result.withPrefixMatcher(combined.group(2));
            for (String className : snapshot.classes()) {
                classResult.addElement(DreambitLookup.simple(className, combined.group(1) + ".class selector", "Matches " + combined.group(1) + " elements with class '" + className + "'."));
            }
            return;
        }

        for (String element : DreambitUiSupport.allElementNames(snapshot)) {
            result.addElement(DreambitLookup.simple(element, "Dreambit element selector", DreambitUiSupport.elementDescription(element, snapshot)));
        }
        for (String className : snapshot.classes()) {
            result.addElement(DreambitLookup.simple("." + className, "Dreambit class selector", "Matches elements whose class list contains '" + className + "'."));
        }
    }

    private static void addProperties(
            CompletionResultSet result,
            String elementName,
            DreambitWorkspaceIndex.Snapshot snapshot) {
        for (DreambitUiSupport.AttributeDefinition definition : DreambitUiSupport.cssProperties(elementName, snapshot)) {
            result.addElement(DreambitLookup.cssProperty(definition));
        }
    }

    private static void addValues(
            CompletionResultSet result,
            DreambitUiSupport.UcssContext context,
            DreambitWorkspaceIndex.Snapshot snapshot) {
        DreambitUiSupport.AttributeDefinition definition =
                DreambitUiSupport.cssPropertyDefinition(context.propertyName(), context.elementName(), snapshot);
        for (String value : DreambitUiSupport.valueSuggestions(definition, snapshot, Set.of(), true)) {
            result.addElement(DreambitLookup.simple(value, "Dreambit UCSS value", definition == null ? "" : definition.description()));
        }
    }
}
