using BaseLib.Extensions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Random;

namespace BaseLib.Utils;

public class OptionPools
{
    private WeightedList<AncientOption>[] _pools; 
    
    /// <summary>
    /// Constructor for an ancient's options that uses a separate pool for each option.
    /// </summary>
    public OptionPools(WeightedList<AncientOption> pool1, WeightedList<AncientOption> pool2, WeightedList<AncientOption> pool3)
    {
        _pools = [pool1, pool2, pool3];
    }
    
    /// <summary>
    /// Constructor for an ancient's options that uses one pool for its first two options and a second pool for its third option.
    /// </summary>
    public OptionPools(WeightedList<AncientOption> pool12, WeightedList<AncientOption> pool3)
    {
        _pools = [pool12, pool12, pool3];
    }

    /// <summary>
    /// Constructor for an ancient's options using one pool for all its options.
    /// </summary>
    public OptionPools(WeightedList<AncientOption> pool)
    {
        _pools = [pool, pool, pool];
    }
    
    public IEnumerable<AncientOption> AllOptions => _pools.SelectMany(pool => pool);
    
    public List<AncientOption> Roll(Rng rng, AncientEventModel ancient)
    {
        List<AncientOption> result = [];

        var pool = _pools[0];
        WeightedList<AncientOption> rollPool = [..pool.Where(option => option.ModelForOption.RelicCanSpawnAtCustomAncient(ancient))];

        // WeightedList.GetRandom throws IndexOutOfRangeException on an empty list, and the filter
        // above can empty a small pool entirely. Three options are requested unconditionally, so a
        // pool with fewer than three eligible entries used to fail the ancient mid-run.
        result.Add(TakeOrThrow(rollPool, rng, ancient));

        if (pool != _pools[1])
        {
            pool = _pools[1];
            rollPool = [..pool.Where(option => option.ModelForOption.RelicCanSpawnAtCustomAncient(ancient))];
        }
        result.Add(TakeOrThrow(rollPool, rng, ancient));

        if (pool != _pools[2])
        {
            pool = _pools[2];
            rollPool = [..pool.Where(option => option.ModelForOption.RelicCanSpawnAtCustomAncient(ancient))];
        }
        result.Add(TakeOrThrow(rollPool, rng, ancient));

        return result;
    }

    private static AncientOption TakeOrThrow(WeightedList<AncientOption> pool, Rng rng, AncientEventModel ancient)
    {
        if (pool.Count == 0)
            throw new InvalidOperationException(
                $"Ancient '{ancient.Id}' has fewer eligible options than the 3 it must present " +
                $"(after filtering by RelicCanSpawnAtCustomAncient). Add more options or loosen the filter.");
        return pool.GetRandom(rng, true);
    }
}