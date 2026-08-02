# Plugin starter

Copy this directory, rename the project, namespace, class, assembly fields, and
`plugin.yml`, then build a distributable package from the repository root:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\package-plugin.ps1 `
    .\examples\PluginStarter\PluginStarter.csproj
```

The package is written beneath `artifacts/plugins`. See
[`docs/PLUGIN_TUTORIAL.md`](../../docs/PLUGIN_TUTORIAL.md) for the complete
workflow and API conventions.
