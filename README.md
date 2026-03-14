# Native Terminal Starter

This repo is a native Windows starter app based on the official unpackaged WinUI 3 Windows App SDK sample.

It is meant to be the first checkpoint for a future terminal app:

- native WinUI 3 shell
- unpackaged startup
- self-contained Windows App SDK deployment
- no Electron
- no Tauri

## Current status

The app builds successfully on this machine through the Windows `dotnet` toolchain.

It is still a starter shell, not a full terminal yet. The next real engineering step is wiring a ConPTY-backed terminal host into the main content area.

## Build

```powershell
& "C:\Program Files\dotnet\dotnet.exe" build .\SelfContainedDeployment.csproj -p:Platform=x64
```

## Run

```powershell
& "C:\Program Files\dotnet\dotnet.exe" run --project .\SelfContainedDeployment.csproj -p:Platform=x64
```

## Next steps

1. Add a terminal host page backed by ConPTY.
2. Add a split layout for terminal plus side workspace.
3. Add WebView2 only after the terminal host is stable.
