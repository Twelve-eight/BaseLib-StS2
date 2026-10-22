using System.Collections;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using BaseLib.Extensions;
using BaseLib.Utils;
using BaseLib.Utils.Patching;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace BaseLib.Patches.Saves;

public static class ExtendedSaveHandlers<DataType, SerializableType> where SerializableType : class
{
    public class ExtendedSaveData
    {
        /// <summary>
        /// Dictionaries for each type of data that will be saved.
        /// </summary>
        public readonly Dictionary<Type, IDictionary> Dictionaries = [];
        /// <summary>
        /// Gets or creates the dictionary for a specific type of data.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <returns></returns>
        /// <exception cref="Exception"></exception>
        public Dictionary<string, T> DictForType<T>() => 
            Dictionaries.TryGetValue(typeof(T), out var dict) ? (Dictionary<string, T>)dict : 
            Dictionaries.TryAdd(typeof(T), dict = new Dictionary<string, T>()) ? (Dictionary<string, T>)dict : throw new Exception("Failed to add missing type to dictionary");

        /// <summary>
        /// Creates data from an instance.
        /// Used when serializing.
        /// </summary>
        public ExtendedSaveData(DataType data)
        {
            foreach (var save in RegisteredSaves)
            {
                save.Getter.Invoke(data, this);
            }
        }

        /// <summary>
        /// Creates an empty data holder.
        /// Used for loading from json.
        /// </summary>
        public ExtendedSaveData()
        {
        
        }
    }

    private static NotNullSpireField<SerializableType, ExtendedSaveData>? _extendedData;
    public static NotNullSpireField<SerializableType, ExtendedSaveData> ExtendedData => 
        _extendedData ??= new NotNullSpireField<SerializableType, ExtendedSaveData>(() => new ExtendedSaveData());

    private static List<ExtendedSaveInfo<DataType, ExtendedSaveData>>? _registeredSaves;
    public static List<ExtendedSaveInfo<DataType, ExtendedSaveData>> RegisteredSaves =>
        _registeredSaves ??= [];

    private static Dictionary<Type, Func<JsonSerializerOptions, JsonPropertyInfo>>? _saveValueTypes;
    private static Dictionary<Type, Func<JsonSerializerOptions, JsonPropertyInfo>> SaveValueTypes =>
        _saveValueTypes ??= [];
    
    private static bool _initializedSaveProps;
    
    /// <summary>
    /// Registers a value to be saved attached to a specific serializable type and be copied in its Serialize/Deserialize methods.
    /// </summary>
    /// <param name="id">The ID of the saved value. Should ideally be a unique string involving the mod's ID.</param>
    /// <param name="getter">Gets the value to save given an instance.</param>
    /// <param name="setter">Given a saved value, attaches it to an instance.</param>
    /// <typeparam name="T">A type that implements IPacketSerializable and has a no-parameter constructor.</typeparam>
    public static void RegisterSave<T>(string id, Func<DataType, T?> getter, Action<DataType, T?> setter)
        where T : IPacketSerializable, new()
    {
        RegisterSave<T>(id, getter, setter, 
            (val, writer) => val.Serialize(writer),
            (reader) =>
            {
                var val = new T();
                val.Deserialize(reader);
                return val;
            });
    }


    /// <summary>
    /// Registers a value to be saved attached to a specific serializable type and be copied in its Serialize/Deserialize methods.
    /// </summary>
    /// <param name="id">The ID of the saved value. Should ideally be a unique string involving the mod's ID.</param>
    /// <param name="getter">Gets the value to save given an instance.</param>
    /// <param name="setter">Given a saved value, attaches it to an instance.</param>
    /// <param name="serializer">Writes the saved value with a PacketWriter.</param>
    /// <param name="deserializer">Retrives the saved value from a PacketReader.</param>
    /// <typeparam name="TargetType">The specific type the saved value is attached to.</typeparam>
    /// <typeparam name="T">The saved type.</typeparam>
    public static void RegisterSave<TargetType, T>(string id, Func<TargetType, T?> getter, Action<TargetType, T?> setter,
        Action<T, PacketWriter> serializer, Func<PacketReader, T> deserializer)
    {
        RegisterSave<T>(id,
            enchant => enchant is not TargetType target ? default : getter(target),
            (enchant, val) =>
            {
                if (enchant is not TargetType target) return;
                setter(target, val);
            }, serializer, deserializer);
    }

    /// <summary>
    /// Registers a value to be saved attached to a specific serializable type and be copied in its Serialize/Deserialize methods.
    /// </summary>
    /// <param name="id">The ID of the saved value. Should ideally be a unique string involving the mod's ID.</param>
    /// <param name="getter">Gets the value to save given an instance.</param>
    /// <param name="setter">Given a saved value, attaches it to an instance.</param>
    /// <param name="serializer">Writes the saved value with a PacketWriter.</param>
    /// <param name="deserializer">Retrives the saved value from a PacketReader.</param>
    /// <typeparam name="T">The saved type.</typeparam>
    public static void RegisterSave<T>(string id, Func<DataType, T?> getter, Action<DataType, T?> setter,
        Action<T, PacketWriter> serializer, Func<PacketReader, T> deserializer)
    {
        ExtendedSaveTypes.RegisterDictionarySaveType<string, T>();
        
        if (!SaveValueTypes.ContainsKey(typeof(T))) 
        {
            if (_initializedSaveProps)
            {
                BaseLibMain.Logger.Warn($"Saved types for {typeof(SerializableType).Name} have already been registered; registered save values of type {typeof(T).Name} will not be saved.");
            }

            SaveValueTypes.Add(typeof(T),
                options => JsonMetadataServices.CreatePropertyInfo(options,
                    SavePatchUtils.QuickProps<SerializableType, Dictionary<string, T>>(
                        $"save_dict_{MakeTypeName(typeof(T))}",
                        obj => ExtendedData[obj].DictForType<T>(),
                        (obj, value) =>
                        {
                            value ??= [];
                            ExtendedData[obj].Dictionaries[typeof(T)] = value;
                        })
                    )
                );
        }

            
        RegisteredSaves.InsertSorted(new (id,
            (model, data) =>
            {
                var val = getter(model);
                if (val == null) return;
                if (!data.DictForType<T>().TryAdd(id, val))
                {
                    BaseLibMain.Logger.Error($"Duplicate {typeof(DataType).Name} save key: [{typeof(T).Name}] {id}");
                }
            },
            (model, data) =>
            {
                if (data.DictForType<T>().TryGetValue(id, out var value))
                {
                    setter(model, value);
                }
            },
            (data, writer) =>
            {
                var val = data.DictForType<T>().GetValueOrDefault(id);
                if (val == null)
                {
                    writer.WriteBool(false);
                }
                else
                {
                    writer.WriteBool(true);
                    serializer(val, writer);
                }
            },
            (data, reader) =>
            {
                bool exists = reader.ReadBool();
                if (exists)
                {
                    var val = deserializer(reader);
                    if (val != null)
                        data.DictForType<T>()[id] = val;
                }
            }
        ));
    }

    private static string MakeTypeName(Type t)
    {
        var name = GetShortName(t);
        if (t.IsGenericType)
        {
            return $"{name}[{t.GenericTypeArguments.Join(MakeTypeName, ",")}]";
        }
        return $"{name}";
    }
    private static string GetShortName(Type t)
    {
        if (t.IsAssignableTo(typeof(IList))) return "List";
        if (t.IsAssignableTo(typeof(IDictionary))) return "Dictionary";
        return t.FullName == null || t.FullName.StartsWith("System") ? t.Name : t.FullName;
    }

    /// <summary>
    /// Creates the JsonPropertyInfo for patch to serializable prop maker in json serialization context
    /// </summary>
    /// <param name="options"></param>
    /// <returns></returns>
    public static IEnumerable<JsonPropertyInfo> CreateExtendedProperties(JsonSerializerOptions options)
    {
        BaseLibMain.Logger.Info($"Adding custom save data to {typeof(SerializableType).Name}.");
        //All save data types must be initialized by this point. Any added later will not be saved/loaded.
        _initializedSaveProps = true;
        foreach (var saveType in SaveValueTypes)
        {
            yield return saveType.Value(options);
        }
    }

    /// <summary>
    /// Loads data from a loaded/deserialized serializable to a new instance.
    /// </summary>
    public static void Load(SerializableType dataSource, DataType holder)
    {
        var data = ExtendedData[dataSource];
        foreach (var save in RegisteredSaves)
        {
            save.Setter.Invoke(holder, data);
        }
    }

    /// <summary>
    /// Write data of each registered save to a PacketWriter.
    /// </summary>
    public static void Write(SerializableType dataSource, PacketWriter writer)
    {
        if (!PostModInitPatch.CanModifyGameplay) return;
        
        var extendedData = ExtendedData[dataSource];
        foreach (var saveValue in RegisteredSaves)
        {
            saveValue.Serializer(extendedData, writer);
        }
    }

    /// <summary>
    /// Read data of each registered save from a PacketReader.
    /// </summary>
    public static void Read(SerializableType dataSource, PacketReader reader)
    {
        if (!PostModInitPatch.CanModifyGameplay) return;
        
        var extendedData = ExtendedData[dataSource];
        foreach (var saveValue in RegisteredSaves)
        {
            saveValue.Deserializer(extendedData, reader);
        }
    }
}

/// <summary>
/// Patches to handle addition of save data to MegaCritSerializerContext,
/// and for serialization/deserialization
/// </summary>
public static class ExtendedSavePatches
{
    public static void Patch(Harmony harmony)
    {
        AddContext<CardModel, SerializableCard>(harmony);
        AddContext<RelicModel, SerializableRelic>(harmony);
        AddContext<PotionModel, SerializablePotion>(harmony);
        AddContext<Reward, SerializableReward>(harmony);
        AddContext<Player, SerializablePlayer>(harmony);
        AddContext<EnchantmentModel, SerializableEnchantment>(harmony);
        AddContext<IRunState, SerializableRun>(harmony);
    }

    private static void AddContext<DataType, SerializableType>(Harmony harmony) where SerializableType : class
    {
        var name = typeof(SerializableType).Name + "PropInit";
        var method = typeof(MegaCritSerializerContext).DeclaredMethod(name);
        if (method == null)
        {
            BaseLibMain.Logger.Error($"Unable to find PropInit for type {typeof(SerializableType).Name}");
            return;
        }

        harmony.Patch(method, postfix: 
            typeof(GenericPostfix<DataType, SerializableType>).Method("AdjustPropArray"));
    }

    class GenericPostfix<DataType, SerializableType> where SerializableType : class
    {
        static void AdjustPropArray(JsonSerializerOptions options, ref JsonPropertyInfo[] __result)
        {
            int oldCount = __result.Length;
            __result = [..__result,
                ..ExtendedSaveHandlers<DataType, SerializableType>.CreateExtendedProperties(options)
            ];
            BaseLibMain.Logger.Info($"Added {__result.Length - oldCount} new properties to {typeof(SerializableType).Name}");
        }
    }
    
    /*--------------------- Serialization Patches ---------------------*/
    [HarmonyPatch(typeof(CardModel), nameof(CardModel.ToSerializable))]
    static class PrepExtendedCardData
    {
        [HarmonyPostfix]
        static void ExtendedDataForCard(CardModel __instance, SerializableCard __result)
        {
            var data = new ExtendedSaveHandlers<CardModel, SerializableCard>.ExtendedSaveData(__instance);
            ExtendedSaveHandlers<CardModel, SerializableCard>.ExtendedData[__result] = data;
        }
    }

    [HarmonyPatch(typeof(CardModel), nameof(CardModel.FromSerializable))]
    static class LoadExtendedCardData
    {
        [HarmonyTranspiler]
        static List<CodeInstruction> InsertLoad(IEnumerable<CodeInstruction> code)
        {
            return new InstructionPatcher(code)
                    .Match(new InstructionMatcher()
                        .call(typeof(SavedProperties), nameof(SavedProperties.Fill)))
                    .TakeLabels(out var labels)
                    .Insert([
                        CodeInstruction.LoadArgument(0).WithLabels(labels), //SerializableCard
                        CodeInstruction.LoadLocal(0), //Creating card
                        CodeInstruction.Call(typeof(ExtendedSaveHandlers<CardModel, SerializableCard>), nameof(ExtendedSaveHandlers<CardModel, SerializableCard>.Load))
                    ]);
        }
    }

    [HarmonyPatch(typeof(SerializableCard), nameof(SerializableCard.Serialize))]
    static class SerializeExtendedCardData
    {
        [HarmonyPostfix] //Prefix instead of postfix due to inconsistent written length of SerializableCard
        //Difference between basegame is not an issue as this serialization is only used for net communication, not saves
        static void WriteExtended(SerializableCard __instance, PacketWriter writer)
        {
            ExtendedSaveHandlers<CardModel, SerializableCard>.Write(__instance, writer);
        }
    }

    [HarmonyPatch(typeof(SerializableCard), nameof(SerializableCard.Deserialize))]
    static class DeserializeExtendedCardData
    {
        [HarmonyPostfix]
        static void ReadExtended(SerializableCard __instance, PacketReader reader)
        {
            ExtendedSaveHandlers<CardModel, SerializableCard>.Read(__instance, reader);
        }
    }
    
    [HarmonyPatch(typeof(RelicModel), nameof(RelicModel.ToSerializable))]
    static class PrepExtendedRelicData
    {
        [HarmonyPostfix]
        static void ExtendedDataForRelic(RelicModel __instance, SerializableRelic __result)
        {
            var data = new ExtendedSaveHandlers<RelicModel, SerializableRelic>.ExtendedSaveData(__instance);
            ExtendedSaveHandlers<RelicModel, SerializableRelic>.ExtendedData[__result] = data;
        }
    }

    [HarmonyPatch(typeof(RelicModel), nameof(RelicModel.FromSerializable))]
    static class LoadExtendedRelicData
    {
        [HarmonyTranspiler]
        static List<CodeInstruction> InsertLoad(IEnumerable<CodeInstruction> code)
        {
            return new InstructionPatcher(code)
                    .Match(new InstructionMatcher()
                        .call(typeof(SavedProperties), nameof(SavedProperties.Fill)))
                    .TakeLabels(out var labels)
                    .Insert([
                        CodeInstruction.LoadArgument(0).WithLabels(labels), //SerializableRelic
                        CodeInstruction.LoadLocal(0), //Creating Relic
                        CodeInstruction.Call(typeof(ExtendedSaveHandlers<RelicModel, SerializableRelic>), nameof(ExtendedSaveHandlers<RelicModel, SerializableRelic>.Load))
                    ]);
        }
    }

    [HarmonyPatch(typeof(SerializableRelic), nameof(SerializableRelic.Serialize))]
    static class SerializeExtendedRelicData
    {
        [HarmonyPostfix]
        static void WriteExtended(SerializableRelic __instance, PacketWriter writer)
        {
            ExtendedSaveHandlers<RelicModel, SerializableRelic>.Write(__instance, writer);
        }
    }

    [HarmonyPatch(typeof(SerializableRelic), nameof(SerializableRelic.Deserialize))]
    static class DeserializeExtendedRelicData
    {
        [HarmonyPostfix]
        static void ReadExtended(SerializableRelic __instance, PacketReader reader)
        {
            ExtendedSaveHandlers<RelicModel, SerializableRelic>.Read(__instance, reader);
        }
    }
    
    [HarmonyPatch(typeof(PotionModel), nameof(PotionModel.ToSerializable))]
    static class PrepExtendedPotionData
    {
        [HarmonyPostfix]
        static void ExtendedDataForPotion(PotionModel __instance, SerializablePotion __result)
        {
            var data = new ExtendedSaveHandlers<PotionModel, SerializablePotion>.ExtendedSaveData(__instance);
            ExtendedSaveHandlers<PotionModel, SerializablePotion>.ExtendedData[__result] = data;
        }
    }

    [HarmonyPatch(typeof(PotionModel), nameof(PotionModel.FromSerializable))]
    static class LoadExtendedPotionData
    {
        [HarmonyPostfix]
        static void LoadExtendedData(SerializablePotion save, PotionModel __result)
        {
            ExtendedSaveHandlers<PotionModel, SerializablePotion>.Load(save, __result);
        }
    }

    [HarmonyPatch(typeof(SerializablePotion), nameof(SerializablePotion.Serialize))]
    static class SerializeExtendedPotionData
    {
        [HarmonyPostfix]
        static void WriteExtended(SerializablePotion __instance, PacketWriter writer)
        {
            ExtendedSaveHandlers<PotionModel, SerializablePotion>.Write(__instance, writer);
        }
    }

    [HarmonyPatch(typeof(SerializablePotion), nameof(SerializablePotion.Deserialize))]
    static class DeserializeExtendedPotionData
    {
        [HarmonyPostfix]
        static void ReadExtended(SerializablePotion __instance, PacketReader reader)
        {
            ExtendedSaveHandlers<PotionModel, SerializablePotion>.Read(__instance, reader);
        }
    }
    
    [HarmonyPatch(typeof(Player), nameof(Player.ToSerializable))]
    static class PrepExtendedPlayerData
    {
        [HarmonyPostfix]
        static void ExtendedDataForPlayer(Player __instance, SerializablePlayer __result)
        {
            var data = new ExtendedSaveHandlers<Player, SerializablePlayer>.ExtendedSaveData(__instance);
            ExtendedSaveHandlers<Player, SerializablePlayer>.ExtendedData[__result] = data;
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.FromSerializable))]
    static class LoadExtendedPlayerData
    {
        [HarmonyPostfix]
        static void LoadExtendedData(SerializablePlayer save, Player __result)
        {
            ExtendedSaveHandlers<Player, SerializablePlayer>.Load(save, __result);
        }
    }

    [HarmonyPatch(typeof(SerializablePlayer), nameof(SerializablePlayer.Serialize))]
    static class SerializeExtendedPlayerData
    {
        [HarmonyPostfix]
        static void WriteExtended(SerializablePlayer __instance, PacketWriter writer)
        {
            ExtendedSaveHandlers<Player, SerializablePlayer>.Write(__instance, writer);
        }
    }

    [HarmonyPatch(typeof(SerializablePlayer), nameof(SerializablePlayer.Deserialize))]
    static class DeserializeExtendedPlayerData
    {
        [HarmonyPostfix]
        static void ReadExtended(SerializablePlayer __instance, PacketReader reader)
        {
            ExtendedSaveHandlers<Player, SerializablePlayer>.Read(__instance, reader);
        }
    }
    
    [HarmonyPatch(typeof(Reward), nameof(Reward.ToSerializable))]
    static class PrepExtendedRewardData
    {
        [HarmonyPostfix]
        static void ExtendedDataForReward(Reward __instance, SerializableReward __result)
        {
            var data = new ExtendedSaveHandlers<Reward, SerializableReward>.ExtendedSaveData(__instance);
            ExtendedSaveHandlers<Reward, SerializableReward>.ExtendedData[__result] = data;
        }
    }

    [HarmonyPatch(typeof(Reward), nameof(Reward.FromSerializable))]
    static class LoadExtendedRewardData
    {
        [HarmonyPostfix]
        static void LoadExtendedData(SerializableReward save, Reward __result)
        {
            ExtendedSaveHandlers<Reward, SerializableReward>.Load(save, __result);
        }
    }

    [HarmonyPatch(typeof(SerializableReward), nameof(SerializableReward.Serialize))]
    static class SerializeExtendedRewardData
    {
        [HarmonyPostfix]
        static void WriteExtended(SerializableReward __instance, PacketWriter writer)
        {
            ExtendedSaveHandlers<Reward, SerializableReward>.Write(__instance, writer);
        }
    }

    [HarmonyPatch(typeof(SerializableReward), nameof(SerializableReward.Deserialize))]
    static class DeserializeExtendedRewardData
    {
        [HarmonyPostfix]
        static void ReadExtended(SerializableReward __instance, PacketReader reader)
        {
            ExtendedSaveHandlers<Reward, SerializableReward>.Read(__instance, reader);
        }
    }
    
    [HarmonyPatch(typeof(EnchantmentModel), nameof(EnchantmentModel.ToSerializable))]
    static class PrepExtendedEnchantmentData
    {
        [HarmonyPostfix]
        static void ExtendedDataForEnchantment(EnchantmentModel __instance, SerializableEnchantment __result)
        {
            var data = new ExtendedSaveHandlers<EnchantmentModel, SerializableEnchantment>.ExtendedSaveData(__instance);
            ExtendedSaveHandlers<EnchantmentModel, SerializableEnchantment>.ExtendedData[__result] = data;
        }
    }

    [HarmonyPatch(typeof(EnchantmentModel), nameof(EnchantmentModel.FromSerializable))]
    static class LoadExtendedEnchantmentData
    {
        [HarmonyPostfix]
        static void LoadExtendedData(SerializableEnchantment save, EnchantmentModel __result)
        {
            ExtendedSaveHandlers<EnchantmentModel, SerializableEnchantment>.Load(save, __result);
        }
    }

    [HarmonyPatch(typeof(SerializableEnchantment), nameof(SerializableEnchantment.Serialize))]
    static class SerializeExtendedEnchantmentData
    {
        [HarmonyPostfix]
        static void WriteExtended(SerializableEnchantment __instance, PacketWriter writer)
        {
            ExtendedSaveHandlers<EnchantmentModel, SerializableEnchantment>.Write(__instance, writer);
        }
    }

    [HarmonyPatch(typeof(SerializableEnchantment), nameof(SerializableEnchantment.Deserialize))]
    static class DeserializeExtendedEnchantmentData
    {
        [HarmonyPostfix]
        static void ReadExtended(SerializableEnchantment __instance, PacketReader reader)
        {
            ExtendedSaveHandlers<EnchantmentModel, SerializableEnchantment>.Read(__instance, reader);
        }
    }
    
    [HarmonyPatch(typeof(RunManager), nameof(RunManager.ToSave))]
    static class PrepExtendedRunData
    {
        [HarmonyPostfix]
        static void ExtendedDataForRun(RunManager __instance, SerializableRun __result)
        {
            var data = new ExtendedSaveHandlers<IRunState, SerializableRun>.ExtendedSaveData(__instance.State!);
            ExtendedSaveHandlers<IRunState, SerializableRun>.ExtendedData[__result] = data;
        }
    }
    
    [HarmonyPatch(typeof(RunManager), nameof(RunManager.CanonicalizeSave))]
    static class CanonicalizeExtendedRunData
    {
        [HarmonyPostfix]
        static void CanonicalizeExtendedData(SerializableRun save, SerializableRun __result)
        {
            ExtendedSaveHandlers<IRunState, SerializableRun>.ExtendedData[__result] = ExtendedSaveHandlers<IRunState, SerializableRun>.ExtendedData[save];
        }
    }

    [HarmonyPatch(typeof(RunState), nameof(RunState.FromSerializable))]
    static class LoadExtendedRunData
    {
        [HarmonyPostfix]
        static void LoadExtendedData(SerializableRun save, RunState __result)
        {
            ExtendedSaveHandlers<IRunState, SerializableRun>.Load(save, __result);
        }
    }

    [HarmonyPatch(typeof(SerializableRun), nameof(SerializableRun.Anonymized))]
    static class CopyExtendedRunData
    {
        [HarmonyPostfix]
        static void CopyExtended(SerializableRun __instance, SerializableRun __result)
        {
            ExtendedSaveHandlers<IRunState, SerializableRun>.ExtendedData[__result] = 
                ExtendedSaveHandlers<IRunState, SerializableRun>.ExtendedData[__instance];
        }
    }

    [HarmonyPatch(typeof(SerializableRun), nameof(SerializableRun.Serialize))]
    static class SerializeExtendedRunData
    {
        [HarmonyPostfix]
        static void WriteExtended(SerializableRun __instance, PacketWriter writer)
        {
            ExtendedSaveHandlers<IRunState, SerializableRun>.Write(__instance, writer);
        }
    }

    [HarmonyPatch(typeof(SerializableRun), nameof(SerializableRun.Deserialize))]
    static class DeserializeExtendedRunData
    {
        [HarmonyPostfix]
        static void ReadExtended(SerializableRun __instance, PacketReader reader)
        {
            ExtendedSaveHandlers<IRunState, SerializableRun>.Read(__instance, reader);
        }
    }
}