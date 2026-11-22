using System;

namespace RespirationSimCore
{
    /// <summary>
    /// Complete per-Body arterial blood gas (PaO2, PaCO2, pH, HCO3-, BE), medullary respiratory center,
    /// pulmonary mechanics/failure, and tri-mod physiological coupling simulator.
    /// Zero Unity dependencies; sub-stepped internally for numerical stability up to 25x time acceleration.
    /// </summary>
    public sealed class BodyRespirationSimulator
    {
        public const float VanillaRespScale = 4.0f; // body.respiratoryRate = ActualRpm * 4 (0..320 -> 0..80/m)
        public const float RestingPaCO2 = 40.0f;
        public const float RestingPaO2 = 97.0f;
        public const float RestingBicarbonate = 24.0f;
        public const float RestingRpm = 15.0f;

        private const int HistoryCapacity = 180; // 180 samples @ 1 Hz = 3 min rolling ABG trend
        private const float MaxSubStepSec = 0.05f;
        private const float O2KineticsScale = 0.28f; // Proportionately scales PaO2 fall & rise so resting apnea SpO2 100%->50% takes ~1.5 min

        // Core blood gas & acid-base state
        public float PaCO2MmHg = RestingPaCO2;
        public float PaO2MmHg = RestingPaO2;
        public float BicarbonateMmolL = RestingBicarbonate;
        public float BloodPh = 7.40f;
        public float BaseExcessMmolL = 0f;

        // Medullary & effector respiratory state
        public float MedullaryExcitability = 1.0f;
        public float MedullaryTargetRpm = RestingRpm;
        public float EffectiveTargetRpm = RestingRpm;
        public float CurrentRpm = RestingRpm;
        public float AlveolarVentilationEff = 1.0f;
        public float PerfusionEff = 1.0f;
        public float MetabolicCo2Production = 1.0f;

        // Pathology & transient timers
        public PulmonaryFailureReason FailureReason = PulmonaryFailureReason.None;
        public float SustainedPeSeconds;
        public float AcuteApneaTimerSec;
        public float AirHungerPanicIntensity;
        public float SimElapsedTimeSec;

        // Sub-modules
        public readonly ExcitabilityModifierSet Modifiers = new ExcitabilityModifierSet();
        public readonly RespirationPharmacology Drugs = new RespirationPharmacology();

        // Telemetry & rolling ABG trend buffer
        public RespirationStepOutput LastOutput;
        public RespirationTelemetrySnapshot LastTelemetry;

        private readonly AbgHistorySample[] _history = new AbgHistorySample[HistoryCapacity];
        private int _historyHead;
        private int _historyCount;
        private float _historySampleAccum;
        private float _pvcCooldownSec;
        private readonly Random _rng;

        // SeizurePhase integer constants matching NeuroSimCore.SeizurePhase
        private const int SeizureTonic = 3;
        private const int SeizureClonic = 4;
        private const int SeizureStatusComp = 6;
        private const int SeizureStatusDecomp = 7;

        public BodyRespirationSimulator(int seed = 20261121)
        {
            _rng = new Random(seed);
            RecalculateAcidBase();
            RefreshTelemetry(opiateSupp: 0f, bzdSupp: 0f, breathing: true, spo2: ComputeSpO2FromPaO2(PaO2MmHg, BloodPh, 37f));
        }

        public void Reset(bool clearDrugs = true, bool clearModifiers = false)
        {
            PaCO2MmHg = RestingPaCO2;
            PaO2MmHg = RestingPaO2;
            BicarbonateMmolL = RestingBicarbonate;
            RecalculateAcidBase();

            MedullaryExcitability = 1.0f;
            MedullaryTargetRpm = RestingRpm;
            EffectiveTargetRpm = RestingRpm;
            CurrentRpm = RestingRpm;
            AlveolarVentilationEff = 1.0f;
            PerfusionEff = 1.0f;
            MetabolicCo2Production = 1.0f;
            FailureReason = PulmonaryFailureReason.None;
            SustainedPeSeconds = 0f;
            AcuteApneaTimerSec = 0f;
            AirHungerPanicIntensity = 0f;
            _pvcCooldownSec = 0f;

            if (clearDrugs) Drugs.ClearAll();
            if (clearModifiers) Modifiers.ClearAll();

            _historyHead = 0;
            _historyCount = 0;
            _historySampleAccum = 0f;

            float spo2 = ComputeSpO2FromPaO2(PaO2MmHg, BloodPh, 37f);
            LastOutput = new RespirationStepOutput
            {
                ActualRespiratoryRateBpm = CurrentRpm,
                VanillaScaledRespRate = CurrentRpm * VanillaRespScale,
                Breathing = true,
                BloodOxygenSpO2 = spo2,
                ConsciousnessCap = 100f,
                CardiacCatecholSensitivityMult = 1.0f
            };
            RefreshTelemetry(0f, 0f, true, spo2);
        }

        public void TriggerAcuteApnea(float durationSec = 5.0f)
        {
            AcuteApneaTimerSec = Math.Max(AcuteApneaTimerSec, RespMath.Clamp(durationSec, 0.5f, 30f));
            CurrentRpm = 0f;
        }

        public void AddPaCO2Delta(float deltaMmHg)
        {
            PaCO2MmHg = RespMath.Clamp(PaCO2MmHg + deltaMmHg, 12f, 150f);
            RecalculateAcidBase();
        }

        public void AddBicarbonateDelta(float deltaMmolL)
        {
            BicarbonateMmolL = RespMath.Clamp(BicarbonateMmolL + deltaMmolL, 6f, 48f);
            RecalculateAcidBase();
        }

        public int CopyHistoryChronological(AbgHistorySample[] destination, int maxCount)
        {
            if (destination == null || destination.Length == 0 || _historyCount <= 0 || maxCount <= 0)
                return 0;

            int count = Math.Min(_historyCount, Math.Min(destination.Length, maxCount));
            int start = (_historyHead - count + HistoryCapacity) % HistoryCapacity;
            for (int i = 0; i < count; i++)
            {
                destination[i] = _history[(start + i) % HistoryCapacity];
            }
            return count;
        }

        public RespirationStepOutput Step(
            float totalDt,
            in RespirationEnvironmentInput env,
            in RespirationSimConfig config)
        {
            if (totalDt <= 0f)
            {
                return LastOutput;
            }

            // Clamp huge frame spikes (e.g., loading hitches) while fully supporting 25x timeScale (~0.5s/frame)
            float clampedTotal = Math.Min(totalDt, 2.0f);
            int subSteps = Math.Max(1, (int)Math.Ceiling(clampedTotal / MaxSubStepSec));
            float subDt = clampedTotal / subSteps;

            float accumBrainDelta = 0f;
            float accumIcpDelta = 0f;
            float accumTraumaDelta = 0f;
            float accumAdrenDelta = 0f;
            bool anyPvcRequest = false;
            RespirationStepOutput stepOut = default;

            for (int i = 0; i < subSteps; i++)
            {
                stepOut = StepSingle(subDt, in env, in config);
                accumBrainDelta += stepOut.BrainHealthDelta;
                accumIcpDelta += stepOut.NeuroIcpDelta;
                accumTraumaDelta += stepOut.TraumaDelta;
                accumAdrenDelta += stepOut.PanicAdrenalineDelta;
                if (stepOut.RequestCardiacPvc) anyPvcRequest = true;
            }

            stepOut.BrainHealthDelta = accumBrainDelta;
            stepOut.NeuroIcpDelta = accumIcpDelta;
            stepOut.TraumaDelta = accumTraumaDelta;
            stepOut.PanicAdrenalineDelta = accumAdrenDelta;
            stepOut.RequestCardiacPvc = anyPvcRequest;

            LastOutput = stepOut;
            return stepOut;
        }

        private RespirationStepOutput StepSingle(
            float dt,
            in RespirationEnvironmentInput env,
            in RespirationSimConfig config)
        {
            SimElapsedTimeSec += dt;
            if (_pvcCooldownSec > 0f) _pvcCooldownSec = Math.Max(0f, _pvcCooldownSec - dt);
            if (AcuteApneaTimerSec > 0f) AcuteApneaTimerSec = Math.Max(0f, AcuteApneaTimerSec - dt);

            Modifiers.Tick(dt);

            var output = new RespirationStepOutput
            {
                ConsciousnessCap = 100f,
                CardiacCatecholSensitivityMult = 1.0f
            };

            if (!env.Alive)
            {
                MedullaryExcitability = 0f;
                MedullaryTargetRpm = 0f;
                EffectiveTargetRpm = 0f;
                CurrentRpm = 0f;
                AlveolarVentilationEff = 0f;
                PerfusionEff = 0f;
                RecalculateAcidBase();
                float deadSpo2 = ComputeSpO2FromPaO2(PaO2MmHg, BloodPh, env.TemperatureC);
                output.ActualRespiratoryRateBpm = 0f;
                output.VanillaScaledRespRate = 0f;
                output.Breathing = false;
                output.BloodOxygenSpO2 = deadSpo2;
                RefreshTelemetry(0f, 0f, false, deadSpo2);
                RecordHistoryIfDue(dt);
                return output;
            }

            float maxRpm = RespMath.Clamp(config.MaxRespiratoryRateBpm, 25f, 120f);
            float rateMult = RespMath.Clamp(config.Co2DynamicsRateMultiplier, 0.25f, 3.0f);

            // 1. Step pharmacology (Nikethamide & Sodium Bicarbonate)
            Drugs.Step(dt, BloodPh, out float bicarbGainMmolL, out float bicarbCo2ReleaseMmHg);

            // 2. Evaluate Pulmonary Failure status (thorax destruction, severe hemothorax, fatal PE, external override)
            if (env.HasPulmonaryEmbolism)
            {
                // Severe PE progressively causes pulmonary infarction if unresolved
                float peAccumRate = env.ThoraxMuscleHealth < 45f ? 1.8f : 1.0f;
                SustainedPeSeconds = Math.Min(300f, SustainedPeSeconds + dt * peAccumRate);
            }
            else
            {
                SustainedPeSeconds = Math.Max(0f, SustainedPeSeconds - dt * 3.0f);
            }

            PulmonaryFailureReason failure = PulmonaryFailureReason.None;
            if (config.EnablePulmonaryFailure)
            {
                if (env.ThoraxMuscleHealth < 4.0f)
                    failure |= PulmonaryFailureReason.ThoraxDestroyed;

                // Hysteresis on hemothorax (triggers at threshold e.g. 85%, clears once drained below threshold - 5%)
                float hemoThresh = RespMath.Clamp(config.HemothoraxFailureThreshold, 50f, 99f);
                bool wasHemoFailed = (FailureReason & PulmonaryFailureReason.SevereHemothorax) != 0;
                if (env.Hemothorax >= hemoThresh || (wasHemoFailed && env.Hemothorax >= hemoThresh - 5f))
                    failure |= PulmonaryFailureReason.SevereHemothorax;

                // Fatal PE: PE combined with critical thorax damage (<22%) or prolonged untreated infarction (>90s)
                if (env.HasPulmonaryEmbolism && (env.ThoraxMuscleHealth < 22f || SustainedPeSeconds >= 90f))
                    failure |= PulmonaryFailureReason.FatalPulmonaryEmbolism;
            }
            if (Modifiers.HasExternalPulmonaryFailure())
                failure |= PulmonaryFailureReason.ExternalOverride;

            FailureReason = failure;
            bool isPulmonaryFailure = failure != PulmonaryFailureReason.None;

            // 3. Pulmonary capillary perfusion efficiency Q [0..1]
            float perfusion = 1.0f;
            if (env.CardiacModPresent)
            {
                float coNorm = RespMath.Clamp(env.CardiacLvOutputLpm / 5.78f, 0f, 1.3f);
                if (env.CardiacIsVfOrAsystole)
                {
                    // In VF/Asystole, zero native LV output; AutoPump CPR provides partial rescue pulmonary perfusion
                    perfusion = env.HasAutoPumpWorking && env.BloodPressure > 55f
                        ? RespMath.Clamp((env.BloodPressure - 35f) / 70f, 0.25f, 0.55f)
                        : 0.02f;
                }
                else
                {
                    perfusion = RespMath.Clamp01(coNorm);
                    if (env.HasAutoPumpWorking && env.BloodPressure > 60f)
                        perfusion = Math.Max(perfusion, 0.45f);
                }
            }
            else
            {
                // Vanilla fallback perfusion from BP & HR
                if (env.HeartRate < 20f)
                {
                    perfusion = env.HasAutoPumpWorking && env.BloodPressure > 55f ? 0.42f : 0.02f;
                }
                else
                {
                    perfusion = RespMath.Clamp01((env.BloodPressure - 15f) / 75f);
                }
            }
            // Severe blood loss reduces oxygen-carrying & CO2-transport capacity
            if (env.BloodVolumePercentage < 0.70f)
            {
                perfusion *= RespMath.Clamp(env.BloodVolumePercentage / 0.70f, 0.12f, 1.0f);
            }
            PerfusionEff = RespMath.Clamp01(perfusion);

            // 4. Mechanical & airway ventilation efficiency (peripheral effector, separate from medullary drive!)
            float mechEff = 1.0f;
            // Hemothorax compresses lung volume progressively before full pulmonary failure
            if (env.Hemothorax > 5f)
            {
                mechEff *= RespMath.Clamp(1.0f - (env.Hemothorax / 100f) * 0.72f, 0.15f, 1.0f);
            }
            // Damaged chest wall musculature limits tidal excursion
            if (env.ThoraxMuscleHealth < 70f)
            {
                mechEff *= RespMath.Clamp(0.35f + (env.ThoraxMuscleHealth / 70f) * 0.65f, 0.20f, 1.0f);
            }
            if (env.ThoraxBroken)
            {
                mechEff *= 0.84f;
            }
            // Pulmonary embolism creates huge alveolar dead space (V/Q mismatch)
            if (env.HasPulmonaryEmbolism)
            {
                mechEff *= 0.42f;
            }
            // Neuro-respiratory airway effects (when NeurologyOverhaulMod is installed):
            if (env.NeuroModPresent)
            {
                // Non-selective beta2 blockade (Propranolol) induces bronchoconstriction, worse under demand
                if (env.NeuroBeta2Blockade > 0.10f)
                {
                    float demandFactor = (env.Stamina < 65f || CurrentRpm > 22f) ? 1.0f : 0.45f;
                    mechEff *= RespMath.Clamp(1.0f - env.NeuroBeta2Blockade * 0.28f * demandFactor, 0.62f, 1.0f);
                }
                // Severe vagal hypertonia -> muscarinic bronchospasm & secretions (reversed by Atropine!)
                if (env.NeuroVagalTone > 70f && !env.NeuroDiveReflexActive)
                {
                    float vagalSpasm = RespMath.Clamp01((env.NeuroVagalTone - 70f) / 30f);
                    float atropineRescue = RespMath.Clamp01(env.NeuroAtropineConc * 0.90f);
                    mechEff *= RespMath.Clamp(1.0f - vagalSpasm * 0.34f * (1f - atropineRescue), 0.58f, 1.0f);
                }
                // Seizure mechanical respiratory muscle compromise:
                // Clonic & Compensated SE severely impair coordinated ventilation
                if (env.NeuroSeizurePhase == SeizureClonic)
                    mechEff *= 0.42f;
                else if (env.NeuroSeizurePhase == SeizureStatusComp)
                    mechEff *= 0.32f;
            }

            bool seizureTonicApnea = env.NeuroModPresent && env.NeuroSeizurePhase == SeizureTonic;
            bool airwayBlocked = env.HeadTourniquetOccluded || (env.InWater && !env.HasScubaGear);
            bool mechanicalLock = isPulmonaryFailure || env.HeadTourniquetOccluded || seizureTonicApnea || AcuteApneaTimerSec > 0f;

            AlveolarVentilationEff = (airwayBlocked || mechanicalLock)
                ? 0f
                : RespMath.Clamp01(mechEff * RespMath.Clamp(PerfusionEff / 0.75f, 0.02f, 1.0f));

            // 5. Metabolic CO2 production (VCO2, resting = 1.0)
            float metaMult = RespMath.Clamp(env.MetabolismSettingMult > 0.05f ? env.MetabolismSettingMult : 1.0f, 0.5f, 2.0f);
            float tempQ10 = (float)Math.Pow(1.07, RespMath.Clamp(env.TemperatureC - 37.0f, -12f, 7f));
            float staminaDeficit = RespMath.Clamp01((100f - env.Stamina) / 100f);
            float activityCo2 =
                staminaDeficit * 0.95f +
                (env.Exercising ? 0.55f : 0f) +
                RespMath.Clamp(env.MoveLoad, 0f, 2.0f) * 0.38f;
            float totalAdren = Math.Max(0f, env.CurAdrenaline) + Math.Min(200f, Math.Max(0f, env.CardiacExogenousAdrenaline));
            float stressCo2 = (totalAdren / 100f) * 0.28f + (env.SepticShock / 100f) * 0.42f;
            float sleepFactor = env.Sleeping ? 0.85f : 1.0f;

            float neuroCo2 = 0f;
            if (env.NeuroModPresent)
            {
                if (env.NeuroSympatheticTone > 55f)
                    neuroCo2 += RespMath.Clamp((env.NeuroSympatheticTone - 55f) / 95f, 0f, 1.2f) * 0.55f;
                if (env.NeuroSeizurePhase == SeizureTonic)
                    neuroCo2 += 1.45f;
                else if (env.NeuroSeizurePhase == SeizureClonic)
                    neuroCo2 += 1.85f;
                else if (env.NeuroSeizurePhase == SeizureStatusComp)
                    neuroCo2 += 2.35f;
                else if (env.NeuroSeizurePhase == SeizureStatusDecomp)
                    neuroCo2 += 1.15f;
            }

            float vco2 = RespMath.Clamp((1.0f + activityCo2 + stressCo2 + neuroCo2) * tempQ10 * sleepFactor * (0.75f + 0.25f * metaMult), 0.35f, 5.2f);
            MetabolicCo2Production = vco2;

            // 6. Medullary Respiratory Center Excitability Calculation
            Modifiers.ComputeAggregate(
                out float extAdd,
                out float extMult,
                out float extChemoMult,
                out float extCo2ThreshOffset);

            // 6a. Opiate suppression (central depression + right-shifted & flattened CO2 response curve)
            float opiate = Math.Max(0f, env.OpiateReception);
            float opiateThresholdShift = opiate * 0.28f;
            float opiateSlopeMult = opiate > 0.1f
                ? 1.0f / (1.0f + (float)Math.Pow(opiate / 32.0f, 1.70f))
                : 1.0f;
            float opiateBaseMult = RespMath.Clamp(1.0f - opiate / 78.0f, 0f, 1.0f);
            float opiateSuppFactor = RespMath.Clamp01(1.0f - opiateBaseMult * (0.55f + 0.45f * opiateSlopeMult));

            // 6b. Benzodiazepine / Scopolamine / SleepingPills / Alpha2Delta synergy suppression
            float bzdDrive = 0f;
            float alpha2DeltaSynergy = 0f;
            if (env.NeuroModPresent)
            {
                bzdDrive = env.NeuroMidazolamConc * 0.58f + env.NeuroClonazepamConc * 0.24f + env.NeuroScopolamineConc * 0.12f;
                if (opiate > 12f && env.NeuroAlpha2DeltaActivity > 0.2f)
                {
                    alpha2DeltaSynergy = Math.Min(0.45f, env.NeuroAlpha2DeltaActivity * 0.16f * RespMath.Clamp01((opiate - 12f) / 40f));
                }
            }
            float sleepPillDrive = 0f;
            if (env.SleepingPillsAmount > 10f)
            {
                // Matches vanilla SleepingPills.cs: overdose threshold drops sharply when combined with opiates (>15)
                float odThresh = opiate > 15f ? 150f : 900f;
                sleepPillDrive = RespMath.Clamp(env.SleepingPillsAmount / odThresh, 0f, 1.4f) * (opiate > 15f ? 0.55f : 0.28f);
            }
            float opiateBzdSynergy = opiate > 10f ? (1.0f + Math.Min(1.1f, (opiate - 10f) * 0.022f)) : 1.0f;
            float totalGabaDepress = (bzdDrive + sleepPillDrive + alpha2DeltaSynergy) * opiateBzdSynergy;
            float bzdSlopeMult = 1.0f / (1.0f + totalGabaDepress * 0.85f);
            float bzdBaseMult = RespMath.Clamp(1.0f - totalGabaDepress * 0.42f, 0.08f, 1.0f);
            float bzdThresholdShift = totalGabaDepress * 5.2f;
            float bzdSuppFactor = RespMath.Clamp01(1.0f - bzdBaseMult * (0.5f + 0.5f * bzdSlopeMult));

            // 6c. CO2 Central Chemoreceptor activation
            float co2Threshold = 35.5f + opiateThresholdShift + bzdThresholdShift + extCo2ThreshOffset;
            float totalChemoGain = RespMath.Clamp(
                opiateSlopeMult * bzdSlopeMult * Drugs.NikethamideChemoSensitivityMult * extChemoMult,
                0.03f,
                3.5f);

            // Tonic wakefulness drive keeps minimal rhythm when awake near threshold; drops during sleep/hypocapnia
            float wakeTonic = env.Conscious && !env.Sleeping ? 0.25f : 0.10f;
            if (PaCO2MmHg < 28f)
            {
                // Hypocapnia diminishes tonic drive (post-hyperventilation pause)
                wakeTonic *= RespMath.Clamp01((PaCO2MmHg - 18f) / 10f);
            }

            float co2Above = Math.Max(0f, PaCO2MmHg - co2Threshold);
            float co2Drive = wakeTonic + co2Above * 0.1667f * totalChemoGain;

            // Biphasic CO2 narcosis: extreme hypercapnia (>85 mmHg) progressively anesthetizes the medulla
            float co2NarcosisMult = PaCO2MmHg > 85f
                ? RespMath.Clamp(1.0f - (PaCO2MmHg - 85f) / 48.0f, 0.06f, 1.0f)
                : 1.0f;

            // 6d. Metabolic Acidosis (pH) Peripheral/Central Chemoreceptor Drive (Kussmaul breathing!)
            float phDrive = 0f;
            if (BloodPh < 7.36f)
            {
                // Strong carotid body H+ drive overrides hypocapnic braking to sustain Kussmaul compensation (Winters' formula)
                phDrive = (7.36f - BloodPh) * 18.5f * RespMath.Clamp(totalChemoGain, 0.15f, 2.2f);
            }
            else if (BloodPh > 7.45f)
            {
                // Alkalemia mildly dampens central drive
                phDrive = -(BloodPh - 7.45f) * 1.8f;
            }
            // Extreme lethal acidemia (<6.95) depresses medullary neurons
            float acidFailureMult = BloodPh < 6.95f
                ? RespMath.Clamp((BloodPh - 6.65f) / 0.30f, 0.05f, 1.0f)
                : 1.0f;

            // 6e. Hypoxic Peripheral Chemoreceptor Drive (carotid/aortic bodies)
            float hypoxicDrive = 0f;
            if (PaO2MmHg < 65f)
            {
                hypoxicDrive = RespMath.Clamp((65f - PaO2MmHg) / 34f, 0f, 1.35f) * 1.05f * Math.Max(0.25f, opiateSlopeMult);
            }
            // Severe brainstem hypoxia / ischemia depresses medulla (unperfused arrest drops to 0;
            // isolated hypoxia retains minimal hypoxic gasping floor so Naloxone/airway rescue can restart breathing!)
            float brainstemVitalMult = 1.0f;
            if (PaO2MmHg < 30f)
                brainstemVitalMult *= RespMath.Clamp((PaO2MmHg - 12f) / 18f, 0.22f, 1.0f);
            if (PerfusionEff < 0.28f)
                brainstemVitalMult *= RespMath.Clamp((PerfusionEff - 0.06f) / 0.22f, 0f, 1.0f);
            if (env.BrainHealth < 25f)
                brainstemVitalMult *= RespMath.Clamp(env.BrainHealth / 25f, 0.12f, 1.0f);
            if (env.StrokeAmount > 45f)
                brainstemVitalMult *= RespMath.Clamp(1.0f - (env.StrokeAmount - 45f) / 65f, 0.15f, 1.0f);
            if (env.TemperatureC < 29f && !env.Conscious)
                brainstemVitalMult *= 0.45f;

            // 6f. Non-chemoreceptor physiological & neural modulations
            float physioAdd =
                Math.Max(0f, env.AveragePain - 15f) * 0.0065f +
                (totalAdren / 100f) * 0.25f +
                Math.Max(0f, env.TemperatureC - 37.5f) * 0.12f;

            // Exercise feedforward central command (motor cortex -> medulla during exertion)
            physioAdd += RespMath.Clamp(env.MoveLoad, 0f, 2.0f) * 0.18f + staminaDeficit * 0.35f;

            // Third-party CUCoreLib CCLBody.RespiratoryRate (scaled from 0..100 vanilla units to excitability units)
            // Note: 60 vanilla units = 15 bpm = 1.0 excitability -> 1.0 excitability = 60 vanilla units
            physioAdd += env.ExternalCclRespRateOffset / 60.0f;

            // NeurologyOverhaulMod direct brainstem neural inputs (zero double-counting!)
            float neuroCentralMult = 1.0f;
            if (env.NeuroModPresent)
            {
                if (env.NeuroSympatheticTone > 25f)
                    physioAdd += RespMath.Clamp((env.NeuroSympatheticTone - 25f) / 100f, 0f, 1.2f) * 0.42f;
                if (env.NeuroAtropineConc > 0.6f)
                    physioAdd += Math.Min(0.45f, (env.NeuroAtropineConc - 0.6f) * 0.28f);
                if (env.NeuroBzdWithdrawalSeverity > 0.15f)
                    physioAdd += env.NeuroBzdWithdrawalSeverity * 0.65f;

                // Cushing reflex (brainstem compression from high ICP) strongly depresses medullary center
                if (env.NeuroCushingIntensity > 0.25f)
                    neuroCentralMult *= RespMath.Clamp(1.0f - (env.NeuroCushingIntensity - 0.25f) * 0.92f, 0.22f, 1.0f);

                // Mammalian dive reflex suppresses inspiratory drive underwater
                if (env.NeuroDiveReflexActive)
                    neuroCentralMult *= 0.20f;

                // Late Decompensated Status Epilepticus -> brainstem exhaustion
                if (env.NeuroSeizurePhase == SeizureStatusDecomp)
                    neuroCentralMult *= 0.18f;
            }

            // Combine all medullary terms (Nikethamide & external API modifiers applied cleanly here)
            float rawExcitability =
                (co2Drive + phDrive + hypoxicDrive + physioAdd) *
                opiateBaseMult *
                bzdBaseMult *
                co2NarcosisMult *
                acidFailureMult *
                brainstemVitalMult *
                neuroCentralMult;

            // Nikethamide & external mod additive/multiplicative overrides (can rescue opiate/BZD central depression!)
            float rescuedExcitability =
                (rawExcitability + Drugs.NikethamideExcitabilityAdd + extAdd) * extMult;

            // Still respect lethal biological floor when brain is completely anoxic/dead or extMult == 0
            if (extMult <= 0.0001f)
            {
                rescuedExcitability = 0f;
            }
            else if (brainstemVitalMult < 0.12f)
            {
                rescuedExcitability *= RespMath.Clamp(brainstemVitalMult / 0.12f, 0.15f, 1.0f);
            }

            MedullaryExcitability = RespMath.Clamp(rescuedExcitability, 0f, 6.0f);
            MedullaryTargetRpm = RespMath.Clamp(MedullaryExcitability * RestingRpm, 0f, maxRpm);

            // 7. Apply Pulmonary Failure / Mechanical Lock & Smooth Respiratory Rate Approach
            if (mechanicalLock)
            {
                EffectiveTargetRpm = 0f;
                // Rapid cessation when lungs fail or airway/muscles lock
                CurrentRpm = RespMath.MoveTowards(CurrentRpm, 0f, dt * 45.0f);
                if (isPulmonaryFailure && CurrentRpm < 2.5f)
                    CurrentRpm = 0f;
            }
            else
            {
                EffectiveTargetRpm = MedullaryTargetRpm;
                // Medulla smoothly adjusts breathing toward target (faster acceleration in hypercapnia/rescue, smooth settling)
                float approachRate = EffectiveTargetRpm > CurrentRpm
                    ? (CurrentRpm < 2.5f ? 3.5f : 7.5f)
                    : 5.5f;
                CurrentRpm = RespMath.MoveTowards(CurrentRpm, EffectiveTargetRpm, dt * approachRate);
            }

            CurrentRpm = RespMath.Clamp(CurrentRpm, 0f, maxRpm);
            bool breathing = env.Alive && !isPulmonaryFailure && !env.HeadTourniquetOccluded && CurrentRpm > 2.5f;

            // 8. Blood Gas Dynamics: PaCO2, HCO3-, pH, PaO2 & SpO2
            // Frequency -> alveolar ventilation factor (dead space causes mild tapering above 40/m)
            float freqFactor = CurrentRpm <= 40f
                ? (CurrentRpm / RestingRpm)
                : (40f / RestingRpm + (CurrentRpm - 40f) / 22f);

            float effectiveVaRatio = (breathing && !airwayBlocked)
                ? freqFactor * AlveolarVentilationEff
                : 0f;

            // 8a. PaCO2 ODE: dPaCO2/dt = k * (40 * VCO2 - VA * PaCO2) + bicarbonate CO2 release
            const float kCo2 = 0.032f;
            float dPaCO2 = kCo2 * rateMult * (RestingPaCO2 * vco2 - effectiveVaRatio * PaCO2MmHg) * dt;
            PaCO2MmHg = RespMath.Clamp(PaCO2MmHg + dPaCO2 + bicarbCo2ReleaseMmHg, 12f, 150f);

            // 8b. Metabolic HCO3- dynamics (lactic acidosis vs clearance & renal compensation + NaHCO3 infusion)
            float lactateAcidLoad = 0f;
            if (PerfusionEff < 0.65f)
                lactateAcidLoad += (0.65f - PerfusionEff) * 0.14f;
            if (PaO2MmHg < 52f)
                lactateAcidLoad += ((52f - PaO2MmHg) / 40f) * 0.12f;
            if (env.SepticShock > 20f)
                lactateAcidLoad += ((env.SepticShock - 20f) / 80f) * 0.085f;
            if (env.Hunger < 5f)
                lactateAcidLoad += RespMath.Clamp((5f - env.Hunger) / 55f, 0f, 1f) * 0.025f; // Starvation ketosis
            if (env.NeuroModPresent)
            {
                if (env.NeuroSeizurePhase == SeizureClonic)
                    lactateAcidLoad += 0.11f;
                else if (env.NeuroSeizurePhase == SeizureStatusComp)
                    lactateAcidLoad += 0.22f;
                else if (env.NeuroSeizurePhase == SeizureStatusDecomp)
                    lactateAcidLoad += 0.18f;
            }

            // Renal / hepatic baseline target HCO3- (slow partial compensation over minutes for sustained acid-base shifts)
            float targetRenalHco3 = RespMath.Clamp(RestingBicarbonate + (PaCO2MmHg - RestingPaCO2) * 0.12f, 16f, 34f);
            float clearanceRate = (PerfusionEff > 0.65f && PaO2MmHg > 60f) ? 0.0085f : 0.0020f;

            float dHco3 = (-lactateAcidLoad + (targetRenalHco3 - BicarbonateMmolL) * clearanceRate) * dt + bicarbGainMmolL;
            BicarbonateMmolL = RespMath.Clamp(BicarbonateMmolL + dHco3, 6f, 48f);

            // Recompute arterial pH & Base Excess via Henderson-Hasselbalch
            RecalculateAcidBase();

            // 8c. Arterial PaO2 & SpO2 (Hill + Bohr effect)
            if (effectiveVaRatio > 0.02f)
            {
                // Alveolar gas equation: PAO2 = PiO2 - PaCO2 / RQ (~149 - PaCO2/0.8)
                float alveolarO2 = RespMath.Clamp(149.0f - PaCO2MmHg / 0.80f, 14f, 138f);
                // Normal A-a gradient ~2 mmHg; increases with hemothorax, PE, hyperviscosity, poor perfusion
                float aaGradient = 2.0f;
                aaGradient += env.Hemothorax * 0.42f;
                if (env.HasPulmonaryEmbolism) aaGradient += 28.0f;
                float excessVisc = Math.Max(0f, Math.Abs(env.BloodViscosity) - 40f);
                aaGradient += excessVisc * 0.35f;
                if (PerfusionEff < 0.70f) aaGradient += (0.70f - PerfusionEff) * 35.0f;
                if (env.BloodVolumePercentage < 0.60f) aaGradient += (0.60f - env.BloodVolumePercentage) * 55.0f;

                float ventSufficiency = RespMath.Clamp01(effectiveVaRatio / Math.Max(0.4f, vco2 * 0.75f));
                float targetPaO2 = RespMath.Clamp((alveolarO2 - aaGradient) * (0.25f + 0.75f * ventSufficiency), 12f, 118f);

                float o2Rate = targetPaO2 > PaO2MmHg
                    ? (0.12f + 0.14f * Math.Min(2.0f, effectiveVaRatio))
                    : (0.08f + 0.05f * vco2);
                PaO2MmHg = RespMath.Lerp(PaO2MmHg, targetPaO2, Math.Min(1f, dt * o2Rate * O2KineticsScale * rateMult));
            }
            else
            {
                // Complete apnea / underwater / pulmonary failure: oxygen stores consumed proportionally to metabolic demand
                float o2DecayPerSec = (1.65f + 1.10f * vco2) * O2KineticsScale * rateMult;
                PaO2MmHg = RespMath.MoveTowards(PaO2MmHg, 12f, dt * o2DecayPerSec);
            }
            PaO2MmHg = RespMath.Clamp(PaO2MmHg, 10f, 125f);

            float spO2 = ComputeSpO2FromPaO2(PaO2MmHg, BloodPh, env.TemperatureC);

            // 8d. Air-Hunger Suffocation Panic & Trauma:
            // Triggers when conscious, medulla is excited (> 1.25), AND actual RR is far below medullary target RR
            // (e.g. pulmonary failure, airway occlusion, respiratory muscle lock)
            float rpmDeficit = MedullaryTargetRpm - CurrentRpm;
            bool airHungerTriggered =
                env.Conscious &&
                MedullaryExcitability > 1.25f &&
                MedullaryTargetRpm >= 18f &&
                rpmDeficit >= 10f &&
                CurrentRpm <= MedullaryTargetRpm * 0.55f;

            if (airHungerTriggered)
            {
                float deficitSev = RespMath.Clamp01((rpmDeficit - 8f) / 35f);
                float excitSev = RespMath.Clamp01((MedullaryExcitability - 1.25f) / 1.75f);
                float targetPanic = RespMath.Clamp01(0.35f + 0.65f * Math.Max(deficitSev, excitSev));
                AirHungerPanicIntensity = RespMath.MoveTowards(AirHungerPanicIntensity, targetPanic, dt * 0.32f);
            }
            else
            {
                AirHungerPanicIntensity = RespMath.MoveTowards(AirHungerPanicIntensity, 0f, dt * 0.45f);
            }

            if (AirHungerPanicIntensity > 0.08f && env.Conscious)
            {
                output.AirHungerPanicIntensity = AirHungerPanicIntensity;
                output.HorrifiedTargetLevel = RespMath.Clamp(25f + AirHungerPanicIntensity * 65f, 0f, 90f);
                output.TraumaDelta += AirHungerPanicIntensity * 0.85f * dt;
                output.PanicAdrenalineDelta += AirHungerPanicIntensity * 6.5f * dt;
            }

            // 9. Systemic & Tri-Mod Physiological Effects (pH, PaCO2, Nikethamide, PE)
            if (config.EnableAcidBaseSystemicEffects)
            {
                // 9a. CO2 Narcosis & severe acidosis consciousness suppression
                if (PaCO2MmHg > 62f)
                {
                    float narcosisCap = RespMath.Clamp(100f - (PaCO2MmHg - 62f) * 2.4f, 8f, 95f);
                    output.ConsciousnessCap = Math.Min(output.ConsciousnessCap, narcosisCap);
                }
                if (BloodPh < 7.22f)
                {
                    float acidCap = RespMath.Clamp(100f - (7.22f - BloodPh) * 220f, 8f, 92f);
                    output.ConsciousnessCap = Math.Min(output.ConsciousnessCap, acidCap);
                }
                if (PaCO2MmHg < 23f)
                {
                    // Severe hypocapnic cerebral vasoconstriction (lightheadedness / presyncope)
                    float hypoCap = RespMath.Clamp(100f - (23f - PaCO2MmHg) * 3.5f, 52f, 96f);
                    output.ConsciousnessCap = Math.Min(output.ConsciousnessCap, hypoCap);
                }

                // 9b. Extreme lethal acidemia direct brain injury
                if (BloodPh < 7.05f)
                {
                    output.BrainHealthDelta -= (7.05f - BloodPh) * 0.85f * dt;
                }

                // 9c. Neuromuscular tremor (respiratory alkalosis hypocalcemia OR Nikethamide CNS toxicity)
                float alkTremor = BloodPh > 7.50f ? RespMath.Clamp01((BloodPh - 7.50f) / 0.16f) * 0.65f : 0f;
                float nikTox = Drugs.NikethamideConvulsionToxicity;
                output.TremorShakeIntensity = Math.Max(alkTremor, Math.Min(1.0f, nikTox * 0.65f));

                // 9d. Feedback to RealisticCardiacEcgMod
                if (env.HasPulmonaryEmbolism)
                {
                    // Pulmonary Embolism -> acute pulmonary hypertension -> Right Ventricular Ischemia/Strain!
                    output.CardiacRvIschemiaAdd = Math.Max(output.CardiacRvIschemiaAdd, RespMath.Clamp(0.45f + SustainedPeSeconds / 160f, 0.45f, 0.92f));
                }
                if (isPulmonaryFailure)
                {
                    output.CardiacRvIschemiaAdd = Math.Max(output.CardiacRvIschemiaAdd, 0.35f);
                }

                if (BloodPh < 7.32f)
                {
                    float acidSev = RespMath.Clamp01((7.32f - BloodPh) / 0.32f);
                    // Acidosis depresses catecholamine responsiveness and increases myocardial ischemia/arrhythmia vulnerability
                    output.CardiacGlobalIschemiaAdd = acidSev * 0.48f;
                    output.CardiacCatecholSensitivityMult = RespMath.Clamp(1.0f - acidSev * 0.45f, 0.45f, 1.0f);
                    if (BloodPh < 7.12f)
                    {
                        output.CardiacSinusBpmDelta -= (7.12f - BloodPh) * 75f; // Severe acidotic bradycardia
                    }
                    if (acidSev > 0.45f && _pvcCooldownSec <= 0f && _rng.NextDouble() < acidSev * 0.18 * dt)
                    {
                        output.RequestCardiacPvc = true;
                        _pvcCooldownSec = 4.0f;
                    }
                }
                else if (BloodPh > 7.50f)
                {
                    // Alkalosis lowers ionized Ca2+/K+ -> increased ventricular ectopy (PVC)
                    float alkSev = RespMath.Clamp01((BloodPh - 7.50f) / 0.18f);
                    if (_pvcCooldownSec <= 0f && _rng.NextDouble() < alkSev * 0.14 * dt)
                    {
                        output.RequestCardiacPvc = true;
                        _pvcCooldownSec = 5.5f;
                    }
                }

                // Moderate hypercapnia mildly stimulates cardiac sympathetic rate (before severe acidotic depression)
                if (PaCO2MmHg > 48f && BloodPh >= 7.15f)
                {
                    output.CardiacSinusBpmDelta += Math.Min(22f, (PaCO2MmHg - 48f) * 0.65f);
                }

                // 9e. Feedback to NeurologyOverhaulMod
                // Hypercapnia -> cerebral vasodilation -> ICP rise (Cushing reflex); Hypocapnia -> mild ICP relief
                if (PaCO2MmHg > 50f)
                {
                    output.NeuroIcpDelta = ((PaCO2MmHg - 50f) / 60f) * 0.022f * dt;
                    output.NeuroSympatheticDriveAdd = Math.Min(36f, (PaCO2MmHg - 50f) * 0.85f);
                }
                else if (PaCO2MmHg < 34f)
                {
                    output.NeuroIcpDelta = -((34f - PaCO2MmHg) / 25f) * 0.012f * dt;
                }

                // Alkalosis / hypocapnia lowers seizure threshold and precipitates Absence seizures!
                if (BloodPh > 7.48f || PaCO2MmHg < 29f)
                {
                    float alkFactor = Math.Max(
                        BloodPh > 7.48f ? (BloodPh - 7.48f) / 0.15f : 0f,
                        PaCO2MmHg < 29f ? (29f - PaCO2MmHg) / 14f : 0f);
                    alkFactor = RespMath.Clamp(alkFactor, 0f, 1.5f);
                    output.NeuroSeizureThresholdDelta -= alkFactor * 16f;
                    output.NeuroCorticalExcitabilityAdd += alkFactor * 10f;
                    output.NeuroAbsenceTriggerBoost = alkFactor * 0.08f * dt;
                }

                // Nikethamide overdose CNS pro-convulsant toxicity
                if (nikTox > 0.05f)
                {
                    output.NeuroSeizureThresholdDelta -= nikTox * 28f;
                    output.NeuroCorticalExcitabilityAdd += nikTox * 26f;
                }

                // CO2 narcosis / severe acidemia -> encephalopathic slow delta EEG
                if (PaCO2MmHg > 68f || BloodPh < 7.20f)
                {
                    output.NeuroForceSlowEeg = true;
                }
            }

            output.ActualRespiratoryRateBpm = CurrentRpm;
            output.VanillaScaledRespRate = CurrentRpm * VanillaRespScale;
            output.Breathing = breathing;
            output.BloodOxygenSpO2 = spO2;

            RefreshTelemetry(opiateSuppFactor, bzdSuppFactor, breathing, spO2);
            RecordHistoryIfDue(dt);
            return output;
        }

        private void RecalculateAcidBase()
        {
            // Henderson-Hasselbalch: pH = 6.1 + log10(HCO3- / (0.03 * PaCO2))
            double denom = Math.Max(0.10, 0.03 * PaCO2MmHg);
            double ratio = Math.Max(0.05, BicarbonateMmolL / denom);
            BloodPh = RespMath.Clamp((float)(6.1 + Math.Log10(ratio)), 6.60f, 7.80f);
            // Siggaard-Andersen approximation for Base Excess (mmol/L)
            BaseExcessMmolL = RespMath.Clamp((BicarbonateMmolL - 24.0f) + 14.8f * (BloodPh - 7.40f), -30f, 30f);
        }

        /// <summary>
        /// Hill oxygen-hemoglobin dissociation curve with Bohr effect (pH & temperature shift of P50).
        /// Calibrated so normal PaO2 ~97 mmHg at pH 7.40, 37C yields ~98.5% SpO2.
        /// </summary>
        public static float ComputeSpO2FromPaO2(float paO2MmHg, float bloodPh, float tempC)
        {
            if (paO2MmHg <= 0.1f) return 0f;

            // Bohr shift: acidosis & hyperthermia shift P50 right (higher P50 -> lower SpO2 for same PaO2)
            float phShift = 0.48f * (7.40f - RespMath.Clamp(bloodPh, 6.7f, 7.75f));
            float tempShift = 0.024f * (RespMath.Clamp(tempC, 24f, 43f) - 37.0f);
            float p50 = RespMath.Clamp(24.0f * (float)Math.Exp(phShift + tempShift), 16.0f, 40.0f);

            const double n = 3.0;
            double po2n = Math.Pow(paO2MmHg, n);
            double p50n = Math.Pow(p50, n);
            double sat = 100.0 * po2n / (po2n + p50n);
            return RespMath.Clamp((float)sat, 0f, 100f);
        }

        public AcidBaseStatus ClassifyAcidBase()
        {
            bool acidemia = BloodPh < 7.35f;
            bool alkalemia = BloodPh > 7.45f;
            bool highCo2 = PaCO2MmHg > 45f;
            bool lowCo2 = PaCO2MmHg < 35f;
            bool lowHco3 = BicarbonateMmolL < 22f;
            bool highHco3 = BicarbonateMmolL > 26f;

            if (acidemia)
            {
                if (highCo2 && lowHco3) return AcidBaseStatus.MixedAcidosis;
                if (highCo2) return AcidBaseStatus.RespiratoryAcidosis;
                if (lowHco3) return AcidBaseStatus.MetabolicAcidosis;
                return AcidBaseStatus.RespiratoryAcidosis;
            }
            if (alkalemia)
            {
                if (lowCo2 && highHco3) return AcidBaseStatus.MixedAlkalosis;
                if (lowCo2) return AcidBaseStatus.RespiratoryAlkalosis;
                if (highHco3) return AcidBaseStatus.MetabolicAlkalosis;
                return AcidBaseStatus.RespiratoryAlkalosis;
            }
            if (highCo2 || lowCo2 || lowHco3 || highHco3)
            {
                return AcidBaseStatus.CompensatedAcidBase;
            }
            return AcidBaseStatus.Normal;
        }

        public static string GetAcidBaseDisplayName(AcidBaseStatus status) => status switch
        {
            AcidBaseStatus.Normal => "酸碱平衡正常",
            AcidBaseStatus.RespiratoryAcidosis => "呼吸性酸中毒",
            AcidBaseStatus.RespiratoryAlkalosis => "呼吸性碱中毒",
            AcidBaseStatus.MetabolicAcidosis => "代谢性酸中毒",
            AcidBaseStatus.MetabolicAlkalosis => "代谢性碱中毒",
            AcidBaseStatus.MixedAcidosis => "混合性酸中毒",
            AcidBaseStatus.MixedAlkalosis => "混合性碱中毒",
            AcidBaseStatus.CompensatedAcidBase => "代偿期酸碱偏移",
            _ => "酸碱平衡正常"
        };

        public static string GetFailureReasonDisplayName(PulmonaryFailureReason reason)
        {
            if (reason == PulmonaryFailureReason.None) return "无";
            string text = "";
            if ((reason & PulmonaryFailureReason.ThoraxDestroyed) != 0) text += "胸廓/肺实质毁损 ";
            if ((reason & PulmonaryFailureReason.SevereHemothorax) != 0) text += "重度血胸压陷 ";
            if ((reason & PulmonaryFailureReason.FatalPulmonaryEmbolism) != 0) text += "致命肺栓塞梗死 ";
            if ((reason & PulmonaryFailureReason.ExternalOverride) != 0) text += "外源性肺衰竭 ";
            return text.Trim();
        }

        private void RefreshTelemetry(float opiateSupp, float bzdSupp, bool breathing, float spo2)
        {
            LastTelemetry = new RespirationTelemetrySnapshot
            {
                PaO2MmHg = PaO2MmHg,
                PaCO2MmHg = PaCO2MmHg,
                BloodPh = BloodPh,
                BicarbonateMmolL = BicarbonateMmolL,
                BaseExcessMmolL = BaseExcessMmolL,
                SpO2Percent = spo2,

                MedullaryExcitability = MedullaryExcitability,
                MedullaryTargetRpm = MedullaryTargetRpm,
                EffectiveTargetRpm = EffectiveTargetRpm,
                ActualRpm = CurrentRpm,
                Breathing = breathing,

                AlveolarVentilationEff = AlveolarVentilationEff,
                PerfusionEff = PerfusionEff,
                MetabolicCo2Production = MetabolicCo2Production,
                OpiateSuppressionFactor = opiateSupp,
                BzdSuppressionFactor = bzdSupp,
                NikethamideConc = Drugs.NikethamideConc,
                BicarbonateInfusionPending = Drugs.BicarbonateDepotMmolL,
                AirHungerPanicIntensity = AirHungerPanicIntensity,

                FailureReason = FailureReason,
                AcidBase = ClassifyAcidBase()
            };
        }

        private void RecordHistoryIfDue(float dt)
        {
            _historySampleAccum += dt;
            if (_historySampleAccum < 1.0f && _historyCount > 0) return;
            _historySampleAccum = 0f;

            _history[_historyHead] = new AbgHistorySample(
                SimElapsedTimeSec,
                PaO2MmHg,
                PaCO2MmHg,
                BloodPh,
                BicarbonateMmolL,
                CurrentRpm,
                MedullaryExcitability);

            _historyHead = (_historyHead + 1) % HistoryCapacity;
            if (_historyCount < HistoryCapacity) _historyCount++;
        }
    }
}
