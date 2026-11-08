# KeepOne

KeepOne (`Dubluri.exe`) finds duplicate files by content and removes them safely. It runs on Windows x64 with no installation and no administrator rights.

The user interface is in Romanian. Button and option names below are shown in Romanian, with the English meaning in parentheses.

## How to use it

1. **Configurare** (Setup): add your protected folders and search locations, then tick the file types. Press **Pornește scanarea** (Start scan).
2. **Rezultate** (Results): tick the copies to remove, one at a time, by folder, or with an auto-selection rule (keep the priority location, the oldest, the newest, or the shortest path).
   When selecting by folder, if all copies of a group are inside that folder, the option **Dacă toate copiile sunt în folder, păstrează una** (If all copies are in the folder, keep one) leaves one copy unselected, according to the chosen auto-selection rule. With the option off, the group stays unchanged.
3. Choose the action (Recycle Bin, quarantine, or permanent deletion) and press **Execută** (Execute). With **Doar simulare** (Simulation only) you see what would happen without changing anything.
4. **Erori și operații** (Errors and operations): shows what was skipped and the result of the last operation. If there were access errors, you can restart the application as administrator from here.

`Dubluri.exe` must stay in the same folder as `av_libglesv2.dll`, `libHarfBuzzSharp.dll` and `libSkiaSharp.dll`.

Settings, the hash cache and the CSV logs are stored in `%AppData%\Dubluri`.

## How protection works

Nothing inside protected folders or their subfolders is ever deleted, moved or renamed. Files there take part in the comparison as originals, but they cannot be selected. The **Ascunde copiile din folderele protejate** (Hide copies in protected folders) option in Results only removes them from the list: they remain the kept originals, and groups whose copies are all protected disappear.

- **The check lives in the deletion layer.** `DeletionService` is the only place that removes anything. Right before each file it calls `ProtectionGuard.IsProtected` again, even if the UI allowed the selection.
- **`IsProtected` compares several forms of the path:** the normalized form (`.`, `..`, letter case, the `\\?\` prefix, long paths, a false prefix such as `D:\Photos2`) and the real path (junctions, symlinks, `subst`, mapped drives, 8.3 names). It then compares the physical identity of the parent folders, which also catches aliases such as `\\localhost\C$\Photos`.
- **When in doubt, the answer is "protected":** a path that cannot be resolved is not deleted.
- **Inaccessible protected folder** (for example, an unplugged USB drive): deletions are blocked until you reconnect it or remove it from the list.
- **Settings that cannot be read:** the protected list is unknown, so deletions are blocked until you confirm.

Other safeguards, applied before each file:

- at least one copy remains in every group;
- the file has not changed since the scan;
- another identical copy exists, verified byte by byte;
- a hardlink or alias to the same file does not count as a copy;
- with **Recycle Bin**, if Windows would delete permanently (network share, USB drive, file too large for the bin), the operation is cancelled and quarantine is recommended instead.

The application never deletes folders. Scanning does not follow junctions or symlinks, and it skips cloud-only files.

## Building and testing

Requires the .NET 10 SDK.

```
dotnet test
dotnet publish Dubluri.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
```

The symlink test is skipped unless you run as administrator or have Developer Mode enabled. The tests use real files, and one of them sends a small text file to the Recycle Bin.

## Porting to Linux

`Dubluri.Core` and the Avalonia UI contain no Windows-specific code. A port needs:

1. A new project, `Dubluri.Platform.Linux`, implementing the interfaces in `Dubluri.Core/Platform/PlatformInterfaces.cs`:

   | Interface | On Linux |
   |---|---|
   | `ITrashService` | `~/.local/share/Trash` (FreeDesktop specification), plus a check that a trash exists on each volume |
   | `IFileIdentityService` | device + inode (`stat`) |
   | `IPathResolver` | `realpath`, case-sensitive comparison |
   | `IElevationService` | `pkexec` |
   | `ISystemFolders` | `/proc`, `/sys`, `/dev`, `/usr`, `/bin`, etc. |
   | `IShellService` | `xdg-open` or `org.freedesktop.FileManager1` |

2. The Linux branch in `Dubluri.App/PlatformServices.cs`.
3. Publishing with `-r linux-x64`.
4. Adapting the tests that use `cmd.exe` (`mklink`, `subst`) and Windows paths.
