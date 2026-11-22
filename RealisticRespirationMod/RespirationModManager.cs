using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RealisticRespirationMod.API;
using RealisticRespirationMod.Compat;
using RealisticRespirationMod.UI;
using RespirationSimCore;
using UnityEngine;

namespace RealisticRespirationMod
{
    public sealed class BodyRespirationController
    {
        public readonly BodyRespirationSimulator Sim;
        public bool HasSteppedOnce;
        public float LastWrittenRespRate = BodyRespirationSimulator.RestingRpm * BodyRespirationSimulator.VanillaRespScale;
        public float LastWrittenBloodOxygen = 98.5f;
        public float PreCirculationRespRate;
        public float PreCirculationBloodOxygen;
        public bool CompactAbgCardOpen;

        public BodyRespirationController(Body body)
        {
            Sim = new BodyRespirationSimulator(body.GetInstanceID() ^ 0x52455350);
        }
    }

    public static class RespirationModManager
    {
        public const string NikethamideLiquidId = "nikethamide";
        public const string NikethamideAmpouleItemId = "nikethamide_ampoule";
        public const string SodiumBicarbonateLiquidId = "sodium_bicarbonate";
        public const string SodiumBicarbonateBottleItemId = "sodium_bicarbonate_bottle";
        public const string PortableAbgItemId = "abgmonitor";

        private static readonly ConditionalWeakTable<Body, BodyRespirationController> Controllers =
            new ConditionalWeakTable<Body, BodyRespirationController>();

        private static readonly List<WeakReference<BodyRespirationController>> ActiveRefs =
            new List<WeakReference<BodyRespirationController>>();

        private static readonly object RegistryLock = new object();

        // Cached reflection to read third-party CUCoreLib CCLBody.RespiratoryRate without double-counting NeuroOverhaulMod
        private static readonly MethodInfo? GetBodyFormulaDataMethod =
            AccessTools.Method("CUCoreLib.Helpers.StatusExtensions:GetBodyFormulaData", new[] { typeof(Body) });

        private static readonly FieldInfo? FormulaRespRateField =
            AccessTools.Field(AccessTools.TypeByName("CUCoreLib.Data.BodyFormulaData"), "RespiratoryRate");

        public static BodyRespirationController GetOrCreate(Body body)
        {
            if (body == null) throw new ArgumentNullException(nameof(body));
            if (Controllers.TryGetValue(body, out var existing))
                return existing;

            lock (RegistryLock)
            {
                if (Controllers.TryGetValue(body, out existing))
                    return existing;

                var created = new BodyRespirationController(body);
                Controllers.Add(body, created);
                ActiveRefs.Add(new WeakReference<BodyRespirationController>(created));
                return created;
            }
        }

        public static BodyRespirationController? TryGet(Body? body)
        {
            if (body == null) return null;
            return Controllers.TryGetValue(body, out var existing) ? existing : null;
        }

        public static void DisposeAll()
        {
            lock (RegistryLock)
            {
                ActiveRefs.Clear();
            }
        }

        public static void ResetBody(Body? body, bool clearDrugs = true, bool clearModifiers = false)
        {
            if (body == null) return;
            var ctrl = GetOrCreate(body);
            ctrl.Sim.Reset(clearDrugs, clearModifiers);
            ctrl.HasSteppedOnce = true;

            float scaled = BodyRespirationSimulator.RestingRpm * BodyRespirationSimulator.VanillaRespScale;
            body.respiratoryRate = scaled;
            body.bloodOxygen = ctrl.Sim.LastOutput.BloodOxygenSpO2;
            body.breathing = body.alive;
            ctrl.LastWrittenRespRate = scaled;
            ctrl.LastWrittenBloodOxygen = body.bloodOxygen;
        }

        public static void AdministerDrug(Body? body, RespirationDrugId drug, float ml, RespirationAdminRoute route, Limb? limb = null)
        {
            if (body == null || ml <= 0f) return;
            var ctrl = GetOrCreate(body);
            ctrl.Sim.Drugs.Administer(drug, ml, route);

            // Oral intake of injectable-grade solutions causes mild gastric irritation
            if (route == RespirationAdminRoute.Oral && ml >= 4f)
            {
                body.sicknessAmount = Mathf.Clamp(body.sicknessAmount + ml * 0.25f, 0f, 100f);
            }
        }

        public static void CapturePreCirculation(Body body)
        {
            if (body == null) return;
            var ctrl = GetOrCreate(body);
            ctrl.PreCirculationRespRate = body.respiratoryRate;
            ctrl.PreCirculationBloodOxygen = body.bloodOxygen;

            if (!ctrl.HasSteppedOnce) return;

            // Detect external acute apnea triggers (CoilScript, CrystalElectric, SoundCannon, alien chunks)
            if (ctrl.LastWrittenRespRate > 12f && body.respiratoryRate <= 0.5f && !ctrl.Sim.LastTelemetry.IsPulmonaryFailure)
            {
                bool headTq = HasHeadTourniquet(body);
                if (!headTq)
                {
                    ctrl.Sim.TriggerAcuteApnea(4.5f);
                }
            }

            // Detect direct external oxygenation boosts (e.g. vanilla 'oxyline' liquid adding +1.666f bloodOxygen per tick)
            float extOxyGain = body.bloodOxygen - ctrl.LastWrittenBloodOxygen;
            if (extOxyGain > 0.35f && extOxyGain < 40f)
            {
                ctrl.Sim.PaO2MmHg = Mathf.Clamp(ctrl.Sim.PaO2MmHg + extOxyGain * 2.2f, 10f, 120f);
            }
        }

        public static void StepAfterCirculation(Body body, Painkillers? painkillers, float dt)
        {
            if (body == null || dt <= 0f || WorldGeneration.world == null || WorldGeneration.world.generatingWorld)
                return;

            var ctrl = GetOrCreate(body);

            // Allow external subscribers to adjust medullary modifiers right before computation
            RespirationApi.RaiseBeforeMedullaryCompute(body, ctrl.Sim);

            var cardiac = CardiacCompatBridge.ReadTelemetry(body);
            var neuro = NeuroCompatBridge.ReadTelemetry(body);

            float moveLoad = (body.standing ? Mathf.Abs(body.moveDir.x) : 0f)
                             + body.overEncumberance * 0.8f
                             + (body.attackCooldown > 0f ? 0.5f : 0f);

            float thoraxMuscle = (body.limbs != null && body.limbs.Length > 1 && body.limbs[1] != null)
                ? body.limbs[1].muscleHealth
                : 100f;
            bool thoraxBroken = body.limbs != null && body.limbs.Length > 1 && body.limbs[1] != null && body.limbs[1].broken;

            float sleepingPillsAmt = 0f;
            if (body.TryGetComponent<SleepingPills>(out var sp) && sp != null)
            {
                sleepingPillsAmt = sp.amount;
            }

            float metaSetting = 1.0f;
            try
            {
                metaSetting = WorldGeneration.GetRunSettingFloat("metabolismrate");
                if (metaSetting <= 0.05f) metaSetting = 1.0f;
            }
            catch { }

            var env = new RespirationEnvironmentInput
            {
                Alive = body.alive,
                Conscious = body.conscious,
                Sleeping = body.sleeping,
                Standing = body.standing,
                Exercising = body.exercising,
                InWater = body.inWater,
                HasScubaGear = body.hasScubaGear,
                HeadTourniquetOccluded = HasHeadTourniquet(body),
                HasAutoPumpWorking = HasWorkingAutoPump(body),

                BrainHealth = body.brainHealth,
                Consciousness = body.consciousness,
                BloodVolumePercentage = body.bloodVolumePercentage,
                BloodPressure = body.bloodPressure,
                HeartRate = body.heartRate,
                BloodViscosity = body.bloodViscosity,
                TemperatureC = body.temperature,
                Stamina = body.stamina,
                Hunger = body.hunger,
                AveragePain = body.averagePain,
                CurAdrenaline = body.curAdrenaline,
                SepticShock = body.septicShock,
                StrokeAmount = body.strokeAmount,
                Hemothorax = body.hemothorax,
                ThoraxMuscleHealth = thoraxMuscle,
                ThoraxBroken = thoraxBroken,
                HasPulmonaryEmbolism = body.hasPulmonaryEmbolism,
                MoveLoad = moveLoad,
                MetabolismSettingMult = metaSetting,

                OpiateReception = painkillers != null ? painkillers.actualOpiateReception : 0f,
                SleepingPillsAmount = sleepingPillsAmt,
                ExternalCclRespRateOffset = ReadThirdPartyCclRespiratoryOffset(body),

                CardiacModPresent = cardiac.Available,
                CardiacIsVfOrAsystole = cardiac.IsVfOrAsystole,
                CardiacLvOutputLpm = cardiac.LvCardiacOutputLpm,
                CardiacExogenousAdrenaline = cardiac.ExogenousAdrenaline,

                NeuroModPresent = neuro.Available,
                NeuroSympatheticTone = neuro.SympatheticTone,
                NeuroVagalTone = neuro.VagalTone,
                NeuroCushingIntensity = neuro.CushingIntensity,
                NeuroDiveReflexActive = neuro.DiveReflexActive,
                NeuroMidazolamConc = neuro.MidazolamConc,
                NeuroClonazepamConc = neuro.ClonazepamConc,
                NeuroScopolamineConc = neuro.ScopolamineConc,
                NeuroAlpha2DeltaActivity = neuro.Alpha2DeltaActivity,
                NeuroAtropineConc = neuro.AtropineConc,
                NeuroBeta2Blockade = neuro.Beta2Blockade,
                NeuroBzdWithdrawalSeverity = neuro.BzdWithdrawalSeverity,
                NeuroSeizurePhase = neuro.SeizurePhase
            };

            var cfg = RespirationConfig.ToSimConfig();
            RespirationStepOutput output = ctrl.Sim.Step(dt, in env, in cfg);

            // Apply authoritative respiratory rate (0..320 => 0..80/m) & blood oxygenation
            body.respiratoryRate = Mathf.Clamp(output.VanillaScaledRespRate, 0f, RespirationConfig.MaxRespiratoryRateBpm * BodyRespirationSimulator.VanillaRespScale);
            body.bloodOxygen = Mathf.Clamp(output.BloodOxygenSpO2, 0f, 100f);

            // Update private-setter readout via Harmony AccessTools helper or direct property
            SetRespiratoryReadout(body, Mathf.RoundToInt(output.ActualRespiratoryRateBpm) + "/m");

            ctrl.HasSteppedOnce = true;
            ctrl.LastWrittenRespRate = body.respiratoryRate;
            ctrl.LastWrittenBloodOxygen = body.bloodOxygen;

            if (output.RequestCardiacPvc && CardiacCompatBridge.IsLoaded)
            {
                CardiacCompatBridge.TriggerPrematureBeat(body);
            }

            RespirationApi.RaiseAfterRespirationStep(body, in ctrl.Sim.LastTelemetry);
        }

        public static void ApplyPostHandleBodyEffects(Body body, float dt)
        {
            if (body == null || dt <= 0f) return;
            var ctrl = TryGet(body);
            if (ctrl == null || !ctrl.HasSteppedOnce) return;

            ref readonly var output = ref ctrl.Sim.LastOutput;
            ref readonly var snap = ref ctrl.Sim.LastTelemetry;

            // Enforce inability to breathe during pulmonary failure or head airway occlusion
            if (!body.alive || snap.IsPulmonaryFailure || HasHeadTourniquet(body))
            {
                body.breathing = false;
                if (snap.IsPulmonaryFailure)
                {
                    body.respiratoryRate = 0f;
                    SetRespiratoryReadout(body, "0/m");
                }
            }
            else
            {
                body.breathing = output.Breathing;
            }

            if (!body.alive) return;

            // Consciousness ceiling from CO2 narcosis / severe acidemia (takes min -> never overrides NeuroMod caps!)
            if (output.ConsciousnessCap < 99.5f && body.consciousness > output.ConsciousnessCap)
            {
                body.consciousness = Mathf.MoveTowards(body.consciousness, output.ConsciousnessCap, dt * 32f);
            }

            if (Mathf.Abs(output.BrainHealthDelta) > 0.00001f)
            {
                body.brainHealth = Mathf.Clamp(body.brainHealth + output.BrainHealthDelta, 0f, 100f);
            }

            if (output.TremorShakeIntensity > 0.02f)
            {
                body.miscShakeIntensity = Mathf.Max(body.miscShakeIntensity, output.TremorShakeIntensity);
            }

            // Push PaCO2 / pH / Nikethamide feedback to NeurologyOverhaulMod if installed
            NeuroCompatBridge.PushFeedbackToNeuro(body, in output, dt);
        }

        public static void OpenOrToggleAbgMinigame(Body? body, Item? item = null)
        {
            if (body == null || MinigameBase.main == null || !body.alive || !body.conscious)
                return;

            // Hotkey/panel button requires biochip; Portable ABG Monitor item bypasses chip restriction
            if (item == null && WorldGeneration.unchipped)
                return;

            if (MinigameBase.main.currentMinigame is AbgAnalyzerMinigame)
            {
                MinigameBase.main.EndMinigame();
                return;
            }

            if (MinigameBase.main.currentMinigame)
                return;

            MinigameBase.main.StartMinigame(new AbgAnalyzerMinigame(body, item != null), item);
        }

        private static readonly PropertyInfo? RespReadoutProp =
            AccessTools.Property(typeof(Body), nameof(Body.respiratoryRateReadout));

        private static void SetRespiratoryReadout(Body body, string text)
        {
            RespReadoutProp?.SetValue(body, text);
        }

        private static bool HasHeadTourniquet(Body body)
        {
            return body.limbs != null &&
                   body.limbs.Length > 0 &&
                   body.limbs[0] != null &&
                   body.limbs[0].GetComponent<TourniquetScript>() != null;
        }

        private static bool HasWorkingAutoPump(Body body)
        {
            try
            {
                var wearables = body.GetAllWearables();
                if (wearables == null) return false;
                for (int i = 0; i < wearables.Count; i++)
                {
                    Item w = wearables[i];
                    if (w != null && w.id == "autopump" && w.battery != null && w.battery.hasCharge)
                        return true;
                }
            }
            catch { }
            return false;
        }

        private static float ReadThirdPartyCclRespiratoryOffset(Body body)
        {
            if (GetBodyFormulaDataMethod == null || FormulaRespRateField == null) return 0f;
            try
            {
                object? formulaData = GetBodyFormulaDataMethod.Invoke(null, new object[] { body });
                if (formulaData == null) return 0f;
                if (FormulaRespRateField.GetValue(formulaData) is not Dictionary<string, float> dict || dict.Count == 0)
                    return 0f;

                float sum = 0f;
                bool filterNeuro = NeuroCompatBridge.IsLoaded;
                foreach (var kv in dict)
                {
                    if (filterNeuro && string.Equals(kv.Key, "NeuroOverhaulMod", StringComparison.OrdinalIgnoreCase))
                        continue;
                    sum += kv.Value;
                }
                return sum;
            }
            catch
            {
                return 0f;
            }
        }
    }
}
