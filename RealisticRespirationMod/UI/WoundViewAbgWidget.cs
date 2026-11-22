using RespirationSimCore;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace RealisticRespirationMod.UI
{
    /// <summary>
    /// Chipped-only Arterial Blood Gas (ABG) button docked directly to the right of WoundView.oxyText.
    /// Clicking the button toggles a non-blocking compact floating ABG card right inside WoundView
    /// (allowing players to administer drugs / chest drains while watching live PaO2 / PaCO2 / pH / HCO3-),
    /// and the card provides an [展开] button to open the full centered AbgAnalyzerMinigame!
    /// </summary>
    internal sealed class WoundViewAbgWidget
    {
        private bool _initialized;

        private GameObject? _buttonGo;
        private Image? _buttonBg;
        private TextMeshProUGUI? _buttonLabel;

        private GameObject? _cardGo;
        private Image? _cardBorder;
        private TextMeshProUGUI? _cardBodyText;
        private TextMeshProUGUI? _cardStatusText;

        private UITooltip? _respTooltip;

        public void EnsureAndUpdate(WoundView view)
        {
            if (view == null || view.body == null) return;

            bool chipped = !WorldGeneration.unchipped && view.gameObject.activeInHierarchy;
            if (!_initialized)
            {
                BuildUi(view);
                _initialized = true;
            }

            var ctrl = RespirationModManager.GetOrCreate(view.body);
            ref readonly var s = ref ctrl.Sim.LastTelemetry;

            // Update rich hover tooltip on respiratoryText (always helpful when chipped)
            if (_respTooltip != null)
            {
                string failStr = s.IsPulmonaryFailure
                    ? $"\n<color=#ff5555>肺衰竭: {BodyRespirationSimulator.GetFailureReasonDisplayName(s.FailureReason)}</color>"
                    : "";
                _respTooltip.tipName = "呼吸系统与延髓中枢";
                _respTooltip.tipDesc =
                    $"实际呼吸: {s.ActualRpm:0.0}/m (目标 {s.MedullaryTargetRpm:0.0}/m, 上限 {RespirationConfig.MaxRespiratoryRateBpm:0}/m)\n" +
                    $"延髓兴奋度: {Mathf.RoundToInt(s.MedullaryExcitability * 100f)}%  |  肺泡通气效率: {Mathf.RoundToInt(s.AlveolarVentilationEff * 100f)}%\n" +
                    $"PaCO2: {s.PaCO2MmHg:0.0} mmHg  |  PaO2: {s.PaO2MmHg:0.0} mmHg  |  pH: {s.BloodPh:0.00}" +
                    failStr;
            }

            if (_buttonGo != null && _buttonGo.activeSelf != chipped)
            {
                _buttonGo.SetActive(chipped);
            }

            bool showCard = chipped && ctrl.CompactAbgCardOpen;
            if (_cardGo != null && _cardGo.activeSelf != showCard)
            {
                _cardGo.SetActive(showCard);
                if (showCard)
                {
                    _cardGo.transform.SetAsLastSibling();
                    PositionCardNearButton(view);
                }
            }

            if (!chipped) return;

            bool abnormalPh = s.BloodPh < 7.35f || s.BloodPh > 7.45f;
            bool critical = s.IsPulmonaryFailure || s.BloodPh < 7.20f || s.BloodPh > 7.58f || s.PaCO2MmHg > 62f || s.PaO2MmHg < 55f;

            if (_buttonBg != null && _buttonLabel != null)
            {
                if (critical)
                {
                    _buttonBg.color = new Color(0.38f, 0.10f, 0.12f, 0.95f);
                    _buttonLabel.color = new Color(1f, 0.55f, 0.50f, 1f);
                }
                else if (abnormalPh || s.PaCO2MmHg > 46f || s.PaCO2MmHg < 34f || s.PaO2MmHg < 78f)
                {
                    _buttonBg.color = new Color(0.30f, 0.22f, 0.08f, 0.94f);
                    _buttonLabel.color = new Color(1f, 0.86f, 0.42f, 1f);
                }
                else
                {
                    _buttonBg.color = ctrl.CompactAbgCardOpen
                        ? new Color(0.12f, 0.30f, 0.36f, 0.96f)
                        : new Color(0.08f, 0.18f, 0.24f, 0.92f);
                    _buttonLabel.color = new Color(0.48f, 0.95f, 1f, 1f);
                }
                _buttonLabel.text = ctrl.CompactAbgCardOpen ? "血气▾" : "血气";
            }

            if (!showCard) return;

            if (_cardBorder != null)
            {
                _cardBorder.color = critical
                    ? new Color(0.45f, 0.12f, 0.14f, 0.96f)
                    : new Color(0.06f, 0.14f, 0.20f, 0.95f);
            }

            if (_cardBodyText != null)
            {
                string phColor = (s.BloodPh < 7.35f || s.BloodPh > 7.45f) ? (s.BloodPh < 7.20f || s.BloodPh > 7.58f ? "#ff5a5a" : "#ffd25a") : "#64f0a8";
                string po2Color = s.PaO2MmHg < 60f ? "#ff5a5a" : (s.PaO2MmHg < 80f ? "#ffd25a" : "#5ae4ff");
                string pco2Color = (s.PaCO2MmHg > 55f || s.PaCO2MmHg < 26f) ? "#ff5a5a" : ((s.PaCO2MmHg > 45f || s.PaCO2MmHg < 35f) ? "#ffd25a" : "#88f0b8");
                string hco3Color = (s.BicarbonateMmolL < 18f || s.BicarbonateMmolL > 30f) ? "#ffd25a" : "#cce8ff";
                string medColor = s.MedullaryExcitability < 0.55f ? "#ff6e6e" : (s.MedullaryExcitability > 1.85f ? "#ffb84d" : "#70f5c8");

                _cardBodyText.text =
                    $"pH    : <color={phColor}>{s.BloodPh:0.00}</color>  (7.35-7.45)\n" +
                    $"PaO2  : <color={po2Color}>{s.PaO2MmHg:0.0}</color> mmHg  (SpO2 {Mathf.RoundToInt(s.SpO2Percent)}%)\n" +
                    $"PaCO2 : <color={pco2Color}>{s.PaCO2MmHg:0.0}</color> mmHg  (35-45)\n" +
                    $"HCO3- : <color={hco3Color}>{s.BicarbonateMmolL:0.1}</color> mmol/L (BE {s.BaseExcessMmolL:+0.0;-0.0;0.0})\n" +
                    $"延髓中枢: <color={medColor}>{Mathf.RoundToInt(s.MedullaryExcitability * 100f)}%</color>  通气效率: {Mathf.RoundToInt(s.AlveolarVentilationEff * 100f)}%";
            }

            if (_cardStatusText != null)
            {
                string diag = BodyRespirationSimulator.GetAcidBaseDisplayName(s.AcidBase);
                if (s.IsPulmonaryFailure)
                {
                    _cardStatusText.text = $"<color=#ff5050>⚠ 肺衰竭 ({BodyRespirationSimulator.GetFailureReasonDisplayName(s.FailureReason)})</color>";
                }
                else if (s.OpiateSuppressionFactor > 0.35f || s.BzdSuppressionFactor > 0.30f)
                {
                    _cardStatusText.text = $"<color=#ffaa44>{diag} | 药物中枢抑制</color>";
                }
                else
                {
                    _cardStatusText.text = diag;
                }
            }
        }

        private void BuildUi(WoundView view)
        {
            TMP_FontAsset? font = view.oxyText != null
                ? view.oxyText.font
                : (view.respiratoryText != null ? view.respiratoryText.font : TMP_Settings.defaultFontAsset);

            // Attach live tooltip to respiratoryText if not already present
            if (view.respiratoryText != null)
            {
                _respTooltip = view.respiratoryText.GetComponent<UITooltip>() ?? view.respiratoryText.gameObject.AddComponent<UITooltip>();
                _respTooltip.skipLocale = true;
            }

            // 1. Dock [血气] button directly to the RIGHT of WoundView.oxyText
            RectTransform? oxyRt = view.oxyText != null ? view.oxyText.rectTransform : null;
            Transform btnParent = oxyRt != null && oxyRt.parent != null ? oxyRt.parent : view.transform;

            var btnGo = new GameObject("RespAbgButton", typeof(RectTransform), typeof(Image), typeof(Button));
            btnGo.transform.SetParent(btnParent, false);
            _buttonGo = btnGo;

            var rt = btnGo.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(44f, 20f);
            if (oxyRt != null)
            {
                rt.anchorMin = oxyRt.anchorMin;
                rt.anchorMax = oxyRt.anchorMax;
                rt.pivot = new Vector2(0f, 0.5f);
                float rightEdgeX = oxyRt.anchoredPosition.x + (1f - oxyRt.pivot.x) * oxyRt.rect.width;
                float centerY = oxyRt.anchoredPosition.y + (0.5f - oxyRt.pivot.y) * oxyRt.rect.height;
                rt.anchoredPosition = new Vector2(rightEdgeX + 6f, centerY);
            }
            else
            {
                rt.anchorMin = new Vector2(0.5f, 0.5f);
                rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot = new Vector2(0f, 0.5f);
                rt.anchoredPosition = new Vector2(60f, 0f);
            }

            _buttonBg = btnGo.GetComponent<Image>();
            _buttonBg.color = new Color(0.08f, 0.18f, 0.24f, 0.92f);

            var lblGo = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
            lblGo.transform.SetParent(btnGo.transform, false);
            var lblRt = lblGo.GetComponent<RectTransform>();
            lblRt.anchorMin = Vector2.zero;
            lblRt.anchorMax = Vector2.one;
            lblRt.sizeDelta = Vector2.zero;

            var lbl = lblGo.GetComponent<TextMeshProUGUI>();
            if (font != null) lbl.font = font;
            lbl.fontSize = 11.5f;
            lbl.alignment = TextAlignmentOptions.Center;
            lbl.color = new Color(0.48f, 0.95f, 1f, 1f);
            lbl.text = "血气";
            lbl.raycastTarget = false;
            _buttonLabel = lbl;

            var tip = btnGo.AddComponent<UITooltip>();
            tip.skipLocale = true;
            tip.tipName = "芯片内置血气分析仪 (ABG)";
            tip.tipDesc = "点击展开/收起实时血气浮动卡片（查看 PaO2、PaCO2、血液 pH、HCO3- 与延髓呼吸中枢），卡片右上角可展开完整分析仪界面。";

            var btn = btnGo.GetComponent<Button>();
            btn.onClick.AddListener(() =>
            {
                Body? targetBody = view.body != null ? view.body : (PlayerCamera.main != null ? PlayerCamera.main.body : null);
                if (targetBody == null) return;
                var ctrl = RespirationModManager.GetOrCreate(targetBody);
                ctrl.CompactAbgCardOpen = !ctrl.CompactAbgCardOpen;
                if (PlayerCamera.main != null)
                    PlayerCamera.main.PlayUISound(PlayerCamera.UISoundType.MiniClick, ctrl.CompactAbgCardOpen ? 1.12f : 0.95f);
            });

            // 2. Build compact non-blocking floating ABG card docked cleanly beside the button
            var cardGo = new GameObject("RespCompactAbgCard", typeof(RectTransform), typeof(Image));
            cardGo.transform.SetParent(view.transform, false);
            cardGo.transform.SetAsLastSibling();
            _cardGo = cardGo;

            var cardRt = cardGo.GetComponent<RectTransform>();
            cardRt.sizeDelta = new Vector2(268f, 148f);
            cardRt.anchorMin = new Vector2(0.5f, 0.5f);
            cardRt.anchorMax = new Vector2(0.5f, 0.5f);
            cardRt.pivot = new Vector2(0f, 0.5f);
            cardRt.anchoredPosition = new Vector2(115f, -10f);

            _cardBorder = cardGo.GetComponent<Image>();
            _cardBorder.color = new Color(0.06f, 0.14f, 0.20f, 0.95f);

            var innerGo = new GameObject("InnerBg", typeof(RectTransform), typeof(Image));
            innerGo.transform.SetParent(cardGo.transform, false);
            var innerRt = innerGo.GetComponent<RectTransform>();
            innerRt.anchorMin = Vector2.zero;
            innerRt.anchorMax = Vector2.one;
            innerRt.offsetMin = new Vector2(2f, 2f);
            innerRt.offsetMax = new Vector2(-2f, -2f);
            innerGo.GetComponent<Image>().color = new Color(0.03f, 0.06f, 0.10f, 0.96f);

            // Header title
            var titleGo = new GameObject("Title", typeof(RectTransform), typeof(TextMeshProUGUI));
            titleGo.transform.SetParent(innerGo.transform, false);
            var titleRt = titleGo.GetComponent<RectTransform>();
            titleRt.anchorMin = new Vector2(0f, 1f);
            titleRt.anchorMax = new Vector2(1f, 1f);
            titleRt.pivot = new Vector2(0f, 1f);
            titleRt.anchoredPosition = new Vector2(6f, -3f);
            titleRt.sizeDelta = new Vector2(-92f, 18f);
            var titleTmp = titleGo.GetComponent<TextMeshProUGUI>();
            if (font != null) titleTmp.font = font;
            titleTmp.fontSize = 11.5f;
            titleTmp.alignment = TextAlignmentOptions.Left;
            titleTmp.color = new Color(0.45f, 0.92f, 1f, 1f);
            titleTmp.text = "动脉血气分析 (ABG)";
            titleTmp.raycastTarget = false;

            // [展开] button -> launches full AbgAnalyzerMinigame
            CreateMiniButton(innerGo.transform, "ExpandBtn", font,
                new Vector2(-24f, -3f), new Vector2(44f, 17f),
                new Color(0.11f, 0.28f, 0.36f, 1f), new Color(0.55f, 0.98f, 1f, 1f), "展开 ↗",
                () =>
                {
                    Body? targetBody = view.body != null ? view.body : (PlayerCamera.main != null ? PlayerCamera.main.body : null);
                    if (targetBody == null) return;
                    RespirationModManager.OpenOrToggleAbgMinigame(targetBody, item: null);
                    if (PlayerCamera.main != null)
                        PlayerCamera.main.PlayUISound(PlayerCamera.UISoundType.MiniClick, 1.15f);
                });

            // [×] close button -> collapses compact card
            CreateMiniButton(innerGo.transform, "CloseBtn", font,
                new Vector2(-3f, -3f), new Vector2(18f, 17f),
                new Color(0.32f, 0.12f, 0.14f, 1f), new Color(1f, 0.72f, 0.72f, 1f), "×",
                () =>
                {
                    Body? targetBody = view.body != null ? view.body : (PlayerCamera.main != null ? PlayerCamera.main.body : null);
                    if (targetBody == null) return;
                    RespirationModManager.GetOrCreate(targetBody).CompactAbgCardOpen = false;
                    if (PlayerCamera.main != null)
                        PlayerCamera.main.PlayUISound(PlayerCamera.UISoundType.MiniClick, 0.9f);
                });

            // Main telemetry text
            var bodyTxtGo = new GameObject("BodyText", typeof(RectTransform), typeof(TextMeshProUGUI));
            bodyTxtGo.transform.SetParent(innerGo.transform, false);
            var bodyTxtRt = bodyTxtGo.GetComponent<RectTransform>();
            bodyTxtRt.anchorMin = new Vector2(0f, 0f);
            bodyTxtRt.anchorMax = new Vector2(1f, 1f);
            bodyTxtRt.offsetMin = new Vector2(7f, 22f);
            bodyTxtRt.offsetMax = new Vector2(-6f, -22f);
            var bodyTmp = bodyTxtGo.GetComponent<TextMeshProUGUI>();
            if (font != null) bodyTmp.font = font;
            bodyTmp.fontSize = 10.5f;
            bodyTmp.lineSpacing = 6f;
            bodyTmp.enableWordWrapping = false;
            bodyTmp.alignment = TextAlignmentOptions.TopLeft;
            bodyTmp.color = new Color(0.86f, 0.94f, 1f, 1f);
            bodyTmp.raycastTarget = false;
            _cardBodyText = bodyTmp;

            // Footer clinical diagnosis line
            var statusGo = new GameObject("StatusText", typeof(RectTransform), typeof(TextMeshProUGUI));
            statusGo.transform.SetParent(innerGo.transform, false);
            var statusRt = statusGo.GetComponent<RectTransform>();
            statusRt.anchorMin = new Vector2(0f, 0f);
            statusRt.anchorMax = new Vector2(1f, 0f);
            statusRt.pivot = new Vector2(0.5f, 0f);
            statusRt.anchoredPosition = new Vector2(0f, 4f);
            statusRt.sizeDelta = new Vector2(-12f, 17f);
            var statusTmp = statusGo.GetComponent<TextMeshProUGUI>();
            if (font != null) statusTmp.font = font;
            statusTmp.fontSize = 11f;
            statusTmp.alignment = TextAlignmentOptions.Left;
            statusTmp.color = new Color(0.70f, 0.96f, 0.85f, 1f);
            statusTmp.raycastTarget = false;
            _cardStatusText = statusTmp;

            cardGo.SetActive(false);
        }

        private static void CreateMiniButton(
            Transform parent,
            string name,
            TMP_FontAsset? font,
            Vector2 topRightAnchoredPos,
            Vector2 size,
            Color bgCol,
            Color txtCol,
            string label,
            UnityEngine.Events.UnityAction onClick)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(1f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(1f, 1f);
            rt.anchoredPosition = topRightAnchoredPos;
            rt.sizeDelta = size;
            go.GetComponent<Image>().color = bgCol;

            var tGo = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
            tGo.transform.SetParent(go.transform, false);
            var tRt = tGo.GetComponent<RectTransform>();
            tRt.anchorMin = Vector2.zero;
            tRt.anchorMax = Vector2.one;
            tRt.sizeDelta = Vector2.zero;
            var tmp = tGo.GetComponent<TextMeshProUGUI>();
            if (font != null) tmp.font = font;
            tmp.fontSize = 10.5f;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = txtCol;
            tmp.text = label;
            tmp.raycastTarget = false;

            go.GetComponent<Button>().onClick.AddListener(onClick);
        }

        private void PositionCardNearButton(WoundView view)
        {
            if (_cardGo == null || _buttonGo == null || view == null) return;
            var cardRt = _cardGo.GetComponent<RectTransform>();
            var viewRt = view.GetComponent<RectTransform>();
            if (cardRt == null || viewRt == null) return;

            Vector3 worldRight = _buttonGo.transform.TransformPoint(new Vector3(48f, 0f, 0f));
            Vector3 localPos = view.transform.InverseTransformPoint(worldRight);

            float halfW = viewRt.rect.width * 0.5f;
            float halfH = viewRt.rect.height * 0.5f;
            float cardW = cardRt.sizeDelta.x;
            float cardH = cardRt.sizeDelta.y;

            // If placing to the right overflows WoundView, flip cleanly to the left of the button
            if (halfW > 100f && localPos.x + cardW > halfW - 10f)
            {
                Vector3 worldLeft = _buttonGo.transform.TransformPoint(new Vector3(-6f, 0f, 0f));
                Vector3 leftLocal = view.transform.InverseTransformPoint(worldLeft);
                localPos.x = Mathf.Max(-halfW + 10f, leftLocal.x - cardW);
            }
            if (halfH > 80f)
            {
                localPos.y = Mathf.Clamp(localPos.y, -halfH + cardH * 0.5f + 10f, halfH - cardH * 0.5f - 10f);
            }

            cardRt.localPosition = new Vector3(localPos.x, localPos.y, 0f);
        }
    }
}
