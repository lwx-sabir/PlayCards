#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace PlayCard.EditorTools
{
    /// <summary>
    /// Cuts a packed playing-card SHEET into individual cards, then either exports them as separate PNGs or
    /// builds the gapless 13×5 (rank-column × suit-row) atlas <c>CardSkin</c> uses. <b>Auto-Detect Grid</b>
    /// finds each card's exact bounds (bright cards on a dark bg), so there's no pitch drift even with uneven
    /// spacing. Tools ▸ Khela ▸ Card Atlas Repacker.
    /// </summary>
    public sealed class CardAtlasRepacker : EditorWindow
    {
        private Texture2D source;

        [SerializeField] private string suitSeq = "H,C,D,S";   // suit order in the sheet
        [SerializeField] private int suitStride = 14;           // cells per suit (13 cards + 1 back)
        [SerializeField] private bool aceFirst = true;
        [SerializeField] private string targetRowOrder = "H,S,C,D";

        // Detected per-column / per-row spans (start,len) in top-down pixels — the slicing source of truth.
        private List<Vector2Int> cols = new List<Vector2Int>(); // x: start, y: len
        private List<Vector2Int> rows = new List<Vector2Int>();

        private Vector2 scroll;
        private const string Ranks = "A23456789TJQK"; // sheet order when aceFirst (T = 10)

        [MenuItem("Tools/Khela/Card Atlas Repacker")]
        private static void Open() => GetWindow<CardAtlasRepacker>("Card Atlas Repacker");

        private void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);

            EditorGUILayout.HelpBox(
                "1) Source sheet (Read/Write enabled).  2) Auto-Detect Grid.  3) Check the green boxes sit on " +
                "the cards.  4) Build the atlas, or Extract the cards as separate PNGs.", MessageType.Info);

            source = (Texture2D)EditorGUILayout.ObjectField("Source Sheet", source, typeof(Texture2D), false);

            using (new EditorGUI.DisabledScope(source == null))
                if (GUILayout.Button("Auto-Detect Grid", GUILayout.Height(24))) AutoDetect();

            EditorGUILayout.LabelField($"Detected: {cols.Count} cols × {rows.Count} rows", EditorStyles.miniLabel);

            suitSeq = EditorGUILayout.TextField("Suit Order (sheet)", suitSeq);
            suitStride = EditorGUILayout.IntField("Cells Per Suit", suitStride);
            aceFirst = EditorGUILayout.Toggle("Ace First", aceFirst);
            targetRowOrder = EditorGUILayout.TextField("Target Row Order", targetRowOrder);

            DrawPreview();

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(!Detected))
            {
                if (GUILayout.Button("Build 13×5 Atlas", GUILayout.Height(28))) Build();
                if (GUILayout.Button("Extract Cards (separate PNGs)")) Extract();
            }

            EditorGUILayout.EndScrollView();
        }

        private bool Detected => cols.Count >= 2 && rows.Count >= 1;

        // Cards are bright on a dark bg: project brightness onto X and Y, the bright runs are the card spans.
        private void AutoDetect()
        {
            if (!source.isReadable) { NeedReadable(); return; }

            int w = source.width, h = source.height;
            var px = source.GetPixels32();
            var colHas = new bool[w];
            var rowTopDown = new bool[h];

            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    var c = px[y * w + x];
                    if (c.a > 120 && (c.r + c.g + c.b) > 360) // ~avg > 120, opaque
                    {
                        colHas[x] = true;
                        rowTopDown[h - 1 - y] = true; // flip → top-down so rows[0] is the TOP row
                    }
                }

            int minRun = Mathf.Max(8, Mathf.Min(w, h) / 60);
            cols = Runs(colHas, minRun);
            rows = Runs(rowTopDown, minRun);

            if (!Detected)
                EditorUtility.DisplayDialog("Repacker", "Couldn't detect a card grid — is the background dark and the cards light?", "OK");
            else
                Debug.Log($"[Repacker] detected {cols.Count}×{rows.Count}; first card {cols[0].y}×{rows[0].y}px.");
            Repaint();
        }

        private static List<Vector2Int> Runs(bool[] has, int minLen)
        {
            var list = new List<Vector2Int>();
            int i = 0;
            while (i < has.Length)
            {
                if (!has[i]) { i++; continue; }
                int s = i;
                while (i < has.Length && has[i]) i++;
                if (i - s >= minLen) list.Add(new Vector2Int(s, i - s)); // (start, length)
            }
            return list;
        }

        // Source rect for grid cell (col,row) using the detected per-column/row positions — no drift.
        private bool CellRect(int col, int row, out int sx, out int syBottomUp, out int w, out int h)
        {
            sx = syBottomUp = w = h = 0;
            if (col >= cols.Count || row >= rows.Count) return false;
            sx = cols[col].x; w = cols[col].y;
            int syTop = rows[row].x; h = rows[row].y;
            syBottomUp = source.height - syTop - h; // texture Y is bottom-up
            return true;
        }

        private void DrawPreview()
        {
            if (source == null) return;
            float scale = Mathf.Min(position.width - 30f, 720f) / source.width;
            var rect = GUILayoutUtility.GetRect(source.width * scale, source.height * scale);
            GUI.DrawTexture(rect, source, ScaleMode.StretchToFill);
            if (!Detected) return;

            Handles.BeginGUI();
            var fill = new Color(0f, 1f, 0.3f, 0.06f);
            var line = new Color(0f, 1f, 0.3f, 0.9f);
            for (int r = 0; r < rows.Count; r++)
                for (int c = 0; c < cols.Count; c++)
                    Handles.DrawSolidRectangleWithOutline(
                        new Rect(rect.x + cols[c].x * scale, rect.y + rows[r].x * scale,
                                 cols[c].y * scale, rows[r].y * scale), fill, line);
            Handles.EndGUI();
        }

        // Iterate the 52 faces: suit s (sheet order) × rank r (0..12). Returns (col,row) in the sheet + our
        // target column (2..A) + target row (CardSkin order).
        private IEnumerable<(int col, int row, int tCol, int tRow, char suit, char rank)> Faces()
        {
            var srcSuits = suitSeq.Replace(" ", "").Split(',');
            var tgtSuits = targetRowOrder.Replace(" ", "").Split(',');
            for (int s = 0; s < 4 && s < srcSuits.Length; s++)
            {
                int tRow = Array.IndexOf(tgtSuits, srcSuits[s]);
                if (tRow < 0) continue;
                for (int r = 0; r < 13; r++)
                {
                    int idx = s * suitStride + r;
                    int col = idx % cols.Count, row = idx / cols.Count;
                    int tCol = aceFirst ? (r == 0 ? 12 : r - 1) : r;
                    yield return (col, row, tCol, tRow, srcSuits[s][0], Ranks[r]);
                }
            }
        }

        private void Build()
        {
            if (!source.isReadable) { NeedReadable(); return; }
            int cw = cols[0].y, ch = rows[0].y;
            var outTex = new Texture2D(13 * cw, 5 * ch, TextureFormat.RGBA32, false);
            outTex.SetPixels32(new Color32[outTex.width * outTex.height]);

            foreach (var f in Faces())
            {
                if (!CellRect(f.col, f.row, out int sx, out int sy, out int w, out int h)) continue;
                int cpW = Mathf.Min(cw, w), cpH = Mathf.Min(ch, h);
                if (sx < 0 || sy < 0 || sx + cpW > source.width || sy + cpH > source.height) continue;
                outTex.SetPixels(f.tCol * cw, outTex.height - f.tRow * ch - ch, cpW, cpH, source.GetPixels(sx, sy, cpW, cpH));
            }

            outTex.Apply();
            var outPath = Path.Combine(Path.GetDirectoryName(AssetDatabase.GetAssetPath(source)), "Atlas_13x5.png");
            File.WriteAllBytes(outPath, outTex.EncodeToPNG());
            DestroyImmediate(outTex);
            AssetDatabase.ImportAsset(outPath);
            Debug.Log($"[Repacker] wrote {outPath} ({13 * cw}×{5 * ch}px). Assign to CardSkin ▸ Front Atlas (13×5, Row Order {targetRowOrder}).");
            EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<Texture2D>(outPath));
        }

        private void Extract()
        {
            if (!source.isReadable) { NeedReadable(); return; }
            var dir = Path.Combine(Path.GetDirectoryName(AssetDatabase.GetAssetPath(source)), "Cards");
            Directory.CreateDirectory(dir);
            int n = 0;
            foreach (var f in Faces())
            {
                if (!CellRect(f.col, f.row, out int sx, out int sy, out int w, out int h)) continue;
                if (sx < 0 || sy < 0 || sx + w > source.width || sy + h > source.height) continue;
                var card = new Texture2D(w, h, TextureFormat.RGBA32, false);
                card.SetPixels(source.GetPixels(sx, sy, w, h));
                card.Apply();
                File.WriteAllBytes(Path.Combine(dir, $"Card_{f.suit}{f.rank}.png"), card.EncodeToPNG());
                DestroyImmediate(card);
                n++;
            }
            AssetDatabase.Refresh();
            Debug.Log($"[Repacker] extracted {n} cards → {dir}");
            EditorUtility.RevealInFinder(dir);
        }

        private static void NeedReadable() => EditorUtility.DisplayDialog("Repacker",
            "Enable Read/Write on the source texture (Inspector → Advanced → Read/Write), Apply, then try again.", "OK");
    }
}
#endif
