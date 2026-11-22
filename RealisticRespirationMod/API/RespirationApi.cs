using System;
using RespirationSimCore;

namespace RealisticRespirationMod.API
{
    /// <summary>
    /// Public, mod-interoperability API for RealisticRespirationMod.
    /// All core methods intentionally accept only Unity/game Body + primitive types (string, float, bool)
    /// so external mods can either reference this assembly directly OR soft-call via Harmony AccessTools.Method
    /// with zero compile-time dependency!
    /// </summary>
    public static class RespirationApi
    {
        /// <summary>
        /// Invoked right before each Body's medullary respiratory center computes its step.
        /// Subscribers may inspect or update modifiers on the target body.
        /// </summary>
        public static event Action<Body, BodyRespirationSimulator>? OnBeforeMedullaryCompute;

        /// <summary>
        /// Invoked immediately after each Body's respiratory & blood gas step completes.
        /// </summary>
        public static event Action<Body, RespirationTelemetrySnapshot>? OnAfterRespirationStep;

        internal static void RaiseBeforeMedullaryCompute(Body body, BodyRespirationSimulator sim)
        {
            OnBeforeMedullaryCompute?.Invoke(body, sim);
        }

        internal static void RaiseAfterRespirationStep(Body body, in RespirationTelemetrySnapshot snapshot)
        {
            OnAfterRespirationStep?.Invoke(body, snapshot);
        }

        // --- Medullary Respiratory Center Excitability Modifiers (Keyed per sourceId) ---

        /// <summary>
        /// Registers or updates a keyed medullary respiratory center excitability modifier for a specific Body.
        /// Multiple mods using distinct sourceIds stack cleanly without clobbering each other.
        /// </summary>
        /// <param name="body">Target character Body.</param>
        /// <param name="sourceId">Unique stable identifier for the calling mod/effect (e.g. "com.mymod.caffeine").</param>
        /// <param name="additiveOffset">Additive excitability shift (0 = neutral; +1.0 adds ~15/m drive; negative depresses).</param>
        /// <param name="multiplier">Multiplicative excitability scale (1.0 = neutral; 0.5 halves drive; 0 = central paralysis).</param>
        /// <param name="durationSec">Auto-expiration duration in game seconds (&lt; 0 = persistent until explicitly removed).</param>
        public static void SetMedullaryExcitabilityModifier(
            Body? body,
            string sourceId,
            float additiveOffset,
            float multiplier = 1f,
            float durationSec = -1f)
        {
            if (body == null || string.IsNullOrWhiteSpace(sourceId)) return;
            RespirationModManager.GetOrCreate(body).Sim.Modifiers.SetExcitabilityModifier(
                sourceId,
                additiveOffset,
                multiplier,
                durationSec);
        }

        /// <summary>
        /// Registers or updates a keyed CO2 chemosensitivity / threshold modifier on the medullary respiratory center.
        /// </summary>
        /// <param name="body">Target character Body.</param>
        /// <param name="sourceId">Unique stable identifier for the calling mod/effect.</param>
        /// <param name="sensitivityMult">CO2 response slope multiplier (1.0 = normal; &lt;1 blunts CO2 drive; &gt;1 sensitizes).</param>
        /// <param name="co2ThresholdOffsetMmHg">Shifts CO2 activation threshold in mmHg (+ right-shifts / depresses; - left-shifts / sensitizes).</param>
        /// <param name="durationSec">Auto-expiration duration in game seconds (&lt; 0 = persistent until removed).</param>
        public static void SetChemosensitivityModifier(
            Body? body,
            string sourceId,
            float sensitivityMult,
            float co2ThresholdOffsetMmHg = 0f,
            float durationSec = -1f)
        {
            if (body == null || string.IsNullOrWhiteSpace(sourceId)) return;
            RespirationModManager.GetOrCreate(body).Sim.Modifiers.SetChemosensitivityModifier(
                sourceId,
                sensitivityMult,
                co2ThresholdOffsetMmHg,
                durationSec);
        }

        /// <summary>Removes a previously registered excitability/chemosensitivity modifier by sourceId.</summary>
        public static bool RemoveModifier(Body? body, string sourceId)
        {
            if (body == null || string.IsNullOrWhiteSpace(sourceId)) return false;
            var ctrl = RespirationModManager.TryGet(body);
            return ctrl != null && ctrl.Sim.Modifiers.RemoveExcitabilityModifier(sourceId);
        }

        /// <summary>Clears all external excitability and pulmonary failure overrides on the given Body.</summary>
        public static void ClearAllModifiers(Body? body)
        {
            if (body == null) return;
            var ctrl = RespirationModManager.TryGet(body);
            ctrl?.Sim.Modifiers.ClearAll();
        }

        // --- Pulmonary Failure & Blood Gas Intervention API ---

        /// <summary>
        /// Forces or clears an external pulmonary failure state (lungs cannot ventilate even if medulla is active).
        /// </summary>
        public static void SetPulmonaryFailureOverride(
            Body? body,
            string sourceId,
            bool failed,
            float durationSec = -1f)
        {
            if (body == null || string.IsNullOrWhiteSpace(sourceId)) return;
            RespirationModManager.GetOrCreate(body).Sim.Modifiers.SetPulmonaryFailureOverride(
                sourceId,
                failed,
                durationSec);
        }

        /// <summary>Directly adds or subtracts arterial PaCO2 (mmHg) — useful for ventilator/ECMO/toxin mods.</summary>
        public static void AddPaCO2Delta(Body? body, float deltaMmHg)
        {
            if (body == null) return;
            RespirationModManager.GetOrCreate(body).Sim.AddPaCO2Delta(deltaMmHg);
        }

        /// <summary>Directly adds or subtracts plasma bicarbonate HCO3- (mmol/L) — useful for buffer/metabolic mods.</summary>
        public static void AddBicarbonateDelta(Body? body, float deltaMmolL)
        {
            if (body == null) return;
            RespirationModManager.GetOrCreate(body).Sim.AddBicarbonateDelta(deltaMmolL);
        }

        // --- Read-Only Physiological Telemetry Queries (Zero-Dependency Reflection-Friendly) ---

        public static float GetPaCO2(Body? body) =>
            body != null ? RespirationModManager.GetOrCreate(body).Sim.PaCO2MmHg : BodyRespirationSimulator.RestingPaCO2;

        public static float GetPaO2(Body? body) =>
            body != null ? RespirationModManager.GetOrCreate(body).Sim.PaO2MmHg : BodyRespirationSimulator.RestingPaO2;

        public static float GetBloodPh(Body? body) =>
            body != null ? RespirationModManager.GetOrCreate(body).Sim.BloodPh : 7.40f;

        public static float GetBicarbonate(Body? body) =>
            body != null ? RespirationModManager.GetOrCreate(body).Sim.BicarbonateMmolL : BodyRespirationSimulator.RestingBicarbonate;

        public static float GetBaseExcess(Body? body) =>
            body != null ? RespirationModManager.GetOrCreate(body).Sim.BaseExcessMmolL : 0f;

        /// <summary>Normalized medullary excitability (1.0 = normal resting baseline, 0 = arrested, up to ~6.0 max drive).</summary>
        public static float GetMedullaryExcitability(Body? body) =>
            body != null ? RespirationModManager.GetOrCreate(body).Sim.MedullaryExcitability : 1.0f;

        /// <summary>Target respiratory rate commanded by the medulla (0..80 breaths/min).</summary>
        public static float GetTargetRespiratoryRateBpm(Body? body) =>
            body != null ? RespirationModManager.GetOrCreate(body).Sim.MedullaryTargetRpm : BodyRespirationSimulator.RestingRpm;

        /// <summary>Actual realized respiratory rate after pulmonary mechanics/failure (0..80 breaths/min).</summary>
        public static float GetActualRespiratoryRateBpm(Body? body) =>
            body != null ? RespirationModManager.GetOrCreate(body).Sim.CurrentRpm : BodyRespirationSimulator.RestingRpm;

        public static bool IsPulmonaryFailure(Body? body) =>
            body != null && RespirationModManager.GetOrCreate(body).Sim.FailureReason != PulmonaryFailureReason.None;

        public static string GetPulmonaryFailureReasonText(Body? body) =>
            body != null
                ? BodyRespirationSimulator.GetFailureReasonDisplayName(RespirationModManager.GetOrCreate(body).Sim.FailureReason)
                : "无";

        public static float GetAlveolarVentilationEfficiency(Body? body) =>
            body != null ? RespirationModManager.GetOrCreate(body).Sim.AlveolarVentilationEff : 1.0f;

        public static RespirationTelemetrySnapshot GetTelemetrySnapshot(Body? body) =>
            body != null ? RespirationModManager.GetOrCreate(body).Sim.LastTelemetry : default;
    }
}
