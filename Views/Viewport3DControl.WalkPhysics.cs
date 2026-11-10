using AIArchitectStudio.Models;
using AIArchitectStudio.Services;
using AIArchitectStudio.Services.Rendering;
using HelixToolkit.WinUI;
using SharpDX;
using ProjectionCamera = HelixToolkit.WinUI.ProjectionCamera;

namespace AIArchitectStudio.Views;

/// <summary>
/// Волна 6 — физический режим «Проход»: капсула человека R = 0,28 м (глаза 1,70 м, макушка 1,85 м) вместо «призрака».
/// Сквозь глухие стены, колонны, парапеты и закрытые двери пройти нельзя — движение скользит вдоль поверхности; E (или клик
/// по двери) открывает / закрывает дверь перед собой — открытая поворачивается на петлях на 90°; ноги стоят на плитах, рельефе
/// участка и ступенях маршей (подступенок ≤ 0,22 м — автоматически, винтовые — по углу), ниже — падение по гравитации.
/// Логика — <see cref="WalkPhysicsService"/> (её же проверяет автотест), здесь — кадр, вид и подсказки.
/// </summary>
public sealed partial class Viewport3DControl
{
    private WalkPhysicsWorld? _walkWorld;
    private readonly HashSet<string> _walkOpenDoors = new(StringComparer.Ordinal);
    private double _walkVy;
    private double _walkStatusFloor;
    private string? _walkBlockedType;
    private bool _walkOnStairs;
    private ArchitecturalElement? _walkDoorAhead;
    private DateTime _walkBlockedAt;
    private Windows.Foundation.Point _walkPressPosition;

    /// <summary>Мир прохода по видимым элементам и рельефу участка (кэш до изменения сцены; открытые двери сохраняются).</summary>
    private WalkPhysicsWorld WalkWorld()
    {
        if (_walkWorld is not null) return _walkWorld;
        var elements = _scene?.VisibleElements.ToList() ?? [];
        Func<double, double, double>? ground = _siteTerrain is { } terrain ? (x, z) => terrain.HeightAt(x, z) : null;
        _walkWorld = WalkPhysicsService.Build(elements, ground, _walkOpenDoors);
        return _walkWorld;
    }

    /// <summary>Шаг капсулы за кадр: смещение (dx, dz), вертикаль, упор и подсказка двери.</summary>
    private void WalkPhysicsStep(double dx, double dz, double dt)
    {
        var world = WalkWorld();
        var result = WalkPhysicsService.Step(world, new WalkState(_walkX, _walkZ, _walkFloor, _walkVy), dx, dz, dt);
        _walkX = result.State.X;
        _walkZ = result.State.Z;
        _walkFloor = result.State.Feet;
        _walkVy = result.State.VerticalSpeed;
        string? blocked = result.Blocked && (Math.Abs(dx) + Math.Abs(dz) > 1e-9) ? result.BlockedType ?? ElementTypes.Wall : null;
        if (blocked is not null) _walkBlockedAt = DateTime.UtcNow;
        else if ((DateTime.UtcNow - _walkBlockedAt).TotalMilliseconds < 450) blocked = _walkBlockedType;
#if DEBUG
        if (_debugHoldWalkStatus && blocked is null) blocked = _walkBlockedType;
#endif
        var door = WalkPhysicsService.DoorAhead(world, result.State, _walkYaw);
        bool changed = blocked != _walkBlockedType || !ReferenceEquals(door, _walkDoorAhead) || result.OnStairs != _walkOnStairs;
        _walkBlockedType = blocked;
        _walkDoorAhead = door;
        _walkOnStairs = result.OnStairs;
        if (changed) UpdateWalkStatus();
    }

    /// <summary>Хвост строки статуса прогулки: упор, лестница, подсказка двери.</summary>
    private string WalkPhysicsStatus()
    {
        if (_vm is null) return string.Empty;
        var loc = _vm.Loc;
        var parts = new List<string>();
        if (_walkBlockedType is { } t)
            parts.Add(loc.Format("W6WalkBlocked", t switch
            {
                ElementTypes.Door => loc.Get("W6WalkDoorClosed"),
                ElementTypes.Column => loc.Get("W6WalkColumn"),
                ElementTypes.Stairs => loc.Get("W6WalkStairSide"),
                ElementTypes.CurtainWall => loc.Get("W6WalkGlass"),
                _ => loc.Get("W6WalkWall")
            }));
        if (_walkOnStairs) parts.Add(loc.Get("W6WalkOnStairs"));
        if (_walkDoorAhead is { } d) parts.Add(loc.Get(_walkOpenDoors.Contains(d.Id) ? "W6WalkDoorCloseHint" : "W6WalkDoorOpenHint"));
        return parts.Count == 0 ? string.Empty : " · " + string.Join(" · ", parts);
    }

    /// <summary>E: открыть / закрыть дверь перед собой (true — дверь была).</summary>
    private bool ToggleDoorAhead()
    {
        if (!_walkActive) return false;
        var world = WalkWorld();
        var door = WalkPhysicsService.DoorAhead(world, new WalkState(_walkX, _walkZ, _walkFloor), _walkYaw);
        if (door is null) return false;
        ToggleDoor(door);
        return true;
    }

    private void ToggleDoor(ArchitecturalElement door)
    {
        var world = WalkWorld();
        bool open = WalkPhysicsService.ToggleDoor(world, door);
        if (open) _walkOpenDoors.Add(door.Id);
        else _walkOpenDoors.Remove(door.Id);
        // Закрываемая дверь не прищемляет: капсулу выталкивает из полотна.
        (_walkX, _walkZ) = WalkCollisionService.Resolve(world.ActiveObstacles, _walkX, _walkZ, WalkPhysicsService.Radius);
        ApplyDoorVisuals();
        UpdateWalkStatus();
    }

    /// <summary>Открытые двери поворачиваются на петлях (левая кромка проёма) на 90° внутрь; закрытые — на месте.</summary>
    private void ApplyDoorVisuals()
    {
        if (_scene is null) return;
        foreach (var door in _scene.Elements.Where(e => e.Type == ElementTypes.Door))
        {
            if (!_modelsById.TryGetValue(door.Id, out var models) || !_geometryById.TryGetValue(door.Id, out var geometry)) continue;
            var baseTransform = geometry.Transform.ToDx();
            Matrix transform = baseTransform;
            if (_walkOpenDoors.Contains(door.Id))
            {
                var (hx, hz) = ArchitecturalElement.RotateY(-door.Width / 2, 0, door.RotationY);
                var pivot = new Vector3((float)(door.X + hx), 0, (float)(door.Z + hz));
                transform = baseTransform * Matrix.Translation(-pivot) * Matrix.RotationY(MathUtil.PiOverTwo) * Matrix.Translation(pivot);
            }
            foreach (var m in models)
            {
                if (m is MeshGeometryModel3D mesh) mesh.HxTransform3D = transform;
                else if (m is LineGeometryModel3D line) line.HxTransform3D = transform;
            }
        }
    }

    /// <summary>Клик по двери в прогулке — открыть / закрыть (если она в пределах досягаемости).</summary>
    private bool TryToggleDoorAt(Windows.Foundation.Point position)
    {
        if (!_walkActive || _viewport is null) return false;
        foreach (var hit in _viewport.FindHits(position))
        {
            if (hit.ModelHit is not Element3D model || !_hitMap.TryGetValue(model, out var element)) continue;
            if (element.Type != ElementTypes.Door) return false;
            double d = Math.Sqrt((element.X - _walkX) * (element.X - _walkX) + (element.Z - _walkZ) * (element.Z - _walkZ));
            if (d > WalkPhysicsService.DoorReach + 0.6) return false;
            ToggleDoor(element);
            return true;
        }
        return false;
    }

#if DEBUG
    /// <summary>Съёмка / проверка: шаги прохода тем же путём, что клавиши (dx, dz за шаг, n шагов по 1/60 с).</summary>
    public void DebugWalkSteps(double dirX, double dirZ, double distance)
    {
        if (!_walkActive) return;
        double len = Math.Sqrt(dirX * dirX + dirZ * dirZ);
        if (len < 1e-9) return;
        int steps = Math.Max(1, (int)Math.Ceiling(distance / (1.6 / 60)));
        for (int i = 0; i < steps; i++) WalkPhysicsStep(dirX / len * distance / steps, dirZ / len * distance / steps, 1.0 / 60);
        _walkEye = _walkFloor + EyeHeight;
        if (_viewport?.Camera is ProjectionCamera camera)
        {
            camera.Position = new Vector3((float)_walkX, (float)_walkEye, (float)_walkZ);
            camera.LookDirection = WalkLook();
        }
        UpdateWalkStatus();
    }

    public void DebugWalkPose(double x, double z, double feet, double yawDeg, double pitchDeg)
    {
        if (!_walkActive) return;
        _walkX = x;
        _walkZ = z;
        _walkFloor = feet;
        _walkYaw = yawDeg * Math.PI / 180;
        _walkPitch = pitchDeg * Math.PI / 180;
        _walkEye = feet + EyeHeight;
        if (_viewport?.Camera is ProjectionCamera camera)
        {
            camera.Position = new Vector3((float)_walkX, (float)_walkEye, (float)_walkZ);
            camera.LookDirection = WalkLook();
        }
        WalkPhysicsStep(0, 0, 1.0 / 60);
        UpdateWalkStatus();
    }

    public bool DebugToggleDoorAhead() => ToggleDoorAhead();

    private bool _debugHoldWalkStatus;

    /// <summary>Съёмка: индикатор упора не гаснет без движения.</summary>
    public void DebugHoldWalkStatus(bool hold) => _debugHoldWalkStatus = hold;

    public double DebugWalkX => _walkX;
    public double DebugWalkZ => _walkZ;
    public double DebugWalkFeet => _walkFloor;
#endif
}
