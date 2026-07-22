using Godot;

namespace Ashfall.Client;

/// <summary>
/// Third-person character with real physics: gravity, slopes, and collision
/// against the terrain mesh.
///
/// Movement is camera-relative — pushing "forward" means forward from where
/// you are looking, not a fixed world axis. This is currently client-side
/// only; making it server-authoritative is the open architectural question.
/// </summary>
public partial class PlayerBody : CharacterBody3D
{
    [Export] public float WalkSpeed { get; set; } = 6.0f;
    [Export] public float JumpSpeed { get; set; } = 6.5f;

    /// <summary>How quickly the body turns to face its direction of travel.</summary>
    [Export] public float TurnRate { get; set; } = 12.0f;

    private float _gravity = 24.0f;

    /// <summary>Facing in radians, reported to the server for remote rendering.</summary>
    public float Facing => _visual?.Rotation.Y ?? 0f;

    /// <summary>Hard reset of position — used for server corrections and spawn.</summary>
    public void Teleport(Vector3 position)
    {
        GlobalPosition = position;
        Velocity = Vector3.Zero;
    }

    private Node3D _cameraPivot = null!;
    private Node3D _visual = null!;

    public override void _Ready()
    {
        _cameraPivot = GetNode<Node3D>("../CameraRig");
        _visual = GetNode<Node3D>("Visual");
    }

    public override void _PhysicsProcess(double delta)
    {
        var velocity = Velocity;

        if (!IsOnFloor()) velocity.Y -= _gravity * (float)delta;
        else if (Input.IsActionPressed("ui_accept")) velocity.Y = JumpSpeed;

        // Input is interpreted in the camera's frame, then flattened, so
        // looking up or down never changes how fast you walk.
        var input = Input.GetVector("ui_left", "ui_right", "ui_up", "ui_down");
        var basis = _cameraPivot.GlobalBasis;
        var forward = -basis.Z with { Y = 0 };
        var right = basis.X with { Y = 0 };
        var direction = (right.Normalized() * input.X + forward.Normalized() * input.Y).Normalized();

        if (direction.LengthSquared() > 0.01f)
        {
            velocity.X = direction.X * WalkSpeed;
            velocity.Z = direction.Z * WalkSpeed;

            float facing = Mathf.Atan2(-direction.X, -direction.Z);
            _visual.Rotation = _visual.Rotation with
            {
                Y = Mathf.LerpAngle(_visual.Rotation.Y, facing, (float)delta * TurnRate),
            };
        }
        else
        {
            velocity.X = Mathf.MoveToward(velocity.X, 0, WalkSpeed);
            velocity.Z = Mathf.MoveToward(velocity.Z, 0, WalkSpeed);
        }

        Velocity = velocity;
        MoveAndSlide();
    }
}
