# Build Patchwork

Run these commands from the repository root on Windows x64. The Windows .NET Framework 4.x compiler and WPF libraries are required; no SDK or NuGet download is needed. Mono.Cecil and its license are included in `source/lib`.

```powershell
& .\source\build.ps1 -OutputDirectory .\build
& .\source\build.ps1 -OutputDirectory .\build-tests -Console -Tests
& .\build-tests\Patchwork.exe --self-test --data-dir .\test-data
& .\source\installer\build-installer.ps1 -WorkDirectory .\build-setup -OutputFile .\Patchwork-Setup.exe
```

Tests and mutation hooks compile only with `-Tests`; the installer uses a production build. Use a separate `--data-dir PATH` for isolated testing. Builds include no patch packs.

Use a release tag, such as `v1.1.1`, to build the exact published source. The default branch also includes current documentation. See [icon validation](ICON-VALIDATION.md) and [UI validation](UI-VALIDATION.md) for evidence and limits.

The neutral Windows icon is checked in. Regenerate it with `& .\source\build-icons.ps1` after changing the logo renderer.
