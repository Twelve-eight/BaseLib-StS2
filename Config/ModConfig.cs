using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using BaseLib.Config.UI;
using BaseLib.Extensions;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.Settings;

namespace BaseLib.Config;

// NMainMenu is recreated every time you return from a run, etc., so this isn't a run-once as it seems.
// We also check for errors when exiting the mod config submenu, as that won't trigger this code.
[HarmonyPatch(typeof(NMainMenu), nameof(NMainMenu._Ready))]
public static class NMainMenu_Ready_Patch
{
    public static void Postfix()
    {
        if (ModConfig.ModConfigLogger.PendingUserMessages.Count == 0) return;
        Callable.From(ModConfig.ShowAndClearPendingErrors).CallDeferred();
    }
}

[HarmonyPatch(typeof(NGame), nameof(NGame.Quit))]
public static class NGameQuitModConfigSavePatch
{
    public static void Prefix()
    {
        BaseLibMain.Logger.Info("NGame.Quit(): saving all ModConfigs");
        foreach (var modConfig in ModConfigRegistry.GetAll())
            modConfig.Save();
    }
}

public abstract partial class ModConfig
{
    private const string SettingsTheme = "res://themes/settings_screen_line_header.tres";

    /// <summary>
    /// Event that fires when <see cref="Changed()"/> is called. Custom controls must call Changed() when mutating
    /// a property.
    /// </summary>
    public event EventHandler? ConfigChanged;
    public event Action? OnConfigReloaded;
    public void ConfigReloaded() => OnConfigReloaded?.Invoke();

    private readonly string _path;
    private readonly string? _legacyPath;
    public string ModPrefix { get; private set; }
    [ConfigIgnore] public string? ModId { get; set; } // Injected by ModConfigRegistry

    private readonly string _modConfigName;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private CancellationTokenSource? _saveDebounceToken;
    private readonly SemaphoreSlim _configFileLock = new SemaphoreSlim(1, 1);
    private readonly Lock _debounceLock = new();

    protected readonly List<PropertyInfo> ConfigProperties = [];
    private readonly Dictionary<string, object?> _defaultValues = new();
    private bool _hasVisibleSettings;
    private bool _hasButton;

    public static class ModConfigLogger
    {
        public static List<string> PendingUserMessages { get; } = [];

        /// <summary>
        /// Show a message in the console, and optionally in the GUI. Only use showInGui=true if truly necessary;
        /// players won't enjoy having warnings/errors shoved in their faces unless it's something that truly impacts them.
        /// </summary>
        public static void Warn(string message, bool showInGui = false)
        {
            BaseLibMain.Logger.Warn(message);
            if (showInGui && !PendingUserMessages.Contains(message)) PendingUserMessages.Add(message);
        }

        /// <inheritdoc cref="Warn" />
        public static void Error(string message, bool showInGui = true)
        {
            BaseLibMain.Logger.Error(message);
            if (showInGui && !PendingUserMessages.Contains(message)) PendingUserMessages.Add(message);
        }
    }

    static ModConfig()
    {
        // Enable serialization/deserialization of Godot.Color, preserving "overbright" values (>1.0)
        TypeDescriptor.AddAttributes(typeof(Color), new TypeConverterAttribute(typeof(GodotColorConverter)));
    }

    public ModConfig(string? filename = null)
    {
        ModPrefix = GetType().GetPrefix();
        ModId = null;

        var configType = GetType();
        var assemblyName = configType.Assembly.GetName().Name;
        var defaultFilename = BuildDefaultFilename(assemblyName, configType.Namespace);
        _modConfigName = defaultFilename;

        if (string.IsNullOrEmpty(defaultFilename) && string.IsNullOrEmpty(filename))
        {
            var message = "Cannot determine a safe configuration file path for " +
                          $"{configType.FullName} (assembly {assemblyName}). " +
                          "You must either place your configuration class inside a namespace, " +
                          "or explicitly provide a filename in the constructor.";
            ModConfigLogger.Error(message); // Shows it in the GUI when opening ANY mod config menu
            throw new InvalidOperationException(message);
        }

        string configFilename;
        if (filename == null)
        {
            // The default name itself contains dots (namespace separators), so Path.HasExtension cannot detect a
            // missing extension here; always append the config extension.
            configFilename = defaultFilename + ".cfg";
        }
        else
        {
            configFilename = SpecialCharRegex().Replace(filename, "");
            // "HasExtension", not "Contains('.')": a dotted name such as "MyMod.Config" is not an extension.
            if (!Path.HasExtension(configFilename)) configFilename += ".cfg";
        }

        _path = Path.Combine(OS.GetUserDataDir(), "mod_configs", configFilename);
        // Older BaseLib versions named the default file after the root namespace only ("<root>.cfg"), so two
        // configs of the same mod shared one file and overwrote each other. Read that file once if the new one
        // does not exist yet; it is never written again and stays on disk as a backup.
        _legacyPath = filename == null ? GetLegacyConfigPath(configType, configFilename) : null;

        CheckConfigProperties();
        Init();
    }

    /// <summary>
    /// Builds the default config file name from the assembly and the full namespace, so that different mods (and
    /// different namespaces within one mod) never share a file. The regex keeps dots but strips path separators and
    /// any other character that is not safe in a file name.
    /// </summary>
    private static string BuildDefaultFilename(string? assemblyName, string? fullNamespace)
    {
        var safeAssemblyName = SpecialCharRegex().Replace(assemblyName ?? "", "");
        var safeNamespace = SpecialCharRegex().Replace(fullNamespace ?? "", "");

        if (string.IsNullOrEmpty(safeNamespace)) return safeAssemblyName;
        return string.IsNullOrEmpty(safeAssemblyName) ? safeNamespace : $"{safeAssemblyName}.{safeNamespace}";
    }

    /// <summary>
    /// Returns the path this config used before the default name included the assembly name, or null when that name
    /// is identical to <paramref name="newFilename"/>.
    /// </summary>
    private static string? GetLegacyConfigPath(Type configType, string newFilename)
    {
        var legacyFilename = SpecialCharRegex().Replace(configType.GetRootNamespace(), "") + ".cfg";
        if (string.Equals(legacyFilename, newFilename, StringComparison.OrdinalIgnoreCase)) return null;

        return Path.Combine(OS.GetUserDataDir(), "mod_configs", legacyFilename);
    }

    public bool HasSettings() => ConfigProperties.Count > 0;
    public bool HasVisibleSettings() => _hasVisibleSettings;

    /// <summary>
    /// Absolute path of the file backing this config. Two configs that share a path overwrite each other's settings;
    /// <see cref="ModConfigRegistry.Register"/> logs such collisions.
    /// </summary>
    internal string ConfigPath => _path;

    /// <summary>
    /// If true, this ModConfig will show up in the mod list. By default, a mod is shown if it has any visible setting
    /// or button, but for a fully custom UI, there may be rare cases where you need to override this.
    /// </summary>
    public virtual bool VisibleInModList() => _hasVisibleSettings || _hasButton;

    private void CheckConfigProperties()
    {
        var configType = GetType();

        _hasVisibleSettings = false;
        _hasButton = false;
        ConfigProperties.Clear();

        foreach (var property in configType.GetProperties())
        {
            if (property.GetCustomAttribute<ConfigIgnoreAttribute>() != null) continue;
            if (!property.CanRead || !property.CanWrite) continue;
            if (property.GetMethod?.IsStatic != true)
            {
                ModConfigLogger.Warn($"Ignoring {_modConfigName} property {property.Name}: only static properties are supported");
                continue;
            }

            ConfigProperties.Add(property);

            if (property.GetCustomAttribute<ConfigHideInUI>() == null)
                _hasVisibleSettings = true;
        }

        _hasButton = configType
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
            .Any(m => m.GetCustomAttribute<ConfigButtonAttribute>() != null);
    }

    public T? GetDefaultValue<T>(string propertyName)
    {
        if (_defaultValues.TryGetValue(propertyName, out var val) && val is T typedValue)
        {
            return typedValue;
        }

        return default;
    }

    /// <summary>
    /// Restores the default value for all relevant config properties: all except those marked with
    /// [<see cref="ConfigIgnoreAttribute">ConfigIgnore</see>] or
    /// [<see cref="ConfigIgnoreRestoreDefaultsAttribute">ConfigIgnoreRestoreDefaults</see>].
    /// </summary>
    protected virtual void RestoreDefaultsNoConfirm()
    {
        foreach (var property in ConfigProperties)
        {
            if (property.GetCustomAttribute<ConfigIgnoreRestoreDefaultsAttribute>() != null) continue;

            var defaultValue = GetDefaultValue<object?>(property.Name);
            property.SetValue(null, defaultValue);
        }

        Save();
        OnConfigReloaded?.Invoke();
    }

    public abstract void SetupConfigUI(Control optionContainer);

    private void Init()
    {
        foreach (var property in ConfigProperties)
        {
            _defaultValues.TryAdd(property.Name, property.GetValue(null));
        }

        if (File.Exists(_path))
        {
            Load();
        }
        else if (_legacyPath is { } legacyPath && File.Exists(legacyPath))
        {
            BaseLibMain.Logger.Info(
                $"Migrating config for {_modConfigName} from legacy file {legacyPath} to {_path}. " +
                "The legacy file is no longer written and is kept as a backup.");
            // Persist the migrated values right away, so the migration only happens once. LoadInternal already
            // re-saves by itself when the file had missing or invalid values.
            if (LoadFrom(legacyPath) && !File.Exists(_path)) Save();
        }
        else
        {
            Save(); // Save default values
        }
    }

    public void Changed()
    {
        ConfigChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Static helper to reload your mod's config, for when you don't have easy access to the instance.
    /// </summary>
    /// <typeparam name="T">Your ModConfig subclass</typeparam>
    /// <example><code>
    /// internal class MyConfig : SimpleModConfig { ... }
    /// ModConfig.Load&lt;MyConfig&gt;();
    /// </code></example>
    public static void Load<T>() where T : ModConfig
    {
        ModConfigRegistry.Get<T>()?.Load();
    }

    /// <summary>
    /// <para>Save after <paramref name="delayMs"/> ms, unless SaveDebounced was called again during the delay, in which
    /// case the old request is canceled and the new one replaces it.</para>
    /// <para>Note that you ONLY ever need to call this if you yourself modify the properties at runtime. BaseLib will
    /// always save when the user changes something in the settings screen.</para>
    /// </summary>
    /// <typeparam name="T">Your ModConfig subclass</typeparam>
    /// <example><code>
    /// internal class MyConfig : SimpleModConfig { ... }
    /// ModConfig.SaveDebounced&lt;MyConfig&gt;();
    /// </code></example>
    public static void SaveDebounced<T>(int delayMs = 1000) where T : ModConfig
    {
        ModConfigRegistry.Get<T>()?.SaveDebounced(delayMs);
    }

    /// <summary>
    /// <para>Save after <paramref name="delayMs"/> ms, unless SaveDebounced was called again during the delay, in which case
    /// the old request is canceled and the new one replaces it.</para>
    /// <para>Note that you ONLY ever need to call this if you yourself modify the properties at runtime. BaseLib will always
    /// save when the user changes something in the settings screen.</para>
    /// </summary>
    public void SaveDebounced(int delayMs = 1000) => SaveDebouncedInternal(delayMs);

    private async void SaveDebouncedInternal(int delayMs = 1000)
    {
        try
        {
            // Cancel the previous request, if any, prior to replacing it; lock to prevent a race condition
            // when SaveDebounced is called from multiple threads simultaneously
            CancellationToken token;
            lock (_debounceLock)
            {
                _saveDebounceToken?.Cancel();
                _saveDebounceToken = new CancellationTokenSource();
                token = _saveDebounceToken.Token;
            }

            await Task.Delay(delayMs, token);

            if (!await _configFileLock.WaitAsync(TimeSpan.FromSeconds(3), token))
            {
                BaseLibMain.Logger.Warn($"Timed out waiting for {_modConfigName} save lock in SaveDebounced; skipping save");
                return;
            }

            try
            {
                SaveInternal();
            }
            finally
            {
                _configFileLock.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // Another save request came in to replace this one; this is fully expected
        }
        catch (Exception ex)
        {
            ModConfigLogger.Error($"Failed to save config for {_modConfigName}: {ex.Message}");
        }
    }

    /// <summary>
    /// Immediately save the current configuration to disk. Prefer using <see cref="SaveDebounced"/> (on the instance or
    /// its static variant) instead unless you absolutely must save *now*. Indeed, using SaveDebounced(0) is likely still
    /// better than calling this directly.<br/>
    /// </summary>
    public void Save()
    {
        if (!_configFileLock.Wait(TimeSpan.FromSeconds(3)))
        {
            BaseLibMain.Logger.Warn($"Timed out waiting to save config {_modConfigName}.");
            return;
        }

        try { SaveInternal(); }
        finally { _configFileLock.Release(); }
    }

    private void SaveInternal()
    {
        Dictionary<string, string> values = [];

        // Convert all the values. A single property that cannot be converted (no TypeConverter, throwing getter,
        // a property hidden with 'new', ...) must not stop the remaining settings from being written: the config
        // would silently never hit the disk again.
        foreach (var property in ConfigProperties)
        {
            try
            {
                var value = property.GetValue(null);
                var converter = TypeDescriptor.GetConverter(property.PropertyType);
                var stringValue = converter.ConvertToInvariantString(value);

                if (stringValue != null)
                {
                    if (!values.TryAdd(property.Name, stringValue))
                    {
                        // A derived class hid a config property with 'new'; only the first value encountered is
                        // saved (property enumeration order is unspecified).
                        ModConfigLogger.Warn(
                            $"{_modConfigName} has more than one config property named {property.Name}; " +
                            "only one value is saved. Rename the hidden property or mark it [ConfigIgnore].");
                    }
                }
                else
                {
                    ModConfigLogger.Warn(
                        $"Failed to convert {_modConfigName} property {property.Name} to string for saving; " +
                        "it will be omitted.");
                }
            }
            catch (Exception e)
            {
                ModConfigLogger.Warn(
                    $"Failed to convert {_modConfigName} property {property.Name} to string for saving; " +
                    $"it will be omitted. Error: {e.Message}");
            }
        }

        // Serialize to JSON and write to the config file
        // To prevent corruption on write failures, we first write to a temporary file, and if successful,
        // replace the config file with the temporary file.
        var tempFile = _path + ".new";
        try
        {
            new FileInfo(_path).Directory?.Create();

            using (var fileStream = File.Create(tempFile))
            {
                JsonSerializer.Serialize(fileStream, values, JsonOptions);
            }

            File.Move(tempFile, _path, overwrite: true);
        }
        catch (Exception e)
        {
            ModConfigLogger.Error($"Failed to save config {_modConfigName}: {e.Message}");
        }
        finally
        {
            try
            {
                File.Delete(tempFile);
            }
            catch  { /* ignore */ }
        }
    }

    public void Load()
    {
        LoadFrom(_path);
    }

    /// <summary>
    /// Loads the config file at <paramref name="filePath"/> into the properties. Returns false when the file could
    /// not be read at all (lock timeout, missing file, unexpected I/O error); empty or corrupt files are handled as
    /// soft errors and reported through <see cref="ModConfigLogger"/>.
    /// </summary>
    private bool LoadFrom(string filePath)
    {
        if (!_configFileLock.Wait(TimeSpan.FromSeconds(3)))
        {
            BaseLibMain.Logger.Warn($"Timed out waiting to load config {_modConfigName}.");
            return false;
        }

        try
        {
            return LoadInternal(filePath);
        }
        finally
        {
            _configFileLock.Release();
        }
    }

    private bool LoadInternal(string filePath)
    {
        if (!File.Exists(filePath))
        {
            ModConfigLogger.Error($"Load for {_modConfigName} failed. File not found: {filePath}");
            return false;
        }

        // Missing fields or bad values (will overwrite the config if true)
        var hasSoftErrors = false;

        try
        {
            Dictionary<string, string>? values;
            using (var fileStream = File.OpenRead(filePath))
            {
                values = JsonSerializer.Deserialize<Dictionary<string, string>>(fileStream);
            }

            if (values == null)
            {
                ModConfigLogger.Warn($"Config file {_modConfigName} was empty or null. Will re-save using default values.");
                hasSoftErrors = true;
            }
            else
            {
                foreach (var property in ConfigProperties)
                {
                    if (!values.TryGetValue(property.Name, out var value))
                    {
                        // Missing value; might be due to a new mod version, etc. Re-save later to fill it in.
                        ModConfigLogger.Warn($"Config {_modConfigName} has no value for {property.Name}; will re-save to fill in the default.");
                        hasSoftErrors = true;
                        continue;
                    }

                    if (!TryApplyPropertyValue(property, value)) hasSoftErrors = true;
                }

                if (hasSoftErrors)
                    ModConfigLogger.Warn($"Loaded config {_modConfigName} with some missing or invalid fields.");
            }
        }
        catch (JsonException)
        {
            var fileInfo = new FileInfo(filePath);

            if (!fileInfo.Exists || fileInfo.Length == 0)
            {
                ModConfigLogger.Warn($"Config file {_modConfigName} was missing or empty, likely due to a previous crash. Restoring config to defaults.");
            }
            else
            {
                var brokenPath = filePath + ".broken";
                try
                {
                    File.Move(filePath, brokenPath, overwrite: true);
                }
                catch { /* ignore */ }

                ModConfigLogger.Error(
                    $"Failed to parse mod configuration for {_modConfigName}; the file is corrupt.\n" +
                    $"The broken file was backed up to: {brokenPath}\n" +
                    $"The mod's configuration has been restored to default.");
            }

            hasSoftErrors = true;
        }
        catch (Exception e)
        {
            ModConfigLogger.Error($"Unexpected error loading config {_modConfigName}: {e.Message}");
            return false;
        }

        if (!hasSoftErrors) return true;

        // If we got here via Load()/LoadFrom() we hold _configFileLock, so we can't call Save() here, or it will deadlock
        ModConfigLogger.Warn($"Saving fresh config for {_modConfigName} to correct soft errors (missing fields, invalid fields).");
        SaveInternal();
        return true;
    }

    // Convert a single value and update the property. Return true on success, false on failure.
    private static bool TryApplyPropertyValue(PropertyInfo property, string value)
    {
        try
        {
            var converter = TypeDescriptor.GetConverter(property.PropertyType);
            var configVal = converter.ConvertFromInvariantString(value);

            if (configVal == null)
            {
                ModConfigLogger.Warn($"Failed to load saved config value \"{value}\" for property {property.Name}:" +
                                     "Converter returned null.");
                return false;
            }

            var oldVal = property.GetValue(null);
            if (!configVal.Equals(oldVal))
            {
                property.SetValue(null, configVal);
            }

            return true;
        }
        catch (Exception ex)
        {
            ModConfigLogger.Warn($"Failed to load saved config value \"{value}\" for property {property.Name}. " +
                                 $"Error: {ex.Message}");
            return false;
        }
    }

    protected string GetLabelText(string labelName)
    {
        return GetLabelText(labelName, true);
    }
    protected string GetLabelText(string labelName, bool slugify)
    {
        var key = slugify ? StringHelper.Slugify(labelName) : labelName;
        var loc = LocString.GetIfExists("settings_ui", $"{ModPrefix}{key}.title");
        return loc != null ? loc.GetFormattedText() : labelName;
    }

    protected static string GetBaseLibLabelText(string labelName)
    {
        var loc = LocString.GetIfExists("settings_ui", $"BASELIB-{StringHelper.Slugify(labelName)}.title");
        return loc != null ? loc.GetFormattedText() : labelName;
    }

    /// <summary>
    /// Creates a raw control, with no layout (label, margins), no automatic hover tip, etc.<br/>
    /// Use the Create*Option methods instead unless you need a custom layout (or use them, and customize them).
    /// </summary>
    /// <param name="property">The property this control is bound to. Fetch with e.g. GetType().GetProperty() in
    /// a ModConfig.</param>
    protected NConfigTickbox CreateRawTickboxControl(PropertyInfo property)
    {
        var tickbox = new NConfigTickbox();
        tickbox.Initialize(this, property);
        return tickbox;
    }

    /// <inheritdoc cref="CreateRawTickboxControl"/>
    protected NConfigSlider CreateRawSliderControl(PropertyInfo property)
    {
        var slider = new NConfigSlider();
        slider.Initialize(this, property);
        return slider;
    }

    /// <inheritdoc cref="CreateRawTickboxControl"/>
    protected NConfigLineEdit CreateRawLineEditControl(PropertyInfo property)
    {
        var lineEdit = new NConfigLineEdit();
        lineEdit.Initialize(this, property);
        return lineEdit;
    }

    /// <inheritdoc cref="CreateRawTickboxControl"/>
    protected NConfigColorPicker CreateRawColorPickerControl(PropertyInfo property)
    {
        var colorPicker = new NConfigColorPicker();
        colorPicker.Initialize(this, property);
        return colorPicker;
    }

    /// <summary>
    /// Creates a raw button control. You may want <see cref="SimpleModConfig.CreateButton" /> instead.
    /// </summary>
    /// <param name="labelText">The text to place on the button</param>
    /// <param name="onPressed">Action to perform when the user clicks/presses the button.</param>
    protected NConfigButton CreateRawButtonControl(string labelText, Action onPressed)
    {
        var button = new NConfigButton();
        button.Initialize(labelText, onPressed);
        return button;
    }

    /// <inheritdoc cref="CreateRawTickboxControl"/>
    protected NDropdownPositioner CreateRawDropdownControl(PropertyInfo property)
    {
        var dropdown = new NConfigDropdown();
        dropdown.Initialize(this, property, ModPrefix, Changed);
        dropdown.SetFromProperty();

        var dropdownPositioner = new NDropdownPositioner();
        dropdownPositioner.SetCustomMinimumSize(new(324, 64));
        dropdownPositioner.FocusMode = Control.FocusModeEnum.None;
        dropdownPositioner.SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd;
        dropdownPositioner.SizeFlagsVertical = Control.SizeFlags.Fill;

        dropdownPositioner._dropdownNode = dropdown;

        dropdownPositioner.AddChild(dropdown);
        dropdownPositioner.MouseFilter = Control.MouseFilterEnum.Ignore;

        return dropdownPositioner;
    }

    /// <inheritdoc cref="CreateRawTickboxControl"/>
    public static MegaRichTextLabel CreateRawLabelControl(string labelText, int fontSize)
    {
        var kreonNormal = PreloadManager.Cache.GetAsset<Font>("res://themes/kreon_regular_shared.tres");
        var kreonBold = PreloadManager.Cache.GetAsset<Font>("res://themes/kreon_bold_shared.tres");

        MegaRichTextLabel label = new()
        {
            Name = "Label",
            Theme = PreloadManager.Cache.GetAsset<Theme>(SettingsTheme),
            AutoSizeEnabled = false,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            FocusMode = Control.FocusModeEnum.None,
            BbcodeEnabled = true,
            ScrollActive = false,
            VerticalAlignment = VerticalAlignment.Center,
            Text = labelText
        };

        label.AddThemeFontOverride("normal_font", kreonNormal);
        label.AddThemeFontOverride("bold_font", kreonBold);
        label.AddThemeFontSizeOverrideAll(fontSize);

        return label;
    }

    protected static ColorRect CreateDividerControl()
    {
        return new ColorRect
        {
            Name = "Divider",
            CustomMinimumSize = new Vector2(0, 2),
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Color = new Color(0.909804f, 0.862745f, 0.745098f, 0.25098f)
        };
    }

    public static void ShowAndClearPendingErrors()
    {
        var pendingMessages = ModConfigLogger.PendingUserMessages;
        if (pendingMessages.Count <= 0) return;

        var errorPopup = NErrorPopup.Create("Mod configuration error",
            string.Join('\n', pendingMessages), false);
        if (errorPopup == null || NModalContainer.Instance == null) return;
        NModalContainer.Instance.Add(errorPopup);

        var vertPopup = errorPopup.GetNodeOrNull<NVerticalPopup>("VerticalPopup");
        if (vertPopup == null) return;
        vertPopup.BodyLabel.AddThemeFontSizeOverrideAll(22);

        pendingMessages.Clear();
    }

    [GeneratedRegex("[^a-zA-Z0-9_.]")]
    private static partial Regex SpecialCharRegex();
}