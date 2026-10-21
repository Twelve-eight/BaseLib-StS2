using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.Vfx.Utilities;
using System.Reflection;

namespace BaseLib.Utils;


/// <summary>
/// Utility class for defining an <see cref="NVfxParticleSystem"/> in code, representing a set of
/// one shot particle emitters.
/// The built system is freed once the longest-lived particle has expired, instead of after the
/// fixed one second of the vanilla scene.
/// If using the same visuals repeatedly, it is recommended to define your builder once and call
/// <see cref="Build"/> to create a new instance when necessary.
/// </summary>
public class ParticleSystemBuilder
{
    private static Dictionary<int, CurveTexture> _curveDictionary = [];

    /// <summary>
    /// <see cref="NVfxParticleSystem"/> only exposes its self-destruct delay as a private exported
    /// field, read once in <c>_Ready</c>. It has to be written before the node enters the tree.
    /// </summary>
    private static readonly FieldInfo? RootLifetimeField =
        AccessTools.Field(typeof(NVfxParticleSystem), "_lifetime");

    private static bool _rootLifetimeWarningLogged;

    private static CurveTexture GetCurveCached(GodotUtils.CurveBuilder curveBuilder, int width = 128)
    {
        if (!_curveDictionary.TryGetValue(curveBuilder.GetHashCode(), out var curve))
        {
            curve = new();
            curve.Width = width;
            curve.Curve = curveBuilder.Curve;
                
            _curveDictionary.Add(curveBuilder.GetHashCode(), curve);
        }

        return curve;
    }
    
    private class OneShotParticleData()
    {
        public required Func<Texture2D> Texture { get; set; }
        public int Amount { get; set; } = 1;
        public float Lifetime { get; set; } = 1f;
        
        public event Action<ParticleProcessMaterial>? SetupMaterial;


        public GpuParticles2D Build()
        {
            GpuParticles2D emitter = new();
            emitter.Texture = Texture();
            emitter.Amount = Amount;
            //A single burst that ends on its own after Lifetime seconds, so the root can be freed
            //as soon as the last particle is gone. OneShot and Lifetime are set before Emitting.
            emitter.Lifetime = Lifetime;
            emitter.OneShot = true;

            var processMaterial = new ParticleProcessMaterial();
            processMaterial.ParticleFlagDisableZ = true;
            SetupMaterial?.Invoke(processMaterial);
            
            emitter.ProcessMaterial = processMaterial;
            
            emitter.Emitting = true;
            
            return emitter;
        }
    }

    private List<OneShotParticleData> _particles = [];
    
    public ParticleSystemBuilder AddParticle(string texturePath)
    {
        return AddParticle(() => PreloadManager.Cache.GetTexture2D(texturePath));
    }
    
    /// <summary>
    /// Adds a one shot particle emitter to the system.
    /// </summary>
    /// <param name="baseTexture">Produces the emitter's texture; evaluated once per <see cref="Build"/>.</param>
    /// <param name="particleCount">Number of particles emitted per burst.</param>
    /// <param name="lifetime">Seconds each particle lives. Also drives when the built
    /// <see cref="NVfxParticleSystem"/> frees itself: after the longest particle lifetime of the whole builder.</param>
    /// <param name="setupParticleProcess">Optional callback that configures the emitter's process material.</param>
    public ParticleSystemBuilder AddParticle(Func<Texture2D> baseTexture, int particleCount = 1, float lifetime = 1f,
        Action<ParticleProcessMaterial>? setupParticleProcess = null)
    {
        var particleData = new OneShotParticleData()
        {
            Texture = baseTexture,
            Amount = particleCount,
            Lifetime = lifetime
        };
        particleData.SetupMaterial += setupParticleProcess;
        
        _particles.Add(particleData);
        return this;
    }

    /// <summary>
    /// Sets up a particle to grow and then fade out.
    /// The parameters are used to define a start and end point for a curve for the particle's scale.
    /// </summary>
    /// <param name="baseColor">The initial color before fading. Defaults to white.</param>
    /// <param name="initialScale">Recommended to be between 0 and 1.</param>
    /// <param name="curveIn">0 means the curve will start by moving horizontally;
    /// a positive value moves upward initially,
    /// a negative value moves downwards initially.
    /// Almost any value can be used, but values within +-3 are generally enough.</param>
    /// <param name="finalScale">Recommended to be between 0 and 1.</param>
    /// <param name="curveOut">0 means the curve will end by moving horizontally;
    /// a positive value ends the curve by moving upwards,
    /// a negative value ends the curve by moving downwards.
    /// Almost any value can be used, but values within +-3 are generally enough.</param>
    public ParticleSystemBuilder GrowFade(Color? baseColor = null, float initialScale = 0.4f, float curveIn = 0.76f, float finalScale = 0.65f, float curveOut = 0f)
    {
        if (_particles.Count == 0)
            throw new InvalidOperationException("Cannot set particle process type without first adding a particle.");

        var color = baseColor ?? StsColors.halfTransparentWhite;

        GodotUtils.CurveBuilder curveBuilder = new();
        curveBuilder.AddPoint(0, initialScale, rightTangent: curveIn)
            .AddPoint(1, finalScale, leftTangent: curveOut);

        var scaleCurve = GetCurveCached(curveBuilder);

        curveBuilder = new();
        curveBuilder.AddPoint(0.2f, 1, rightTangent: -2.463f);
        curveBuilder.AddPoint(1, 0, leftTangent: -0.313f);
        
        var alphaCurve = GetCurveCached(curveBuilder);
        
        var particle = _particles[^1];
        particle.SetupMaterial += processMaterial =>
        {
            processMaterial.ParticleFlagDisableZ = true;
            processMaterial.Gravity = new(0, 0, 0);
            processMaterial.ScaleCurve = scaleCurve;
            processMaterial.Color = color;
            processMaterial.AlphaCurve = alphaCurve;
        };
        return this;
    }

    public NVfxParticleSystem Build()
    {
        NVfxParticleSystem root = new();
        
        foreach (var particle in _particles)
        {
            root.AddChild(particle.Build());
        }

        //NVfxParticleSystem reads _lifetime in _Ready, so it must be written before the root enters
        //the tree; otherwise the vfx is freed after the vanilla one second and longer particles are
        //cut off. An empty builder keeps that vanilla default.
        if (_particles.Count > 0)
            SetRootLifetime(root, _particles.Max(particle => particle.Lifetime));

        return root;
    }

    private static void SetRootLifetime(NVfxParticleSystem root, float lifetime)
    {
        if (RootLifetimeField == null)
        {
            WarnRootLifetimeOnce($"NVfxParticleSystem._lifetime was not found; the vfx will be freed after the vanilla " +
                                 $"1s instead of {lifetime}s, so longer-lived particles may be cut short.");
            return;
        }

        try
        {
            RootLifetimeField.SetValue(root, lifetime);
        }
        catch (Exception e)
        {
            //A cosmetic vfx must never break the caller; keep the vanilla self-destruct delay instead.
            WarnRootLifetimeOnce($"NVfxParticleSystem._lifetime could not be set: {e.Message}. The vfx will be freed " +
                                 $"after the vanilla 1s instead of {lifetime}s.");
        }
    }

    private static void WarnRootLifetimeOnce(string message)
    {
        if (_rootLifetimeWarningLogged) return;
        _rootLifetimeWarningLogged = true;
        BaseLibMain.Logger.Warn(message);
    }
}