namespace BaseLib.Config;

public static class ModConfigRegistry
{
    private static readonly Dictionary<string, ModConfig> ModConfigs = new();

    public static void Register(string modId, ModConfig config)
    {
        // Checked before the early return below, so that even a config without settings gets a collision warning.
        var existing = ModConfigs.Values.FirstOrDefault(c =>
            string.Equals(c.ConfigPath, config.ConfigPath, StringComparison.OrdinalIgnoreCase));
        if (existing != null && !string.Equals(existing.ModId, modId, StringComparison.Ordinal))
        {
            BaseLibMain.Logger.Error(
                $"Mod config file collision: mods '{existing.ModId}' and '{modId}' both use {config.ConfigPath}. " +
                "Whichever mod saves last overwrites the other's settings. Give one of the configs an explicit " +
                "filename in its ModConfig constructor.");
        }

        if (!config.HasSettings() && !config.VisibleInModList()) return;
        config.ModId = modId;
        ModConfigs[modId] = config;
        
        BaseLibMain.Logger.Info($"Registered config for mod {modId}");
    }

    public static ModConfig? Get(string? modId)
    {
        if (modId == null) return null;
        return ModConfigs.GetValueOrDefault(modId);
    }

    public static T? Get<T>() where T : ModConfig
    {
        return ModConfigs.Values.OfType<T>().FirstOrDefault();
    }

    public static List<ModConfig> GetAll() =>
        ModConfigs.Values
        .OrderBy(m => m.ModId)
        .ToList();
}