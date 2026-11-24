using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;

namespace StarSaviorCostumeViewer
{
    internal sealed partial class PartVisibility
    {
        private static bool IsColorBinding(TextureBinding binding)
        {
            return new[] { "_BaseMap", "_MainTex", "_BaseColorMap", "_BaseColorTexture", "_AlbedoMap", "_DiffuseMap" }.Contains(binding.Property);
        }

        private Dictionary<string, byte[]> LinkedPaintFiles(BrushStroke stroke, Color32[] painted, byte[] encoded)
        {
            var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            files.Add(stroke.Binding.File, encoded);
            var owner = stroke.Target.Part;
            var targets = Parts.Where(p => p.Renderer == owner.Renderer && p.GeometrySlot == owner.GeometrySlot)
                .SelectMany(Textures).Where(IsColorBinding)
                .GroupBy(b => new { File = b.File.ToUpperInvariant(), Scale = b.Material.GetTextureScale(b.Property), Offset = b.Material.GetTextureOffset(b.Property) })
                .Select(g => g.First());
            var sourceScale = stroke.Binding.Material.GetTextureScale(stroke.Binding.Property);
            var sourceOffset = stroke.Binding.Material.GetTextureOffset(stroke.Binding.Property);
            foreach (var binding in targets)
            {
                var scale = binding.Material.GetTextureScale(binding.Property);
                var offset = binding.Material.GetTextureOffset(binding.Property);
                if (string.Equals(binding.File, stroke.Binding.File, StringComparison.OrdinalIgnoreCase) &&
                    scale == sourceScale && offset == sourceOffset) continue;

                string fullPath = Path.GetFullPath(binding.File);
                if (!fullPath.StartsWith(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("작업 폴더 밖의 연동 PNG는 편집할 수 없습니다.");
                var image = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
                try
                {
                    byte[] bytes;
                    if (!files.TryGetValue(binding.File, out bytes)) bytes = File.ReadAllBytes(binding.File);
                    if (!ImageConversion.LoadImage(image, bytes, false)) throw new IOException("연동 재질 PNG를 읽을 수 없습니다.");
                    int width = image.width, height = image.height;
                    if ((long)width * height > 33554432) throw new InvalidDataException("연동 PNG가 너무 큽니다.");
                    var pixels = image.GetPixels32();
                    var protectedPixels = new byte[width * height];
                    foreach (var part in Parts.Where(p => p.Visible && HasGeometry(p) && p.Renderer.enabled && p.Renderer.gameObject.activeInHierarchy))
                    foreach (var other in Textures(part).Where(b => string.Equals(b.File, binding.File, StringComparison.OrdinalIgnoreCase)))
                    {
                        var fine = EnsureMesh(part);
                        var triangles = fine.Slots[part.GeometrySlot].Where(piece => piece.Visible &&
                            !(part.Renderer == owner.Renderer && part.GeometrySlot == owner.GeometrySlot && piece == stroke.Target.Piece))
                            .SelectMany(piece => fine.PieceIndices(part.GeometrySlot, piece)).ToArray();
                        UvRaster.Paint(new UvRasterLayer(fine, triangles, other), width, height, p => protectedPixels[p] = 1);
                    }

                    bool changed = false;
                    var indices = stroke.Target.Part.Detail.PieceIndices(owner.GeometrySlot, stroke.Target.Piece);
                    var uv = stroke.Target.Part.Detail.UV;
                    for (int t = 0; t < indices.Length; t += 3)
                    {
                        var a = uv[indices[t]];
                        var b = uv[indices[t + 1]];
                        var c = uv[indices[t + 2]];
                        var ta = Vector2.Scale(a, scale) + offset;
                        var tb = Vector2.Scale(b, scale) + offset;
                        var tc = Vector2.Scale(c, scale) + offset;
                        ta = Vector2.Scale(ta, new Vector2(width, height));
                        tb = Vector2.Scale(tb, new Vector2(width, height));
                        tc = Vector2.Scale(tc, new Vector2(width, height));
                        float area = Cross2(tb - ta, tc - ta);
                        if (Mathf.Abs(area) < 1e-8f) continue;
                        int x0 = Mathf.FloorToInt(Mathf.Min(ta.x, Mathf.Min(tb.x, tc.x)));
                        int x1 = Mathf.CeilToInt(Mathf.Max(ta.x, Mathf.Max(tb.x, tc.x)));
                        int y0 = Mathf.FloorToInt(Mathf.Min(ta.y, Mathf.Min(tb.y, tc.y)));
                        int y1 = Mathf.CeilToInt(Mathf.Max(ta.y, Mathf.Max(tb.y, tc.y)));
                        if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) > 67108864)
                            throw new InvalidDataException("추가 재질의 UV 범위가 너무 큽니다.");
                        for (int y = y0; y <= y1; y++)
                        for (int x = x0; x <= x1; x++)
                        {
                            var point = new Vector2(x + .5f, y + .5f);
                            float u = Cross2(tb - point, tc - point) / area;
                            float v = Cross2(tc - point, ta - point) / area;
                            if (u < 0 || v < 0 || u + v > 1.000001f) continue;
                            int target = WrapPixel(y, height, binding.Original.wrapMode) * width + WrapPixel(x, width, binding.Original.wrapMode);
                            if (!stroke.LinkedUv && protectedPixels[target] != 0) continue;
                            var raw = a * u + b * v + c * (1 - u - v);
                            var source = Vector2.Scale(raw, sourceScale) + sourceOffset;
                            int original = WrapPixel(Mathf.FloorToInt(source.y * stroke.Height), stroke.Height, stroke.Binding.Original.wrapMode) * stroke.Width +
                                WrapPixel(Mathf.FloorToInt(source.x * stroke.Width), stroke.Width, stroke.Binding.Original.wrapMode);
                            if (painted[original].a >= stroke.Original[original].a || painted[original].a >= pixels[target].a) continue;
                            pixels[target].a = painted[original].a;
                            changed = true;
                        }
                    }
                    if (!changed) continue;
                    PadAlphaEdges(binding.File, pixels, width, height);
                    image.SetPixels32(pixels);
                    image.Apply(false, false);
                    files[binding.File] = AssetExporter.EncodePng(image);
                }
                finally { UnityEngine.Object.Destroy(image); }
            }
            return files;
        }

        private bool SaveLinkedPaint(Dictionary<string, byte[]> files)
        {
            var before = files.Keys.ToDictionary(file => file, File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);
            foreach (var pair in before)
            {
                if (paintOriginals.ContainsKey(pair.Key)) continue;
                paintOriginals.Add(pair.Key, pair.Value);
                string backup = Path.Combine(folder, "BrushBackups");
                Directory.CreateDirectory(backup);
                File.WriteAllBytes(Path.Combine(backup, Guid.NewGuid().ToString("N") + "_" + Path.GetFileName(pair.Key)), pair.Value);
            }
            RecordUndo();
            var written = new List<string>();
            try
            {
                foreach (var pair in files)
                {
                    ProfileFiles.WriteAtomic(pair.Key, pair.Value);
                    written.Add(pair.Key);
                }
            }
            catch
            {
                foreach (string file in written) ProfileFiles.WriteAtomic(file, before[file]);
                throw;
            }
            bool supported = true;
            foreach (var pair in files)
            {
                paintTextures[pair.Key] = pair.Value;
                supported &= NotifyTexture(pair.Key, true);
            }
            return supported;
        }
    }
}
