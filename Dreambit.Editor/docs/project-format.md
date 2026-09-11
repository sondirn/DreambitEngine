# Dreambit project format

Dreambit projects declare portable Editor/build information in `.dreambit/project.json`.

```json
{
  "schemaVersion": 1,
  "projectId": "77da2ddb-0a4f-4e64-95d9-c8aef5c7f329",
  "name": "MyGame",
  "solution": "MyGame.sln",
  "gameProject": "src/MyGame/MyGame.csproj",
  "contentProject": "src/MyGame.Content/MyGame.Content.csproj",
  "contentRoot": "src/MyGame.Content/Assets",
  "launcherProject": "src/MyGame.VK/MyGame.VK.csproj",
  "targetRenderer": "DesktopVK",
  "sdk": {
    "version": "0.9.4"
  }
}
```

All configured paths must be relative, remain inside the project root, and resolve to existing files or directories. Schema version 1 supports one game project, one Content project/raw asset root, and one DesktopVK launcher.

The project file contains no SDK installation path or user preferences. SDK packages, dock layouts, recents, and the per-project process lease live in the user's local Dreambit.Editor data directory. `.dreambit/user/` is reserved and ignored, but Milestone 2 does not currently need to write there.
