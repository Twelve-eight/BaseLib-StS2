using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using BaseLib.Patches.Saves;
using BaseLib.Patches.Utils;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace BaseLib.Utils;

/// <summary>
/// Marks a SpireField whose value will be copied if it has been set.
/// SpireFields utilizing this interface add themselves to <see cref="CloneFields"/>
/// if their Clone method should be called.
/// </summary>
public interface ICloneableField
{
    private static NotNullSpireField<AbstractModel, HashSet<ICloneableField>> CloneFields = new(() => []);

    /// <summary>
    /// Adds an ICloneableField to the set of fields whose Clone method will be called when the model is cloned.
    /// </summary>
    public static void AddClonedField(AbstractModel model, ICloneableField field)
    {
        CloneFields[model].Add(field);
    }
    
    /// <summary>
    /// Copies this field's data from a source model to a destination model when the model is cloned.
    /// </summary>
    public void Clone(AbstractModel src, AbstractModel dst);
    
    [HarmonyPatch(typeof(AbstractModel), nameof(AbstractModel.MutableClone))]
    private static class CloneSpireFields {
        [HarmonyPostfix]
        static void ModifyResult(AbstractModel __instance, AbstractModel __result)
        {
            var cloneFields = CloneFields[__instance];
            foreach (var cloneableField in cloneFields)
            {
                cloneableField.Clone(__instance, __result);
            }
        }
    }
}

/// <summary>
/// A basic wrapper around <seealso cref="ConditionalWeakTable{TKey, TValue}"/> for convenience.
/// While this can be used to store value types, they will be boxed and thus is somewhat inefficient.
/// </summary>
public class SpireField<TKey, TVal> : ICloneableField where TKey : class
{
    private readonly ConditionalWeakTable<TKey, object?> _table = [];
    private readonly Func<TKey, TVal?> _defaultVal;

    public SpireField(Func<TVal?> defaultVal)
    {
        _defaultVal = _ => defaultVal();
    }

    public SpireField(Func<TKey, TVal?> defaultVal)
    {
        _defaultVal = defaultVal;
    }

    /// <summary>
    /// Causes this SpireField's value to be copied when the model it is attached to is cloned. Only valid for
    /// SpireFields attached to types inheriting from AbstractModel.
    /// The value will only be copied over if it has been set at least once outside of the default value, or if the
    /// default value is a reference type and its value has been retrieved at least once.
    /// Note that this is a shallow clone; reference types will be assigned directly to the new instance, not copied.
    /// Optional cloneVal parameter will change how the value is copied to the new instance.
    /// </summary>
    /// <param name="cloneVal">A function to copy the value to the new model. Receives the source model,
    /// destination model, and the field's value for the source model. Should call Set on this SpireField with the
    /// destination model or otherwise copy values over.</param>
    public SpireField<TKey, TVal> CopyOnClone(Action<TKey, TKey, TVal?>? cloneVal = null)
    {
        if (!typeof(TKey).IsAssignableTo(typeof(AbstractModel)))
        {
            throw new InvalidOperationException(
                $"Cannot enable CopyOnClone for SpireField on type {typeof(TKey).Name}; only valid for SpireFields attached to AbstractModel types.");
        }
        _cloneFunc = cloneVal ?? ((_, dst, val) => Set(dst, val));
        return this;
    }

    private Action<TKey, TKey, TVal?>? _cloneFunc;
    /// <summary>
    /// Returns true if this SpireField's value should be copied over when a model it is attached to is cloned.
    /// </summary>
    public bool ShouldClone => _cloneFunc != null;

    /// <summary>
    /// Copies this SpireField's value from one AbstractModel to another.
    /// Only usable if this SpireField is attached to a model type.
    /// </summary>
    public void Clone(AbstractModel src, AbstractModel dst)
    {
        if (src is not TKey srcKey || dst is not TKey dstKey)
            throw new ArgumentException(
                $"Unable to clone SpireField on type {typeof(TKey).Name} from {src.GetType().Name} to {dst.GetType().Name}.");
        if (!ShouldClone) return;
        _cloneFunc!(srcKey, dstKey, Get(srcKey));
    }

    public TVal? this[TKey obj]
    {
        get => Get(obj);
        set => Set(obj, value);
    }

    public TVal? Get(TKey obj) {
        if (_table.TryGetValue(obj, out var result)) return (TVal?)result;

        _table.Add(obj, result = _defaultVal(obj));
        if (ShouldClone && !typeof(TVal).IsValueType && obj is AbstractModel model)
        {
            ICloneableField.AddClonedField(model, this);
        }
        return (TVal?)result;
    }

    public void Set(TKey obj, TVal? val)
    {
        _table.AddOrUpdate(obj, val);
        if (ShouldClone && obj is AbstractModel model)
        {
            ICloneableField.AddClonedField(model, this);
        }
    }
}

/// <summary>
/// A SpireField containing an object whose value is guaranteed to not be null.
/// </summary>
public class NotNullSpireField<TKey, TVal> : ICloneableField where TKey : class where TVal : class
{
    private readonly ConditionalWeakTable<TKey, TVal> _table = [];
    private readonly Func<TKey, TVal> _defaultVal;
    
    public NotNullSpireField(Func<TVal> defaultVal)
    {
        _defaultVal = _ => defaultVal();
    }

    public NotNullSpireField(Func<TKey, TVal> defaultVal)
    {
        _defaultVal = defaultVal;
    }

    /// <summary>
    /// Causes this SpireField's value to be copied when the model it is attached to is cloned. Only valid for
    /// SpireFields attached to types inheriting from AbstractModel.
    /// The value will only be copied over if it has been set at least once outside of the default value, or if the
    /// default value is a reference type.
    /// Note that this is a shallow clone; reference types will be assigned directly to the new instance, not copied.
    /// Optional cloneVal parameter will change how the value is copied to the new instance.
    /// </summary>
    public NotNullSpireField<TKey, TVal> CopyOnClone(Action<TKey, TKey, TVal>? cloneVal = null)
    {
        if (!typeof(TKey).IsAssignableTo(typeof(AbstractModel)))
        {
            throw new InvalidOperationException(
                $"Cannot enable CopyOnClone for SpireField on type {typeof(TKey).Name}; only valid for SpireFields attached to AbstractModel types.");
        }
        _cloneFunc = cloneVal ?? ((_, dst, val) => Set(dst, val));
        return this;
    }

    private Action<TKey, TKey, TVal>? _cloneFunc;
    /// <summary>
    /// Returns true if this SpireField's value should be copied over when a model it is attached to is cloned.
    /// </summary>
    public bool ShouldClone => _cloneFunc != null;

    /// <summary>
    /// Copies this SpireField's value from one AbstractModel to another.
    /// Only usable if this SpireField is attached to a model type.
    /// </summary>
    public void Clone(AbstractModel src, AbstractModel dst)
    {
        if (src is not TKey srcKey || dst is not TKey dstKey)
            throw new ArgumentException(
                $"Unable to clone SpireField on type {typeof(TKey).Name} from {src.GetType().Name} to {dst.GetType().Name}.");
        if (!ShouldClone) return;

        _cloneFunc!(srcKey, dstKey, Get(srcKey));
    }
    
    public TVal this[TKey obj]
    {
        get => Get(obj);
        set => Set(obj, value);
    }

    public TVal Get(TKey obj) {
        if (_table.TryGetValue(obj, out var result)) return result;

        var defaultVal = _defaultVal(obj);
        
        _table.Add(obj, defaultVal);
        if (ShouldClone && !typeof(TVal).IsValueType && obj is AbstractModel model)
        {
            ICloneableField.AddClonedField(model, this);
        }
        return defaultVal;
    }

    public void Set(TKey obj, TVal val)
    {
        _table.AddOrUpdate(obj, val);
        if (ShouldClone && obj is AbstractModel model)
        {
            ICloneableField.AddClonedField(model, this);
        }
    }
}

public class ReadonlySpireField<TKey, TVal> : NotNullSpireField<TKey, TVal> where TKey : class where TVal : class
{
    /// <inheritdoc />
    public ReadonlySpireField(Func<TVal> defaultVal) : base(defaultVal)
    {
        
    }

    /// <inheritdoc />
    public ReadonlySpireField(Func<TKey, TVal> defaultVal) : base(defaultVal)
    {
        
    }
    
    /// <summary>
    /// Throws an exception if called.
    /// </summary>
    [Obsolete("ReadonlySpireField cannot be set; exception will be thrown.")]
    public new void Set(TKey obj, TVal? val)
    {
        throw new InvalidOperationException("The value of a ReadonlySpireField should not be set. If possible, modify its current value instead.");
    }
}

internal interface IAddedNodes<TParentType> where TParentType : Node
{
    protected static List<IAddedNodes<TParentType>> _addedNodes = [];
    private static bool _patched = false;

    Node? GetNode(TParentType obj);
        
    protected static void PatchNodeReady()
    {
        if (_patched) return;
        _patched = true;
        
        BaseLibMain.Logger.Info($"Patching type {typeof(TParentType).Name} to add nodes.");
        
        var harmony = BaseLibMain.MainHarmony;
        var method = AccessTools.DeclaredMethod(typeof(TParentType), "_Ready", []);

        if (method != null)
        {
            var unconditionalMethod = typeof(IAddedNodes<TParentType>).DeclaredMethod(nameof(UnconditionalAdd));
            BaseLibMain.Logger.Info($"Adding postfix {unconditionalMethod.FullDescription()}");
            harmony.Patch(method, postfix: unconditionalMethod);
            return;
        }
        
        method = AccessTools.Method(typeof(TParentType), "_Ready", []);

        if (method == null)
        {
            BaseLibMain.Logger.Error($"Failed to patch _Ready method for type {typeof(TParentType).Name} to add nodes; _Ready method not found.");
            return;
        }

        var conditionalMethod = typeof(IAddedNodes<TParentType>).DeclaredMethod(nameof(ConditionalAdd));
        BaseLibMain.Logger.Info($"Adding postfix {conditionalMethod.FullDescription()}");
        harmony.Patch(method, postfix: conditionalMethod);
    }

    private static void UnconditionalAdd(TParentType __instance)
    {
        foreach (var add in _addedNodes)
        {
            var child = add.GetNode(__instance);
            if (__instance.IsAncestorOf(child)) continue;
            __instance.AddChild(child);
        }
    }

    private static void ConditionalAdd(object __instance)
    {
        if (__instance is not TParentType parent) return;
        UnconditionalAdd(parent);
    }
}

/// <summary>
/// Adds a node as a child to all instances of the specified parent node type.
/// </summary>
public class AddedNode<TParentType, TNode> : ReadonlySpireField<TParentType, TNode>, IAddedNodes<TParentType> where TParentType : Node where TNode : Node
{
    
    public AddedNode(Func<TParentType, TNode> defaultVal) : base(defaultVal)
    {
        IAddedNodes<TParentType>._addedNodes.Add(this);
        IAddedNodes<TParentType>.PatchNodeReady();
    }

    /// <summary>
    /// An AddedNode that adds a specific scene as a child.
    /// </summary>
    /// <param name="scenePath">.tscn resource file path of the scene to instantiate.</param>
    /// <param name="extraSetup">If additional properties of the scene need to be set up before it is added as a child,
    /// or to add it as a child in a specific way.</param>
    public AddedNode(string scenePath, Action<TParentType, TNode>? extraSetup = null) :
        this(parent =>
        {
            var scene = ResourceLoader.Load<PackedScene>(scenePath).Instantiate<TNode>();
            extraSetup?.Invoke(parent, scene);
            return scene;
        })
    { }

    public Node? GetNode(TParentType obj)
    {
        return Get(obj);
    }
}

internal interface ISavedSpireField
{
    public bool IsBasegameSupported { get; }

    /// <summary>
    /// Save key of this field, namespaced by the id of the mod that created it.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Save key this field used before keys were namespaced by mod id. Still read as a fallback and written
    /// alongside <see cref="Name"/>, so saves stay readable by older BaseLib versions and vice versa.
    /// </summary>
    string LegacyName { get; }

    Type TargetType { get; }
    void Export(object model, SavedProperties props);
    void Import(object model, SavedProperties props);
    bool RegisterCustomSave();
}

/// <summary>
/// A SpireField whose value will automatically be saved and loaded.
/// Only functions on model types that support SavedProperty, so mainly just cards and relics.
/// </summary>
/// <remarks>
/// Save keys are namespaced by the id of the mod that created the field (<c>SomeMod:CardModel_charge</c>), so two
/// mods may use the same field name on the same model type without reading each other's values.
/// <see cref="LegacyName"/> (the key used before namespacing, <c>CardModel_charge</c>) is read as a fallback and
/// written next to the namespaced key. Saves made by an older BaseLib therefore load with their values, and saves
/// written by this version still load after a rollback to an older BaseLib.
/// </remarks>
public class SavedSpireField<TKey, TVal> : SpireField<TKey, TVal>, ISavedSpireField where TKey : class
{
    /// <summary>
    /// Separates the mod id from the type/field name in <see cref="Name"/>. Neither a mod id (used as a
    /// resource directory name) nor a C# type or member name can contain it.
    /// </summary>
    private const char ModIdSeparator = ':';

    private static readonly object PrimaryImportMarker = new();

    private readonly Type[] _ownerCandidateTypes;
    private readonly ConditionalWeakTable<TKey, object> _primaryImports = [];

    private string? _modIdOverride;
    private string? _name;

    public SavedSpireField(Func<TVal?> defaultVal, string name) : this(_ => defaultVal(), name) { }

    public SavedSpireField(Func<TKey, TVal?> defaultVal, string name) : base(defaultVal)
    {
        LegacyName = $"{typeof(TKey).Name}_{name}";
        _ownerCandidateTypes = ResolveOwnerCandidates();

        IsBasegameSupported = SavePatchUtils.IsStoreTypeBaseSupported(typeof(TVal)) &&
                              SavePatchUtils.IsHolderTypeBaseSupported(typeof(TKey));

        SavedSpireFieldPatch.Register(this);
    }

    public bool IsBasegameSupported { get; init; }

    /// <summary>
    /// The key this field is saved under: the owning mod's id, <see cref="ModIdSeparator"/>, then
    /// <see cref="LegacyName"/>.
    /// </summary>
    /// <remarks>
    /// Resolved on first use and cached as soon as the answer is final. BaseLib finalizes it during post-mod init,
    /// after every mod has been loaded and before anything is saved or loaded, so the value is stable for the rest
    /// of the session. A field with no mod on its creation stack keeps using <see cref="LegacyName"/> and warns;
    /// a field created by an assembly that is not registered to a mod keeps that assembly name as its prefix and
    /// warns. Both warnings are logged once: the result is cached as soon as it is final.
    /// </remarks>
    public string Name
    {
        get
        {
            if (_name != null) return _name;

            if (TryResolveModId(SavedSpireFieldPatch.ModsLoaded, out string modId))
            {
                _name = FormatName(modId);
                return _name;
            }

            // The mod list is still being built, so this is only a guess and must not be cached: a mod is added to
            // the loaded mod list after its initializer, and therefore after any field it creates.
            return FormatName(modId);
        }
    }

    /// <summary>
    /// The key this field used before save keys were namespaced by mod id, <c>{TKey type name}_{name}</c>.
    /// </summary>
    public string LegacyName { get; }

    public Type TargetType { get; } = typeof(TKey);

    /// <summary>
    /// Explicitly sets the mod id used as the prefix of <see cref="Name"/>.
    /// Only needed when the owning mod cannot be detected automatically, for example when the field is created by
    /// a helper assembly that is not registered to a mod. Must be called before the field is first saved, i.e.
    /// during mod initialization; values set later do not update the already-registered network property names.
    /// </summary>
    public SavedSpireField<TKey, TVal> WithModId(string modId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        if (modId.Contains(ModIdSeparator))
            throw new ArgumentException($"A mod id cannot contain '{ModIdSeparator}'.", nameof(modId));

        _modIdOverride = modId;
        _name = null;
        return this;
    }

    private string FormatName(string modId) =>
        modId.Length == 0 ? LegacyName : $"{modId}{ModIdSeparator}{LegacyName}";

    /// <summary>
    /// Used to serialize value over net when custom save is used (value/target type not compatible with SavedProperty)
    /// </summary>
    public Action<TVal, PacketWriter>? Serializer { get; set; } = null;
    /// <summary>
    /// Used to deserialize value over net when custom save is used (value/target type not compatible with SavedProperty)
    /// </summary>
    public Func<PacketReader, TVal>? Deserializer { get; set; } = null;

    /// <summary>
    /// Store value in a SavedProperties instance.
    /// Writes the namespaced key and, unless it is the same, the pre-namespacing key.
    /// </summary>
    public void Export(object model, SavedProperties props)
    {
        string name = Name;
        TVal? value = Get((TKey)model);

        AddToProperties(props, name, value);
        if (!string.Equals(name, LegacyName, StringComparison.Ordinal))
            AddToProperties(props, LegacyName, value);
    } 

    /// <summary>
    /// Load value from a SavedProperties instance.
    /// The namespaced key wins; the pre-namespacing key is only used when the namespaced key is absent, which is
    /// the case for saves made by BaseLib versions without mod-namespaced keys.
    /// </summary>
    public void Import(object model, SavedProperties props)
    {
        if (TryGetFromProperties<TVal>(props, Name, out var val))
        {
            Set((TKey)model, val);
            return;
        }

        if (TryGetFromProperties<TVal>(props, LegacyName, out var legacyVal))
            Set((TKey)model, legacyVal);
    }

    public bool RegisterCustomSave()
    {
        Action<TVal, PacketWriter>? serializer = Serializer;
        Func<PacketReader, TVal>? deserializer = Deserializer;
        if (serializer == null || deserializer == null)
        {
            if (typeof(TVal).IsAssignableTo(typeof(IPacketSerializable)))
            {
                serializer = (val, writer) => ((IPacketSerializable)val!).Serialize(writer);
                deserializer = (reader) =>
                {
                    var val = (TVal)typeof(TVal).CreateInstance();
                    ((IPacketSerializable)val).Deserialize(reader);
                    return val;
                };
            }
            else if (!SavePatchUtils.TryGetSerializerDeserializer(out serializer, out deserializer))
            {
                BaseLibMain.Logger.Error($"Unable to register custom save for SavedSpireField {Name}; no serialization defined for " +
                                         $"type {typeof(TVal).Name}. Set Serializer/Deserializer properties of SavedSpireField.");
                return false;
            }
        }

        if (!ExtendedSaveTypes.IsSaveTypeSupported(typeof(TVal)))
        {
            throw new ArgumentException($"Type {typeof(TVal).Name} is not registered for saving; register the type with ExtendedSaveTypes in your mod's initializer.");
        }

        string name = Name;
        if (!ExtendedSaveTypes.RegisterSavedValue<TKey, TVal>($"spirefield_{name}", Get, ImportPrimary, serializer, deserializer))
            return false;

        if (string.Equals(name, LegacyName, StringComparison.Ordinal)) return true;

        // Old saves stored this value under the pre-namespacing id. Register that id as well so those saves keep
        // their values, and keep writing it so those values survive a rollback to a BaseLib without namespaced ids.
        ExtendedSaveTypes.RegisterSavedValue<TKey, TVal>($"spirefield_{LegacyName}", Get, ImportLegacy, serializer, deserializer);
        return true;
    }

    private void ImportPrimary(TKey model, TVal? val)
    {
        _primaryImports.AddOrUpdate(model, PrimaryImportMarker);
        Set(model, val);
    }

    private void ImportLegacy(TKey model, TVal? val)
    {
        // Saved values are applied in id order, which cannot be controlled relative to the namespaced id.
        // The namespaced id always belongs to this field; the legacy id may hold another mod's value for the same
        // field name, so it must never overwrite a value that the namespaced id already provided.
        if (_primaryImports.TryGetValue(model, out _)) return;
        Set(model, val);
    }

    /// <summary>
    /// The types whose methods created this field, innermost frame first, with BaseLib, the game and the shared host
    /// assemblies removed. Kept as a list because the owning mod cannot be determined yet: a mod is only registered
    /// in <see cref="WhatMod"/> after its initializer has run, which is after any field it constructs. Having every
    /// frame lets the caller mod be found behind a shared helper assembly (see <see cref="TryResolveModId"/>).
    /// </summary>
    private static Type[] ResolveOwnerCandidates()
    {
        Assembly baseLibAssembly = typeof(SavedSpireField<,>).Assembly;
        Assembly gameAssembly = typeof(AbstractModel).Assembly;

        List<Type> candidates = [];
        HashSet<Assembly> seen = [];

        var trace = new StackTrace(1, false);
        for (int i = 0; i < trace.FrameCount; i++)
        {
            Type? declaringType = trace.GetFrame(i)?.GetMethod()?.DeclaringType;
            if (declaringType == null) continue;

            Assembly assembly = declaringType.Assembly;
            if (assembly == baseLibAssembly || assembly == gameAssembly) continue;
            if (IsSharedHostAssembly(assembly)) continue;
            if (!seen.Add(assembly)) continue;

            candidates.Add(declaringType);
        }

        return [..candidates];
    }

    /// <summary>
    /// Whether <paramref name="assembly"/> belongs to the runtime, the engine or a patching library that mods are
    /// compiled against rather than to a mod. Such a frame must never be taken for the field's owner: every mod
    /// would then resolve to the same save key prefix, which silently reintroduces save value collisions.
    /// </summary>
    private static bool IsSharedHostAssembly(Assembly assembly)
    {
        if (assembly.IsDynamic) return true;

        string? name = assembly.GetName().Name;
        if (name == null) return true;

        // UNCONFIRMED: these are the assembly names of the runtime, engine and patch libraries seen in this repo; the
        // list is a conservative superset. An unrecognised host assembly does not corrupt anything: it is reported by
        // the "assembly is not registered to a loaded mod" warning instead of being used silently.
        return name is "mscorlib" or "netstandard" or "System" or "0Harmony" or "HarmonyLib" or "GodotSharp"
                   or "Newtonsoft.Json" or "MonoMod"
               || name.StartsWith("System.", StringComparison.Ordinal)
               || name.StartsWith("Microsoft.", StringComparison.Ordinal)
               || name.StartsWith("GodotSharp.", StringComparison.Ordinal)
               || name.StartsWith("MonoMod.", StringComparison.Ordinal)
               || name.StartsWith("Newtonsoft.", StringComparison.Ordinal);
    }

    /// <summary>
    /// Name of the assembly of the innermost creating frame that is not part of the runtime, the engine, BaseLib or
    /// the game, or null when there is no such frame.
    /// </summary>
    private string? OwnerAssemblyName() =>
        _ownerCandidateTypes.Length == 0 ? null : _ownerCandidateTypes[0].Assembly.GetName().Name;

    /// <summary>
    /// Resolves the id of the mod that owns this field.
    /// </summary>
    /// <param name="final">
    /// True once every mod has been loaded. An unmatched field is then reported and its fallback is authoritative.
    /// While mods are still loading, an unmatched field is usually one whose own mod has not been registered yet,
    /// so the answer stays provisional.
    /// </param>
    /// <param name="modId">
    /// The mod id when this returns true. When it returns false this holds the best guess (the innermost creating
    /// assembly name, or an empty string when there is none) which the caller must not cache.
    /// </param>
    private bool TryResolveModId(bool final, out string modId)
    {
        if (!string.IsNullOrWhiteSpace(_modIdOverride))
        {
            modId = _modIdOverride!;
            return true;
        }

        // Innermost frame first: when a mod creates the field through a shared helper, the helper is the assembly
        // the constructor was called from, but the caller mod is the one whose values are being saved.
        foreach (Type candidate in _ownerCandidateTypes)
        {
            string? id = WhatMod.FindMod(candidate)?.manifest?.id;
            if (string.IsNullOrWhiteSpace(id)) continue;

            modId = id!;
            return true;
        }

        // No frame belongs to a mod at all (BaseLib, the game and the shared host assemblies were filtered out), so
        // no mod can ever match and the answer will not change while the remaining mods load.
        if (_ownerCandidateTypes.Length == 0)
        {
            modId = "";
            BaseLibMain.Logger.Warn($"Unable to determine the mod that owns SavedSpireField {LegacyName}; " +
                                    $"it will use the pre-namespacing save key and can collide with other mods. " +
                                    $"Call {nameof(WithModId)} during mod initialization to set the mod id explicitly.");
            return true;
        }

        string? assemblyName = OwnerAssemblyName();
        modId = assemblyName ?? "";
        if (!final) return false;

        BaseLibMain.Logger.Warn($"SavedSpireField {LegacyName} was created by assembly '{assemblyName}', which is not " +
                                $"registered to a loaded mod. Its save key uses that assembly name as the prefix, so " +
                                $"every mod that shares this assembly gets the same prefix and can read each other's " +
                                $"values. Call {nameof(WithModId)} during mod initialization to set the mod id explicitly.");
        return true;
    }

    private static void AddToProperties(SavedProperties props, string name, object? value)
    {
        switch (value)
        {
            case null:
                return;
            case int i:
                (props.ints ??= []).Add(new(name, i));
                break;
            case bool b:
                (props.bools ??= []).Add(new(name, b));
                break;
            case string s:
                (props.strings ??= []).Add(new(name, s));
                break;
            case Enum e:
                (props.ints ??= []).Add(new(name, Convert.ToInt32(e)));
                break;
            case ModelId mid:
                (props.modelIds ??= []).Add(new(name, mid));
                break;
            case SerializableCard card:
                (props.cards ??= []).Add(new(name, card));
                break;
            case int[] iArr:
                (props.intArrays ??= []).Add(new(name, iArr));
                break;
            case Enum[] eArr:
                (props.intArrays ??= []).Add(new(name, eArr.Select(Convert.ToInt32).ToArray()));
                break;
            case SerializableCard[] cArr:
                (props.cardArrays ??= []).Add(new(name, cArr));
                break;
            case List<SerializableCard> cList:
                (props.cardArrays ??= []).Add(new(name, cList.ToArray()));
                break;
        }
    }

    /// <summary>
    /// Reads a saved value by name.
    /// </summary>
    /// <remarks>
    /// <c>SavedProperty&lt;T&gt;</c> is a struct, so <c>props.ints?.FirstOrDefault(...)</c> is a
    /// <c>Nullable&lt;SavedProperty&lt;T&gt;&gt;</c> that is null ONLY when the whole list is absent
    /// from the save. When the list exists but has no entry with this name, FirstOrDefault returns
    /// <c>default</c>, which lifts to a <em>non-null</em> nullable - so a missing entry used to be
    /// reported as "present with default values". Every branch therefore also checks
    /// <c>name == null</c>, which can only happen for the struct default.
    /// A matching entry whose <c>value</c> is null is treated as absent as well: <c>AddToProperties</c> never writes a
    /// null value, so it can only come from a third-party writer or a damaged save. Using it would replace the
    /// field's default with null (<c>string</c>, <c>ModelId</c>, <c>SerializableCard</c>) or throw in the array
    /// branches (<c>int[]</c>, enum arrays, <c>SerializableCard[]</c>, <c>List&lt;SerializableCard&gt;</c>).
    /// Returning false keeps the field's current value, or falls back to <see cref="LegacyName"/> when the null
    /// entry was stored under <see cref="Name"/>.
    /// </remarks>
    private static bool TryGetFromProperties<T>(SavedProperties props, string name, out T? value)
    {
        value = default;

        if (typeof(T) == typeof(int) || typeof(T).IsEnum)
        {
            var found = props.ints?.FirstOrDefault(p => p.name == name);
            if (found == null || found.Value.name == null) return false; // struct default == not found
            
            value = typeof(T).IsEnum
                ? (T)Enum.ToObject(typeof(T), found.Value.value)
                : (T)(object)found.Value.value;
            return true;
        }
        else if (typeof(T) == typeof(bool))
        {
            var found = props.bools?.FirstOrDefault(p => p.name == name);
            if (found == null || found.Value.name == null) return false; // struct default == not found
            
            value = (T)(object)found.Value.value;
            return true;
        }
        else if (typeof(T) == typeof(string))
        {
            var found = props.strings?.FirstOrDefault(p => p.name == name);
            if (found == null || found.Value.name == null || found.Value.value == null) return false; // struct default or null value == not found
            
            value = (T)(object)found.Value.value;
            return true;
        }
        else if (typeof(T) == typeof(ModelId))
        {
            var found = props.modelIds?.FirstOrDefault(p => p.name == name);
            if (found == null || found.Value.name == null || found.Value.value == null) return false; // struct default or null value == not found
            
            value = (T)(object)found.Value.value;
            return true;
        }
        else if (
            typeof(T) == typeof(int[])
            || (typeof(T).IsArray && typeof(T).GetElementType()!.IsEnum)
        )
        {
            var found = props.intArrays?.FirstOrDefault(p => p.name == name);
            if (found == null || found.Value.name == null || found.Value.value == null) return false; // struct default or null value == not found
            
            if (typeof(T).IsArray && typeof(T).GetElementType()!.IsEnum)
            {
                Type enumType = typeof(T).GetElementType()!;
                Array enumArr = Array.CreateInstance(enumType, found.Value.value.Length);
                for (int i = 0; i < found.Value.value.Length; i++)
                    enumArr.SetValue(Enum.ToObject(enumType, found.Value.value[i]), i);
                value = (T)(object)enumArr;
            }
            else
            {
                value = (T)(object)found.Value.value;
            }
            return true;
        }
        else if (typeof(T) == typeof(SerializableCard))
        {
            var found = props.cards?.FirstOrDefault(p => p.name == name);
            if (found == null || found.Value.name == null || found.Value.value == null) return false; // struct default or null value == not found
            
            value = (T)(object)found.Value.value;
            return true;
        }
        else if (
            typeof(T) == typeof(SerializableCard[])
            || typeof(T) == typeof(List<SerializableCard>)
        )
        {
            var found = props.cardArrays?.FirstOrDefault(p => p.name == name);
            if (found == null || found.Value.name == null || found.Value.value == null) return false; // struct default or null value == not found
            
            value =
                typeof(T) == typeof(List<SerializableCard>)
                    ? (T)(object)found.Value.value.ToList()
                    : (T)(object)found.Value.value;
            return true;
        }
        return false;
    }
}