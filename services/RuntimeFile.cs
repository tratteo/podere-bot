namespace PodereBot.Services;

/// <summary>
/// Runtime files used to live inside the publish folder, which the updater replaces on every
/// deploy. They now live at the content root; this moves the old copy across exactly once.
/// </summary>
internal static class RuntimeFile
{
    /// <summary>
    /// Moves <paramref name="fileName"/> from <paramref name="publishFolder"/> to
    /// <paramref name="contentRoot"/> when the target does not exist yet and the source does.
    /// Returns true when something was migrated.
    /// </summary>
    public static bool Migrate(string fileName, string contentRoot, string publishFolder, ILogger logger)
    {
        var target = Path.Join(contentRoot, fileName);
        var legacy = Path.Join(publishFolder, fileName);

        if (File.Exists(target) || !File.Exists(legacy) || IsSamePath(target, legacy))
            return false;

        try
        {
            Directory.CreateDirectory(contentRoot);
            File.Move(legacy, target);
            logger.LogInformation("migrated {f} from the publish folder to {t}", fileName, target);
            return true;
        }
        catch (Exception moveError)
        {
            // A different filesystem cannot be renamed across; copy then remove the source.
            try
            {
                File.Copy(legacy, target, overwrite: false);
                File.Delete(legacy);
                logger.LogInformation("copied {f} to {t} and removed the publish copy", fileName, target);
                return true;
            }
            catch (Exception copyError)
            {
                logger.LogError(
                    copyError,
                    "unable to migrate {f} from {s} to {t} (move failed: {m})",
                    fileName,
                    legacy,
                    target,
                    moveError.Message
                );
                return false;
            }
        }
    }

    private static bool IsSamePath(string a, string b) =>
        string.Equals(
            Path.GetFullPath(a),
            Path.GetFullPath(b),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal
        );
}
