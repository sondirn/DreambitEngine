# Dreambit Engine Templates

Version 0.9.4 provides separate package-based and source-submodule project commands.

## Generated project

- `DreambitGame` - reusable game code and scenes.
- `DreambitGame.Content` - raw assets and the game's MonoGame content builder.
- `DreambitGame.VK` - the DesktopVK executable.
- `.dreambit/project.json` - portable paths, project identity, renderer, and SDK version.
- Repository-level central package versions for `DreambitEngine` and `DreambitEngine.Build`.
- No DreambitEngine Git submodule, setup scripts, machine-specific SDK path, or build-tool project references.

`dreambit-game` is the package-based template used by Dreambit.Editor. `dreambit-game-source`
creates a Git repository and pins DreambitEngine as a submodule at `.dreambit/engine`.
Both templates maintain development blobs under `.cache/dreambit/bake`. Debug builds run from the
blobs. Use **Build > Bake Pak** in Dreambit Editor before a Release build or publish; Release copies
that verified PAK into the output.

## Pack and install locally

PowerShell:

```powershell
./scripts/install-local.ps1
```

Bash:

```bash
./scripts/install-local.sh
```

These scripts pack the coordinated runtime/build/template package set into the same user SDK-cache layout consumed by Dreambit.Editor, then install the project template for direct command-line use.

Create a project:

```powershell
dotnet new dreambit-game -n MyGame --game-title "My Game" --sdkVersion 0.9.4

# Source checkout at .dreambit/engine
dotnet new dreambit-game-source -n MyGame --game-title "My Game" --sdkVersion 0.9.4 --allow-scripts yes
```

Dreambit.Editor normally performs template installation, generation, and package restore automatically.

## Test

```powershell
./scripts/test-template.ps1
```

The smoke test packs all four SDK packages, uses an isolated template hive, generates a dotted-name project, validates `.dreambit/project.json`, restores from the local package feed, and builds the generated solution.
