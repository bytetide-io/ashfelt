using Godot;

namespace Ashfall.Client;

/// <summary>
/// The visible character body, shared by the local <see cref="PlayerBody"/> and
/// every <see cref="RemotePlayers"/> puppet so animation lives in one place
/// rather than being re-derived on each side of the client.
///
/// It is fed intent, never geometry: a driver reports how fast the body is
/// moving and when it strikes, and the rig turns that into a gait and a swing.
/// Locomotion cadence is tied to ground speed (a faster body takes faster,
/// wider strides), so a walk and a run animate from the same call with no
/// separate state to select.
///
/// ---- Drop-in contract for real art -------------------------------------
/// This build is a procedural placeholder — limbs are primitives swung in code,
/// matching the repo's "placeholder now, real asset later" pipeline (ASSETS.md).
/// A CC0 rigged character (Quaternius' Universal Animation Library or Kenney's
/// Animated Characters both fit the licence rules and the low-res look) drops in
/// without touching any caller: import the .glb, replace <see cref="BuildBody"/>
/// with the model instance, and route the SAME two entry points —
/// <see cref="SetLocomotion"/> and <see cref="PlayGather"/> — into an
/// AnimationTree blend (idle↔walk↔run on a 1D blend of planar speed, gather as a
/// OneShot). The API is the contract; the primitives are not.
/// </summary>
public partial class CharacterRig : Node3D
{
    /// <summary>Ground speed (m/s) at and above which the gait is a full run —
    /// the reference the walk↔run amplitude is normalised against.</summary>
    [Export] public float RunSpeed { get; set; } = 6.0f;

    // Gait shape. Amplitudes are radians of joint swing at a full run; the bob
    // is how far the torso dips each footfall. All scale down toward a still
    // idle as ground speed falls to zero, so one curve covers walk and run.
    private const float LegSwingRad = 0.72f;
    private const float ArmSwingRad = 0.55f;
    private const float RunLeanRad = 0.16f;
    private const float BobHeightM = 0.05f;

    /// <summary>Radians of gait phase advanced per metre travelled. Cadence is
    /// distance-based so footfalls stay planted to the ground rather than
    /// sliding — the classic cause of a "moonwalking" placeholder.</summary>
    private const float PhasePerMetre = 2.1f;

    // Idle: a slow breath so a standing character is never a frozen statue.
    private const float BreatheHz = 0.5f;
    private const float BreatheHeightM = 0.015f;

    /// <summary>How fast the gait fades in and out, so starting and stopping
    /// eases rather than snapping between poses.</summary>
    private const float BlendRate = 10.0f;

    // Gather: a two-handed overhead strike, retriggered on each chop.
    private const float GatherSeconds = 0.55f;
    private float _gatherTimer;

    private float _phase;       // gait cycle, radians
    private float _idlePhase;   // breathing cycle, radians
    private float _speed;       // last reported planar speed, m/s
    private bool _grounded = true;
    private float _gait;        // eased 0→1 "how much of the run pose to apply"

    private Node3D _motion = null!;   // bob + lean, kept off the facing root
    private Node3D _leftLeg = null!;
    private Node3D _rightLeg = null!;
    private Node3D _leftArm = null!;
    private Node3D _rightArm = null!;

    public override void _Ready() => BuildBody();

    /// <summary>Reported by the movement driver every physics frame: planar
    /// (ground-plane) speed in m/s and whether the body is on the floor.</summary>
    public void SetLocomotion(float planarSpeed, bool grounded)
    {
        _speed = planarSpeed;
        _grounded = grounded;
    }

    /// <summary>Play the harvest strike once. Called the instant a chop request
    /// is sent, so the swing reads as the cause of the hit, not a reaction to it.</summary>
    public void PlayGather() => _gatherTimer = GatherSeconds;

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        float run = Mathf.Min(1f, _speed / RunSpeed);
        bool moving = _grounded && _speed > 0.15f;

        _gait = Mathf.MoveToward(_gait, moving ? run : 0f, BlendRate * dt);
        _phase += dt * _speed * PhasePerMetre;
        _idlePhase += dt * BreatheHz * Mathf.Tau;

        float legSwing = Mathf.Sin(_phase) * LegSwingRad * _gait;
        float armSwing = Mathf.Sin(_phase) * ArmSwingRad * _gait;

        // Arms counter-swing the legs; opposite sides mirror so the body walks
        // rather than hopping with both legs together.
        _leftLeg.Rotation = _leftLeg.Rotation with { X = legSwing };
        _rightLeg.Rotation = _rightLeg.Rotation with { X = -legSwing };

        float gather = ApplyGather(dt);
        // Blend the locomotion arm pose out as the strike takes over, so a chop
        // while walking swings the arms cleanly instead of fighting the gait.
        _leftArm.Rotation = _leftArm.Rotation with { X = Mathf.Lerp(-armSwing, gather, _gatherActive) };
        _rightArm.Rotation = _rightArm.Rotation with { X = Mathf.Lerp(armSwing, gather, _gatherActive) };

        // The torso dips twice per stride (once per footfall) and leans into a
        // run; a strike pitches it forward over the swing.
        float bob = -Mathf.Abs(Mathf.Sin(_phase)) * BobHeightM * _gait
                    + Mathf.Sin(_idlePhase) * BreatheHeightM * (1f - _gait);
        float lean = RunLeanRad * _gait + _gatherActive * GatherLeanRad;
        _motion.Position = _motion.Position with { Y = bob };
        _motion.Rotation = _motion.Rotation with { X = lean };
    }

    private const float GatherLeanRad = 0.28f;
    private float _gatherActive; // 0→1, how much the strike pose is in effect

    /// <summary>Advances the strike and returns the shoulder angle to hold this
    /// frame: wind the arms up overhead, drive them down through the hit, then
    /// settle. <see cref="_gatherActive"/> tracks how strongly to apply it.</summary>
    private float ApplyGather(float dt)
    {
        if (_gatherTimer <= 0f)
        {
            _gatherActive = Mathf.MoveToward(_gatherActive, 0f, BlendRate * dt);
            return 0f;
        }

        _gatherTimer = Mathf.Max(0f, _gatherTimer - dt);
        _gatherActive = 1f;

        float t = 1f - _gatherTimer / GatherSeconds; // 0 at wind-up, 1 at recover
        const float WindUpRad = -2.3f; // arms raised back overhead
        const float StrikeRad = 0.9f;  // arms driven down and forward
        if (t < 0.35f) return Mathf.Lerp(0f, WindUpRad, t / 0.35f);
        if (t < 0.6f) return Mathf.Lerp(WindUpRad, StrikeRad, (t - 0.35f) / 0.25f);
        return Mathf.Lerp(StrikeRad, 0f, (t - 0.6f) / 0.4f);
    }

    // ---- Placeholder body ------------------------------------------------
    // Proportions in metres for a ~1.8 m body centred on the rig origin (feet at
    // -0.9, matching the collision capsule it replaces). A joint is a bare Node3D
    // pivot at the hip/shoulder with its limb mesh hung below it, so rotating the
    // pivot swings the whole limb from the joint.

    private void BuildBody()
    {
        _motion = new Node3D { Name = "Motion" };
        AddChild(_motion);

        var skin = WorldMaterials.Flat(DesignSystem.Muted);
        var tunic = WorldMaterials.Flat(DesignSystem.EmberDeep);
        var trouser = WorldMaterials.Flat(DesignSystem.Ink700);

        _motion.AddChild(Part(new BoxMesh { Size = new Vector3(0.42f, 0.55f, 0.28f) }, tunic,
            new Vector3(0, 0.22f, 0)));
        _motion.AddChild(Part(new SphereMesh { Radius = 0.17f, Height = 0.34f }, skin,
            new Vector3(0, 0.67f, 0)));

        _leftLeg = Limb(_motion, trouser, hipX: -0.11f, length: 0.85f, thickness: 0.19f);
        _rightLeg = Limb(_motion, trouser, hipX: 0.11f, length: 0.85f, thickness: 0.19f);
        _leftArm = Limb(_motion, skin, hipX: -0.28f, length: 0.6f, thickness: 0.14f, jointY: 0.46f);
        _rightArm = Limb(_motion, skin, hipX: 0.28f, length: 0.6f, thickness: 0.14f, jointY: 0.46f);
    }

    /// <summary>A limb: a pivot Node3D at the joint with its mesh hung below, so
    /// the mesh top sits at the joint and the pivot's X-rotation swings it.</summary>
    private static Node3D Limb(
        Node3D parent, Material material, float hipX, float length, float thickness, float jointY = -0.05f)
    {
        var joint = new Node3D { Position = new Vector3(hipX, jointY, 0) };
        joint.AddChild(Part(
            new BoxMesh { Size = new Vector3(thickness, length, thickness) },
            material, new Vector3(0, -length / 2f, 0)));
        parent.AddChild(joint);
        return joint;
    }

    private static MeshInstance3D Part(Mesh mesh, Material material, Vector3 position) => new()
    {
        Mesh = mesh,
        MaterialOverride = material,
        Position = position,
    };
}
