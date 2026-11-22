using System.Collections.Generic;
using UnityEngine;

namespace RealisticRespirationMod.UI
{
    /// <summary>
    /// Procedural crisp pixel-art sprites for Nikethamide Ampoule, Sodium Bicarbonate Bottle,
    /// Portable Arterial Blood Gas Analyzer, and Respiratory/Acid-Base Moodles.
    /// </summary>
    internal static class RespirationSprites
    {
        private static readonly Dictionary<string, Sprite> Cache = new Dictionary<string, Sprite>();
        private static Sprite? _ampouleMask;
        private static Sprite? _bottleMask;

        public static Sprite GetNikethamideAmpouleSprite()
        {
            const string key = "item_nikethamide_ampoule";
            if (Cache.TryGetValue(key, out var cached)) return cached;

            const int s = 24;
            var tex = MakeTex(s, s);
            var px = new Color32[s * s];

            Color32 glass = new Color32(215, 240, 250, 225);
            Color32 outline = new Color32(50, 68, 82, 255);
            Color32 band = new Color32(255, 165, 45, 255);
            Color32 fill = new Color32(255, 220, 120, 210);

            for (int y = 0; y < s; y++)
            {
                for (int x = 0; x < s; x++)
                {
                    bool body = x >= 8 && x <= 15 && y >= 2 && y <= 14;
                    bool neck = x >= 10 && x <= 13 && y >= 15 && y <= 18;
                    bool tip = x >= 11 && x <= 12 && y >= 19 && y <= 21;
                    if (!body && !neck && !tip) continue;

                    bool border = (body && (x == 8 || x == 15 || y == 2)) ||
                                  (neck && (x == 10 || x == 13)) ||
                                  (tip && (x == 11 || x == 12 || y == 21));
                    if (border) px[y * s + x] = outline;
                    else if (neck && y == 16) px[y * s + x] = band;
                    else if (body && y <= 11) px[y * s + x] = fill;
                    else px[y * s + x] = glass;
                }
            }

            tex.SetPixels32(px);
            tex.Apply(false);
            var spr = Sprite.Create(tex, new Rect(0, 0, s, s), new Vector2(0.5f, 0.5f), 24f);
            Cache[key] = spr;
            return spr;
        }

        public static Sprite GetAmpouleLiquidMask()
        {
            if (_ampouleMask != null) return _ampouleMask;
            const int s = 24;
            var tex = MakeTex(s, s);
            var px = new Color32[s * s];
            for (int y = 3; y <= 13; y++)
                for (int x = 9; x <= 14; x++)
                    px[y * s + x] = new Color32(255, 255, 255, 255);
            tex.SetPixels32(px);
            tex.Apply(false);
            _ampouleMask = Sprite.Create(tex, new Rect(0, 0, s, s), new Vector2(0.5f, 0.5f), 24f);
            return _ampouleMask;
        }

        public static Sprite GetSodiumBicarbonateBottleSprite()
        {
            const string key = "item_nahco3_bottle";
            if (Cache.TryGetValue(key, out var cached)) return cached;

            const int s = 24;
            var tex = MakeTex(s, s);
            var px = new Color32[s * s];

            Color32 outline = new Color32(45, 60, 75, 255);
            Color32 glass = new Color32(220, 240, 250, 235);
            Color32 label = new Color32(72, 168, 235, 255);
            Color32 labelStripe = new Color32(240, 250, 255, 255);
            Color32 cap = new Color32(38, 108, 178, 255);

            for (int y = 0; y < s; y++)
            {
                for (int x = 0; x < s; x++)
                {
                    bool body = x >= 6 && x <= 17 && y >= 2 && y <= 16;
                    bool capBox = x >= 8 && x <= 15 && y >= 17 && y <= 21;
                    if (!body && !capBox) continue;

                    bool border = (body && (x == 6 || x == 17 || y == 2 || y == 16)) ||
                                  (capBox && (x == 8 || x == 15 || y == 21));
                    if (border) px[y * s + x] = outline;
                    else if (capBox) px[y * s + x] = cap;
                    else if (y >= 6 && y <= 12) px[y * s + x] = (y == 9) ? labelStripe : label;
                    else px[y * s + x] = glass;
                }
            }

            tex.SetPixels32(px);
            tex.Apply(false);
            var spr = Sprite.Create(tex, new Rect(0, 0, s, s), new Vector2(0.5f, 0.5f), 24f);
            Cache[key] = spr;
            return spr;
        }

        public static Sprite GetBottleLiquidMask()
        {
            if (_bottleMask != null) return _bottleMask;
            const int s = 24;
            var tex = MakeTex(s, s);
            var px = new Color32[s * s];
            for (int y = 3; y <= 15; y++)
                for (int x = 7; x <= 16; x++)
                    px[y * s + x] = new Color32(255, 255, 255, 255);
            tex.SetPixels32(px);
            tex.Apply(false);
            _bottleMask = Sprite.Create(tex, new Rect(0, 0, s, s), new Vector2(0.5f, 0.5f), 24f);
            return _bottleMask;
        }

        public static Sprite GetPortableAbgSprite()
        {
            const string key = "item_abgmonitor";
            if (Cache.TryGetValue(key, out var cached)) return cached;

            const int s = 24;
            var tex = MakeTex(s, s);
            var px = new Color32[s * s];

            Color32 casing = new Color32(204, 218, 228, 255);
            Color32 border = new Color32(48, 64, 78, 255);
            Color32 screen = new Color32(14, 30, 44, 255);
            Color32 cyan = new Color32(72, 224, 255, 255);
            Color32 amber = new Color32(255, 195, 72, 255);
            Color32 green = new Color32(82, 245, 148, 255);
            Color32 redDrop = new Color32(225, 58, 68, 255);

            for (int y = 0; y < s; y++)
            {
                for (int x = 0; x < s; x++)
                {
                    bool body = x >= 4 && x <= 19 && y >= 2 && y <= 21;
                    if (!body) continue;
                    bool edge = x == 4 || x == 19 || y == 2 || y == 21;
                    bool inScreen = x >= 6 && x <= 17 && y >= 10 && y <= 19;
                    if (edge) px[y * s + x] = border;
                    else if (inScreen) px[y * s + x] = screen;
                    else px[y * s + x] = casing;
                }
            }

            // Three mini blood-gas bars on screen (PaO2 cyan, PaCO2 amber, pH green)
            for (int x = 7; x <= 16; x++) px[17 * s + x] = cyan;
            for (int x = 7; x <= 14; x++) px[14 * s + x] = amber;
            for (int x = 7; x <= 15; x++) px[11 * s + x] = green;

            // Sample blood cartridge slot at bottom
            for (int y = 4; y <= 7; y++)
                for (int x = 14; x <= 17; x++)
                    px[y * s + x] = redDrop;

            // Keypad buttons
            px[6 * s + 7] = border;
            px[6 * s + 9] = border;
            px[6 * s + 11] = border;
            px[4 * s + 7] = border;
            px[4 * s + 9] = border;

            tex.SetPixels32(px);
            tex.Apply(false);
            var spr = Sprite.Create(tex, new Rect(0, 0, s, s), new Vector2(0.5f, 0.5f), 24f);
            Cache[key] = spr;
            return spr;
        }

        /// <summary>
        /// Generates clean 24x24 Moodle icons:
        /// glyph: 0 = lungs with X (pulmonary failure), 1 = CO2 bubbles up (hypercapnia),
        /// 2 = CO2 down / fast waves (hypocapnia/tachypnea), 3 = pH drop/scale (acidosis/alkalosis),
        /// 4 = brainstem down arrow (opiate/central respiratory depression).
        /// </summary>
        public static Sprite GetMoodleIcon(string key, Color primary, Color accent, int glyph)
        {
            string fullKey = $"moodle_{key}_{glyph}";
            if (Cache.TryGetValue(fullKey, out var cached)) return cached;

            const int s = 24;
            var tex = MakeTex(s, s);
            var px = new Color32[s * s];
            Color32 p = primary;
            Color32 a = accent;
            Color32 dark = new Color32(20, 26, 32, 210);

            void Plot(int x, int y, Color32 c)
            {
                if (x >= 0 && x < s && y >= 0 && y < s) px[y * s + x] = c;
            }

            switch (glyph)
            {
                case 0: // Lungs + red X (Pulmonary Failure)
                    for (int y = 5; y <= 17; y++)
                    {
                        for (int x = 4; x <= 9; x++)
                            if (!(x == 4 && (y == 5 || y == 17))) Plot(x, y, p);
                        for (int x = 14; x <= 19; x++)
                            if (!(x == 19 && (y == 5 || y == 17))) Plot(x, y, p);
                    }
                    for (int y = 13; y <= 20; y++) { Plot(11, y, dark); Plot(12, y, dark); }
                    for (int i = 0; i < 12; i++)
                    {
                        Plot(6 + i, 6 + i, a);
                        Plot(7 + i, 6 + i, a);
                        Plot(17 - i, 6 + i, a);
                        Plot(16 - i, 6 + i, a);
                    }
                    break;

                case 1: // Rising CO2 cloud / up arrow (Hypercapnia)
                    for (int y = 5; y <= 13; y++)
                        for (int x = 5; x <= 18; x++)
                            if ((x - 11.5f) * (x - 11.5f) / 42f + (y - 9f) * (y - 9f) / 20f <= 1f)
                                Plot(x, y, p);
                    for (int y = 11; y <= 20; y++) { Plot(11, y, a); Plot(12, y, a); }
                    for (int k = 0; k < 4; k++) { Plot(11 - k, 20 - k, a); Plot(12 + k, 20 - k, a); }
                    break;

                case 2: // Fast breath waves (Tachypnea / Hypocapnia)
                    for (int x = 3; x <= 20; x++)
                    {
                        int w1 = 16 + (int)Mathf.Round(Mathf.Sin(x * 0.9f) * 2.2f);
                        int w2 = 9 + (int)Mathf.Round(Mathf.Sin(x * 0.9f + 1.4f) * 2.2f);
                        Plot(x, w1, p); Plot(x, w1 + 1, p);
                        Plot(x, w2, a); Plot(x, w2 + 1, a);
                    }
                    break;

                case 3: // Acid-base diamond / droplet (pH imbalance)
                    for (int y = 4; y <= 20; y++)
                    {
                        int span = y <= 12 ? (y - 4) : (20 - y);
                        for (int x = 12 - span; x <= 11 + span; x++)
                            Plot(x, y, (y == 12 || x == 11 || x == 12) ? a : p);
                    }
                    break;

                case 4: // Central medullary depression (brainstem + downward arrow)
                default:
                    for (int y = 12; y <= 19; y++)
                        for (int x = 6; x <= 17; x++)
                            if ((x - 11.5f) * (x - 11.5f) / 36f + (y - 15.5f) * (y - 15.5f) / 16f <= 1f)
                                Plot(x, y, p);
                    for (int y = 4; y <= 13; y++) { Plot(11, y, a); Plot(12, y, a); }
                    for (int k = 0; k < 4; k++) { Plot(11 - k, 4 + k, a); Plot(12 + k, 4 + k, a); }
                    break;
            }

            tex.SetPixels32(px);
            tex.Apply(false);
            var spr = Sprite.Create(tex, new Rect(0, 0, s, s), new Vector2(0.5f, 0.5f), 33.33f);
            Cache[fullKey] = spr;
            return spr;
        }

        private static Texture2D MakeTex(int w, int h) =>
            new Texture2D(w, h, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
    }
}
