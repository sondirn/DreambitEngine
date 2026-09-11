using Microsoft.Xna.Framework.Content.Pipeline;
using MonoGame.Framework.Content.Pipeline.Builder;

var projectDirectory = Path.GetFullPath(
    Path.Combine(AppContext.BaseDirectory, "..", ".."));

var defaultParameters = new ContentBuilderParams
{
    Mode = ContentBuilderMode.Builder,
    WorkingDirectory = projectDirectory,
    SourceDirectory = "Assets",
    Platform = TargetPlatform.DesktopGL
};

var builder = new GameContentBuilder();

if (args.Length > 0)
    builder.Run(args);
else
    builder.Run(defaultParameters);

return builder.FailedToBuild > 0 ? -1 : 0;

public sealed class GameContentBuilder : ContentBuilder
{
    public override IContentCollection GetContentCollection()
    {
        var content = new ContentCollection();

        // AssetBaker owns textures, documents, YAML, and audio.
        // MonoGame's content pipeline owns GPU effects and raw fonts.
        content.Include<WildcardRule>("*.fx");
        content.IncludeCopy<WildcardRule>("*.ttf");
        content.Exclude<WildcardRule>("*.xnb");
        content.IncludeCopy<WildcardRule>("*.pak");

        return content;
    }
}
