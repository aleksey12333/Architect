using System.Numerics;
using AIArchitectStudio.Models;
using AIArchitectStudio.Models.Geometry;

namespace AIArchitectStudio.Rendering;

/// <summary>
/// Вращаемое 3D-превью BIM-модели без графического движка (студия «Реконструкция по фото и плану»): геометрия — тот же
/// <see cref="ElementGeometryBuilder"/>, что у вьюпорта; цвет части — цвет фасада со снимка (<see cref="CurvedDesign.FacadeColor"/>)
/// или базовый цвет материала; освещение — Солнце с мягкой тенью и рассеянный свет неба, стекло — полупрозрачным тоном с бликом,
/// рёбра элементов — тонкими линиями. Камера — орбита вокруг центра габарита (азимут и высота в градусах).
/// </summary>
public static class ElementPreviewRenderer
{
    private static readonly Vector3 ToSun = Vector3.Normalize(new Vector3(-0.5f, 0.75f, -0.45f));

    public static byte[] Render(IReadOnlyList<ArchitecturalElement> elements, int width, int height, double azimuthDeg, double elevationDeg, bool dark,
        int supersample = 1, bool shadows = true)
    {
        var raster = new SoftwareRasterizer();
        var colors = new List<(Vector3 Color, bool Glass)>();
        var hostedBy = elements.Where(e => e.HostElementId is not null).GroupBy(e => e.HostElementId!).ToDictionary(g => g.Key, g => (IReadOnlyList<ArchitecturalElement>)g.ToList());
        foreach (var e in elements)
        {
            if (e.Category is BimCategory.WallOpening or BimCategory.SlabOpening) continue;
            ElementGeometry g;
            try
            {
                g = ElementGeometryBuilder.Build(e, hostedBy.GetValueOrDefault(e.Id) ?? [], new GeometryContext(-1, 0, 0));
            }
            catch (ArgumentException)
            {
                continue;
            }
            string? tint = CurvedDesign.FacadeColor(e);
            foreach (var part in g.Parts)
            {
                if (part.Material == MaterialKind.Void) continue;
                bool glass = MaterialCatalog.IsTransparent(part.Material);
                var baseColor = MaterialCatalog.BaseColor(part.Material);
                var color = new Vector3(baseColor.Red, baseColor.Green, baseColor.Blue);
                if (tint is not null && IsTintable(part.Material) && CurvedDesign.TryParseHex(tint, out var rgb)) color = new Vector3(rgb.R / 255f, rgb.G / 255f, rgb.B / 255f);
                int material = colors.Count;
                colors.Add((color, glass));
                var mesh = part.Mesh;
                var pos = mesh.Positions;
                var nrm = mesh.Normals;
                var idx = mesh.TriangleIndices;
                for (int i = 0; i + 2 < idx.Count; i += 3)
                {
                    RasterVertex V(int k) => new(Vector3.Transform(pos[idx[k]], g.Transform), nrm is null ? Vector3.UnitY : Vector3.Normalize(Vector3.TransformNormal(nrm[idx[k]], g.Transform)), Vector2.Zero);
                    raster.AddTriangle(V(i), V(i + 1), V(i + 2), material);
                }
            }
            if (g.Edges is { } edges)
            {
                var ep = edges.Positions;
                var ei = edges.Indices;
                uint edge = dark ? 0x70202428u : 0x8C2C2E33u;
                for (int i = 0; i + 1 < ei.Count; i += 2)
                    raster.AddLine(Vector3.Transform(ep[ei[i]], g.Transform), Vector3.Transform(ep[ei[i + 1]], g.Transform), edge);
            }
        }
        // Земля под зданием (принимает тень).
        var (min, max) = raster.Bounds();
        var center = (min + max) / 2;
        float radius = MathF.Max(5, (max - min).Length() / 2);
        int ground = colors.Count;
        colors.Add((dark ? new Vector3(0.20f, 0.21f, 0.23f) : new Vector3(0.86f, 0.87f, 0.85f), false));
        float gr = radius * 1.6f;
        var up = Vector3.UnitY;
        raster.AddTriangle(new(new(center.X - gr, -0.02f, center.Z - gr), up, Vector2.Zero), new(new(center.X - gr, -0.02f, center.Z + gr), up, Vector2.Zero), new(new(center.X + gr, -0.02f, center.Z + gr), up, Vector2.Zero), ground);
        raster.AddTriangle(new(new(center.X - gr, -0.02f, center.Z - gr), up, Vector2.Zero), new(new(center.X + gr, -0.02f, center.Z + gr), up, Vector2.Zero), new(new(center.X + gr, -0.02f, center.Z - gr), up, Vector2.Zero), ground);

        double az = azimuthDeg * Math.PI / 180, el = Math.Clamp(elevationDeg, 5, 85) * Math.PI / 180;
        float dist = radius / MathF.Tan(19f * MathF.PI / 180f) * 0.92f;
        var eye = center + new Vector3((float)(Math.Cos(el) * Math.Sin(az)), (float)Math.Sin(el), (float)(-Math.Cos(el) * Math.Cos(az))) * dist;
        var camera = new RasterCamera(eye, center, 38);
        var skyTop = dark ? new Vector3(0.10f, 0.11f, 0.13f) : new Vector3(0.80f, 0.86f, 0.93f);
        var skyLow = dark ? new Vector3(0.16f, 0.17f, 0.20f) : new Vector3(0.95f, 0.96f, 0.97f);

        Vector3 Shade(RasterFragment f)
        {
            var (c, glass) = colors[f.Material];
            float diff = MathF.Max(0, Vector3.Dot(f.Normal, ToSun)) * (0.45f + 0.55f * f.SunVisibility);
            var ambient = Vector3.Lerp(new Vector3(0.42f, 0.40f, 0.38f), new Vector3(0.58f, 0.63f, 0.70f), f.Normal.Y * 0.5f + 0.5f) * 0.55f;
            var lit = c * (new Vector3(1f, 0.97f, 0.92f) * diff * 0.85f + ambient);
            if (!glass) return lit;
            var reflect = Vector3.Reflect(-f.ToEye, f.Normal);
            float spec = MathF.Pow(MathF.Max(0, Vector3.Dot(reflect, ToSun)), 60);
            return Vector3.Lerp(c * 0.55f, skyLow * c * 1.4f, 0.35f + 0.4f * MathF.Pow(1 - Math.Clamp(Vector3.Dot(f.Normal, f.ToEye), 0, 1), 3)) + new Vector3(spec * 0.6f);
        }

        Vector3 Background(float fx, float fy) => Vector3.Lerp(skyTop, skyLow, fy);
        return raster.Render(camera, width, height, supersample, shadows ? ToSun : null, Shade, Background, 1536);
    }

    private static bool IsTintable(MaterialKind kind) => kind is not (MaterialKind.Frame or MaterialKind.Void or MaterialKind.DoorWood or MaterialKind.Water
        or MaterialKind.Insulation or MaterialKind.Screed or MaterialKind.Waterproofing);
}
