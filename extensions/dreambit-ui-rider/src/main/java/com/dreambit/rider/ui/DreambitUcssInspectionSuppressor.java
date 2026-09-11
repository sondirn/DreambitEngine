package com.dreambit.rider.ui;

import com.intellij.codeInspection.InspectionSuppressor;
import com.intellij.codeInspection.SuppressQuickFix;
import com.intellij.psi.PsiElement;
import org.jetbrains.annotations.NotNull;
import org.jetbrains.annotations.Nullable;

public final class DreambitUcssInspectionSuppressor implements InspectionSuppressor {
    private static final SuppressQuickFix[] NONE = new SuppressQuickFix[0];

    @Override
    public boolean isSuppressedFor(@NotNull PsiElement element, @NotNull String toolId) {
        return element.getContainingFile() != null &&
                element.getContainingFile().getVirtualFile() != null &&
                "ucss".equalsIgnoreCase(element.getContainingFile().getVirtualFile().getExtension());
    }

    @Override
    public SuppressQuickFix @NotNull [] getSuppressActions(@Nullable PsiElement element, @NotNull String toolId) {
        return NONE;
    }
}
