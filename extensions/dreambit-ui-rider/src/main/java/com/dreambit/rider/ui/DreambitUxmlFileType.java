package com.dreambit.rider.ui;

import com.intellij.lang.Language;
import com.intellij.openapi.fileTypes.LanguageFileType;
import org.jetbrains.annotations.NotNull;

import javax.swing.*;
import java.util.Objects;

public final class DreambitUxmlFileType extends LanguageFileType {
    public static final DreambitUxmlFileType INSTANCE = new DreambitUxmlFileType();

    private DreambitUxmlFileType() {
        super(Objects.requireNonNull(Language.findLanguageByID("XML"), "XML language is unavailable"));
    }

    @Override public @NotNull String getName() { return "Dreambit UXML"; }
    @Override public @NotNull String getDescription() { return "Dreambit retained UI layout/component"; }
    @Override public @NotNull String getDefaultExtension() { return "uxml"; }
    @Override public Icon getIcon() { return null; }
}
