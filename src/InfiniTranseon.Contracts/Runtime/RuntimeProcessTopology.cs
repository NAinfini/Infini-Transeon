namespace InfiniTranseon.Contracts.Runtime;

public enum RuntimeProcessRole
{
    App,
    EngineHost,
    ModelWorker,
    ProviderWorker,
    UpdaterWorker,
}

public static class RuntimeProcessTopology
{
    public static bool IsAllowedResidentSet(
        IEnumerable<RuntimeProcessRole> processRoles,
        bool localModelEnabled)
    {
        ArgumentNullException.ThrowIfNull(processRoles);

        RuntimeProcessRole[] roles = processRoles.ToArray();
        if (roles.Length is < 2 or > 3 || roles.Distinct().Count() != roles.Length)
        {
            return false;
        }

        if (!roles.Contains(RuntimeProcessRole.App) || !roles.Contains(RuntimeProcessRole.EngineHost))
        {
            return false;
        }

        RuntimeProcessRole[] extraRoles = roles
            .Where(role => role is not RuntimeProcessRole.App and not RuntimeProcessRole.EngineHost)
            .ToArray();

        return extraRoles.Length == 0
            || (localModelEnabled && extraRoles is [RuntimeProcessRole.ModelWorker]);
    }
}
