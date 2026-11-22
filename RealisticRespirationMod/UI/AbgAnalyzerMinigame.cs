using System;
using System.Collections.Generic;
using RespirationSimCore;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace RealisticRespirationMod.UI
{
    /// <summary>
    /// Full-size centered clinical Arterial Blood Gas (ABG) Analyzer Minigame.
    /// Opened either via [展开] on the chipped WoundView compact ABG card, via configurable hotkey (default K),
    /// or via the Portable ABG Analyzer ('abgmonitor') item even when unchipped.
    /// Displays live PaO2, PaCO2, pH, HCO3-, BE with reference-range bars, medullary excitability,
    /// pulmonary failure diagnostics, and a 3-minute rolling multi-parameter trend graph.
    /// </summary>
    public sealed class AbgAnalyzerMinigame : Minigame
    {
        private const int ChartW = 496;
        private const int ChartH = 172;

        private readonly Body _body;
        private readonly bool _openedWithItem;

        private GameObject? _rootGo;
        private Texture2D? _chartTex;
        private Color32[] _chartBg = Array.Empty<Color32>();
        private Color32[] _chartFrame = Array.Empty<Color32>();
        private readonly AbgHistorySample[] _historyBuf = new AbgHistorySample[180];

        private bool _frozen;
        private float _refreshTimer;

        private RectTransform? _freezeBtnRect;
        private RectTransform? _closeBtnRect;
        private TextMeshProUGUI? _freezeBtnText;

        private TextMeshProUGUI? _headerDiagnosisText;
        private TextMeshProUGUI? _respOverviewText;
        private TextMeshProUGUI? _drugAndCompatText;

        // 5 ABG parameter rows: pH, PaO2, PaCO2, HCO3-, BE
        private readonly TextMeshProUGUI?[] _paramValues = new TextMeshProUGUI?[5];
        private readonly Image?[] _paramBarFills = new Image?[5];
        private readonly RectTransform?[] _paramBarMarkers = new RectTransform?[5];

        public AbgAnalyzerMinigame(Body body, bool openedWithItem)
        {
            _body = body;
            _openedWithItem = openedWithItem;
        }

        public override HandSpriteType HandType() => HandSpriteType.Point;

        public override string GuideLocaleString() => "abgAnalyzerGuide";

        public override bool NeedsItem() => _openedWithItem;

        public override bool CanExit() => true;

        public override void Start()
        {
            TMP_FontAsset? font = ResolveSharedFont();

            var root = new GameObject("AbgAnalyzerScreen", typeof(RectTransform), typeof(Image));
            root.transform.SetParent(Minigame.game.minigameScreen, false);
            root.transform.SetSiblingIndex(0);
            Minigame.game.spawnedMiniGame = root.transform;
            _rootGo = root;

            var rootRt = root.GetComponent<RectTransform>();
            rootRt.anchorMin = new Vector2(0.5f, 0.5f);
            rootRt.anchorMax = new Vector2(0.5f, 0.5f);
            rootRt.pivot = new Vector2(0.5f, 0.5f);
            rootRt.anchoredPosition = Vector2.zero;
            rootRt.sizeDelta = new Vector2(880f, 520f);
            root.GetComponent<Image>().color = new Color(0.06f, 0.10f, 0.14f, 0.97f);

            CreatePanel(root.transform, "Bezel", Vector2.zero, new Vector2(856f, 496f), new Color(0.025f, 0.048f, 0.074f, 1f));

            // Title & Diagnosis Header
            CreateText(root.transform, "MainTitle", font, new Vector2(-245f, 222f), new Vector2(340f, 28f),
                TextAlignmentOptions.Left, 18.5f, new Color(0.45f, 0.94f, 1f, 1f),
                _openedWithItem ? "便携式动脉血气分析仪 (ABG)" : "生物芯片内置血气分析仪 (ABG)");

            _headerDiagnosisText = CreateText(root.transform, "DiagLabel", font, new Vector2(180f, 222f), new Vector2(460f, 28f),
                TextAlignmentOptions.Right, 16.5f, new Color(0.52f, 0.96f, 0.78f, 1f));

            // Left column: 5 ABG Parameter Cards with Reference Range Bars
            BuildParameterRow(root.transform, font, 0, "动脉血 pH", "7.35 - 7.45", 154f, 6.80f, 7.70f, 7.35f, 7.45f);
            BuildParameterRow(root.transform, font, 1, "血氧分压 PaO2", "80 - 100 mmHg", 92f, 15f, 120f, 80f, 100f);
            BuildParameterRow(root.transform, font, 2, "二氧化碳分压 PaCO2", "35 - 45 mmHg", 30f, 15f, 110f, 35f, 45f);
            BuildParameterRow(root.transform, font, 3, "实际碳酸氢根 HCO3-", "22 - 26 mmol/L", -32f, 8f, 42f, 22f, 26f);
            BuildParameterRow(root.transform, font, 4, "碱剩余 BE", "-2.0 ~ +2.0", -94f, -20f, 20f, -2f, 2f);

            // Right column top: Respiratory & Medullary Center Panel (top=198, bottom=78)
            CreatePanel(root.transform, "RespBox", new Vector2(166f, 138f), new Vector2(504f, 120f), new Color(0.045f, 0.085f, 0.125f, 1f));
            _respOverviewText = CreateText(root.transform, "RespOverview", font, new Vector2(166f, 138f), new Vector2(486f, 108f),
                TextAlignmentOptions.TopLeft, 12f, new Color(0.88f, 0.96f, 1f, 1f));
            _respOverviewText.lineSpacing = 12f;
            _respOverviewText.enableWordWrapping = false;
            _respOverviewText.overflowMode = TextOverflowModes.Ellipsis;

            // Legend row cleanly separated below RespBox (top=72, bottom=52)
            var legendTmp = CreateText(root.transform, "ChartLegend", font, new Vector2(166f, 62f), new Vector2(502f, 20f),
                TextAlignmentOptions.Left, 11f, new Color(0.75f, 0.88f, 0.96f, 0.95f),
                "3分钟趋势:  <color=#52e4ff>■ PaO2(0-120)</color>  <color=#ffbe46>■ PaCO2(0-120)</color>  <color=#58f29c>■ pH(6.8-7.7)</color>");
            legendTmp.enableWordWrapping = false;

            // Right column bottom: 3-Minute Live Trend Graph (top=48, bottom=-132)
            CreatePanel(root.transform, "ChartBox", new Vector2(166f, -42f), new Vector2(504f, 180f), new Color(0.03f, 0.06f, 0.09f, 1f));

            _chartTex = new Texture2D(ChartW, ChartH, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            root.AddComponent<AbgTextureCleanup>().Tex = _chartTex;
            BuildChartBackground();

            var chartGo = new GameObject("AbgTrendChart", typeof(RectTransform), typeof(RawImage));
            chartGo.transform.SetParent(root.transform, false);
            var chartRt = chartGo.GetComponent<RectTransform>();
            chartRt.anchorMin = new Vector2(0.5f, 0.5f);
            chartRt.anchorMax = new Vector2(0.5f, 0.5f);
            chartRt.pivot = new Vector2(0.5f, 0.5f);
            chartRt.anchoredPosition = new Vector2(166f, -42f);
            chartRt.sizeDelta = new Vector2(ChartW, ChartH);
            chartGo.GetComponent<RawImage>().texture = _chartTex;

            // Bottom summary bar (drugs / suppression / tri-mod state)
            _drugAndCompatText = CreateText(root.transform, "DrugStatus", font, new Vector2(-108f, -166f), new Vector2(610f, 40f),
                TextAlignmentOptions.Left, 11.5f, new Color(0.76f, 0.86f, 0.94f, 0.95f));
            _drugAndCompatText.lineSpacing = 8f;
            _drugAndCompatText.enableWordWrapping = false;

            // Bottom control buttons
            _freezeBtnRect = CreateButton(root.transform, "FreezeBtn", font, new Vector2(215f, -214f), new Vector2(175f, 34f),
                new Color(0.18f, 0.22f, 0.14f, 1f), out _freezeBtnText);
            _closeBtnRect = CreateButton(root.transform, "CloseBtn", font, new Vector2(362f, -214f), new Vector2(106f, 34f),
                new Color(0.32f, 0.13f, 0.14f, 1f), out var closeTxt);
            if (closeTxt != null) closeTxt.text = "关闭 [ESC]";

            UpdateFreezeLabel();
            RefreshDisplay();
        }

        public override void Update(List<RaycastResult> uiCasts)
        {
            if (_rootGo == null || _chartTex == null) return;

            if (Minigame.game.handStartedClicking && uiCasts != null)
            {
                foreach (RaycastResult hit in uiCasts)
                {
                    GameObject go = hit.gameObject;
                    if (_closeBtnRect != null && (go == _closeBtnRect.gameObject || go.transform.IsChildOf(_closeBtnRect)))
                    {
                        Minigame.game.EndMinigame();
                        return;
                    }
                    if (_freezeBtnRect != null && (go == _freezeBtnRect.gameObject || go.transform.IsChildOf(_freezeBtnRect)))
                    {
                        _frozen = !_frozen;
                        UpdateFreezeLabel();
                        break;
                    }
                }
            }

            if (_frozen) return;

            _refreshTimer += Time.unscaledDeltaTime;
            if (_refreshTimer >= 0.12f)
            {
                _refreshTimer = 0f;
                RefreshDisplay();
            }
        }

        private void UpdateFreezeLabel()
        {
            if (_freezeBtnText != null)
                _freezeBtnText.text = _frozen ? "状态: 已冻结 (继续)" : "状态: 实时采样 (冻结)";
        }

        private void RefreshDisplay()
        {
            var ctrl = RespirationModManager.GetOrCreate(_body);
            ref readonly var s = ref ctrl.Sim.LastTelemetry;

            // Header diagnosis
            if (_headerDiagnosisText != null)
            {
                string diag = BodyRespirationSimulator.GetAcidBaseDisplayName(s.AcidBase);
                string hypox = s.PaO2MmHg < 60f ? " · 重度低氧血症" : (s.PaO2MmHg < 80f ? " · 轻度低氧" : "");
                string fail = s.IsPulmonaryFailure ? " · ⚠ 肺衰竭" : "";
                _headerDiagnosisText.text = diag + hypox + fail;
                _headerDiagnosisText.color = (s.IsPulmonaryFailure || s.BloodPh < 7.20f || s.BloodPh > 7.58f || s.PaO2MmHg < 55f)
                    ? new Color(1f, 0.36f, 0.36f, 1f)
                    : ((s.BloodPh < 7.35f || s.BloodPh > 7.45f || s.PaO2MmHg < 80f)
                        ? new Color(1f, 0.82f, 0.34f, 1f)
                        : new Color(0.48f, 0.96f, 0.78f, 1f));
            }

            // Update 5 parameter cards & bar markers
            UpdateParamRow(0, $"{s.BloodPh:0.00}", s.BloodPh, 6.80f, 7.70f, 7.35f, 7.45f, 7.20f, 7.58f);
            UpdateParamRow(1, $"{s.PaO2MmHg:0.0} mmHg", s.PaO2MmHg, 15f, 120f, 80f, 105f, 55f, 130f);
            UpdateParamRow(2, $"{s.PaCO2MmHg:0.0} mmHg", s.PaCO2MmHg, 15f, 110f, 35f, 45f, 24f, 65f);
            UpdateParamRow(3, $"{s.BicarbonateMmolL:0.1} mmol/L", s.BicarbonateMmolL, 8f, 42f, 22f, 26f, 15f, 34f);
            UpdateParamRow(4, $"{s.BaseExcessMmolL:+0.0;-0.0;0.0} mmol/L", s.BaseExcessMmolL, -20f, 20f, -2.5f, 2.5f, -8f, 8f);

            // Respiratory & Medullary Overview
            if (_respOverviewText != null)
            {
                string failText = s.IsPulmonaryFailure
                    ? $"<color=#ff5252>肺衰竭! ({BodyRespirationSimulator.GetFailureReasonDisplayName(s.FailureReason)})</color>"
                    : "<color=#66f2a8>肺通气泵完整</color>";
                string medColor = s.MedullaryExcitability < 0.55f ? "#ff6868" : (s.MedullaryExcitability > 1.85f ? "#ffbe46" : "#66f2b8");

                _respOverviewText.text =
                    $"呼吸频率: <b>{s.ActualRpm:0.0}/m</b> (上限{RespirationConfig.MaxRespiratoryRateBpm:0})  |  目标接近值: <b>{s.MedullaryTargetRpm:0.0}/m</b>\n" +
                    $"延髓呼吸中枢: <color={medColor}><b>{Mathf.RoundToInt(s.MedullaryExcitability * 100f)}%</b></color>  |  血氧饱和度 SpO2: <b>{s.SpO2Percent:0.0}%</b>\n" +
                    $"通气效率: <b>{Mathf.RoundToInt(s.AlveolarVentilationEff * 100f)}%</b>  |  肺灌注: <b>{Mathf.RoundToInt(s.PerfusionEff * 100f)}%</b>  |  产CO2: <b>{s.MetabolicCo2Production:0.00}x</b>\n" +
                    $"肺功能状态: {failText}";
            }

            if (_drugAndCompatText != null)
            {
                string opiateStr = s.OpiateSuppressionFactor > 0.05f
                    ? $"<color=#ff8855>阿片中枢抑制:{Mathf.RoundToInt(s.OpiateSuppressionFactor * 100f)}%</color>"
                    : "阿片抑制:0%";
                string bzdStr = s.BzdSuppressionFactor > 0.05f
                    ? $"  <color=#ffaa55>镇静/BZD抑制:{Mathf.RoundToInt(s.BzdSuppressionFactor * 100f)}%</color>"
                    : "";
                string nikStr = s.NikethamideConc > 0.05f
                    ? $"  <color=#70f5c8>尼可刹米:{s.NikethamideConc:0.00}</color>"
                    : "";
                string extStr = ctrl.Sim.Modifiers.ActiveExcitabilityModifierCount > 0
                    ? $"  外部中枢修正:{ctrl.Sim.Modifiers.ActiveExcitabilityModifierCount}项"
                    : "";

                _drugAndCompatText.text =
                    $"药物与中枢影响: {opiateStr}{bzdStr}{nikStr}{extStr}\n" +
                    $"系统联动: 心电大修[{(Compat.CardiacCompatBridge.IsLoaded ? "已耦合" : "独立")}]  神经大修[{(Compat.NeuroCompatBridge.IsLoaded ? "已耦合" : "独立")}]";
            }

            RedrawTrendChart(ctrl.Sim);
        }

        private void UpdateParamRow(int idx, string valueStr, float val, float minV, float maxV, float normLo, float normHi, float critLo, float critHi)
        {
            Color col;
            if (val < critLo || val > critHi) col = new Color(1f, 0.36f, 0.36f, 1f);
            else if (val < normLo || val > normHi) col = new Color(1f, 0.80f, 0.32f, 1f);
            else col = new Color(0.45f, 0.96f, 0.72f, 1f);

            var txt = _paramValues[idx];
            if (txt != null)
            {
                txt.text = valueStr;
                txt.color = col;
            }

            float norm = Mathf.Clamp01((val - minV) / Mathf.Max(0.001f, maxV - minV));
            var marker = _paramBarMarkers[idx];
            if (marker != null)
            {
                marker.anchorMin = new Vector2(norm, 0.5f);
                marker.anchorMax = new Vector2(norm, 0.5f);
                marker.anchoredPosition = Vector2.zero;
            }
            var fill = _paramBarFills[idx];
            if (fill != null)
            {
                fill.color = col;
            }
        }

        private void BuildParameterRow(
            Transform parent,
            TMP_FontAsset? font,
            int idx,
            string label,
            string refRange,
            float posY,
            float minV,
            float maxV,
            float normLo,
            float normHi)
        {
            var box = CreatePanel(parent, $"ParamRow_{idx}", new Vector2(-258f, posY), new Vector2(316f, 54f), new Color(0.045f, 0.085f, 0.125f, 1f));

            CreateText(box.transform, "Name", font, new Vector2(-64f, 13f), new Vector2(172f, 20f),
                TextAlignmentOptions.Left, 12.5f, new Color(0.78f, 0.90f, 0.98f, 1f), label);

            _paramValues[idx] = CreateText(box.transform, "Value", font, new Vector2(76f, 13f), new Vector2(148f, 22f),
                TextAlignmentOptions.Right, 14.5f, Color.white);

            // Reference bar track
            var trackGo = new GameObject("Track", typeof(RectTransform), typeof(Image));
            trackGo.transform.SetParent(box.transform, false);
            var trackRt = trackGo.GetComponent<RectTransform>();
            trackRt.anchorMin = new Vector2(0.5f, 0.5f);
            trackRt.anchorMax = new Vector2(0.5f, 0.5f);
            trackRt.anchoredPosition = new Vector2(0f, -6f);
            trackRt.sizeDelta = new Vector2(292f, 8f);
            trackGo.GetComponent<Image>().color = new Color(0.10f, 0.16f, 0.22f, 1f);

            // Normal range highlight zone on track
            float n0 = Mathf.Clamp01((normLo - minV) / (maxV - minV));
            float n1 = Mathf.Clamp01((normHi - minV) / (maxV - minV));
            var normGo = new GameObject("NormalZone", typeof(RectTransform), typeof(Image));
            normGo.transform.SetParent(trackGo.transform, false);
            var normRt = normGo.GetComponent<RectTransform>();
            normRt.anchorMin = new Vector2(n0, 0f);
            normRt.anchorMax = new Vector2(n1, 1f);
            normRt.offsetMin = Vector2.zero;
            normRt.offsetMax = Vector2.zero;
            normGo.GetComponent<Image>().color = new Color(0.18f, 0.45f, 0.34f, 0.85f);

            // Current value needle marker
            var markGo = new GameObject("Marker", typeof(RectTransform), typeof(Image));
            markGo.transform.SetParent(trackGo.transform, false);
            var markRt = markGo.GetComponent<RectTransform>();
            markRt.sizeDelta = new Vector2(6f, 14f);
            var markImg = markGo.GetComponent<Image>();
            markImg.color = Color.white;
            _paramBarFills[idx] = markImg;
            _paramBarMarkers[idx] = markRt;

            CreateText(box.transform, "Ref", font, new Vector2(0f, -20f), new Vector2(292f, 14f),
                TextAlignmentOptions.Right, 10f, new Color(0.54f, 0.68f, 0.78f, 0.90f), $"参考: {refRange}");
        }

        private void BuildChartBackground()
        {
            _chartBg = new Color32[ChartW * ChartH];
            _chartFrame = new Color32[ChartW * ChartH];
            var bg = new Color32(10, 20, 30, 255);
            var grid = new Color32(22, 42, 58, 255);
            var mid = new Color32(34, 62, 82, 255);

            for (int y = 0; y < ChartH; y++)
            {
                bool hGrid = (y % 38 == 0);
                for (int x = 0; x < ChartW; x++)
                {
                    bool vGrid = (x % 50 == 0);
                    _chartBg[y * ChartW + x] = (y == ChartH / 2) ? mid : ((hGrid || vGrid) ? grid : bg);
                }
            }
        }

        private void RedrawTrendChart(BodyRespirationSimulator sim)
        {
            if (_chartTex == null) return;
            Array.Copy(_chartBg, _chartFrame, _chartBg.Length);

            int count = sim.CopyHistoryChronological(_historyBuf, _historyBuf.Length);
            if (count >= 2)
            {
                var cPo2 = new Color32(82, 228, 255, 255);
                var cPco2 = new Color32(255, 190, 70, 255);
                var cPh = new Color32(88, 242, 156, 255);

                int prevX = 0;
                int prevYPo2 = MapY(_historyBuf[0].PaO2, 0f, 120f);
                int prevYPco2 = MapY(_historyBuf[0].PaCO2, 0f, 120f);
                int prevYPh = MapY(_historyBuf[0].BloodPh, 6.80f, 7.70f);

                for (int i = 1; i < count; i++)
                {
                    int x = Mathf.RoundToInt((i / (float)(count - 1)) * (ChartW - 1));
                    int yPo2 = MapY(_historyBuf[i].PaO2, 0f, 120f);
                    int yPco2 = MapY(_historyBuf[i].PaCO2, 0f, 120f);
                    int yPh = MapY(_historyBuf[i].BloodPh, 6.80f, 7.70f);

                    DrawLine(prevX, prevYPo2, x, yPo2, cPo2);
                    DrawLine(prevX, prevYPco2, x, yPco2, cPco2);
                    DrawLine(prevX, prevYPh, x, yPh, cPh);

                    prevX = x;
                    prevYPo2 = yPo2;
                    prevYPco2 = yPco2;
                    prevYPh = yPh;
                }
            }

            _chartTex.SetPixels32(_chartFrame);
            _chartTex.Apply(false);
        }

        private static int MapY(float val, float minV, float maxV)
        {
            float t = Mathf.Clamp01((val - minV) / Mathf.Max(0.001f, maxV - minV));
            return Mathf.Clamp(Mathf.RoundToInt(4 + t * (ChartH - 9)), 2, ChartH - 3);
        }

        private void DrawLine(int x0, int y0, int x1, int y1, Color32 c)
        {
            int dx = Math.Abs(x1 - x0);
            int dy = Math.Abs(y1 - y0);
            int sx = x0 < x1 ? 1 : -1;
            int sy = y0 < y1 ? 1 : -1;
            int err = dx - dy;

            while (true)
            {
                if (x0 >= 0 && x0 < ChartW && y0 >= 0 && y0 < ChartH)
                {
                    _chartFrame[y0 * ChartW + x0] = c;
                    if (y0 + 1 < ChartH) _chartFrame[(y0 + 1) * ChartW + x0] = c;
                }
                if (x0 == x1 && y0 == y1) break;
                int e2 = err * 2;
                if (e2 > -dy) { err -= dy; x0 += sx; }
                if (e2 < dx) { err += dx; y0 += sy; }
            }
        }

        private static TMP_FontAsset? ResolveSharedFont()
        {
            if (WoundView.view != null && WoundView.view.oxyText != null) return WoundView.view.oxyText.font;
            return TMP_Settings.defaultFontAsset;
        }

        private static GameObject CreatePanel(Transform parent, string name, Vector2 pos, Vector2 size, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            go.GetComponent<Image>().color = color;
            return go;
        }

        private static TextMeshProUGUI CreateText(
            Transform parent, string name, TMP_FontAsset? font,
            Vector2 pos, Vector2 size, TextAlignmentOptions align,
            float fontSize, Color color, string text = "")
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            var tmp = go.GetComponent<TextMeshProUGUI>();
            if (font != null) tmp.font = font;
            tmp.alignment = align;
            tmp.fontSize = fontSize;
            tmp.color = color;
            tmp.text = text;
            tmp.raycastTarget = false;
            return tmp;
        }

        private static RectTransform CreateButton(
            Transform parent, string name, TMP_FontAsset? font,
            Vector2 pos, Vector2 size, Color bgColor, out TextMeshProUGUI label)
        {
            var go = CreatePanel(parent, name, pos, size, bgColor);
            go.GetComponent<Image>().raycastTarget = true;
            label = CreateText(go.transform, "Label", font, Vector2.zero, size, TextAlignmentOptions.Center, 13.5f, Color.white);
            return go.GetComponent<RectTransform>();
        }

        private sealed class AbgTextureCleanup : MonoBehaviour
        {
            public Texture2D? Tex;
            private void OnDestroy()
            {
                if (Tex != null) UnityEngine.Object.Destroy(Tex);
            }
        }
    }
}
