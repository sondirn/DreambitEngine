package com.dreambit.rider.ui;

import com.intellij.codeInsight.navigation.actions.GotoDeclarationHandler;
import com.intellij.openapi.editor.Editor;
import com.intellij.openapi.vfs.VirtualFile;
import com.intellij.psi.PsiElement;
import com.intellij.psi.PsiFile;
import com.intellij.psi.PsiManager;
import org.jetbrains.annotations.Nullable;

import java.util.regex.Matcher;
import java.util.regex.Pattern;

public final class DreambitGotoDeclarationHandler implements GotoDeclarationHandler {
    private static final Pattern SOURCE = Pattern.compile("\\bsource\\s*=\\s*([\"'])([^\"']+)\\1", Pattern.CASE_INSENSITIVE);

    @Override
    public PsiElement @Nullable [] getGotoDeclarationTargets(PsiElement sourceElement, int offset, Editor editor) {
        PsiFile file = sourceElement.getContainingFile();
        if (file == null || file.getVirtualFile() == null ||
                !"uxml".equalsIgnoreCase(file.getVirtualFile().getExtension())) return null;

        String text = file.getText();
        Matcher matcher = SOURCE.matcher(text);
        while (matcher.find()) {
            if (offset < matcher.start(2) || offset > matcher.end(2)) continue;
            DreambitWorkspaceIndex index = sourceElement.getProject().getService(DreambitWorkspaceIndex.class);
            VirtualFile target = index.resolveUxml(file.getVirtualFile(), matcher.group(2));
            if (target == null) return null;
            PsiFile psi = PsiManager.getInstance(sourceElement.getProject()).findFile(target);
            return psi == null ? null : new PsiElement[]{psi};
        }
        return null;
    }
}
