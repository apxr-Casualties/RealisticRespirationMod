using System;
using System.Reflection;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using HarmonyLib;
using RespirationSimCore;
using UnityEngine;

namespace RealisticRespirationMod.Compat
{
    public struct NeuroRespBridgeTelemetry
    {
        public bool Available;
        public float SympatheticTone;
        public float VagalTone;
        public float CushingIntensity;
        public bool DiveReflexActive;
        public float MidazolamConc;
        public float ClonazepamConc;
        public float ScopolamineConc;
        public float Alpha2DeltaActivity;
        public float AtropineConc;
        public float Beta2Blockade;
        public float BzdWithdrawalSeverity;
        public int SeizurePhase;
    }

    /// <summary>
    /// Zero-compile-dependency bidirectional runtime bridge to NeurologyOverhaulMod ("com.casualties.neurology.overhaul").
    /// - Neuro -> Respiration: reads BZD/Scopolamine/Alpha2Delta/Propranolol/Atropine PK, Sympathetic/Vagal/Cushing/Dive
    ///   autonomic tones, and Epilepsy seizure phase (Tonic apnea, Clonic/SE hypermetabolism & muscle lock).
    /// - Respiration/Acid-Base -> Neuro: feeds hypercapnic ICP/sympathetic rise, alkalotic/hypocapnic seizure threshold drop
    ///   & absence seizure trigger risk, Nikethamide overdose convulsion drive, and CO2 narcosis slow EEG encephalopathy.
    /// </summary>
    public static class NeuroCompatBridge
    {
        public static bool IsLoaded { get; private set; }

        private static MethodInfo? _tryGetMethod;
        private static MethodInfo? _applyRespFeedbackMethod; // Scheme A clean hook if present in NeurologyOverhaulMod

        private static FieldInfo? _simField;
        private static FieldInfo? _drugsField;
        private static FieldInfo? _autonomicField;
        private static FieldInfo? _epilepsyField;

        // NeuroPharmacology fields/properties
        private static FieldInfo? _midazolamConcField;
        private static FieldInfo? _clonazepamConcField;
        private static FieldInfo? _scopolamineConcField;
        private static FieldInfo? _atropineConcField;
        private static FieldInfo? _bzdWithdrawalField;
        private static PropertyInfo? _alpha2DeltaProp;
        private static PropertyInfo? _beta2BlockadeProp;

        // AutonomicSystem fields
        private static FieldInfo? _sympToneField;
        private static FieldInfo? _vagalToneField;
        private static FieldInfo? _cushingIntensityField;
        private static FieldInfo? _diveReflexField;
        private static FieldInfo? _estimatedIcpField;

        // EpilepsyEngine fields/methods
        private static FieldInfo? _seizurePhaseField;
        private static FieldInfo? _seizureThresholdField;
        private static FieldInfo? _corticalExcitField;
        private static MethodInfo? _forceTriggerMethod;

        private static readonly System.Random FallbackRng = new System.Random();

        public static void TryInitialize(ManualLogSource logger)
        {
            IsLoaded = false;
            try
            {
                if (!Chainloader.PluginInfos.ContainsKey(RespirationPlugin.NeuroModGuid))
                    return;

                Type? managerType = AccessTools.TypeByName("NeuroOverhaulMod.NeuroModManager");
                Type? ctrlType = AccessTools.TypeByName("NeuroOverhaulMod.BodyNeuroController");
                Type? simType = AccessTools.TypeByName("NeuroSimCore.NeuroSimulator");
                Type? drugsType = AccessTools.TypeByName("NeuroSimCore.NeuroPharmacology");
                Type? ansType = AccessTools.TypeByName("NeuroSimCore.AutonomicSystem");
                Type? epiType = AccessTools.TypeByName("NeuroSimCore.EpilepsyEngine");

                if (managerType == null || ctrlType == null || simType == null || drugsType == null || ansType == null || epiType == null)
                {
                    logger.LogWarning("NeurologyOverhaulMod detected, but expected types were not resolved; soft bridge disabled.");
                    return;
                }

                _tryGetMethod = AccessTools.Method(managerType, "TryGet", new[] { typeof(Body) });
                _simField = AccessTools.Field(ctrlType, "Sim");
                _drugsField = AccessTools.Field(simType, "Drugs");
                _autonomicField = AccessTools.Field(simType, "Autonomic");
                _epilepsyField = AccessTools.Field(simType, "Epilepsy");

                _midazolamConcField = AccessTools.Field(drugsType, "MidazolamConc");
                _clonazepamConcField = AccessTools.Field(drugsType, "ClonazepamConc");
                _scopolamineConcField = AccessTools.Field(drugsType, "ScopolamineConc");
                _atropineConcField = AccessTools.Field(drugsType, "AtropineConc");
                _bzdWithdrawalField = AccessTools.Field(drugsType, "BzdWithdrawalSeverity");
                _alpha2DeltaProp = AccessTools.Property(drugsType, "Alpha2DeltaActivity");
                _beta2BlockadeProp = AccessTools.Property(drugsType, "CentralAndPeripheralBeta2Blockade");

                _sympToneField = AccessTools.Field(ansType, "SympatheticTone");
                _vagalToneField = AccessTools.Field(ansType, "VagalTone");
                _cushingIntensityField = AccessTools.Field(ansType, "CushingIntensity");
                _diveReflexField = AccessTools.Field(ansType, "DiveReflexActive");
                _estimatedIcpField = AccessTools.Field(ansType, "EstimatedIcpIndex");

                _seizurePhaseField = AccessTools.Field(epiType, "Phase");
                _seizureThresholdField = AccessTools.Field(epiType, "SeizureThreshold");
                _corticalExcitField = AccessTools.Field(epiType, "CorticalExcitability");
                _forceTriggerMethod = AccessTools.Method(epiType, "ForceTrigger");

                // Optional Scheme A direct feedback hook on NeurologyOverhaulMod.RespirationCompatBridge
                Type? neuroRespBridgeType = AccessTools.TypeByName("NeuroOverhaulMod.RespirationCompatBridge");
                if (neuroRespBridgeType != null)
                {
                    _applyRespFeedbackMethod = AccessTools.Method(
                        neuroRespBridgeType,
                        "ApplyRespirationFeedback",
                        new[] { typeof(Body), typeof(float), typeof(float), typeof(float), typeof(float), typeof(float), typeof(bool) });
                }

                if (_tryGetMethod == null || _simField == null || _drugsField == null || _autonomicField == null || _epilepsyField == null)
                {
                    logger.LogWarning("NeurologyOverhaulMod reflection bindings incomplete; soft bridge disabled.");
                    return;
                }

                IsLoaded = true;
                logger.LogInfo($"Soft compatibility bridge to NeurologyOverhaulMod initialized (Scheme A direct hook: {_applyRespFeedbackMethod != null}).");
            }
            catch (Exception ex)
            {
                IsLoaded = false;
                logger.LogWarning($"Failed to initialize NeurologyOverhaulMod soft bridge: {ex.Message}");
            }
        }

        public static NeuroRespBridgeTelemetry ReadTelemetry(Body body)
        {
            var res = new NeuroRespBridgeTelemetry { Available = false };
            if (!IsLoaded || body == null || _tryGetMethod == null) return res;

            try
            {
                object? ctrl = _tryGetMethod.Invoke(null, new object[] { body });
                if (ctrl == null) return res;
                object? sim = _simField!.GetValue(ctrl);
                if (sim == null) return res;

                res.Available = true;

                object? drugs = _drugsField!.GetValue(sim);
                if (drugs != null)
                {
                    if (_midazolamConcField != null) res.MidazolamConc = Convert.ToSingle(_midazolamConcField.GetValue(drugs));
                    if (_clonazepamConcField != null) res.ClonazepamConc = Convert.ToSingle(_clonazepamConcField.GetValue(drugs));
                    if (_scopolamineConcField != null) res.ScopolamineConc = Convert.ToSingle(_scopolamineConcField.GetValue(drugs));
                    if (_atropineConcField != null) res.AtropineConc = Convert.ToSingle(_atropineConcField.GetValue(drugs));
                    if (_bzdWithdrawalField != null) res.BzdWithdrawalSeverity = Convert.ToSingle(_bzdWithdrawalField.GetValue(drugs));
                    if (_alpha2DeltaProp != null) res.Alpha2DeltaActivity = Convert.ToSingle(_alpha2DeltaProp.GetValue(drugs));
                    if (_beta2BlockadeProp != null) res.Beta2Blockade = Convert.ToSingle(_beta2BlockadeProp.GetValue(drugs));
                }

                object? ans = _autonomicField!.GetValue(sim);
                if (ans != null)
                {
                    if (_sympToneField != null) res.SympatheticTone = Convert.ToSingle(_sympToneField.GetValue(ans));
                    if (_vagalToneField != null) res.VagalTone = Convert.ToSingle(_vagalToneField.GetValue(ans));
                    if (_cushingIntensityField != null) res.CushingIntensity = Convert.ToSingle(_cushingIntensityField.GetValue(ans));
                    if (_diveReflexField != null) res.DiveReflexActive = Convert.ToBoolean(_diveReflexField.GetValue(ans));
                }

                object? epi = _epilepsyField!.GetValue(sim);
                if (epi != null && _seizurePhaseField != null)
                {
                    res.SeizurePhase = Convert.ToInt32(_seizurePhaseField.GetValue(epi));
                }
            }
            catch
            {
                // Fail-safe
            }

            return res;
        }

        public static void PushFeedbackToNeuro(Body body, in RespirationStepOutput respOut, float dt)
        {
            if (!IsLoaded || body == null || !body.alive || dt <= 0f) return;

            bool hasAny =
                Mathf.Abs(respOut.NeuroIcpDelta) > 0.00001f ||
                respOut.NeuroSympatheticDriveAdd > 0.1f ||
                respOut.NeuroSeizureThresholdDelta < -0.1f ||
                respOut.NeuroCorticalExcitabilityAdd > 0.1f ||
                respOut.NeuroAbsenceTriggerBoost > 0.0001f ||
                respOut.NeuroForceSlowEeg;
            if (!hasAny) return;

            try
            {
                // Preferred path (Scheme A): dedicated clean hook in NeurologyOverhaulMod
                if (_applyRespFeedbackMethod != null)
                {
                    _applyRespFeedbackMethod.Invoke(null, new object[]
                    {
                        body,
                        respOut.NeuroIcpDelta,
                        respOut.NeuroSympatheticDriveAdd,
                        respOut.NeuroSeizureThresholdDelta,
                        respOut.NeuroCorticalExcitabilityAdd,
                        respOut.NeuroAbsenceTriggerBoost,
                        respOut.NeuroForceSlowEeg
                    });
                    return;
                }

                // Fallback direct field update if an older NeurologyOverhaulMod without Scheme A hook is present
                object? ctrl = _tryGetMethod!.Invoke(null, new object[] { body });
                if (ctrl == null) return;
                object? sim = _simField!.GetValue(ctrl);
                if (sim == null) return;

                object? ans = _autonomicField!.GetValue(sim);
                if (ans != null)
                {
                    if (_estimatedIcpField != null && Mathf.Abs(respOut.NeuroIcpDelta) > 0.00001f)
                    {
                        float icp = Convert.ToSingle(_estimatedIcpField.GetValue(ans));
                        _estimatedIcpField.SetValue(ans, Mathf.Clamp01(icp + respOut.NeuroIcpDelta));
                    }
                    if (_sympToneField != null && respOut.NeuroSympatheticDriveAdd > 0.5f)
                    {
                        float symp = Convert.ToSingle(_sympToneField.GetValue(ans));
                        float target = Mathf.Clamp(symp + respOut.NeuroSympatheticDriveAdd * dt * 0.25f, 4f, 150f);
                        _sympToneField.SetValue(ans, target);
                    }
                }

                object? epi = _epilepsyField!.GetValue(sim);
                if (epi != null)
                {
                    if (_seizureThresholdField != null && respOut.NeuroSeizureThresholdDelta < -0.1f)
                    {
                        float th = Convert.ToSingle(_seizureThresholdField.GetValue(epi));
                        _seizureThresholdField.SetValue(epi, Mathf.Clamp(th + respOut.NeuroSeizureThresholdDelta * Mathf.Min(1f, dt * 0.35f), 12f, 115f));
                    }
                    if (_corticalExcitField != null && respOut.NeuroCorticalExcitabilityAdd > 0.1f)
                    {
                        float ce = Convert.ToSingle(_corticalExcitField.GetValue(epi));
                        _corticalExcitField.SetValue(epi, Mathf.Clamp(ce + respOut.NeuroCorticalExcitabilityAdd * dt * 0.35f, 0f, 100f));
                    }
                    if (respOut.NeuroAbsenceTriggerBoost > 0.0001f && _seizurePhaseField != null && _forceTriggerMethod != null)
                    {
                        int phase = Convert.ToInt32(_seizurePhaseField.GetValue(epi));
                        if (phase == 0 && FallbackRng.NextDouble() < respOut.NeuroAbsenceTriggerBoost)
                        {
                            // Trigger Absence Seizure (SeizureKind.Absence = 1)
                            Type? kindType = AccessTools.TypeByName("NeuroSimCore.SeizureKind");
                            if (kindType != null)
                            {
                                object absenceKind = Enum.ToObject(kindType, 1);
                                _forceTriggerMethod.Invoke(epi, new[] { absenceKind, FallbackRng });
                            }
                        }
                    }
                }
            }
            catch { }
        }
    }
}
