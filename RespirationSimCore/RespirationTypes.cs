using System;

namespace RespirationSimCore
{
    [Flags]
    public enum PulmonaryFailureReason : byte
    {
        None = 0,
        ThoraxDestroyed = 1 << 0,
        SevereHemothorax = 1 << 1,
        FatalPulmonaryEmbolism = 1 << 2,
        ExternalOverride = 1 << 3
    }

    public enum AcidBaseStatus : byte
    {
        Normal = 0,
        RespiratoryAcidosis = 1,
        RespiratoryAlkalosis = 2,
        MetabolicAcidosis = 3,
        MetabolicAlkalosis = 4,
        MixedAcidosis = 5,
        MixedAlkalosis = 6,
        CompensatedAcidBase = 7
    }

    public enum RespirationDrugId : byte
    {
        Nikethamide = 0,
        SodiumBicarbonate = 1
    }

    public enum RespirationAdminRoute : byte
    {
        Oral = 0,
        Injectable = 1
    }

    public struct ExcitabilityModifier
    {
        public string SourceId;
        public float AdditiveExcitability;
        public float ExcitabilityMultiplier;
        public float ChemoSensitivityMultiplier;
        public float Co2ThresholdOffsetMmHg;
        /// <summary>Negative = persistent until explicitly removed; > 0 = seconds remaining.</summary>
        public float RemainingDurationSec;
    }

    public struct PulmonaryFailureOverride
    {
        public string SourceId;
        public bool Active;
        public float RemainingDurationSec;
    }

    public readonly struct AbgHistorySample
    {
        public readonly float TimeSec;
        public readonly float PaO2;
        public readonly float PaCO2;
        public readonly float BloodPh;
        public readonly float Bicarbonate;
        public readonly float RespiratoryRateBpm;
        public readonly float MedullaryExcitability;

        public AbgHistorySample(
            float timeSec,
            float paO2,
            float paCO2,
            float bloodPh,
            float bicarbonate,
            float respiratoryRateBpm,
            float medullaryExcitability)
        {
            TimeSec = timeSec;
            PaO2 = paO2;
            PaCO2 = paCO2;
            BloodPh = bloodPh;
            Bicarbonate = bicarbonate;
            RespiratoryRateBpm = respiratoryRateBpm;
            MedullaryExcitability = medullaryExcitability;
        }
    }

    public struct RespirationSimConfig
    {
        public float MaxRespiratoryRateBpm;
        public float Co2DynamicsRateMultiplier;
        public float HemothoraxFailureThreshold;
        public bool EnablePulmonaryFailure;
        public bool EnableAcidBaseSystemicEffects;

        public static RespirationSimConfig Default() => new RespirationSimConfig
        {
            MaxRespiratoryRateBpm = 80f,
            Co2DynamicsRateMultiplier = 1.0f,
            HemothoraxFailureThreshold = 85f,
            EnablePulmonaryFailure = true,
            EnableAcidBaseSystemicEffects = true
        };
    }

    public struct RespirationEnvironmentInput
    {
        public bool Alive;
        public bool Conscious;
        public bool Sleeping;
        public bool Standing;
        public bool Exercising;
        public bool InWater;
        public bool HasScubaGear;
        public bool HeadTourniquetOccluded;
        public bool HasAutoPumpWorking;

        public float BrainHealth;          // 0..100
        public float Consciousness;        // 0..100
        public float BloodVolumePercentage;// ~0.5..1.5 (1.0 normal)
        public float BloodPressure;        // 0..250 (120 normal)
        public float HeartRate;            // 0..250
        public float BloodViscosity;       // -100..100
        public float TemperatureC;         // 20..44 (37 normal)
        public float Stamina;              // 0..100
        public float Hunger;               // -50..125
        public float AveragePain;          // 0..100
        public float CurAdrenaline;        // 0..100
        public float SepticShock;          // 0..100
        public float StrokeAmount;         // 0..100
        public float Hemothorax;           // 0..100
        public float ThoraxMuscleHealth;   // limbs[1].muscleHealth (0..100)
        public bool ThoraxBroken;          // limbs[1].broken
        public bool HasPulmonaryEmbolism;
        public float MoveLoad;             // 0..2+ (movement / encumbrance / attack)
        public float MetabolismSettingMult;// WorldGeneration metabolismrate (~1.0)

        // Vanilla drug inputs
        public float OpiateReception;      // Painkillers.actualOpiateReception
        public float SleepingPillsAmount;  // SleepingPills.amount (0 if absent)

        // Third-party CUCoreLib CCLBody.RespiratoryRate (excluding NeuroOverhaulMod when bridge is active)
        public float ExternalCclRespRateOffset;

        // Soft-compatible RealisticCardiacEcgMod inputs
        public bool CardiacModPresent;
        public bool CardiacIsVfOrAsystole;
        public float CardiacLvOutputLpm;         // ~5.78 normal
        public float CardiacExogenousAdrenaline; // uncapped

        // Soft-compatible NeurologyOverhaulMod inputs
        public bool NeuroModPresent;
        public float NeuroSympatheticTone;       // 0..150 (~20 normal)
        public float NeuroVagalTone;             // 0..100 (~35 normal)
        public float NeuroCushingIntensity;      // 0..1
        public bool NeuroDiveReflexActive;
        public float NeuroMidazolamConc;
        public float NeuroClonazepamConc;
        public float NeuroScopolamineConc;
        public float NeuroAlpha2DeltaActivity;   // Pregabalin + Gabapentin
        public float NeuroAtropineConc;
        public float NeuroBeta2Blockade;         // Propranolol > Metoprolol [0..1]
        public float NeuroBzdWithdrawalSeverity; // 0..1
        public int NeuroSeizurePhase;            // Matches NeuroSimCore.SeizurePhase
    }

    public struct RespirationStepOutput
    {
        // Authoritative body fields to apply
        public float ActualRespiratoryRateBpm;   // 0..80
        public float VanillaScaledRespRate;      // 0..320 (ActualRespiratoryRateBpm * 4)
        public bool Breathing;
        public float BloodOxygenSpO2;            // 0..100

        // Systemic effects onto vanilla Body
        public float ConsciousnessCap;           // 100 = no cap
        public float BrainHealthDelta;           // Direct acidemia brain injury per frame (already scaled by dt)
        public float TremorShakeIntensity;       // Alkalosis / Nikethamide toxicity tremor

        // Feedback to RealisticCardiacEcgMod (applied in HandleCirculation Prefix next frame)
        public float CardiacRvIschemiaAdd;       // 0..1 (PE / Pulmonary Failure right heart strain)
        public float CardiacGlobalIschemiaAdd;   // 0..1 (Severe acidosis myocardial depression/instability)
        public float CardiacSinusBpmDelta;       // Mild hypercapnic sympathetic / severe acidotic depression
        public float CardiacCatecholSensitivityMult; // <1 in severe acidosis
        public bool RequestCardiacPvc;           // Alkalosis / severe acidosis ectopy

        // Feedback to NeurologyOverhaulMod (applied via soft bridge)
        public float NeuroIcpDelta;              // Hypercapnia cerebral vasodilation raises ICP; hypocapnia lowers slightly
        public float NeuroSympatheticDriveAdd;   // Hypercapnic chemoreflex sympathetic surge
        public float NeuroSeizureThresholdDelta; // Negative = pro-convulsant (alkalosis / nikethamide overdose)
        public float NeuroCorticalExcitabilityAdd;
        public bool NeuroForceSlowEeg;           // CO2 narcosis / severe acidotic encephalopathy
        public float NeuroAbsenceTriggerBoost;   // Hyperventilation-induced absence seizure risk
    }

    public struct RespirationTelemetrySnapshot
    {
        public float PaO2MmHg;
        public float PaCO2MmHg;
        public float BloodPh;
        public float BicarbonateMmolL;
        public float BaseExcessMmolL;
        public float SpO2Percent;

        public float MedullaryExcitability;      // 1.0 = normal resting baseline, 0 = arrested, up to ~5.5+
        public float MedullaryTargetRpm;         // What medulla wants (0..80)
        public float EffectiveTargetRpm;         // After pulmonary failure / mechanical lock (0..80)
        public float ActualRpm;                  // Current smoothed RR (0..80)
        public bool Breathing;

        public float AlveolarVentilationEff;     // 0..1
        public float PerfusionEff;               // 0..1
        public float MetabolicCo2Production;     // ~1.0 resting
        public float OpiateSuppressionFactor;    // 0..1 (0 = none, 1 = complete suppression)
        public float BzdSuppressionFactor;       // 0..1
        public float NikethamideConc;
        public float BicarbonateInfusionPending;

        public PulmonaryFailureReason FailureReason;
        public bool IsPulmonaryFailure => FailureReason != PulmonaryFailureReason.None;
        public AcidBaseStatus AcidBase;
    }

    internal static class RespMath
    {
        public static float Clamp(float v, float min, float max) => v < min ? min : (v > max ? max : v);
        public static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
        public static float Lerp(float a, float b, float t) => a + (b - a) * Clamp01(t);
        public static float MoveTowards(float cur, float target, float maxDelta)
        {
            if (Math.Abs(target - cur) <= maxDelta) return target;
            return cur + Math.Sign(target - cur) * maxDelta;
        }
    }
}
