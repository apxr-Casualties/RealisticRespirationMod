using System;
using System.Reflection;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace RealisticRespirationMod.Compat
{
    public struct CardiacRespBridgeTelemetry
    {
        public bool Available;
        public bool IsVfOrAsystole;
        public float LvCardiacOutputLpm;
        public float ExogenousAdrenaline;
    }

    /// <summary>
    /// Zero-compile-dependency runtime bridge to RealisticCardiacEcgMod ("com.casualties.realistic.ecg").
    /// - Cardiac -> Respiration: reads LV cardiac output, VF/Asystole arrest state, and uncapped exogenous adrenaline.
    /// - Respiration/Acid-Base -> Cardiac: injects PE/pulmonary-failure RightVentricularIschemia, severe acidosis
    ///   GlobalIschemia & catecholamine blunting, hypercapnic/acidotic sinus BPM modulation, and acid-base PVC triggers.
    /// </summary>
    public static class CardiacCompatBridge
    {
        public static bool IsLoaded { get; private set; }

        private static MethodInfo? _getOrCreateMethod;
        private static MethodInfo? _triggerPvcMethod;
        private static MethodInfo? _updateEnvironmentMethod;

        private static FieldInfo? _workerField;
        private static FieldInfo? _lastSnapshotField;
        private static FieldInfo? _exogenousAdrenField;

        private static FieldInfo? _workerTimeLockField;
        private static FieldInfo? _workerPendingEnvField;

        private static FieldInfo? _envTargetSinusBpmField;
        private static FieldInfo? _envAdrenalineField;
        private static FieldInfo? _envGlobalIschemiaField;
        private static FieldInfo? _envRvIschemiaField;

        private static FieldInfo? _snapLvCoField;
        private static FieldInfo? _snapRhythmField;

        // CardiacSimCore.RhythmType enum values
        private const int RhythmVf = 4;
        private const int RhythmAsystole = 5;

        public static void TryInitialize(Harmony harmony, ManualLogSource logger)
        {
            IsLoaded = false;
            try
            {
                if (!Chainloader.PluginInfos.ContainsKey(RespirationPlugin.CardiacModGuid))
                    return;

                Type? managerType = AccessTools.TypeByName("RealisticEcgMod.CardiacModManager");
                Type? ctrlType = AccessTools.TypeByName("RealisticEcgMod.BodyCardiacController");
                Type? workerType = AccessTools.TypeByName("CardiacSimCore.CardiacWorkerRunner");
                Type? envType = AccessTools.TypeByName("CardiacSimCore.CardiacEnvironmentInput");
                Type? snapType = AccessTools.TypeByName("CardiacSimCore.CardiacTelemetrySnapshot");

                if (managerType == null || ctrlType == null || workerType == null || envType == null || snapType == null)
                {
                    logger.LogWarning("RealisticCardiacEcgMod detected, but expected types were not resolved; soft bridge disabled.");
                    return;
                }

                _getOrCreateMethod = AccessTools.Method(managerType, "GetOrCreate", new[] { typeof(Body) });
                _triggerPvcMethod = AccessTools.Method(managerType, "TriggerPrematureBeat", new[] { typeof(Body) });
                _updateEnvironmentMethod = AccessTools.Method(workerType, "UpdateEnvironment", new[] { envType });

                _workerField = AccessTools.Field(ctrlType, "Worker");
                _lastSnapshotField = AccessTools.Field(ctrlType, "LastSnapshot");
                _exogenousAdrenField = AccessTools.Field(ctrlType, "ExogenousAdrenaline");

                _workerTimeLockField = AccessTools.Field(workerType, "_timeLock");
                _workerPendingEnvField = AccessTools.Field(workerType, "_pendingEnv");

                _envTargetSinusBpmField = AccessTools.Field(envType, "TargetSinusBpm");
                _envAdrenalineField = AccessTools.Field(envType, "Adrenaline");
                _envGlobalIschemiaField = AccessTools.Field(envType, "GlobalIschemia");
                _envRvIschemiaField = AccessTools.Field(envType, "RightVentricularIschemia");

                _snapLvCoField = AccessTools.Field(snapType, "LeftVentricularCardiacOutputLpm");
                _snapRhythmField = AccessTools.Field(snapType, "Rhythm");

                if (_getOrCreateMethod == null || _workerField == null || _updateEnvironmentMethod == null ||
                    _workerPendingEnvField == null || _envTargetSinusBpmField == null || _snapRhythmField == null)
                {
                    logger.LogWarning("RealisticCardiacEcgMod reflection bindings incomplete; soft bridge disabled.");
                    return;
                }

                // Patch Body.HandleCirculation Prefix at Priority.VeryLow (200):
                // Executes strictly AFTER RealisticCardiacEcgMod Prefix (Normal=400) AND NeurologyOverhaulMod Prefix (Low=300)!
                MethodInfo? handleCirc = AccessTools.Method(typeof(Body), nameof(Body.HandleCirculation));
                if (handleCirc != null)
                {
                    var prefix = new HarmonyMethod(typeof(CardiacCompatBridge), nameof(Body_HandleCirculation_AfterCardiacAndNeuroPrefix))
                    {
                        priority = Priority.VeryLow
                    };
                    harmony.Patch(handleCirc, prefix: prefix);
                }

                IsLoaded = true;
                logger.LogInfo("Soft compatibility bridge to RealisticCardiacEcgMod initialized successfully.");
            }
            catch (Exception ex)
            {
                IsLoaded = false;
                logger.LogWarning($"Failed to initialize RealisticCardiacEcgMod soft bridge: {ex.Message}");
            }
        }

        public static CardiacRespBridgeTelemetry ReadTelemetry(Body body)
        {
            var result = new CardiacRespBridgeTelemetry { Available = false, LvCardiacOutputLpm = 5.78f };
            if (!IsLoaded || body == null || _getOrCreateMethod == null) return result;

            try
            {
                object? ctrl = _getOrCreateMethod.Invoke(null, new object[] { body });
                if (ctrl == null) return result;

                result.Available = true;
                if (_exogenousAdrenField != null)
                    result.ExogenousAdrenaline = Convert.ToSingle(_exogenousAdrenField.GetValue(ctrl));

                object? snap = _lastSnapshotField?.GetValue(ctrl);
                if (snap != null)
                {
                    if (_snapLvCoField != null)
                        result.LvCardiacOutputLpm = Convert.ToSingle(_snapLvCoField.GetValue(snap));
                    if (_snapRhythmField != null)
                    {
                        int rhythmVal = Convert.ToInt32(_snapRhythmField.GetValue(snap));
                        result.IsVfOrAsystole = rhythmVal == RhythmVf || rhythmVal == RhythmAsystole;
                    }
                }
            }
            catch
            {
                // Fail-safe: never break game loop
            }

            return result;
        }

        public static void TriggerPrematureBeat(Body body)
        {
            if (!IsLoaded || body == null || _triggerPvcMethod == null) return;
            try
            {
                _triggerPvcMethod.Invoke(null, new object[] { body });
            }
            catch { }
        }

        private static void Body_HandleCirculation_AfterCardiacAndNeuroPrefix(Body __instance)
        {
            if (!IsLoaded || __instance == null || !__instance.alive) return;

            var respCtrl = RespirationModManager.TryGet(__instance);
            if (respCtrl == null) return;
            ref readonly var respOut = ref respCtrl.Sim.LastOutput;

            bool hasChange =
                respOut.CardiacRvIschemiaAdd > 0.005f ||
                respOut.CardiacGlobalIschemiaAdd > 0.005f ||
                Mathf.Abs(respOut.CardiacSinusBpmDelta) > 0.25f ||
                respOut.CardiacCatecholSensitivityMult < 0.98f;
            if (!hasChange) return;

            try
            {
                object? cardiacCtrl = _getOrCreateMethod!.Invoke(null, new object[] { __instance });
                if (cardiacCtrl == null) return;
                object? worker = _workerField!.GetValue(cardiacCtrl);
                if (worker == null) return;

                object? timeLock = _workerTimeLockField?.GetValue(worker);
                object? boxedEnv;
                if (timeLock != null)
                {
                    lock (timeLock)
                    {
                        boxedEnv = _workerPendingEnvField!.GetValue(worker);
                    }
                }
                else
                {
                    boxedEnv = _workerPendingEnvField!.GetValue(worker);
                }

                if (boxedEnv == null) return;

                // 1. Pulmonary Embolism / Pulmonary Failure -> Right Ventricular Ischemia/Strain
                if (_envRvIschemiaField != null && respOut.CardiacRvIschemiaAdd > 0.005f)
                {
                    float rv = Convert.ToSingle(_envRvIschemiaField.GetValue(boxedEnv));
                    _envRvIschemiaField.SetValue(boxedEnv, Mathf.Clamp01(Mathf.Max(rv, respOut.CardiacRvIschemiaAdd)));
                }

                // 2. Severe Acidosis -> Global Myocardial Depression & Ischemia Instability
                if (_envGlobalIschemiaField != null && respOut.CardiacGlobalIschemiaAdd > 0.005f)
                {
                    float gi = Convert.ToSingle(_envGlobalIschemiaField.GetValue(boxedEnv));
                    _envGlobalIschemiaField.SetValue(boxedEnv, Mathf.Clamp01(gi + respOut.CardiacGlobalIschemiaAdd));
                }

                // 3. Severe Acidosis -> Blunted Myocardial Catecholamine Sensitivity
                if (_envAdrenalineField != null && respOut.CardiacCatecholSensitivityMult < 0.98f)
                {
                    float adren = Convert.ToSingle(_envAdrenalineField.GetValue(boxedEnv));
                    _envAdrenalineField.SetValue(boxedEnv, adren * Mathf.Clamp(respOut.CardiacCatecholSensitivityMult, 0.40f, 1.0f));
                }

                // 4. Hypercapnic sympathetic acceleration / severe acidotic sinus depression
                if (Mathf.Abs(respOut.CardiacSinusBpmDelta) > 0.25f)
                {
                    float bpm = Convert.ToSingle(_envTargetSinusBpmField!.GetValue(boxedEnv));
                    // Preserve extreme vagal SA arrest (<5 BPM) injected by NeuroMod!
                    if (bpm > 10f)
                    {
                        bpm = Mathf.Clamp(bpm + respOut.CardiacSinusBpmDelta, 22f, 225f);
                        _envTargetSinusBpmField.SetValue(boxedEnv, bpm);
                    }
                }

                _updateEnvironmentMethod!.Invoke(worker, new[] { boxedEnv });
            }
            catch { }
        }
    }
}
