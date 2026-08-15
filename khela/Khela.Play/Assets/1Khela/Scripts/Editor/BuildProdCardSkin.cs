#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using PlayCard.Game.Cards;
using UnityEditor;
using UnityEngine;

namespace PlayCard.EditorTools
{
    /// <summary>
    /// Assemble a card pack of individual PNGs into the gapless 13×5 atlas the game reads, and create a matching
    /// <c>CardSkin</c> asset. Runs on WHATEVER folder you select in the Project window, so one menu handles every
    /// pack. Two folder shapes are accepted:
    ///   • FOUR SUIT SUBFOLDERS whose names contain club/diamond/heart/spade, each with 13 rank PNGs
    ///     (e.g. cards_1: <c>club/Club_A.png</c>, cards_2: <c>clubs/clubs_Ace.png</c>).
    ///   • A FLAT folder whose filenames encode BOTH suit and rank
    ///     (e.g. cards_3: <c>VIASS_clubsA.png</c>, <c>VIASS_clubs10.png</c>).
    /// Rank tokens may be letters (A,J,Q,K,T), numbers (2..10) or words (Ace..King), any case.
    ///
    /// Layout it writes (must match <see cref="CardSkin"/>):
    ///   • Columns 0..12 = Two..Ten, Jack, Queen, King, Ace  (== CardSkin.ColumnFor)
    ///   • Rows    0..3  = Hearts, Spades, Clubs, Diamonds (top→bottom)   • Row 4 blank
    ///
    /// Outputs <c>Card_Skin_{suffix}_Atlas.png</c> + <c>Card_Skin_{suffix}.asset</c> into the pack folder
    /// (suffix = folder name minus a leading "cards_"). The BACK is left unassigned — drop a Default-type texture
    /// into the skin's Back slot yourself. Menu: Khela ▸ Cards ▸ Build Card Skin From Selected Folder.
    /// </summary>
    public static class BuildProdCardSkin
    {
        // Rows top→bottom — MUST equal CardSkin.rowOrderTopToBottom.
        private static readonly CardSuit[] RowOrder = { CardSuit.Hearts, CardSuit.Spades, CardSuit.Clubs, CardSuit.Diamonds };

        [MenuItem("Khela/Cards/Build Card Skin From Selected Folder")]
        public static void Build()
        {
            string folder = SelectedFolder();
            if (folder == null)
            {
                EditorUtility.DisplayDialog("Build Card Skin",
                    "Select the card-pack FOLDER in the Project window, then run this again.", "OK");
                return;
            }

            // Collect every card as (suit, column, absolute path) — from suit subfolders OR a flat folder.
            var cards = new List<(CardSuit suit, int col, string path)>();

            var suitDir = new Dictionary<CardSuit, string>();
            foreach (var sub in AssetDatabase.GetSubFolders(folder))
                if (TryMatchSuit(Path.GetFileName(sub), out var s) && !suitDir.ContainsKey(s))
                    suitDir[s] = sub;

            if (suitDir.Count == 4)
            {
                // Shape A: four suit subfolders — rank comes from the filename.
                foreach (var kv in suitDir)
                    foreach (var file in PngsIn(kv.Value))
                    {
                        int col = ColumnForToken(ValueToken(Path.GetFileNameWithoutExtension(file)));
                        if (col >= 0) cards.Add((kv.Key, col, file));
                        else Debug.LogWarning($"[CardSkin] no rank read from '{Path.GetFileName(file)}' — skipped.");
                    }
            }
            else
            {
                // Shape B: flat folder — the filename carries BOTH suit and rank (e.g. VIASS_clubs10).
                foreach (var file in PngsIn(folder))
                    if (TryParseSuitAndRank(ValueToken(Path.GetFileNameWithoutExtension(file)), out var s, out int col))
                        cards.Add((s, col, file));
                    else Debug.LogWarning($"[CardSkin] no suit+rank read from '{Path.GetFileName(file)}' — skipped.");
            }

            if (cards.Count == 0)
            {
                EditorUtility.DisplayDialog("Build Card Skin",
                    "No cards recognised. Need EITHER four club/diamond/heart/spade subfolders, OR a flat folder " +
                    "whose filenames contain the suit + rank (e.g. clubs_A / clubs_Ace / clubsA / clubs10).", "OK");
                return;
            }

            // Cell size from the first readable card.
            int cw = 0, ch = 0;
            foreach (var c in cards) { var t = LoadPng(c.path); if (t != null) { cw = t.width; ch = t.height; Object.DestroyImmediate(t); break; } }
            if (cw == 0) { EditorUtility.DisplayDialog("Build Card Skin", "No readable PNGs found.", "OK"); return; }

            var atlas = new Texture2D(13 * cw, 5 * ch, TextureFormat.RGBA32, false);
            atlas.SetPixels32(new Color32[atlas.width * atlas.height]); // clear → transparent

            int placed = 0;
            foreach (var c in cards)
            {
                int row = System.Array.IndexOf(RowOrder, c.suit);
                if (row < 0) continue;
                var tex = LoadPng(c.path);
                if (tex == null) continue;
                int cpW = Mathf.Min(cw, tex.width), cpH = Mathf.Min(ch, tex.height);
                atlas.SetPixels(c.col * cw, atlas.height - row * ch - ch, cpW, cpH, tex.GetPixels(0, 0, cpW, cpH));
                Object.DestroyImmediate(tex);
                placed++;
            }
            atlas.Apply();

            string name    = Path.GetFileName(folder);
            string suffix  = name.StartsWith("cards_") ? name.Substring("cards_".Length) : name;
            string atlasPath = $"{folder}/Card_Skin_{suffix}_Atlas.png";
            string skinPath  = $"{folder}/Card_Skin_{suffix}.asset";

            File.WriteAllBytes(Path.GetFullPath(atlasPath), atlas.EncodeToPNG());
            Object.DestroyImmediate(atlas);
            AssetDatabase.ImportAsset(atlasPath);
            if (AssetImporter.GetAtPath(atlasPath) is TextureImporter imp)
            {
                imp.textureType     = TextureImporterType.Default;   // NOT Sprite — a Sprite renders gray as _BaseMap
                imp.sRGBTexture     = true;
                imp.mipmapEnabled   = true;
                imp.alphaIsTransparency = true;
                imp.wrapMode        = TextureWrapMode.Repeat;
                imp.filterMode      = FilterMode.Bilinear;
                imp.anisoLevel      = 1;
                imp.maxTextureSize  = 2048;
                imp.SaveAndReimport();
            }

            var skin = AssetDatabase.LoadAssetAtPath<CardSkin>(skinPath);
            if (skin == null) { skin = ScriptableObject.CreateInstance<CardSkin>(); AssetDatabase.CreateAsset(skin, skinPath); }
            skin.displayName = $"Card_Skin_{suffix}";
            skin.frontAtlas  = AssetDatabase.LoadAssetAtPath<Texture>(atlasPath);
            skin.columns = 13;
            skin.rows    = 5;
            skin.rowOrderTopToBottom = (CardSuit[])RowOrder.Clone();
            skin.invertV = true;
            skin.baseMapProperty   = "_BaseMap";
            skin.baseMapStProperty = "_BaseMap_ST";
            // back: left null — assign a Default-type Back texture yourself.
            EditorUtility.SetDirty(skin);
            AssetDatabase.SaveAssets();

            Debug.Log($"[CardSkin] {atlasPath} ({13 * cw}×{5 * ch}px) — placed {placed}/52 (from {cards.Count} files). " +
                      $"Created {skinPath}. NEXT: assign a Back (Default texture, NOT Sprite) + point the table's Card Skin at it.");
            Selection.activeObject = skin;
            EditorGUIUtility.PingObject(skin);
        }

        private static string SelectedFolder()
        {
            var obj = Selection.activeObject;
            if (obj == null) return null;
            string path = AssetDatabase.GetAssetPath(obj);
            return (!string.IsNullOrEmpty(path) && AssetDatabase.IsValidFolder(path)) ? path : null;
        }

        private static IEnumerable<string> PngsIn(string assetFolder)
        {
            foreach (var f in Directory.GetFiles(Path.GetFullPath(assetFolder), "*.png", SearchOption.TopDirectoryOnly))
                yield return f;
        }

        private static bool TryMatchSuit(string s, out CardSuit suit)
        {
            string n = s.ToLowerInvariant();
            if (n.Contains("heart"))   { suit = CardSuit.Hearts;   return true; }
            if (n.Contains("spade"))   { suit = CardSuit.Spades;   return true; }
            if (n.Contains("club"))    { suit = CardSuit.Clubs;    return true; }
            if (n.Contains("diamond")) { suit = CardSuit.Diamonds; return true; }
            suit = default; return false;
        }

        /// <summary>Rank token from a filename: the part after the last '_', else inside parens, else the whole name.</summary>
        private static string ValueToken(string nameNoExt)
        {
            int u = nameNoExt.LastIndexOf('_');
            if (u >= 0 && u < nameNoExt.Length - 1) return nameNoExt.Substring(u + 1);
            int p = nameNoExt.IndexOf('(');
            if (p >= 0) { int q = nameNoExt.IndexOf(')', p); if (q > p) return nameNoExt.Substring(p + 1, q - p - 1); }
            return nameNoExt;
        }

        /// <summary>Flat-name parse: split a "clubs10" / "clubsA" token into suit (prefix) + rank (trailing).</summary>
        private static bool TryParseSuitAndRank(string token, out CardSuit suit, out int col)
        {
            suit = default; col = -1;
            string t = token.Trim().ToLowerInvariant();
            foreach (int len in new[] { 2, 1 })   // rank is the trailing "10" or a single A/J/Q/K/2..9
            {
                if (t.Length <= len) continue;
                int c = ColumnForToken(t.Substring(t.Length - len));
                if (c >= 0 && TryMatchSuit(t.Substring(0, t.Length - len), out suit)) { col = c; return true; }
            }
            return false;
        }

        /// <summary>Atlas column (Two→0 .. Ace→12) from a rank token — letters, numbers, or words. −1 = unknown.</summary>
        private static int ColumnForToken(string t)
        {
            switch (t.Trim().ToLowerInvariant())
            {
                case "2": case "two":            return 0;
                case "3": case "three":          return 1;
                case "4": case "four":           return 2;
                case "5": case "five":           return 3;
                case "6": case "six":            return 4;
                case "7": case "seven":          return 5;
                case "8": case "eight":          return 6;
                case "9": case "nine":           return 7;
                case "10": case "t": case "ten": return 8;
                case "j": case "jack":           return 9;
                case "q": case "queen":          return 10;
                case "k": case "king":           return 11;
                case "a": case "ace":            return 12;
                default:                         return -1;
            }
        }

        private static Texture2D LoadPng(string absPath)
        {
            if (!File.Exists(absPath)) return null;
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (tex.LoadImage(File.ReadAllBytes(absPath))) return tex;
            Object.DestroyImmediate(tex);
            return null;
        }
    }
}
#endif
