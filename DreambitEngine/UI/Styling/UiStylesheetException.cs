using System;
using System.Xml;

namespace Dreambit.UI;

/// <summary>Reports a Dreambit stylesheet syntax or application error.</summary>
public sealed class UiStylesheetException : XmlException
{
    public UiStylesheetException(
        string message,
        string sourcePath,
        int lineNumber,
        int linePosition,
        Exception? innerException = null)
        : base(
            $"{message} Stylesheet: '{sourcePath}', line {lineNumber}, column {linePosition}.",
            innerException,
            lineNumber,
            linePosition)
    {
        SourcePath = sourcePath;
    }

    /// <summary>Gets the source or logical asset path of the stylesheet.</summary>
    public string SourcePath { get; }
}
