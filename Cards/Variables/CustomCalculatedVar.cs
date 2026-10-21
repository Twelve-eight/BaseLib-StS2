using BaseLib.Abstracts;
using BaseLib.Extensions;
using BaseLib.Utils;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;

namespace BaseLib.Cards.Variables;

/// <summary>
/// A calculated var that allows multiple on a single model and works on relics, powers, potions, enchantments,
/// and card modifiers.
/// Requires two additional vars with the same name ending in "Base" and "Extra".
/// A CustomCalculatedVar named "Fire" would need "FireBase" and "FireExtra".
/// WithMultiplier should be called and provide a multiplier calc using the variable's owner's type.
/// A multiplier calc can be individually defined for cards, relics, and powers.
/// </summary>
public class CustomCalculatedVar : CalculatedVar
{
    private Func<RelicModel, Creature?, decimal>? _relicCalc = null;
    private Func<PowerModel, Creature?, decimal>? _powerCalc = null;
    private Func<DynamicVarSource, Creature?, decimal>? _generalCalc = null;

    /// <summary>
    /// Returns the three variables necessary for a calculated var, where the calculated value is equal to
    /// baseVal + (bonus * mult).
    /// </summary>
    public static IEnumerable<DynamicVar> Create(string name, int baseVal, 
        Func<DynamicVarSource, Creature?, decimal> bonus, int mult = 1)
    {
        var calculatedVar = new CustomCalculatedVar(name);
        calculatedVar.GeneralMultiplier(bonus);
        yield return new DynamicVar($"{calculatedVar.Name}Base", baseVal);
        yield return new DynamicVar($"{calculatedVar.Name}Extra", mult);
        yield return calculatedVar;
    }
    
    public CustomCalculatedVar(string name) : base(name)
    {
        
    }

    public virtual decimal CalculateCustom(Creature? target)
    {
        return CalculateCustomVar(this, GetBaseVar(), GetExtraVar(), target,
            Calculate, _relicCalc, _powerCalc, _generalCalc);
    }

    public static decimal CalculateCustomVar(DynamicVar dynVar, DynamicVar baseVar, DynamicVar extraVar,
        Creature? target,
        Func<Creature?, decimal> cardCalc,
        Func<RelicModel, Creature?, decimal>? relicCalc,
        Func<PowerModel, Creature?, decimal>? powerCalc,
        Func<DynamicVarSource, Creature?, decimal>? generalCalc)
    {
        var owner = dynVar._owner;
        switch (owner)
        {
            case CardModel:
                return cardCalc(target);
            case PowerModel power:
                var mult = powerCalc?.Invoke(power, target) ??
                                 throw new InvalidOperationException(
                                     $"{dynVar.GetType().Name} {dynVar.Name} does not have multiplier calc defined for powers in {owner.Id}");
                return baseVar.BaseValue + extraVar.BaseValue * mult;
            case RelicModel relic:
                mult = relicCalc?.Invoke(relic, target) ?? throw new InvalidOperationException(
                         $"{dynVar.GetType().Name} {dynVar.Name} does not have multiplier calc defined for relics in {owner.Id}");
                return baseVar.BaseValue + extraVar.BaseValue * mult;
            case PotionModel potion:
                mult = generalCalc?.Invoke(potion, target) ?? throw new InvalidOperationException(
                    $"{dynVar.GetType().Name} {dynVar.Name} does not have multiplier calc defined for potions in {owner.Id}");
                return baseVar.BaseValue + extraVar.BaseValue * mult; 
            //Treated same as normal cards where calculation is disabled out of combat
            case EnchantmentModel enchant:
                mult = (!CombatManager.Instance.IsInProgress || 
                        GetEnchantmentCombatState(enchant) == null) ? 0 : 
                    generalCalc?.Invoke(enchant, target) ?? throw new InvalidOperationException(
                    $"{dynVar.GetType().Name} {dynVar.Name} does not have multiplier calc defined for enchantments in {owner.Id}");
                return baseVar.BaseValue + extraVar.BaseValue * mult;
            case CardModifier modifier:
                mult = (!CombatManager.Instance.IsInProgress || 
                        modifier.Owner?.Owner?.Creature?.CombatState == null) ? 0 : 
                    generalCalc?.Invoke(modifier, target) ?? throw new InvalidOperationException(
                        $"{dynVar.GetType().Name} {dynVar.Name} does not have multiplier calc defined for card modifiers in {owner.Id}");
                return baseVar.BaseValue + extraVar.BaseValue * mult;
            default:
                return dynVar.BaseValue;
        }
    }

    /// <summary>
    /// The combat state of the card an enchantment is attached to, or null when there is none.
    /// Same checks as the <see cref="DynamicVarSource"/> conversion for enchantments: <see cref="EnchantmentModel.Card"/>
    /// asserts mutability when read, so it may only be touched once the model is known to be mutable, and an
    /// unattached card (enchant previews) or an ownerless card has no combat state.
    /// </summary>
    private static ICombatState? GetEnchantmentCombatState(EnchantmentModel enchant)
    {
        return enchant is { IsMutable: true, Card: not null, Card.IsMutable: true, Card.Owner: not null }
            ? enchant.Card.Owner.Creature.CombatState
            : null;
    }
    
    /// <summary>
    /// Sets a multiplier calculation for a relic. Note that calculation for relics is allowed to occur out of combat,
    /// so combat state may be null.
    /// </summary>
    public CalculatedVar WithMultiplier(Func<RelicModel, Creature?, decimal> multiplierCalc)
    {
        if (_relicCalc != null)
            throw new InvalidOperationException($"Tried to set multiplier calc for relic on CustomCalculatedVar {Name} twice!");
        _relicCalc = multiplierCalc.Target is not AbstractModel ? multiplierCalc : throw new InvalidOperationException("Multiplier calc must be static!");
        return this;
    }
    
    /// <summary>
    /// Sets a multiplier calculation for a power.
    /// </summary>
    public CalculatedVar WithMultiplier(Func<PowerModel, Creature?, decimal> multiplierCalc)
    {
        if (_powerCalc != null)
            throw new InvalidOperationException($"Tried to set multiplier calc for power on CustomCalculatedVar {Name} twice!");
        _powerCalc = multiplierCalc.Target is not AbstractModel ? multiplierCalc : throw new InvalidOperationException("Multiplier calc must be static!");
        return this;
    }
    
    /// <summary>
    /// Sets a calculation that supports any type that can be cast to <see cref="DynamicVarSource"/>.
    /// Note that calculation for relics and potions is allowed to occur out of combat, so combat state may be null.
    /// </summary>
    public CalculatedVar GeneralMultiplier(Func<DynamicVarSource, Creature?, decimal> multiplierCalc)
    {
        if (_generalCalc != null)
            throw new InvalidOperationException($"Tried to set multiplier calc for CustomCalculatedVar {Name} twice!");
        if (multiplierCalc.Target is AbstractModel) throw new InvalidOperationException("Multiplier calc must be static!");

        WithMultiplier((CardModel card, Creature? c) => multiplierCalc(card, c));
        _powerCalc = (pow, target) => multiplierCalc(pow, target);
        _relicCalc = (pow, target) => multiplierCalc(pow, target);
        _generalCalc = multiplierCalc;
        return this;
    }

    protected override DynamicVar GetBaseVar()
    {
        return GetOwnerVar($"{Name}Base");
    }

    protected override DynamicVar GetExtraVar()
    {
        return GetOwnerVar($"{Name}Extra");
    }

    /// <summary>
    /// Looks up one of this var's companion vars ("...Base"/"...Extra") on the owning model.
    /// <see cref="ModelExtensions.GetDynamicVar"/> only knows cards, relics and powers, but this var also supports
    /// enchantments and card modifiers (see <see cref="CalculateCustomVar"/>), which expose the same
    /// <see cref="DynamicVarSet"/>.
    /// </summary>
    private DynamicVar GetOwnerVar(string varKey)
    {
        return _owner switch
        {
            EnchantmentModel enchant => enchant.DynamicVars[varKey],
            CardModifier modifier => modifier.DynamicVars[varKey],
            _ => _owner!.GetDynamicVar(varKey)
        };
    }

    protected override decimal GetBaseValueForIConvertible() => CalculateCustom(null);

    /// <inheritdoc />
    public override string ToString() => CalculateCustom(null).ToString();
}