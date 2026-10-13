using Microsoft.Xna.Framework.Content.Pipeline;
using MonoGame.Framework.Content.Pipeline.Builder;

if (args.Length == 0)
{
    Console.Error.WriteLine(
        "No content platform was supplied. " +
        "Build a host project instead.");

    return -1;
}

var builder = new Builder();
builder.Run(args);

return builder.FailedToBuild > 0 ? -1 : 0;

public sealed class Builder : ContentBuilder
{
    public override IContentCollection GetContentCollection()
    {
        var content = new ContentCollection();

        // The rendering pipeline loads these by asset name. Bake every built-in
        // effect so adding a render pass cannot leave its shader absent at runtime.
        content.Include<WildcardRule>("Effects/*.fx");
        content.IncludeCopy<WildcardRule>("*.ttf");
        content.Exclude<WildcardRule>("*.xnb");

        return content;
    }
}
1