using System.ComponentModel;
using System.Text.Json.Serialization;
using AIArchitectStudio.Models.Transactions;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AIArchitectStudio.Models;

/// <summary>Все поддерживаемые типы BIM-элементов.</summary>
public static class ElementTypes
{
    public const string Foundation = "foundation";
    public const string Wall = "wall";
    public const string CurtainWall = "curtain_wall";
    public const string Column = "column";
    public const string Core = "core";
    public const string Beam = "beam";
    public const string Truss = "truss";
    public const string Diagrid = "diagrid";
    public const string Slab = "slab";
    public const string CantileverSlab = "cantilever_slab";
    public const string Shell = "shell";
    public const string Roof = "roof";
    public const string Stairs = "stairs";
    public const string Window = "window";
    public const string Door = "door";
    public const string Pool = "pool";
    public const string Terrace = "terrace";
    public const string Asset = "asset";

    /// <summary>Сквозной проём в стене без рамы и стекла (остаётся после удаления окна или двери).</summary>
    public const string WallOpening = "wall_opening";

    /// <summary>Сквозное отверстие любой формы в плите, покрытии или фундаменте (лестница, лифт, шахта, атриум, люк).</summary>
    public const string SlabOpening = "slab_opening";

    public static readonly IReadOnlyList<string> All =
    [
        Foundation, Wall, CurtainWall, Column, Core, Beam, Truss, Diagrid, Slab,
        CantileverSlab, Shell, Roof, Stairs, Window, Door, Pool, Terrace, Asset, WallOpening, SlabOpening
    ];

    // Строковые предикаты — для типов из JSON ИИ, импорта и UI; у элемента сцены быстрее перегрузки по BimCategory.

    /// <summary>Типы, которые могут быть «хостом» для окон, дверей и проёмов стен.</summary>
    public static bool IsHostType(string type) => IsHostType(BimCategories.FromType(type));

    /// <summary>Типы, которые всегда привязываются к стене-родителю.</summary>
    public static bool IsHostedType(string type) => IsHostedType(BimCategories.FromType(type));

    /// <summary>Плитные элементы, в которых вырезаются отверстия slab_opening (кровля — только плоская).</summary>
    public static bool IsSlabHostType(string type) => IsSlabHostType(BimCategories.FromType(type));

    /// <summary>Пустоты (проёмы без заполнения): в 3D — полупрозрачный объём выреза, в экспорте — только вырез.</summary>
    public static bool IsVoidType(string type) => IsVoidType(BimCategories.FromType(type));

    /// <summary>Стержневые конструкции с выбираемым профилем сечения.</summary>
    public static bool IsProfiledType(string type) => IsProfiledType(BimCategories.FromType(type));

    /// <summary>Горизонтальные перекрытия/покрытия, скрываемые фильтром этажа.</summary>
    public static bool IsCeilingType(string type) => IsCeilingType(BimCategories.FromType(type));

    /// <summary>Железобетонные (массивные) элементы для подсчёта объёма бетона.</summary>
    public static bool IsConcreteType(string type) => IsConcreteType(BimCategories.FromType(type));

    /// <summary>Остеклённые элементы.</summary>
    public static bool IsGlazingType(string type) => IsGlazingType(BimCategories.FromType(type));

    public static bool IsHostType(BimCategory c) => c is BimCategory.Wall or BimCategory.CurtainWall or BimCategory.Core;

    public static bool IsHostedType(BimCategory c) => c is BimCategory.Window or BimCategory.Door or BimCategory.WallOpening;

    public static bool IsSlabHostType(BimCategory c) =>
        c is BimCategory.Slab or BimCategory.CantileverSlab or BimCategory.Foundation or BimCategory.Roof or BimCategory.Terrace;

    public static bool IsVoidType(BimCategory c) => c is BimCategory.WallOpening or BimCategory.SlabOpening;

    public static bool IsProfiledType(BimCategory c) => c is BimCategory.Column or BimCategory.Beam or BimCategory.Diagrid;

    public static bool IsCeilingType(BimCategory c) => c is BimCategory.Slab or BimCategory.Roof or BimCategory.Shell;

    public static bool IsConcreteType(BimCategory c) =>
        c is BimCategory.Foundation or BimCategory.Wall or BimCategory.Column or BimCategory.Core or BimCategory.Beam or BimCategory.Slab
            or BimCategory.CantileverSlab or BimCategory.Shell or BimCategory.Stairs or BimCategory.Terrace or BimCategory.Pool;

    public static bool IsGlazingType(BimCategory c) => c is BimCategory.CurtainWall or BimCategory.Window;

    public static string Glyph(string type) => type switch
    {
        WallOpening => "",
        SlabOpening => "",
        _ => BaseGlyph(type)
    };

    private static string BaseGlyph(string type) => type switch
    {
        Foundation => "",
        Wall => "",
        CurtainWall => "",
        Column => "",
        Core => "",
        Beam => "",
        Truss => "",
        Diagrid => "",
        Slab or CantileverSlab => "",
        Shell => "",
        Roof => "",
        Stairs => "",
        Window => "",
        Door => "",
        Pool => "",
        Terrace => "",
        _ => ""
    };
}

/// <summary>Формы сечения стержневых элементов (колонны, балки).</summary>
public static class ProfileTypes
{
    /// <summary>Прямоугольник W × D (квадрат при W = D) — строго по своим сторонам.</summary>
    public const string Rectangle = "rectangle";

    /// <summary>Круг диаметром W (эллипс W × D при неравных сторонах).</summary>
    public const string Circle = "circle";

    /// <summary>Круглая труба с толщиной стенки ProfileWallThickness.</summary>
    public const string Pipe = "pipe";

    /// <summary>Двутавр: полки — ProfileWallThickness, стенка — ProfileWebThickness.</summary>
    public const string IBeam = "i_beam";

    /// <summary>Произвольный контур Polygon2D.</summary>
    public const string Polygon = "polygon";

    public static readonly IReadOnlyList<string> All = [Rectangle, Circle, Pipe, IBeam, Polygon];

    /// <summary>Приводит синонимы ИИ и чертежей («square», «round», «двутавр», «HEB», «CHS»…) к каноническому имени.</summary>
    public static string Normalize(string? raw)
    {
        var t = (raw ?? string.Empty).Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
        return t switch
        {
            "rectangle" or "rect" or "square" or "box" or "rectangular" or "прямоугольное" or "прямоугольник" or "квадрат" or "квадратное" => Rectangle,
            "circle" or "round" or "circular" or "cylinder" or "круг" or "круглое" or "круглая" => Circle,
            "pipe" or "tube" or "chs" or "hollow_circle" or "труба" => Pipe,
            "i_beam" or "ibeam" or "i" or "h" or "h_beam" or "heb" or "hea" or "ipe" or "двутавр" or "двутавровая" => IBeam,
            "polygon" or "custom" or "arbitrary" or "полигон" or "контур" => Polygon,
            _ => Rectangle
        };
    }
}

/// <summary>Формы кровли: строится только то, что задано в проекте, без самовольной подмены.</summary>
public static class RoofShapes
{
    public const string Flat = "flat";
    public const string Gable = "gable";
    public const string Hip = "hip";
    public const string Shed = "shed";

    public static readonly IReadOnlyList<string> All = [Flat, Gable, Hip, Shed];

    public static string Normalize(string? raw, string fallback = Gable)
    {
        var t = (raw ?? string.Empty).Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
        return t switch
        {
            "flat" or "plane" or "slab" or "плоская" or "плоское" or "покрытие" => Flat,
            "gable" or "pitched" or "double" or "двускатная" or "двускатное" => Gable,
            "hip" or "hipped" or "вальмовая" or "шатровая" => Hip,
            "shed" or "mono" or "single" or "lean_to" or "односкатная" => Shed,
            _ => fallback
        };
    }
}

/// <summary>Точка контура в плане (плоскость XZ, Y-Up).</summary>
public sealed class Point2D
{
    public double X { get; set; }
    public double Z { get; set; }

    public Point2D() { }
    public Point2D(double x, double z) { X = x; Z = z; }

    public Point2D Clone() => new(X, Z);
}

/// <summary>
/// Базовый BIM-элемент сцены. Система координат Y-Up: земля — плоскость XZ при Y = 0,
/// Position — геометрический центр элемента (центр элемента высотой H на отметке BaseY = BaseY + H / 2).
/// <para>
/// Фасад над <see cref="ParameterSet"/>: свойства — тонкие обёртки над фиксированными слотами встроенных параметров
/// (поведение прежних [ObservableProperty]: сравнение EqualityComparer, PropertyChanging → запись → PropertyChanged и зависимые).
/// Порядок объявления свойств задаёт порядок полей JSON в .aia — не переставлять.
/// </para>
/// <para>
/// Type-Instance (фаза 4): скрытая ссылка на неизменяемый тип <see cref="ElementType"/> из <see cref="BimTypeRegistry"/>.
/// Материал, профиль и его толщины, форма кровли читаются у типа, габариты типа по категории (сечение колонны, толщина стены…) —
/// тоже. Сеттер такого свойства тип не меняет: строит сигнатуру «текущий тип + новое значение» и переключает экземпляр на
/// найденный или новый тип («Дублировать тип» Revit) — остальные экземпляры прежнего типа не затрагиваются.
/// Size и Layers остаются материализованными у экземпляра (массив хранится как есть, список правится на месте); их доля типа ≡ типу.
/// </para>
/// <para>
/// Этажи (фаза 3): в документе элемент привязан к <see cref="BimLevel"/> (слот LevelId); LevelIndex привязанного элемента —
/// номер этажа в списке этажей постройки по отметке. Вне документа (солвер, ИИ, импорт) номер хранится в слоте LevelIndex.
/// </para>
/// <para>
/// Транзакции (фаза 7): элемент документа знает журнал <see cref="TransactionManager"/>; сеттер перед записью слота, типа, этажа
/// или списка записывает «было/стало» в открытый шаг. Undo/Redo пишут значения напрямую (<see cref="ApplySlot"/> и др.)
/// с теми же уведомлениями PropertyChanged, без повторной записи в журнал. Без журнала (вне документа) запись — одна проверка null.
/// </para>
/// </summary>
public sealed class ArchitecturalElement : ObservableObject, IBimElement
{
    /// <summary>Тип нового элемента: стена, бетон, прямоугольный профиль, двускатная кровля, габариты 1 × 1 × 1 м (прежние значения по умолчанию).</summary>
    private static readonly BimElementType DefaultType =
        BimTypeRegistry.Shared.Resolve(ElementTypes.Wall, "concrete", ProfileTypes.Rectangle, RoofShapes.Gable, 0, 0, [1, 1, 1], null);

    private readonly ParameterSet _parameters;
    private BimElementType _type;
    private BimLevel? _level;
    private BimCategory _category = BimCategory.Wall;
    private BimUniqueId.Cache? _uniqueId;
    private string _id;
    private List<Point2D> _polygon2D;
    private List<MaterialLayer> _layers;
    private TransactionManager? _journal;

    public ArchitecturalElement()
    {
        _parameters = ParameterSet.ForInstance();
        _type = DefaultType;
        _id = NewId();
        _polygon2D = [];
        _layers = [];
    }

    /// <summary>
    /// Копия: слоты параметров — по значению, векторы, контур, пирог и пользовательские значения — новые экземпляры;
    /// тип (неизменяемый) и этаж документа — общие с исходным.
    /// </summary>
    private ArchitecturalElement(ArchitecturalElement source, bool keepId)
    {
        _parameters = new ParameterSet(source._parameters);
        _type = source._type;
        _level = source._level;
        _category = source._category;
        _uniqueId = keepId ? source._uniqueId : null;
        _id = keepId ? source._id : NewId();
        _polygon2D = source._polygon2D.Select(p => p.Clone()).ToList();
        _layers = source._layers.Select(l => l.Clone()).ToList();
    }

    /// <summary>Параметры экземпляра: встроенные (слоты) и пользовательские (по Guid определения). Параметры типа — <see cref="ElementType"/>.</summary>
    [JsonIgnore] public ParameterSet Parameters => _parameters;

    /// <summary>
    /// Тип элемента (скрытая ссылка): общий для всех экземпляров с той же сигнатурой. Перед чтением сверяется с материализованными
    /// Size и Layers (пирог мог быть изменён на месте) — при расхождении экземпляр переключается на тип по текущим значениям.
    /// </summary>
    [JsonIgnore]
    public IBimElementType ElementType
    {
        get
        {
            RefreshType();
            return _type;
        }
    }

    /// <summary>Этаж документа, к которому привязан элемент (null — элемент вне документа).</summary>
    [JsonIgnore] public BimLevel? Level => _level?.Table is not null ? _level : null;

    /// <summary>Журнал изменений документа, в котором находится элемент (null — вне документа: изменения не записываются).</summary>
    [JsonIgnore]
    public TransactionManager? Journal
    {
        get => _journal;
        internal set => _journal = value;
    }

    public string Id
    {
        get => _id;
        set
        {
            if (EqualityComparer<string>.Default.Equals(_id, value)) return;
            RecordField(ElementField.Id, _id, value);
            if (Set(ref _id, value, IdArgs)) OnPropertyChanged(UniqueIdChanged);
        }
    }

    public string Name
    {
        get => _parameters.StringAt(StringSlot.Name)!;
        set
        {
            if (Set(ref _parameters.StringAt(StringSlot.Name), value, NameArgs, ParameterSlotKind.String, StringSlot.Name)) OnPropertyChanged(DisplayTitleChanged);
        }
    }

    /// <summary>Строковый тип (<see cref="ElementTypes"/>): сериализуется в .aia, IFC и JSON ИИ; в коде ядра сравнивайте <see cref="Category"/>.</summary>
    public string Type
    {
        get => _parameters.StringAt(StringSlot.Type)!;
        set
        {
            ref string? slot = ref _parameters.StringAt(StringSlot.Type);
            if (EqualityComparer<string?>.Default.Equals(slot, value)) return;
            OnPropertyChanging(TypeArgs.Changing);
            RecordSlot(ParameterSlotKind.String, StringSlot.Type, SlotValue.Of(slot), SlotValue.Of(value));
            slot = value;
            _category = BimCategories.FromType(value);
            var t = _type;
            Retype(t.Material, t.ProfileType, t.RoofShape, t.ProfileWallThickness, t.ProfileWebThickness, Size, _layers);
            OnPropertyChanged(TypeArgs.Changed);
            OnPropertyChanged(DisplayTitleChanged);
            OnPropertyChanged(GlyphChanged);
            OnPropertyChanged(CategoryChanged);
        }
    }

    /// <summary>Категория BIM — типизированное зеркало <see cref="Type"/>, пересчитывается только при смене типа (чтение — одно поле).</summary>
    [JsonIgnore] public BimCategory Category => _category;

    /// <summary>Постоянный GUID: UUID v5 от <see cref="Id"/> (не сериализуется — у старых и новых .aia одинаков для того же Id).</summary>
    [JsonIgnore] public Guid UniqueId => BimUniqueId.Resolve(ref _uniqueId, Id, BimUniqueId.ElementNamespace);

    public string BuildingGroupId
    {
        get => _parameters.StringAt(StringSlot.BuildingGroupId)!;
        set
        {
            int index = LevelIndex;
            if (!Set(ref _parameters.StringAt(StringSlot.BuildingGroupId), value, BuildingGroupIdArgs, ParameterSlotKind.String, StringSlot.BuildingGroupId)) return;
            // Этаж принадлежит постройке: элемент переходит на этаж того же номера новой постройки.
            if (_level?.Table is { } table) AttachLevel(table.Resolve(value, index));
        }
    }

    /// <summary>
    /// Номер этажа. У элемента документа вычисляется: позиция этажа <see cref="LevelId"/> в списке этажей постройки по отметке;
    /// присваивание выбирает (при необходимости создаёт) этаж с этим номером. Вне документа — значение слота.
    /// </summary>
    public int LevelIndex
    {
        get => _level is { Table: not null } level ? level.Index : _parameters.IntAt(IntSlot.LevelIndex);
        set
        {
            if (LevelIndex == value) return;
            OnPropertyChanging(LevelIndexArgs.Changing);
            // Этаж, слот LevelId и слот номера меняются вместе (одна запись журнала LevelChange).
            if (_level?.Table is { } table) AttachLevel(table.Resolve(BuildingGroupId, value));
            else SetLevelState(null, _parameters.StringAt(StringSlot.LevelId), value);
            OnPropertyChanged(LevelIndexArgs.Changed);
        }
    }

    /// <summary>Центр элемента [X, Y, Z] в метрах.</summary>
    public double[] Position
    {
        get => _parameters.VectorAt(VectorSlot.Position);
        set
        {
            if (!Set(ref _parameters.VectorAt(VectorSlot.Position), value, PositionArgs, ParameterSlotKind.Vector, VectorSlot.Position)) return;
            OnPropertyChanged(XChanged);
            OnPropertyChanged(YChanged);
            OnPropertyChanged(ZChanged);
            OnPropertyChanged(BaseYChanged);
            OnPropertyChanged(TopYChanged);
        }
    }

    /// <summary>Габариты [Ширина (вдоль локальной X), Высота (Y), Глубина/толщина (локальная Z)] в метрах.</summary>
    public double[] Size
    {
        get => _parameters.VectorAt(VectorSlot.Size);
        set
        {
            ref double[] slot = ref _parameters.VectorAt(VectorSlot.Size);
            if (EqualityComparer<double[]>.Default.Equals(slot, value)) return;
            OnPropertyChanging(SizeArgs.Changing);
            RecordSlot(ParameterSlotKind.Vector, VectorSlot.Size, SlotValue.Of(slot), SlotValue.Of(value));
            slot = value;
            // Габарит типа (сечение колонны, толщина стены…) изменился — экземпляр уходит на тип с новой сигнатурой.
            if (!_type.Key.SameSize(value))
            {
                var t = _type;
                Retype(t.Material, t.ProfileType, t.RoofShape, t.ProfileWallThickness, t.ProfileWebThickness, value, _layers);
            }
            OnPropertyChanged(SizeArgs.Changed);
            OnPropertyChanged(WidthChanged);
            OnPropertyChanged(HeightChanged);
            OnPropertyChanged(DepthChanged);
            OnPropertyChanged(BaseYChanged);
            OnPropertyChanged(TopYChanged);
        }
    }

    /// <summary>Поворот [X, Y, Z] в градусах.</summary>
    public double[] Rotation
    {
        get => _parameters.VectorAt(VectorSlot.Rotation);
        set
        {
            if (Set(ref _parameters.VectorAt(VectorSlot.Rotation), value, RotationArgs, ParameterSlotKind.Vector, VectorSlot.Rotation)) OnPropertyChanged(RotationYChanged);
        }
    }

    /// <summary>Контур в плане (локальные координаты XZ относительно Position) для полигональных перекрытий.</summary>
    public List<Point2D> Polygon2D
    {
        get => _polygon2D;
        set
        {
            if (EqualityComparer<List<Point2D>>.Default.Equals(_polygon2D, value)) return;
            RecordField(ElementField.Polygon2D, _polygon2D, value);
            Set(ref _polygon2D, value, Polygon2DArgs);
        }
    }

    /// <summary>ID стены-родителя для окон и дверей.</summary>
    public string? HostElementId
    {
        get => _parameters.StringAt(StringSlot.HostElementId);
        set => Set(ref _parameters.StringAt(StringSlot.HostElementId), value, HostElementIdArgs, ParameterSlotKind.String, StringSlot.HostElementId);
    }

    public int RepeatLevels
    {
        get => _parameters.IntAt(IntSlot.RepeatLevels);
        set => Set(ref _parameters.IntAt(IntSlot.RepeatLevels), value, RepeatLevelsArgs, ParameterSlotKind.Int, IntSlot.RepeatLevels);
    }

    public double LevelHeight
    {
        get => _parameters.DoubleAt(DoubleSlot.LevelHeight);
        set => Set(ref _parameters.DoubleAt(DoubleSlot.LevelHeight), value, LevelHeightArgs, ParameterSlotKind.Double, DoubleSlot.LevelHeight);
    }

    public double TwistAnglePerFloor
    {
        get => _parameters.DoubleAt(DoubleSlot.TwistAnglePerFloor);
        set => Set(ref _parameters.DoubleAt(DoubleSlot.TwistAnglePerFloor), value, TwistAnglePerFloorArgs, ParameterSlotKind.Double, DoubleSlot.TwistAnglePerFloor);
    }

    public double TaperScalePerFloor
    {
        get => _parameters.DoubleAt(DoubleSlot.TaperScalePerFloor);
        set => Set(ref _parameters.DoubleAt(DoubleSlot.TaperScalePerFloor), value, TaperScalePerFloorArgs, ParameterSlotKind.Double, DoubleSlot.TaperScalePerFloor);
    }

    /// <summary>Материал — параметр типа.</summary>
    public string Material
    {
        get => _type.Material;
        set
        {
            var t = _type;
            if (EqualityComparer<string?>.Default.Equals(t.Material, value)) return;
            OnPropertyChanging(MaterialArgs.Changing);
            Retype(value, t.ProfileType, t.RoofShape, t.ProfileWallThickness, t.ProfileWebThickness, Size, _layers);
            OnPropertyChanged(MaterialArgs.Changed);
        }
    }

    public bool HasPhysicsError
    {
        get => _parameters.BoolAt(BoolSlot.HasPhysicsError);
        set => Set(ref _parameters.BoolAt(BoolSlot.HasPhysicsError), value, HasPhysicsErrorArgs, ParameterSlotKind.Bool, BoolSlot.HasPhysicsError);
    }

    /// <summary>Форма сечения колонны/балки (<see cref="ProfileTypes"/>): rectangle, circle, pipe, i_beam, polygon — параметр типа.</summary>
    public string ProfileType
    {
        get => _type.ProfileType;
        set
        {
            var t = _type;
            if (EqualityComparer<string?>.Default.Equals(t.ProfileType, value)) return;
            OnPropertyChanging(ProfileTypeArgs.Changing);
            Retype(t.Material, value, t.RoofShape, t.ProfileWallThickness, t.ProfileWebThickness, Size, _layers);
            OnPropertyChanged(ProfileTypeArgs.Changed);
        }
    }

    /// <summary>Толщина стенки трубы или полки двутавра, м (0 — по пропорциям сечения) — параметр типа.</summary>
    public double ProfileWallThickness
    {
        get => _type.ProfileWallThickness;
        set
        {
            var t = _type;
            if (EqualityComparer<double>.Default.Equals(t.ProfileWallThickness, value)) return;
            OnPropertyChanging(ProfileWallThicknessArgs.Changing);
            Retype(t.Material, t.ProfileType, t.RoofShape, value, t.ProfileWebThickness, Size, _layers);
            OnPropertyChanged(ProfileWallThicknessArgs.Changed);
        }
    }

    /// <summary>Толщина стенки двутавра, м (0 — по пропорциям сечения) — параметр типа.</summary>
    public double ProfileWebThickness
    {
        get => _type.ProfileWebThickness;
        set
        {
            var t = _type;
            if (EqualityComparer<double>.Default.Equals(t.ProfileWebThickness, value)) return;
            OnPropertyChanging(ProfileWebThicknessArgs.Changing);
            Retype(t.Material, t.ProfileType, t.RoofShape, t.ProfileWallThickness, value, Size, _layers);
            OnPropertyChanged(ProfileWebThicknessArgs.Changed);
        }
    }

    /// <summary>Форма кровли (<see cref="RoofShapes"/>): flat, gable, hip, shed — только для type = roof; параметр типа.</summary>
    public string RoofShape
    {
        get => _type.RoofShape;
        set
        {
            var t = _type;
            if (EqualityComparer<string?>.Default.Equals(t.RoofShape, value)) return;
            OnPropertyChanging(RoofShapeArgs.Changing);
            Retype(t.Material, t.ProfileType, value, t.ProfileWallThickness, t.ProfileWebThickness, Size, _layers);
            OnPropertyChanged(RoofShapeArgs.Changed);
        }
    }

    /// <summary>Число подступенков марша stairs (0 — по высоте марша, ~170 мм); задаёт генератор П-образных лестниц.</summary>
    public int StepCount
    {
        get => _parameters.IntAt(IntSlot.StepCount);
        set => Set(ref _parameters.IntAt(IntSlot.StepCount), value, StepCountArgs, ParameterSlotKind.Int, IntSlot.StepCount);
    }

    /// <summary>
    /// Многослойный «пирог» конструкции (IfcMaterialLayerSet): у стены — по толщине D, у плиты, кровли и фундамента — по высоте H.
    /// Пустой список — однородный элемент из материала <see cref="Material"/>.
    /// </summary>
    public List<MaterialLayer> Layers
    {
        get => _layers;
        set
        {
            if (EqualityComparer<List<MaterialLayer>>.Default.Equals(_layers, value)) return;
            OnPropertyChanging(LayersArgs.Changing);
            RecordField(ElementField.Layers, _layers, value);
            _layers = value;
            if (!_type.Key.SameLayers(value))
            {
                var t = _type;
                Retype(t.Material, t.ProfileType, t.RoofShape, t.ProfileWallThickness, t.ProfileWebThickness, Size, value);
            }
            OnPropertyChanged(LayersArgs.Changed);
        }
    }

    /// <summary>.aia v6 (фаза 3): Id этажа документа; у элемента вне документа — Id из файла или null.</summary>
    [JsonPropertyName("levelId")]
    [JsonPropertyOrder(1)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LevelId
    {
        get => _level?.Table is not null ? _level.Id : _parameters.StringAt(StringSlot.LevelId);
        set
        {
            if (_level?.Table is { } table)
            {
                if (string.Equals(_level.Id, value, StringComparison.Ordinal)) return;
                // Этаж той же постройки из таблицы документа — привязка (номер следует за этажом), иначе — отвязка.
                if (table.Find(value) is { } level && level.BuildingGroupId == BuildingGroupId)
                {
                    int before = LevelIndex;
                    if (before != level.Index) OnPropertyChanging(LevelIndexArgs.Changing);
                    AttachLevel(level);
                    if (before != level.Index) OnPropertyChanged(LevelIndexArgs.Changed);
                    return;
                }
                SetLevelState(null, value, LevelIndex);
                return;
            }
            SetLevelState(_level, value, _parameters.IntAt(IntSlot.LevelIndex));
        }
    }

    /// <summary>.aia v6 (фаза 4): Guid типа элемента (UUID v5 сигнатуры); при чтении файла тип восстанавливается по значениям элемента.</summary>
    [JsonPropertyName("typeId")]
    [JsonPropertyOrder(1)]
    public Guid TypeId => ElementType.UniqueId;

    /// <summary>
    /// .aia v6: значения пользовательских параметров (Guid определения → значение) — блок «parameters» элемента.
    /// null — значений нет, блок не пишется (файл без пользовательских параметров совпадает с v5 по составу полей).
    /// Порядок 1 — после всех полей v5 (у них порядок 0, сортировка устойчивая): без него сериализатор ставит блок первым.
    /// </summary>
    [JsonPropertyName("parameters")]
    [JsonPropertyOrder(1)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<Guid, ParameterValue>? UserParameters
    {
        get => _parameters.UserValues;
        set
        {
            var before = IsRecording ? UserSnapshot() : null;
            _parameters.ReplaceUser(value);
            if (IsRecording) RecordField(ElementField.UserParameters, before, UserSnapshot());
            OnPropertyChanged(UserParametersChanged);
        }
    }

    [JsonIgnore] public double X => Position.Length > 0 ? Position[0] : 0;
    [JsonIgnore] public double Y => Position.Length > 1 ? Position[1] : 0;
    [JsonIgnore] public double Z => Position.Length > 2 ? Position[2] : 0;
    // Габарит типа читается у типа (сечение колонны, толщина стены…), габарит экземпляра — из Size; значения совпадают до бита.
    [JsonIgnore] public double Width => (_type.Dimensions & TypeDimensions.Width) != 0 ? _type.Width : Size.Length > 0 ? Size[0] : 0;
    [JsonIgnore] public double Height => (_type.Dimensions & TypeDimensions.Height) != 0 ? _type.Height : Size.Length > 1 ? Size[1] : 0;
    [JsonIgnore] public double Depth => (_type.Dimensions & TypeDimensions.Depth) != 0 ? _type.Depth : Size.Length > 2 ? Size[2] : 0;
    [JsonIgnore] public double RotationY => Rotation.Length > 1 ? Rotation[1] : 0;

    /// <summary>Нижняя отметка элемента.</summary>
    [JsonIgnore] public double BaseY => Y - Height / 2.0;

    /// <summary>Верхняя отметка элемента.</summary>
    [JsonIgnore] public double TopY => Y + Height / 2.0;

    [JsonIgnore] public string Glyph => ElementTypes.Glyph(Type);

    [JsonIgnore]
    public string DisplayTitle => string.IsNullOrWhiteSpace(Name) ? $"{Type} · {Id}" : Name;

    /// <summary>Несущая конструкция: признак «Несущая конструкция» не задан или «да» (<see cref="StructuralRole"/>); «нет» — ненесущая перегородка.</summary>
    [JsonIgnore] public bool IsLoadBearing => StructuralRole.IsLoadBearing(this);

    public void SetPosition(double x, double y, double z) => Position = [x, y, z];

    public void SetSize(double width, double height, double depth) =>
        Size = [Math.Max(0.01, width), Math.Max(0.01, height), Math.Max(0.01, depth)];

    public void SetRotationY(double degrees) =>
        Rotation = [Rotation.Length > 0 ? Rotation[0] : 0, NormalizeAngle(degrees), Rotation.Length > 2 ? Rotation[2] : 0];

    /// <summary>Ставит элемент на отметку baseY (центр = baseY + H / 2).</summary>
    public void PlaceOnBase(double baseY) => SetPosition(X, baseY + Height / 2.0, Z);

    // ───────────────────────────── Параметры по идентификатору (Revit: Element.get_Parameter / Parameter.Set) ─────────────────────────────

    /// <summary>Значение встроенного параметра (из слота экземпляра или типа, без упаковки; LevelIndex — вычисленный по этажу).</summary>
    public ParameterValue Get(BuiltInParameter parameter)
    {
        if (BuiltInParameters.Info(parameter).IsTypeParameter) return _type.Parameters.Get(parameter);
        return parameter switch
        {
            BuiltInParameter.LevelIndex => ParameterValue.FromInt(LevelIndex),
            BuiltInParameter.LevelId => ParameterValue.FromElementId(LevelId),
            _ => _parameters.Get(parameter)
        };
    }

    /// <summary>
    /// Запись встроенного параметра через свойство фасада (уведомления, Category, зависимые свойства — как у прямого присваивания).
    /// Значение приводится к типу хранения; компонента вектора (Width, PositionY…) пишется новым массивом, как SetSize/SetPosition.
    /// </summary>
    public bool Set(BuiltInParameter parameter, ParameterValue value)
    {
        ref readonly var info = ref BuiltInParameters.Info(parameter);
        var v = value.ConvertTo(info.StorageType);
        if (v.IsEmpty) return false;
        if (info.IsVectorComponent)
        {
            ref double[] slot = ref _parameters.VectorAt(info.Slot);
            double d = v.AsDouble();
            if (slot.Length > info.Component && slot[info.Component].Equals(d)) return false;
            var next = new double[Math.Max(3, slot.Length)];
            slot.CopyTo(next, 0);
            next[info.Component] = d;
            switch (info.Slot)
            {
                case VectorSlot.Position: Position = next; break;
                case VectorSlot.Size: Size = next; break;
                default: Rotation = next; break;
            }
            return true;
        }

        var before = Get(parameter);
        switch (parameter)
        {
            case BuiltInParameter.Name: Name = v.AsString() ?? string.Empty; break;
            case BuiltInParameter.ElementType: Type = v.AsString() ?? string.Empty; break;
            case BuiltInParameter.BuildingGroup: BuildingGroupId = v.AsString() ?? string.Empty; break;
            case BuiltInParameter.Material: Material = v.AsString() ?? string.Empty; break;
            case BuiltInParameter.ProfileType: ProfileType = v.AsString() ?? string.Empty; break;
            case BuiltInParameter.RoofShape: RoofShape = v.AsString() ?? string.Empty; break;
            case BuiltInParameter.HostElement: HostElementId = string.IsNullOrEmpty(v.AsString()) ? null : v.AsString(); break;
            case BuiltInParameter.LevelIndex: LevelIndex = v.AsInt(); break;
            case BuiltInParameter.RepeatLevels: RepeatLevels = v.AsInt(); break;
            case BuiltInParameter.StepCount: StepCount = v.AsInt(); break;
            case BuiltInParameter.LevelHeight: LevelHeight = v.AsDouble(); break;
            case BuiltInParameter.TwistAnglePerFloor: TwistAnglePerFloor = v.AsDouble(); break;
            case BuiltInParameter.TaperScalePerFloor: TaperScalePerFloor = v.AsDouble(); break;
            case BuiltInParameter.ProfileWallThickness: ProfileWallThickness = v.AsDouble(); break;
            case BuiltInParameter.ProfileWebThickness: ProfileWebThickness = v.AsDouble(); break;
            case BuiltInParameter.HasPhysicsError: HasPhysicsError = v.AsBool(); break;
            case BuiltInParameter.LevelId: LevelId = string.IsNullOrEmpty(v.AsString()) ? null : v.AsString(); break;
            default: return false;
        }
        return Get(parameter) != before;
    }

    // ───────────────────────────── Тип и этаж (Type-Instance, Levels) ─────────────────────────────

    /// <summary>Экземпляр переходит на тип с сигнатурой из переданных значений (поиск дубликата в реестре или новый тип).</summary>
    private void Retype(string? material, string? profile, string? roof, double wall, double web, double[]? size, IReadOnlyList<MaterialLayer>? layers)
    {
        var old = _type;
        _type = BimTypeRegistry.Shared.Resolve(new BimTypeKey(_parameters.StringAt(StringSlot.Type), material, profile, roof, wall, web, size, layers));
        if (!ReferenceEquals(old, _type) && IsRecording) _journal!.Record(new TypeChange(this, old, _type));
    }

    /// <summary>
    /// Сверяет тип с материализованными Size и Layers и при расхождении (пирог изменён на месте — AbsorbThickness, нормализация
    /// файла) переключает экземпляр на тип по текущим значениям. Свойства фасада при этом не меняются.
    /// </summary>
    public void RefreshType()
    {
        var t = _type;
        var size = Size;
        if (t.Key.SameSize(size) && t.Key.SameLayers(_layers)) return;
        Retype(t.Material, t.ProfileType, t.RoofShape, t.ProfileWallThickness, t.ProfileWebThickness, size, _layers);
    }

    /// <summary>Привязка к этажу таблицы документа (BimLevelTable.Bind/Resolve): LevelId и слот номера — по этажу.</summary>
    internal void AttachLevel(BimLevel level) => SetLevelState(level, level.Id, level.Index);

    /// <summary>Этаж документа и слоты LevelId / LevelIndex — одной записью журнала.</summary>
    private void SetLevelState(BimLevel? level, string? levelId, int index)
    {
        ref string? idSlot = ref _parameters.StringAt(StringSlot.LevelId);
        ref int indexSlot = ref _parameters.IntAt(IntSlot.LevelIndex);
        if (ReferenceEquals(_level, level) && string.Equals(idSlot, levelId, StringComparison.Ordinal) && indexSlot == index) return;
        if (IsRecording) _journal!.Record(new LevelChange(this, new LevelState(_level, idSlot, indexSlot), new LevelState(level, levelId, index)));
        _level = level;
        idSlot = levelId;
        indexSlot = index;
    }

    /// <summary>Значение пользовательского параметра (Empty — не задано у элемента).</summary>
    public ParameterValue GetUserParameter(Guid definition) => _parameters.TryGetUser(definition, out var value) ? value : ParameterValue.Empty;

    /// <summary>Записывает пользовательский параметр, приводя значение к типу хранения определения; true — значение изменилось.</summary>
    public bool SetUserParameter(ParameterDefinition definition, ParameterValue value)
    {
        var v = value.ConvertTo(definition.StorageType);
        if (v.IsEmpty) return false;
        var before = IsRecording ? UserSnapshot() : null;
        if (!_parameters.SetUser(definition.Guid, v)) return false;
        if (IsRecording) RecordField(ElementField.UserParameters, before, UserSnapshot());
        OnPropertyChanged(UserParametersChanged);
        return true;
    }

    public bool RemoveUserParameter(Guid definition)
    {
        var before = IsRecording ? UserSnapshot() : null;
        if (!_parameters.RemoveUser(definition)) return false;
        if (IsRecording) RecordField(ElementField.UserParameters, before, UserSnapshot());
        OnPropertyChanged(UserParametersChanged);
        return true;
    }

    /// <summary>После чтения .aia: значения приводятся к типу хранения своих определений; без определения или непредставимые — отбрасываются.</summary>
    internal void NormalizeUserParameters(Func<Guid, ParameterDefinition?> resolve)
    {
        if (_parameters.UserValues is not { } values) return;
        var before = IsRecording ? UserSnapshot() : null;
        var normalized = new List<KeyValuePair<Guid, ParameterValue>>(values.Count);
        foreach (var (guid, value) in values)
            if (resolve(guid) is { } definition && value.ConvertTo(definition.StorageType) is { IsEmpty: false } v)
                normalized.Add(new(guid, v));
        _parameters.ReplaceUser(normalized);
        if (IsRecording) RecordField(ElementField.UserParameters, before, UserSnapshot());
    }

    // ───────────────────────────── Журнал транзакций: запись и прямое применение ─────────────────────────────

    private bool IsRecording => _journal is { IsRecording: true };

    private Dictionary<Guid, ParameterValue>? UserSnapshot() => _parameters.UserValues is { } values ? new Dictionary<Guid, ParameterValue>(values) : null;

    private void RecordSlot(ParameterSlotKind kind, int slot, SlotValue oldValue, SlotValue newValue)
    {
        if (IsRecording) _journal!.Record(new ParameterChange(this, kind, slot, oldValue, newValue));
    }

    private void RecordField(ElementField field, object? oldValue, object? newValue)
    {
        if (IsRecording) _journal!.Record(new ElementFieldChange(this, field, oldValue, newValue));
    }

    /// <summary>Перед правкой слоёв пирога на месте (толщина несущего слоя): содержимое «до» уходит в журнал.</summary>
    internal void BeginLayersEdit()
    {
        if (IsRecording) _journal!.Record(new LayerContentChange(this, _layers));
    }

    /// <summary>Undo/Redo: значение слота пишется как есть (без смены типа и без записи), уведомления — как у сеттера фасада.</summary>
    internal void ApplySlot(ParameterSlotKind kind, int slot, SlotValue value)
    {
        var args = SlotArgs(kind, slot);
        if (args is not null) OnPropertyChanging(args.Changing);
        switch (kind)
        {
            case ParameterSlotKind.Double: _parameters.DoubleAt(slot) = value.Number; break;
            case ParameterSlotKind.Int: _parameters.IntAt(slot) = (int)value.Number; break;
            case ParameterSlotKind.Bool: _parameters.BoolAt(slot) = value.Number != 0; break;
            case ParameterSlotKind.Vector: _parameters.VectorAt(slot) = (double[])value.Reference!; break;
            default:
                _parameters.StringAt(slot) = (string?)value.Reference;
                if (slot == StringSlot.Type) _category = BimCategories.FromType((string?)value.Reference ?? string.Empty);
                break;
        }
        if (args is null) return;
        OnPropertyChanged(args.Changed);
        switch (kind)
        {
            case ParameterSlotKind.Vector when slot == VectorSlot.Position:
                OnPropertyChanged(XChanged);
                OnPropertyChanged(YChanged);
                OnPropertyChanged(ZChanged);
                OnPropertyChanged(BaseYChanged);
                OnPropertyChanged(TopYChanged);
                break;
            case ParameterSlotKind.Vector when slot == VectorSlot.Size:
                OnPropertyChanged(WidthChanged);
                OnPropertyChanged(HeightChanged);
                OnPropertyChanged(DepthChanged);
                OnPropertyChanged(BaseYChanged);
                OnPropertyChanged(TopYChanged);
                break;
            case ParameterSlotKind.Vector:
                OnPropertyChanged(RotationYChanged);
                break;
            case ParameterSlotKind.String when slot == StringSlot.Name:
                OnPropertyChanged(DisplayTitleChanged);
                break;
            case ParameterSlotKind.String when slot == StringSlot.Type:
                OnPropertyChanged(DisplayTitleChanged);
                OnPropertyChanged(GlyphChanged);
                OnPropertyChanged(CategoryChanged);
                break;
        }
    }

    /// <summary>Undo/Redo: тип экземпляра (параметры типа — материал, профиль, форма кровли, габарит типа).</summary>
    internal void ApplyType(BimElementType type)
    {
        if (ReferenceEquals(_type, type)) return;
        _type = type;
        OnPropertyChanged(MaterialArgs.Changed);
        OnPropertyChanged(ProfileTypeArgs.Changed);
        OnPropertyChanged(ProfileWallThicknessArgs.Changed);
        OnPropertyChanged(ProfileWebThicknessArgs.Changed);
        OnPropertyChanged(RoofShapeArgs.Changed);
        OnPropertyChanged(WidthChanged);
        OnPropertyChanged(HeightChanged);
        OnPropertyChanged(DepthChanged);
    }

    /// <summary>Undo/Redo: этаж документа и слоты LevelId / LevelIndex.</summary>
    internal void ApplyLevel(LevelState state)
    {
        OnPropertyChanging(LevelIndexArgs.Changing);
        _level = state.Level;
        _parameters.StringAt(StringSlot.LevelId) = state.LevelId;
        _parameters.IntAt(IntSlot.LevelIndex) = state.LevelIndex;
        OnPropertyChanged(LevelIndexArgs.Changed);
    }

    /// <summary>Undo/Redo: поле вне слотов (Id, контур, список слоёв, пользовательские параметры — копия словаря).</summary>
    internal void ApplyField(ElementField field, object? value)
    {
        switch (field)
        {
            case ElementField.Id:
                if (Set(ref _id, (string)value!, IdArgs)) OnPropertyChanged(UniqueIdChanged);
                break;
            case ElementField.Polygon2D:
                Set(ref _polygon2D, (List<Point2D>)value!, Polygon2DArgs);
                break;
            case ElementField.Layers:
                Set(ref _layers, (List<MaterialLayer>)value!, LayersArgs);
                break;
            default:
                _parameters.ReplaceUser(value as IReadOnlyDictionary<Guid, ParameterValue>);
                OnPropertyChanged(UserParametersChanged);
                break;
        }
    }

    /// <summary>Undo/Redo правки пирога на месте: содержимое того же объекта списка заменяется копиями слоёв.</summary>
    internal void ApplyLayerContent(List<MaterialLayer> target, IReadOnlyList<MaterialLayer> content)
    {
        OnPropertyChanging(LayersArgs.Changing);
        target.Clear();
        foreach (var layer in content) target.Add(layer.Clone());
        OnPropertyChanged(LayersArgs.Changed);
    }

    private static NotifyArgs? SlotArgs(ParameterSlotKind kind, int slot) => kind switch
    {
        ParameterSlotKind.Double => slot switch
        {
            DoubleSlot.LevelHeight => LevelHeightArgs,
            DoubleSlot.TwistAnglePerFloor => TwistAnglePerFloorArgs,
            DoubleSlot.TaperScalePerFloor => TaperScalePerFloorArgs,
            _ => null
        },
        ParameterSlotKind.Int => slot switch
        {
            IntSlot.LevelIndex => LevelIndexArgs,
            IntSlot.RepeatLevels => RepeatLevelsArgs,
            _ => StepCountArgs
        },
        ParameterSlotKind.Bool => HasPhysicsErrorArgs,
        ParameterSlotKind.Vector => slot switch
        {
            VectorSlot.Position => PositionArgs,
            VectorSlot.Size => SizeArgs,
            _ => RotationArgs
        },
        _ => slot switch
        {
            StringSlot.Name => NameArgs,
            StringSlot.Type => TypeArgs,
            StringSlot.BuildingGroupId => BuildingGroupIdArgs,
            StringSlot.HostElementId => HostElementIdArgs,
            _ => null
        }
    };

    /// <summary>Нормализованная форма сечения: явный Polygon2D всегда побеждает.</summary>
    [JsonIgnore]
    public string EffectiveProfile => Polygon2D.Count >= 3 ? ProfileTypes.Polygon : ProfileTypes.Normalize(ProfileType);

    /// <summary>Плоская кровля (вырезаются отверстия slab_opening, экспорт — экструзией).</summary>
    [JsonIgnore]
    public bool IsFlatRoof => _category == BimCategory.Roof && RoofShapes.Normalize(RoofShape) == RoofShapes.Flat;

    /// <summary>Может ли элемент быть хостом отверстий плиты.</summary>
    [JsonIgnore]
    public bool CanHostSlabOpenings => ElementTypes.IsSlabHostType(_category) && (_category != BimCategory.Roof || IsFlatRoof);

    /// <summary>
    /// Толщины трубы / двутавра для сечения b × h (b — ширина полки, h — высота сечения): заданные явно или по пропорциям.
    /// </summary>
    public (double Wall, double Web) ProfileThicknesses(double b, double h)
    {
        double size = Math.Max(0.01, Math.Min(b, h));
        double wall = ProfileWallThickness > 0 ? ProfileWallThickness : Math.Max(0.006, size * (EffectiveProfile == ProfileTypes.Pipe ? 0.08 : 0.07));
        double web = ProfileWebThickness > 0 ? ProfileWebThickness : Math.Max(0.005, wall * 0.6);
        wall = Math.Min(wall, Math.Min(b, h) / 2 * 0.95);
        web = Math.Min(web, b * 0.9);
        return (wall, web);
    }

    /// <summary>Площадь в плане: по контуру Polygon2D (формула Гаусса) или по габаритам.</summary>
    public double PlanArea()
    {
        if (Polygon2D.Count >= 3)
        {
            double sum = 0;
            for (int i = 0; i < Polygon2D.Count; i++)
            {
                var a = Polygon2D[i];
                var b = Polygon2D[(i + 1) % Polygon2D.Count];
                sum += a.X * b.Z - b.X * a.Z;
            }
            return Math.Abs(sum) / 2.0;
        }
        return Width * Depth;
    }

    /// <summary>Сумма коэффициентов площади по всем повторяемым этажам с учётом сужения TaperScalePerFloor.</summary>
    public double RepeatAreaFactor()
    {
        int count = Math.Max(1, RepeatLevels);
        double factor = 0, scale = 1;
        for (int i = 0; i < count; i++)
        {
            factor += scale * scale;
            scale *= TaperScalePerFloor <= 0 ? 1 : TaperScalePerFloor;
        }
        return factor;
    }

    public ArchitecturalElement Clone(bool keepId = true) => new(this, keepId);

    public static string NewId() => "el_" + Guid.NewGuid().ToString("N")[..10];

    public static double NormalizeAngle(double degrees)
    {
        double a = degrees % 360.0;
        if (a > 180) a -= 360;
        if (a <= -180) a += 360;
        return a;
    }

    /// <summary>Поворот вектора (x, z) вокруг оси Y на угол в градусах (правая система, Y-Up).</summary>
    public static (double X, double Z) RotateY(double x, double z, double degrees)
    {
        double r = degrees * Math.PI / 180.0;
        double c = Math.Cos(r), s = Math.Sin(r);
        return (x * c + z * s, -x * s + z * c);
    }

    // ───────────────────────────── Уведомления (кэш аргументов, как у сгенерированных свойств) ─────────────────────────────

    /// <summary>Сгенерированный сеттер [ObservableProperty]: равное значение — без событий, иначе PropertyChanging → запись → PropertyChanged.</summary>
    private bool Set<T>(ref T slot, T value, NotifyArgs args)
    {
        if (EqualityComparer<T>.Default.Equals(slot, value)) return false;
        OnPropertyChanging(args.Changing);
        slot = value;
        OnPropertyChanged(args.Changed);
        return true;
    }

    /// <summary>То же для слота встроенного параметра: «было/стало» записывается в журнал открытого шага.</summary>
    private bool Set<T>(ref T slot, T value, NotifyArgs args, ParameterSlotKind kind, int index)
    {
        if (EqualityComparer<T>.Default.Equals(slot, value)) return false;
        OnPropertyChanging(args.Changing);
        if (IsRecording) _journal!.Record(new ParameterChange(this, kind, index, SlotValue.Of(slot), SlotValue.Of(value)));
        slot = value;
        OnPropertyChanged(args.Changed);
        return true;
    }

    private sealed class NotifyArgs(string name)
    {
        public PropertyChangingEventArgs Changing { get; } = new(name);
        public PropertyChangedEventArgs Changed { get; } = new(name);
    }

    private static readonly NotifyArgs IdArgs = new(nameof(Id)), NameArgs = new(nameof(Name)), TypeArgs = new(nameof(Type)),
        BuildingGroupIdArgs = new(nameof(BuildingGroupId)), LevelIndexArgs = new(nameof(LevelIndex)), PositionArgs = new(nameof(Position)),
        SizeArgs = new(nameof(Size)), RotationArgs = new(nameof(Rotation)), Polygon2DArgs = new(nameof(Polygon2D)), HostElementIdArgs = new(nameof(HostElementId)),
        RepeatLevelsArgs = new(nameof(RepeatLevels)), LevelHeightArgs = new(nameof(LevelHeight)), TwistAnglePerFloorArgs = new(nameof(TwistAnglePerFloor)),
        TaperScalePerFloorArgs = new(nameof(TaperScalePerFloor)), MaterialArgs = new(nameof(Material)), HasPhysicsErrorArgs = new(nameof(HasPhysicsError)),
        ProfileTypeArgs = new(nameof(ProfileType)), ProfileWallThicknessArgs = new(nameof(ProfileWallThickness)),
        ProfileWebThicknessArgs = new(nameof(ProfileWebThickness)), RoofShapeArgs = new(nameof(RoofShape)), StepCountArgs = new(nameof(StepCount)),
        LayersArgs = new(nameof(Layers));

    private static readonly PropertyChangedEventArgs UniqueIdChanged = new(nameof(UniqueId)), DisplayTitleChanged = new(nameof(DisplayTitle)),
        GlyphChanged = new(nameof(Glyph)), CategoryChanged = new(nameof(Category)), XChanged = new(nameof(X)), YChanged = new(nameof(Y)),
        ZChanged = new(nameof(Z)), BaseYChanged = new(nameof(BaseY)), TopYChanged = new(nameof(TopY)), WidthChanged = new(nameof(Width)),
        HeightChanged = new(nameof(Height)), DepthChanged = new(nameof(Depth)), RotationYChanged = new(nameof(RotationY)),
        UserParametersChanged = new(nameof(UserParameters));
}
