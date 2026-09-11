package com.dreambit.rider.ui;

import com.intellij.codeInsight.lookup.LookupElementBuilder;
import com.intellij.openapi.editor.Document;

final class DreambitLookup {
    private DreambitLookup() {}

    static LookupElementBuilder simple(String label, String detail, String documentation) {
        LookupElementBuilder builder = LookupElementBuilder.create(label)
                .withPresentableText(label)
                .withTypeText(detail, true);
        if (documentation != null && !documentation.isBlank()) {
            builder = builder.withTailText("  " + documentation, true);
        }
        return builder;
    }

    static LookupElementBuilder attribute(DreambitUiSupport.AttributeDefinition definition) {
        return LookupElementBuilder.create(definition.name())
                .withTypeText(definition.type() + " · Dreambit UXML", true)
                .withTailText("  " + definition.description(), true)
                .withInsertHandler((context, item) -> {
                    Document document = context.getDocument();
                    int tail = context.getTailOffset();
                    CharSequence chars = document.getCharsSequence();
                    if (tail < chars.length() && chars.charAt(tail) == '=') return;
                    document.insertString(tail, "=\"\"");
                    context.setTailOffset(tail + 3);
                    context.getEditor().getCaretModel().moveToOffset(tail + 2);
                });
    }

    static LookupElementBuilder cssProperty(DreambitUiSupport.AttributeDefinition definition) {
        return LookupElementBuilder.create(definition.name())
                .withTypeText(DreambitUiSupport.cssDetail(definition), true)
                .withTailText("  " + definition.description(), true)
                .withInsertHandler((context, item) -> {
                    Document document = context.getDocument();
                    int tail = context.getTailOffset();
                    CharSequence chars = document.getCharsSequence();
                    if (tail < chars.length() && chars.charAt(tail) == ':') return;
                    document.insertString(tail, ": ;");
                    context.setTailOffset(tail + 3);
                    context.getEditor().getCaretModel().moveToOffset(tail + 2);
                });
    }

    static LookupElementBuilder closingTag(String name) {
        return LookupElementBuilder.create(name)
                .withTypeText("Close current Dreambit element", true)
                .withInsertHandler((context, item) -> {
                    Document document = context.getDocument();
                    int tail = context.getTailOffset();
                    CharSequence chars = document.getCharsSequence();
                    if (tail >= chars.length() || chars.charAt(tail) != '>') {
                        document.insertString(tail, ">");
                        context.setTailOffset(tail + 1);
                    }
                });
    }
}
