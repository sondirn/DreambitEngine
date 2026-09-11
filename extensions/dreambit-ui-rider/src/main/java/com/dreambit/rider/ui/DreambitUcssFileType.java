package com.dreambit.rider.ui;

import com.intellij.lang.Language;
import com.intellij.openapi.fileTypes.LanguageFileType;
import org.jetbrains.annotations.NotNull;

import javax.swing.*;
import java.util.Objects;

public final class DreambitUcssFileType extends LanguageFileType {
    public static final DreambitUcssFileType INSTANCE = new DreambitUcssFileType();

    private DreambitUcssFileType() {
        super(Objects.requireNonNull(Language.findLanguageByID("CSS"), "CSS language is unavailable"));
    }

    @Override public @NotNull String getName() { return "Dreambit UCSS"; }
    @Override public @NotNull String getDescription() { return "Dreambit UI stylesheet"; }
    @Override public @NotNull String getDefaultExtension() { return "ucss"; }
    @Override public Icon getIcon() { return null; }
}
