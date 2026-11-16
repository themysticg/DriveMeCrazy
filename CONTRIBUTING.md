# Contributing to DriveMeCrazy

Thanks for helping! A few guidelines:

1. **Open an issue first** for anything bigger than a small fix, so we can agree on the approach.
2. **Keep the safety rules** in [SECURITY.md](SECURITY.md). Pull requests that download installers from
   non-Microsoft sources, or that accept licence terms automatically, won't be merged.
3. **Tests:** decision logic, such as which update to recommend, belongs in `DriveMeCrazy.Core` and needs unit tests in
   `tests/DriveMeCrazy.Tests`. Run `dotnet test` before opening a PR.
4. **UI:** WPF + MVVM (CommunityToolkit.Mvvm), Fluent theme resources (`{DynamicResource ...}`) so light/dark both work.
   Attach before/after screenshots. `DriveMeCrazy.exe --screenshot <dir>` makes them for you.

## Dev setup

```powershell
dotnet test
dotnet run --project src/DriveMeCrazy.App -p:DmcAsInvoker=true   # unelevated: scanning works, installing is disabled
```

Run the normal build (with the admin manifest) to test installing. Please test installs in a VM or on a machine you can restore.
