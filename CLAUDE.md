# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project state

Empty WPF scaffold generated from the standard Visual Studio "WPF Application" template. `MainWindow.xaml` has an empty `Grid`; `App.xaml.cs` and `MainWindow.xaml.cs` contain no logic beyond `InitializeComponent()`. Any feature work starts from this blank slate — there is no existing architecture, MVVM plumbing, or dependency wiring to fit into yet.

- Target framework: `net8.0-windows` with `<UseWPF>true</UseWPF>`
- `Nullable` and `ImplicitUsings` are both enabled in `Deblur.csproj`
- Single project (`Deblur/Deblur.csproj`) inside the `Deblur.sln` solution
- Windows-only (WPF); build and run from a Windows host

## Commands

Run from the repo root (`C:\Users\priya\source\repos\Deblur`):

```bash
dotnet build Deblur.sln                          # build (Debug by default)
dotnet build Deblur.sln -c Release               # release build
dotnet run --project Deblur/Deblur.csproj        # launch the WPF app
dotnet clean Deblur.sln                          # clear bin/obj
```

No test project exists yet. If tests are added, prefer a sibling `Deblur.Tests` project referenced from `Deblur.sln` and run with `dotnet test`.
