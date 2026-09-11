# Dreambit SDK releases

`Publish-DreambitSdk.ps1` is the supported release entry point. It updates every
engine, Editor, build-package, documentation, and project-template version,
restores once, runs the Editor test suite, creates all four NuGet packages,
updates the `Dreambit-local` NuGet source to the new package directory, and
installs the new project template locally. It does not publish to NuGet.org by
default.

```powershell
./scripts/publish-sdk.cmd 0.1.8
```

Packages are written under `artifacts/packages/<version>` and the local
template registration is updated. To only create packages without installing
the template:

```powershell
Set-ExecutionPolicy Unrestricted -Scope CurrentUser
./scripts/publish-sdk.cmd 0.1.8 -SkipTemplateInstall
```

The `.cmd` entry point applies a process-only PowerShell execution-policy
bypass, so it also works on Windows systems where local scripts are disabled.
It forwards any additional switches to `Publish-DreambitSdk.ps1`.

Pass `-SkipLocalSourceUpdate` to leave the registered local NuGet source
unchanged, or `-LocalSourceName MyFeed` to use a different local source name.

To publish to a registry, opt in with `-Push` and provide `-ApiKey` or the
`NUGET_API_KEY` environment variable. Pass `-NuGetSource` for another
registry. The key is never printed. New package-based projects created after
the release use the new SDK automatically. Existing projects should update the
three Dreambit entries in `Directory.Packages.props` and their
`.dreambit/project.json` SDK version, then run `dotnet restore --force-evaluate`.
