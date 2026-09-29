using System.Collections.Concurrent;
using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;

namespace AnyMakerOverlay;

internal sealed record PreviewMeshPart(string Path, Vector3 Position, float[]? Rotation);

internal sealed class MeshPreview(string gamePath)
{
    private readonly ConcurrentDictionary<string, Bitmap> images = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<Action<Bitmap>>> pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly object pendingLock = new();
    private readonly SemaphoreSlim workers = new(2);

    public Bitmap? Request(string? meshPath, bool component, bool transparentBackground, bool useVertexColors,
        IReadOnlyList<PreviewMeshPart>? dynamicParts, bool turnAround, Action<Bitmap> ready)
    {
        if (string.IsNullOrWhiteSpace(meshPath)) return null;
        var partSignature = new HashCode();
        if (dynamicParts is not null)
            foreach (var part in dynamicParts)
            {
                partSignature.Add(part.Path, StringComparer.OrdinalIgnoreCase);
                partSignature.Add(part.Position);
                if (part.Rotation is not null)
                    foreach (var value in part.Rotation) partSignature.Add(value);
            }
        var key = (component ? "component:" : useVertexColors ? "colored-item:" : transparentBackground ? "paint:" : "item:") +
                  (turnAround ? "turned:" : "") + meshPath + ":" + partSignature.ToHashCode();
        if (images.TryGetValue(key, out var existing)) return existing;
        var start = false;
        lock (pendingLock)
        {
            if (pending.TryGetValue(key, out var callbacks)) callbacks.Add(ready);
            else { pending[key] = [ready]; start = true; }
        }
        if (start)
        {
            _ = Task.Run(async () =>
            {
                await workers.WaitAsync();
                try
                {
                    var path = Path.GetFullPath(Path.Combine(gamePath, "rom", meshPath.Replace('/', Path.DirectorySeparatorChar)));
                    var root = Path.GetFullPath(Path.Combine(gamePath, "rom")) + Path.DirectorySeparatorChar;
                    if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return;
                    var triangles = Read(path);
                    if (dynamicParts is not null)
                    {
                        foreach (var part in dynamicParts)
                        {
                            var partPath = Path.GetFullPath(Path.Combine(gamePath, "rom", part.Path.Replace('/', Path.DirectorySeparatorChar)));
                            if (!partPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(partPath)) continue;
                            triangles.AddRange(Transform(Read(partPath), part));
                        }
                    }
                    if (turnAround) triangles = TurnAround(triangles);
                    var image = Render(triangles, component, transparentBackground, useVertexColors);
                    images[key] = image;
                    List<Action<Bitmap>> callbacks;
                    lock (pendingLock) { callbacks = pending[key]; pending.Remove(key); }
                    foreach (var callback in callbacks) callback(image);
                }
                catch { /* A missing or unusual mesh keeps its neutral placeholder. */ }
                finally { lock (pendingLock) pending.Remove(key); workers.Release(); }
            });
        }
        return null;
    }

    internal sealed record Vertex(Vector3 Position, Color Color);
    internal sealed record Triangle(Vertex A, Vertex B, Vertex C);

    private static List<Triangle> Transform(List<Triangle> triangles, PreviewMeshPart part)
    {
        Vertex Move(Vertex vertex)
        {
            var p = vertex.Position;
            var r = part.Rotation;
            if (r is { Length: 9 })
                p = new Vector3(r[0] * p.X + r[1] * p.Y + r[2] * p.Z,
                    r[3] * p.X + r[4] * p.Y + r[5] * p.Z,
                    r[6] * p.X + r[7] * p.Y + r[8] * p.Z);
            return vertex with { Position = p + part.Position };
        }
        return triangles.Select(triangle => new Triangle(
            Move(triangle.A), Move(triangle.B), Move(triangle.C))).ToList();
    }

    private static List<Triangle> TurnAround(List<Triangle> triangles)
    {
        static Vertex Turn(Vertex vertex) => vertex with
        {
            Position = new Vector3(-vertex.Position.X, vertex.Position.Y, -vertex.Position.Z)
        };
        return triangles.Select(triangle => new Triangle(
            Turn(triangle.A), Turn(triangle.B), Turn(triangle.C))).ToList();
    }

    internal static List<Triangle> Read(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        if (new string(reader.ReadChars(4)) != "mesh" || reader.ReadInt32() != 5)
            throw new InvalidDataException("Unsupported mesh format");
        var partCount = reader.ReadInt32();
        if (partCount is < 0 or > 100) throw new InvalidDataException("Invalid part count");
        var triangles = new List<Triangle>();
        for (var part = 0; part < partCount; part++)
        {
            var nameBytes = reader.ReadInt32();
            if (nameBytes is < 0 or > 2048) throw new InvalidDataException("Invalid mesh name");
            reader.ReadBytes(nameBytes);
            var metadata = reader.ReadBytes(88);
            if (metadata.Length != 88) throw new EndOfStreamException();
            var extraBytes = BitConverter.ToInt32(metadata, 68);
            if (extraBytes is < 0 or > 4096) throw new InvalidDataException("Invalid mesh metadata");
            reader.ReadBytes(extraBytes); // optional material string
            var bounds = new double[6];
            for (var i = 0; i < bounds.Length; i++) bounds[i] = reader.ReadDouble();
            var vertexBytes = reader.ReadInt32();
            if (vertexBytes < 0 || vertexBytes > stream.Length - stream.Position)
                throw new InvalidDataException("Invalid vertex data");
            var vertexData = reader.ReadBytes(vertexBytes);
            var indexBytes = reader.ReadInt32();
            if (indexBytes < 0 || indexBytes % 12 != 0 || indexBytes > stream.Length - stream.Position)
                throw new InvalidDataException("Invalid triangle data");
            var indices = new int[indexBytes / 4];
            for (var i = 0; i < indices.Length; i++) indices[i] = reader.ReadInt32();
            var maxIndex = indices.Length == 0 ? -1 : indices.Max();
            var stride = DetectStride(vertexData, maxIndex, bounds);
            var vertices = new Vertex[vertexBytes / stride];
            for (var i = 0; i < vertices.Length; i++)
            {
                var offset = i * stride;
                var point = new Vector3(BitConverter.ToSingle(vertexData, offset), BitConverter.ToSingle(vertexData, offset + 4), BitConverter.ToSingle(vertexData, offset + 8));
                var color = Color.FromArgb(vertexData[offset + 15], vertexData[offset + 12], vertexData[offset + 13], vertexData[offset + 14]);
                vertices[i] = new Vertex(point, color);
            }
            for (var i = 0; i < indexBytes / 12; i++)
            {
                var a = indices[i * 3];
                var b = indices[i * 3 + 1];
                var c = indices[i * 3 + 2];
                if ((uint)a >= vertices.Length || (uint)b >= vertices.Length || (uint)c >= vertices.Length) continue;
                triangles.Add(new(vertices[a], vertices[b], vertices[c]));
            }
        }
        return triangles;
    }

    private static int DetectStride(byte[] data, int maxIndex, double[] bounds)
    {
        if (data.Length == 0) return 16;
        var bestStride = 0;
        var bestScore = double.NegativeInfinity;
        for (var stride = 16; stride <= 80; stride += 4)
        {
            if (data.Length % stride != 0) continue;
            var count = data.Length / stride;
            if (maxIndex >= count) continue;
            var sample = Math.Min(64, count);
            var valid = 0;
            var alpha = 0;
            for (var i = 0; i < sample; i++)
            {
                var offset = (i * count / sample) * stride;
                var x = BitConverter.ToSingle(data, offset);
                var y = BitConverter.ToSingle(data, offset + 4);
                var z = BitConverter.ToSingle(data, offset + 8);
                if (float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z) &&
                    x >= bounds[0] - .05 && x <= bounds[3] + .05 &&
                    y >= bounds[1] - .05 && y <= bounds[4] + .05 &&
                    z >= bounds[2] - .05 && z <= bounds[5] + .05) valid++;
                if (data[offset + 15] == 255) alpha++;
            }
            var score = 3.0 * valid / sample + alpha / (double)sample +
                        (maxIndex + 1) / (double)count;
            if (score > bestScore) { bestScore = score; bestStride = stride; }
        }
        if (bestStride == 0 || bestScore < 2.5) throw new InvalidDataException("Unknown vertex layout");
        return bestStride;
    }

    private readonly record struct Projected(float X, float Y, float Depth);

    internal static Bitmap Render(List<Triangle> triangles, bool component = false,
        bool transparentBackground = false, bool useVertexColors = false)
    {
        const int size = 96;
        const int samples = 2;
        const int width = size * samples;
        var image = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        var background = transparentBackground ? Color.Transparent : Color.FromArgb(35, 37, 42);
        using (var graphics = Graphics.FromImage(image)) graphics.Clear(background);
        if (triangles.Count == 0) return image;

        static Projected Project(Vector3 point)
        {
            const float c = .819152f, s = .573576f; // 35 degree turn
            const float cp = .906308f, sp = .422618f; // 25 degree elevation
            var x = point.X * c - point.Z * s;
            var z = point.X * s + point.Z * c;
            return new(x, -point.Y * cp + z * sp, point.Y * sp + z * cp);
        }

        var points = triangles.SelectMany(t => new[] { Project(t.A.Position), Project(t.B.Position), Project(t.C.Position) }).ToArray();
        var minX = points.Min(p => p.X); var maxX = points.Max(p => p.X);
        var minY = points.Min(p => p.Y); var maxY = points.Max(p => p.Y);
        var scale = 82f * samples / Math.Max(.001f, Math.Max(maxX - minX, maxY - minY));
        var centerX = (minX + maxX) / 2; var centerY = (minY + maxY) / 2;
        var pixels = Enumerable.Repeat(background.ToArgb(), width * width).ToArray();
        var depths = Enumerable.Repeat(float.NegativeInfinity, width * width).ToArray();
        var lightDirection = Vector3.Normalize(new Vector3(-.4f, .8f, .5f));

        static float Edge(float ax, float ay, float bx, float by, float px, float py) =>
            (px - ax) * (by - ay) - (py - ay) * (bx - ax);

        foreach (var triangle in triangles)
        {
            var a = Project(triangle.A.Position);
            var b = Project(triangle.B.Position);
            var c = Project(triangle.C.Position);
            var ax = width / 2f + (a.X - centerX) * scale;
            var ay = width / 2f + (a.Y - centerY) * scale;
            var bx = width / 2f + (b.X - centerX) * scale;
            var by = width / 2f + (b.Y - centerY) * scale;
            var cx = width / 2f + (c.X - centerX) * scale;
            var cy = width / 2f + (c.Y - centerY) * scale;
            var area = Edge(ax, ay, bx, by, cx, cy);
            if (Math.Abs(area) < .0001f) continue;

            var normal = Vector3.Cross(triangle.B.Position - triangle.A.Position, triangle.C.Position - triangle.A.Position);
            if (normal.LengthSquared() < 1e-12f) continue;
            normal = Vector3.Normalize(normal);
            var shade = .55f + .45f * Math.Abs(Vector3.Dot(normal, lightDirection));
            var color = PreviewColor(triangle, component, useVertexColors, shade);
            var left = Math.Clamp((int)Math.Floor(Math.Min(ax, Math.Min(bx, cx))), 0, width - 1);
            var right = Math.Clamp((int)Math.Ceiling(Math.Max(ax, Math.Max(bx, cx))), 0, width - 1);
            var top = Math.Clamp((int)Math.Floor(Math.Min(ay, Math.Min(by, cy))), 0, width - 1);
            var bottom = Math.Clamp((int)Math.Ceiling(Math.Max(ay, Math.Max(by, cy))), 0, width - 1);
            for (var y = top; y <= bottom; y++)
                for (var x = left; x <= right; x++)
                {
                    var px = x + .5f;
                    var py = y + .5f;
                    var wa = Edge(bx, by, cx, cy, px, py) / area;
                    var wb = Edge(cx, cy, ax, ay, px, py) / area;
                    var wc = 1f - wa - wb;
                    if (wa < -.00001f || wb < -.00001f || wc < -.00001f) continue;
                    var index = y * width + x;
                    var depth = wa * a.Depth + wb * b.Depth + wc * c.Depth;
                    if (depth <= depths[index]) continue;
                    depths[index] = depth;
                    pixels[index] = color;
                }
        }

        using var large = new Bitmap(width, width, PixelFormat.Format32bppArgb);
        var data = large.LockBits(new Rectangle(0, 0, width, width), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try { Marshal.Copy(pixels, 0, data.Scan0, pixels.Length); }
        finally { large.UnlockBits(data); }
        using (var graphics = Graphics.FromImage(image))
        {
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            graphics.DrawImage(large, new Rectangle(0, 0, size, size));
        }
        return image;
    }

    private static int PreviewColor(Triangle triangle, bool component, bool useVertexColors, float shade)
    {
        var r = (triangle.A.Color.R + triangle.B.Color.R + triangle.C.Color.R) / 3;
        var g = (triangle.A.Color.G + triangle.B.Color.G + triangle.C.Color.G) / 3;
        var b = (triangle.A.Color.B + triangle.B.Color.B + triangle.C.Color.B) / 3;
        if (useVertexColors) return Color.FromArgb(Math.Clamp((int)(r * shade), 0, 255),
            Math.Clamp((int)(g * shade), 0, 255), Math.Clamp((int)(b * shade), 0, 255)).ToArgb();
        if (!component) return Color.FromArgb((int)(142 * shade), (int)(145 * shade), (int)(150 * shade)).ToArgb();

        // Red is the game's body tint channel; port colors and neutral details are actual mesh paint.
        if (r > g * 1.45f && r > b * 1.45f && r > 65 && g < 95)
            (r, g, b) = (142, 145, 150);
        else if (r > g * 1.25f && g > b * 1.5f && r > 100)
            (r, g, b) = (150, 128, 103); // muted mechanical/copper marking
        else
        {
            // Keep port colors recognizable without restoring the overly bright preview colors.
            var grey = (r + g + b) / 3;
            r = (r * 3 + grey) / 4;
            g = (g * 3 + grey) / 4;
            b = (b * 3 + grey) / 4;
        }
        return Color.FromArgb(Math.Clamp((int)(r * shade), 0, 255),
            Math.Clamp((int)(g * shade), 0, 255), Math.Clamp((int)(b * shade), 0, 255)).ToArgb();
    }
}
