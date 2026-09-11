using System;

namespace Dreambit.UI;

internal static class UiAssetPath
{
    public const string XmlSourceExtension = ".uxml";
    public const string StylesheetSourceExtension = ".ucss";

    public static string ToBakedXml(string sourcePath)
    {
        if (sourcePath.EndsWith(".xmlb", StringComparison.OrdinalIgnoreCase))
            return sourcePath;
        if (sourcePath.EndsWith(XmlSourceExtension, StringComparison.OrdinalIgnoreCase))
            return sourcePath[..^XmlSourceExtension.Length] + ".xmlb";
        if (sourcePath.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            return sourcePath[..^".xml".Length] + ".xmlb";
        return sourcePath + ".xmlb";
    }

    public static string ToBakedStylesheet(string sourcePath)
    {
        if (sourcePath.EndsWith(".cssb", StringComparison.OrdinalIgnoreCase))
            return sourcePath;
        if (sourcePath.EndsWith(StylesheetSourceExtension, StringComparison.OrdinalIgnoreCase))
            return sourcePath[..^StylesheetSourceExtension.Length] + ".cssb";
        if (sourcePath.EndsWith(".css", StringComparison.OrdinalIgnoreCase))
            return sourcePath[..^".css".Length] + ".cssb";
        return sourcePath + ".cssb";
    }

    public static string GetSiblingStylesheet(string documentPath)
    {
        if (documentPath.EndsWith(".xmlb", StringComparison.OrdinalIgnoreCase))
            return documentPath[..^".xmlb".Length] + StylesheetSourceExtension;
        if (documentPath.EndsWith(XmlSourceExtension, StringComparison.OrdinalIgnoreCase))
            return documentPath[..^XmlSourceExtension.Length] + StylesheetSourceExtension;
        if (documentPath.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            return documentPath[..^".xml".Length] + ".css";
        return documentPath + StylesheetSourceExtension;
    }
}
