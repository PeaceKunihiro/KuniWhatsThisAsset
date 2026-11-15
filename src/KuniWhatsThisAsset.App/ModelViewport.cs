using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using KuniWhatsThisAsset.Core;

namespace KuniWhatsThisAsset.App;
public sealed class ModelViewport : Grid
{
    private readonly Viewport3D viewport = new() { ClipToBounds = true, IsHitTestVisible = false };
    private readonly PerspectiveCamera camera = new() { FieldOfView = 45 };
    private readonly TextBlock info = new() { Margin = new Thickness(10), VerticalAlignment = VerticalAlignment.Bottom, TextWrapping = TextWrapping.Wrap, Background = new SolidColorBrush(Color.FromArgb(220, 238, 223, 203)) };
    private Point last;
    private double yaw = .5, pitch = .15, distance = 4;
    private Vector3D pan;
    public ModelViewport()
    {
        Background = new SolidColorBrush(Color.FromRgb(238, 223, 203));
        viewport.Camera = camera; Children.Add(viewport); Children.Add(info);
        var reset = new Button { Content = "全体表示・正面復帰", HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8), Padding = new Thickness(8, 3, 8, 3) };
        reset.Click += (_, _) => Reset(); Children.Add(reset);
        MouseDown += (_, e) => { last = e.GetPosition(this); CaptureMouse(); e.Handled = true; };
        MouseUp += (_, e) => { ReleaseMouseCapture(); e.Handled = true; };
        MouseMove += (_, e) =>
        {
            if (!IsMouseCaptured) return;
            Point point = e.GetPosition(this); Vector delta = point - last; last = point;
            if (e.RightButton == MouseButtonState.Pressed || e.MiddleButton == MouseButtonState.Pressed || (Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            {
                Vector3D forward = camera.LookDirection; forward.Normalize();
                Vector3D right = Vector3D.CrossProduct(forward, camera.UpDirection); right.Normalize();
                Vector3D up = Vector3D.CrossProduct(right, forward); up.Normalize();
                pan += (-right * delta.X + up * delta.Y) * distance / Math.Max(ActualHeight, 100);
            }
            else { yaw -= delta.X * .01; pitch = Math.Clamp(pitch + delta.Y * .01, -1.45, 1.45); }
            UpdateCamera(); e.Handled = true;
        };
        MouseWheel += (_, e) => { distance = Math.Clamp(distance * Math.Exp(-e.Delta / 1000d), .05, 100); UpdateCamera(); e.Handled = true; };
        SizeChanged += (_, _) => UpdateCamera();
    }
    public void ClearModel() { viewport.Children.Clear(); info.Text = ""; }
    public void SetModel(ModelPreviewData model, IReadOnlyDictionary<string, Material>? materials = null)
    {
        model.Validate();
        ClearModel();
        var content = new Model3DGroup();
        var bounds = Rect3D.Empty;
        foreach (var mesh in model.Meshes)
            for (int i = 0; i < mesh.Positions.Length; i += 3) bounds.Union(new Point3D(mesh.Positions[i], mesh.Positions[i + 1], mesh.Positions[i + 2]));
        double scale = Math.Max(bounds.SizeX, Math.Max(bounds.SizeY, bounds.SizeZ));
        if (scale <= 1e-12) scale = 1;
        var center = new Point3D(bounds.X + bounds.SizeX / 2, bounds.Y + bounds.SizeY / 2, bounds.Z + bounds.SizeZ / 2);
        foreach (var mesh in model.Meshes)
        {
            var points = new Point3DCollection(mesh.Positions.Length / 3);
            var normals = new Vector3DCollection(mesh.Normals.Length / 3);
            for (int i = 0; i < mesh.Positions.Length; i += 3)
            {
                points.Add(new((mesh.Positions[i] - center.X) / scale * 2, (mesh.Positions[i + 1] - center.Y) / scale * 2, (mesh.Positions[i + 2] - center.Z) / scale * 2));
                normals.Add(new(mesh.Normals[i], mesh.Normals[i + 1], mesh.Normals[i + 2]));
            }
            var geometry = new MeshGeometry3D { Positions = points, Normals = normals, TriangleIndices = new Int32Collection(mesh.Indices) };
            if (mesh.UV != null) for (int i = 0; i < mesh.UV.Length; i += 2) geometry.TextureCoordinates.Add(new Point(mesh.UV[i], mesh.UV[i + 1]));
            geometry.Freeze();
            Material material = materials != null && materials.TryGetValue(mesh.MaterialName, out var mapped) ? mapped : new DiffuseMaterial(new SolidColorBrush(Color.FromScRgb(1, mesh.Color[0], mesh.Color[1], mesh.Color[2]))); material.Freeze();
            var shape = new GeometryModel3D(geometry, material) { BackMaterial = material }; shape.Freeze(); content.Children.Add(shape);
        }
        content.Children.Add(new AmbientLight(Color.FromRgb(190, 190, 190)));
        content.Children.Add(new DirectionalLight(Color.FromRgb(45, 45, 45), new Vector3D(-1, -2, -3)));
        content.Freeze(); viewport.Children.Add(new ModelVisual3D { Content = content });
        var s = model.Statistics;
        info.Text = $"頂点 {s.VertexCount:N0} / ポリゴン（三角形） {s.TriangleCount:N0}\nMesh {s.MeshCount:N0} / 依存Material {s.MaterialCount:N0} / Bone {s.BoneCount:N0} / Skin {(s.HasSkinning ? "あり" : "なし")}\n左ドラッグ: 回転 · 右/中ドラッグ: 移動 · ホイール: ズーム\n" + string.Join("\n", model.Notes);
        Reset();
    }
    private void Reset() { yaw = 0; pitch = 0; pan = new(); distance = 3.1; UpdateCamera(); }
    private void UpdateCamera()
    {
        double aspect = Math.Max(.1, ActualWidth / Math.Max(1, ActualHeight));
        double fitDistance = distance * Math.Max(1, aspect);
        var direction = new Vector3D(Math.Sin(yaw) * Math.Cos(pitch), Math.Sin(pitch), Math.Cos(yaw) * Math.Cos(pitch));
        camera.Position = new Point3D(pan.X + direction.X * fitDistance, pan.Y + direction.Y * fitDistance, pan.Z + direction.Z * fitDistance);
        camera.LookDirection = -direction; camera.UpDirection = new(0, 1, 0);
        camera.NearPlaneDistance = .001; camera.FarPlaneDistance = 10000;
    }
}
