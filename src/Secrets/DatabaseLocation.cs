using Croicu.Desk.Tools.Base;

namespace Croicu.Desk.Tools.Secrets;

/// <summary>
/// Finds the KeePass database to open: the path saved in the settings if there is one, otherwise it
/// asks the user once with a file picker and saves the answer. The picker is invoked <b>only</b> when
/// the saved value is missing or empty (issue #50) -- a saved value that is present but unusable is an
/// error to fix, not a reason to open a dialog on an elevated background process. Either way the path
/// must pass <see cref="DatabasePathRule"/>, including the saved one (the per-user file is writable by
/// any process running as the user).
/// </summary>
internal sealed class DatabaseLocation
{
    internal const string PickerTitle = "Desk Tools - select your KeePass database";

    private readonly IDatabasePathStore _store;
    private readonly IFilePicker _picker;

    internal DatabaseLocation(IDatabasePathStore store, IFilePicker picker)
    {
        _store = store;
        _picker = picker;
    }

    /// <summary>
    /// The full path of the database to open. Throws <see cref="AppError"/> -- with a message meant for
    /// a developer, not softened -- if the saved path is unusable, the user cancelled the picker, or the
    /// picked file isn't acceptable; nothing is saved in any of those cases.
    /// </summary>
    internal string Resolve()
    {
        var saved = _store.Get();
        if (!string.IsNullOrWhiteSpace(saved))
        {
            var problem = DatabasePathRule.Check(saved);
            if (problem is not null)
            {
                throw new AppError(
                    $"the configured KeePass database '{saved}' ({_store.Description}) cannot be used: {problem} Fix or delete that setting to be asked again.",
                    LogCategory.Secrets);
            }

            Logger.Diagnostic($"secrets: using the configured KeePass database '{saved}'.", LogCategory.Secrets);
            return Path.GetFullPath(saved);
        }

        Logger.Info("secrets: no KeePass database is configured; asking the user to pick one.", LogCategory.Secrets);

        var picked = _picker.Pick(PickerTitle, "KeePass databases", DatabasePathRule.Extension.TrimStart('.'));
        if (picked is null)
        {
            throw new AppError(
                $"no KeePass database is configured and none was chosen. The location is read from {_store.Description}; "
                + $"it must be an absolute path to a local KeePassXC '{DatabasePathRule.Extension}' file (KDBX 4). "
                + "Pick one in the file dialog, or set that value yourself, then retry.",
                LogCategory.Secrets);
        }

        var pickedProblem = DatabasePathRule.Check(picked);
        if (pickedProblem is not null)
        {
            throw new AppError($"'{picked}' cannot be used as the KeePass database: {pickedProblem} Nothing was saved.", LogCategory.Secrets);
        }

        var fullPath = Path.GetFullPath(picked);
        _store.Set(fullPath);
        Logger.Info($"secrets: KeePass database set to '{fullPath}' ({_store.Description}).", LogCategory.Secrets);
        return fullPath;
    }
}
