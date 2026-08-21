using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.EditorTools
{
    /// <summary>
    /// Builds the pre-fractured piggy blast prefab from the artist's PSD — one UI Image per layer, each at its
    /// exact position in the document, so the assembled pieces are pixel-identical to the intact pig.
    ///
    /// Two sources are married here, and that is the whole tool: the 2D PSD Importer gives us a SPRITE per layer
    /// (but throws the layer positions away outside of character-rig mode), while the PSD file itself carries each
    /// layer's document-space bounds in its header — no pixel data is read, just names and rectangles. Matching
    /// the two by layer name yields sprite + placement, which becomes an Image under a canvas-sized parent.
    ///
    /// Re-run after any art change — it overwrites the prefab in place, so scene references survive.
    /// </summary>
    public static class PiggyFracturedBuilder
    {
        private const string PsbPath = "Assets/1Khela/GUI/piggy-fractured.psb";
        private const string PrefabPath = "Assets/1Khela/Prefabs/UI/Piggy/Piggy_Fractured.prefab";

        [MenuItem("Tools/Khela/Piggy/Build Fractured Pieces Prefab")]
        public static void Build()
        {
            // ---- the layer table, straight from the PSD binary ----
            List<PsdLayer> layers;
            int docW, docH;
            try { layers = ReadLayers(PsbPath, out docW, out docH); }
            catch (Exception ex)
            {
                Debug.LogError($"PiggyFracturedBuilder: cannot parse '{PsbPath}' — {ex.Message}");
                return;
            }

            // ---- the sprites the importer generated ----
            var sprites = new Dictionary<string, Sprite>(StringComparer.Ordinal);
            foreach (var o in AssetDatabase.LoadAllAssetRepresentationsAtPath(PsbPath))
                if (o is Sprite s && !sprites.ContainsKey(s.name)) sprites.Add(s.name, s);

            if (sprites.Count == 0)
            {
                Debug.LogError("PiggyFracturedBuilder: no sprites found in the .psb. In its import settings set " +
                               "Texture Type = Sprite (2D and UI), Sprite Mode = Multiple, Individual Sprites " +
                               "(Mosaic) = ON, Use as Rig (Character Mode) = OFF, then Apply and re-run.");
                return;
            }

            // ---- assemble: a document-sized parent, one Image per raster layer ----
            var root = new GameObject("Piggy_Fractured", typeof(RectTransform));
            try
            {
                var rootRt = (RectTransform)root.transform;
                rootRt.sizeDelta = new Vector2(docW, docH);

                int built = 0;
                var missing = new List<string>();
                var fullImage = new List<string>();
                foreach (var layer in layers)
                {
                    // A layer spanning (almost) the whole document is the INTACT image or a background — "Layer 0"
                    // in this file — never a shard. Skipped by geometry, not by name, so a re-export where it is
                    // called "Background" is caught the same way.
                    float coverage = (layer.Right - layer.Left) * (float)(layer.Bottom - layer.Top) / (docW * (float)docH);
                    if (coverage >= 0.9f) { fullImage.Add(layer.Name); continue; }

                    if (!sprites.TryGetValue(layer.Name, out var sprite)) { missing.Add(layer.Name); continue; }

                    var go = new GameObject(layer.Name, typeof(RectTransform), typeof(Image));
                    var rt = (RectTransform)go.transform;
                    rt.SetParent(rootRt, false);

                    // PSD coordinates are top-left origin, Y down; anchor every piece to the parent's top-left so
                    // the document rect maps 1:1 onto the parent rect with nothing but a sign flip on Y.
                    rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
                    rt.pivot = new Vector2(0f, 1f);
                    rt.anchoredPosition = new Vector2(layer.Left, -layer.Top);
                    rt.sizeDelta = new Vector2(layer.Right - layer.Left, layer.Bottom - layer.Top);

                    var img = go.GetComponent<Image>();
                    img.sprite = sprite;
                    img.raycastTarget = false;
                    built++;
                }

                if (fullImage.Count > 0)
                    Debug.Log("PiggyFracturedBuilder: skipped full-image layer(s): " + string.Join(", ", fullImage));
                if (missing.Count > 0)
                    Debug.LogWarning("PiggyFracturedBuilder: no sprite for layer(s): " + string.Join(", ", missing) +
                                     " — hidden or renamed by the importer? They were skipped.");

                Directory.CreateDirectory(Path.GetDirectoryName(PrefabPath)!);
                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
                Debug.Log($"PiggyFracturedBuilder: built '{PrefabPath}' — {built} piece(s), document {docW}x{docH}.");
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        // ================= minimal PSD reader: header + layer names/bounds, no pixel data =================

        private sealed class PsdLayer
        {
            public string Name;
            public int Top, Left, Bottom, Right;
        }

        private static List<PsdLayer> ReadLayers(string path, out int docW, out int docH)
        {
            using var fs = File.OpenRead(path);
            using var r = new BinaryReader(fs);

            if (ReadAscii(r, 4) != "8BPS") throw new Exception("not a PSD/PSB (missing 8BPS signature)");
            int version = ReadU16(r);                       // 1 = PSD (4-byte lengths), 2 = PSB (8-byte)
            if (version != 1 && version != 2) throw new Exception($"unknown PSD version {version}");
            r.BaseStream.Seek(6, SeekOrigin.Current);       // reserved
            ReadU16(r);                                     // channels
            docH = ReadI32(r);
            docW = ReadI32(r);
            ReadU16(r);                                     // depth
            ReadU16(r);                                     // colour mode

            Skip(r, ReadU32(r));                            // colour mode data
            Skip(r, ReadU32(r));                            // image resources

            ReadLen(r, version);                            // layer-and-mask section length (unused)
            ReadLen(r, version);                            // layer info length (unused)
            int layerCount = Math.Abs((short)ReadU16(r));   // negative = first alpha is merged transparency

            var layers = new List<PsdLayer>(layerCount);
            for (int i = 0; i < layerCount; i++)
            {
                var l = new PsdLayer
                {
                    Top = ReadI32(r),
                    Left = ReadI32(r),
                    Bottom = ReadI32(r),
                    Right = ReadI32(r),
                };

                int channels = ReadU16(r);
                for (int c = 0; c < channels; c++)
                {
                    ReadU16(r);                             // channel id
                    ReadLen(r, version);                    // channel data length (data itself lives later; skip)
                }

                if (ReadAscii(r, 4) != "8BIM") throw new Exception($"layer {i}: bad blend signature");
                ReadAscii(r, 4);                            // blend key
                r.BaseStream.Seek(4, SeekOrigin.Current);   // opacity, clipping, flags, filler

                long extraLen = ReadU32(r);
                long extraEnd = r.BaseStream.Position + extraLen;

                Skip(r, ReadU32(r));                        // layer mask data
                Skip(r, ReadU32(r));                        // blending ranges

                int nameLen = r.ReadByte();                 // Pascal name, padded so (1+len) is a multiple of 4
                l.Name = Encoding.ASCII.GetString(r.ReadBytes(nameLen));
                Skip(r, 3 - nameLen % 4);

                // Additional info blocks: 'luni' has the REAL (unicode) name — the Pascal one is truncated ASCII —
                // and 'lsct' marks group folders/dividers, which are not pieces and must not become Images.
                bool isGroup = false;
                while (r.BaseStream.Position < extraEnd - 8)
                {
                    string sig = ReadAscii(r, 4);
                    if (sig != "8BIM" && sig != "8B64") break;
                    string key = ReadAscii(r, 4);
                    uint len = ReadU32(r);
                    long next = r.BaseStream.Position + len + (len % 2);   // blocks are padded to 2

                    if (key == "luni")
                    {
                        int chars = ReadI32(r);
                        var bytes = r.ReadBytes(chars * 2);
                        l.Name = Encoding.BigEndianUnicode.GetString(bytes);
                    }
                    else if (key == "lsct" && len >= 4)
                    {
                        int type = ReadI32(r);
                        if (type >= 1 && type <= 3) isGroup = true;   // folder open/closed/divider
                    }

                    r.BaseStream.Seek(next, SeekOrigin.Begin);
                }
                r.BaseStream.Seek(extraEnd, SeekOrigin.Begin);

                bool hasArea = l.Right > l.Left && l.Bottom > l.Top;
                if (!isGroup && hasArea) layers.Add(l);
            }

            return layers;
        }

        private static string ReadAscii(BinaryReader r, int n) => Encoding.ASCII.GetString(r.ReadBytes(n));
        private static int ReadU16(BinaryReader r) => (r.ReadByte() << 8) | r.ReadByte();
        private static int ReadI32(BinaryReader r)
            => (r.ReadByte() << 24) | (r.ReadByte() << 16) | (r.ReadByte() << 8) | r.ReadByte();
        private static uint ReadU32(BinaryReader r) => (uint)ReadI32(r);
        private static long ReadLen(BinaryReader r, int version)
            => version == 2 ? ((long)ReadU32(r) << 32) | ReadU32(r) : ReadU32(r);
        private static void Skip(BinaryReader r, long n) { if (n > 0) r.BaseStream.Seek(n, SeekOrigin.Current); }
    }
}
