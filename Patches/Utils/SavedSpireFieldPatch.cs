using BaseLib.Patches.Saves;
using BaseLib.Utils;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace BaseLib.Patches.Utils;

[HarmonyPatch(typeof(SavedProperties))]
static class SavedSpireFieldPatch
{
    private static readonly List<ISavedSpireField> RegisteredFields = [];

    /// <summary>
    /// True once every mod has been loaded, i.e. once <see cref="AddFieldsSorted"/> has run. A field created before
    /// that cannot be matched to its mod yet: a mod is only added to the loaded mod list after its initializer, which
    /// is after any field it creates.
    /// </summary>
    internal static bool ModsLoaded { get; private set; }

    public static void Register<TKey, TVal>(SavedSpireField<TKey, TVal> field)
        where TKey : class => RegisteredFields.Add(field);

    private static IEnumerable<ISavedSpireField> GetFieldsForModel(object model) =>
        RegisteredFields.Where(f => f.TargetType.IsInstanceOfType(model));
    
    [HarmonyPatch(nameof(SavedProperties.FromInternal))]
    [HarmonyPostfix]
    static void PostfixFromInternal(ref SavedProperties? __result, object model)
    {
        var props = __result ?? new SavedProperties();
        foreach (var field in GetFieldsForModel(model))
            field.Export(model, props);

        // FromInternal returns null when nothing was saved, and callers rely on that. Only attach a holder that we
        // created when our fields actually wrote something; an empty holder would read as "properties exist".
        if (__result == null && HasAnyValue(props))
            __result = props;
    }

    /// <summary>
    /// Mirrors the empty check that <see cref="SavedProperties.FromInternal"/> applies before returning null.
    /// The base game method is private, so it cannot be reused directly.
    /// </summary>
    private static bool HasAnyValue(SavedProperties props) =>
        props.ints is { Count: > 0 } ||
        props.bools is { Count: > 0 } ||
        props.strings is { Count: > 0 } ||
        props.intArrays is { Count: > 0 } ||
        props.modelIds is { Count: > 0 } ||
        props.cards is { Count: > 0 } ||
        props.cardArrays is { Count: > 0 };

    [HarmonyPatch(nameof(SavedProperties.FillInternal))]
    [HarmonyPostfix]
    static void PostfixFillInternal(SavedProperties __instance, object model)
    {
        foreach (var field in GetFieldsForModel(model))
            field.Import(model, __instance);
    }
    
    internal static void AddFieldsSorted()
    {
        BaseLibMain.Logger.Info($"Found {RegisteredFields.Count} SavedSpireFields.");

        // Every mod has been loaded by this point. From here on a field that cannot be matched to a mod is reported
        // instead of being treated as "its mod has not been registered yet".
        ModsLoaded = true;

        RegisteredFields.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));

        foreach (var field in RegisteredFields)
        {
            if (field.IsBasegameSupported)
            {
                InjectNameIntoBaseGameCache(field.Name);

                // The pre-namespacing key is written next to the namespaced one (see SavedSpireField.Export), so it
                // needs a net ID too. It is expected to be shared with mods that had the same field name before
                // namespacing, so a duplicate is not an error.
                if (!string.Equals(field.Name, field.LegacyName, StringComparison.Ordinal))
                    InjectNameIntoBaseGameCache(field.LegacyName, mustBeUnique: false);
            }
            else if (!field.RegisterCustomSave())
            {
                BaseLibMain.Logger.Error($"SavedSpireField {field.Name} will not be saved as it is of an unsupported type.");
            }
        }
    }

    /// <summary>
    /// Used to add names for fake saved properties.
    /// </summary>
    private static Type? _targetType;
    private static bool _beta = false;
    private static void InjectNameIntoBaseGameCache(string name, bool mustBeUnique = true)
    {
        if (_targetType == null)
        {
            _targetType =
                AccessTools.TypeByName("MegaCrit.Sts2.Core.Saves.Runs.SavedPropertiesTypeCache");
            if (_targetType == null)
            {
                _targetType = typeof(ModelIdSerializationCache);
                _beta = true;
            }
        }
        var propertyToId = AccessTools.StaticFieldRefAccess<Dictionary<string, int>>(
            _targetType, "_propertyNameToNetIdMap");
        var idToProperty = AccessTools.StaticFieldRefAccess<List<string>>(
            _targetType, "_netIdToPropertyNameMap");
        var bitSizeProperty = AccessTools.PropertySetter(
            _targetType, _beta ? "PropertyIdBitSize" : "NetIdBitSize");

        if (!propertyToId.ContainsKey(name))
        {
            propertyToId[name] = idToProperty.Count;
            idToProperty.Add(name);
            
            BaseLibMain.Logger.Debug($"Added saved property name to basegame cache: {name} => {propertyToId[name]}");

            int newBitSize = Mathf.CeilToInt(Math.Log2(idToProperty.Count));

            bitSizeProperty.Invoke(null, [newBitSize]);
        }
        else if (mustBeUnique)
        {
            BaseLibMain.Logger.Error($"SavedSpireField name is not unique: {name}");
        }
        else
        {
            BaseLibMain.Logger.Debug($"Saved property name already registered, reusing it for a legacy key: {name}");
        }
    }
}
