package com.dreambit.rider.ui;

import com.intellij.openapi.editor.Document;
import com.intellij.openapi.editor.ElementColorProvider;
import com.intellij.psi.PsiDocumentManager;
import com.intellij.psi.PsiElement;
import org.jetbrains.annotations.NotNull;
import org.jetbrains.annotations.Nullable;

import java.awt.*;
import java.util.Locale;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

public final class DreambitColorProvider implements ElementColorProvider {
    private static final Pattern COLOR = Pattern.compile("^[\\s\"']*#([0-9a-fA-F]{6})([0-9a-fA-F]{2})?[\\s\"']*$");
    private static final Pattern INNER = Pattern.compile("#([0-9a-fA-F]{6})([0-9a-fA-F]{2})?");

    @Override
    public @Nullable Color getColorFrom(@NotNull PsiElement element) {
        if (element.getFirstChild() != null || element.getContainingFile() == null || element.getContainingFile().getVirtualFile() == null) return null;
        String extension = element.getContainingFile().getVirtualFile().getExtension();
        if (!"uxml".equalsIgnoreCase(extension) && !"ucss".equalsIgnoreCase(extension)) return null;

        Matcher matcher = COLOR.matcher(element.getText());
        if (!matcher.matches()) return null;
        int rgb = Integer.parseInt(matcher.group(1), 16);
        int alpha = matcher.group(2) == null ? 255 : Integer.parseInt(matcher.group(2), 16);
        return new Color((rgb >> 16) & 0xff, (rgb >> 8) & 0xff, rgb & 0xff, alpha);
    }

    @Override
    public void setColorTo(@NotNull PsiElement element, @NotNull Color color) {
        if (element.getContainingFile() == null) return;
        Document document = PsiDocumentManager.getInstance(element.getProject()).getDocument(element.getContainingFile());
        if (document == null) return;

        Matcher matcher = INNER.matcher(element.getText());
        if (!matcher.find()) return;
        boolean authoredAlpha = matcher.group(2) != null;
        String replacement = String.format(
                Locale.ROOT,
                authoredAlpha || color.getAlpha() < 255 ? "#%02X%02X%02X%02X" : "#%02X%02X%02X",
                color.getRed(), color.getGreen(), color.getBlue(), color.getAlpha());

        int start = element.getTextRange().getStartOffset() + matcher.start();
        int end = element.getTextRange().getStartOffset() + matcher.end();
        document.replaceString(start, end, replacement);
        PsiDocumentManager.getInstance(element.getProject()).commitDocument(document);
    }
}
