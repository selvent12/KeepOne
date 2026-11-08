using Dubluri.Core.Platform;
using Dubluri.Platform.Windows;

namespace Dubluri.App;

/// <summary>Singurul loc din interfata care stie ce sistem de operare ruleaza. Pe Linux se adauga aici implementarile.</summary>
public sealed record PlatformServices(
    IPathResolver Paths,
    IFileIdentityService Identity,
    ITrashService Trash,
    IElevationService Elevation,
    ISystemFolders SystemFolders,
    IShellService Shell)
{
    public static PlatformServices Create()
    {
        if (OperatingSystem.IsWindows())
            return new PlatformServices(new WindowsPathResolver(), new NtfsFileIdentityService(), new WindowsTrashService(),
                                        new WindowsElevationService(), new WindowsSystemFolders(), new WindowsShellService());

        throw new PlatformNotSupportedException("Această versiune funcționează doar pe Windows.");
    }
}
