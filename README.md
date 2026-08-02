# Chunk Survival Plugins

This is the public source of the Chunk Survival plugin API, official plugins,
and starter examples. The game server itself is maintained separately.

## Build

Install the .NET 9 SDK, then build the solution:

```powershell
dotnet build .\ChunkSurvivalPlugins.sln -c Release
```

Create installable ZIP files for every official plugin:

```powershell
pwsh .\tools\package-all-plugins.ps1 -Configuration Release
```

Packages are written to `artifacts/plugins`. Each ZIP contains the plugin's
directory, manifest, main assembly, and any plugin-owned dependencies. The
server supplies `BlockGame.PluginApi.dll`, so it is intentionally excluded.

## Releases

GitHub Actions builds and packages every pull request and every push to
`master`. To publish a release:

- Game-client releases use `client-vX.Y.Z` in the private game repository.
- Plugin-bundle releases use `plugin-vX.Y.Z` in this repository.

1. Set the same release version in each official plugin's `plugin.yml`.
2. Commit and push the release changes.
3. Create and push a matching tag such as `plugin-v1.2.0`.

The release workflow verifies the manifest versions, builds the solution,
creates one ZIP per plugin plus `SHA256SUMS.txt`, and publishes them on the
GitHub Release for that tag.

## Developing with the private game server

This repository should be the source of truth for plugin code. The private
game repository can include it as a public Git submodule pinned to a tested
commit. That keeps the plugin source physically present for server integration
tests without maintaining two copies or giving the public workflow access to
the private repository.

The intended flow is:

1. Develop plugin and API changes in this repository's submodule working tree.
2. Build and run the private server's integration tests against that checkout.
3. Commit and push plugin changes here.
4. Update the private repository's submodule pointer to the tested commit.

Actions build and release the public code; they should not copy arbitrary files
from the private repository into this one.
