using System;
using System.Collections.Generic;

namespace RespirationSimCore
{
    /// <summary>
    /// Per-Body keyed registry of external medullary excitability and pulmonary failure modifiers.
    /// Keyed by sourceId so multiple mods can safely stack/update/expire their contributions without clobbering each other.
    /// </summary>
    public sealed class ExcitabilityModifierSet
    {
        private readonly Dictionary<string, ExcitabilityModifier> _excitabilityMods =
            new Dictionary<string, ExcitabilityModifier>(StringComparer.Ordinal);

        private readonly Dictionary<string, PulmonaryFailureOverride> _failureOverrides =
            new Dictionary<string, PulmonaryFailureOverride>(StringComparer.Ordinal);

        private readonly List<string> _expiredKeys = new List<string>(8);

        public int ActiveExcitabilityModifierCount => _excitabilityMods.Count;
        public int ActiveFailureOverrideCount => _failureOverrides.Count;

        public void SetExcitabilityModifier(
            string sourceId,
            float additiveExcitability,
            float excitabilityMultiplier = 1f,
            float durationSec = -1f)
        {
            if (string.IsNullOrWhiteSpace(sourceId)) return;
            sourceId = sourceId.Trim();

            if (_excitabilityMods.TryGetValue(sourceId, out var existing))
            {
                existing.AdditiveExcitability = additiveExcitability;
                existing.ExcitabilityMultiplier = RespMath.Clamp(excitabilityMultiplier, 0f, 5f);
                existing.RemainingDurationSec = durationSec;
                _excitabilityMods[sourceId] = existing;
            }
            else
            {
                _excitabilityMods[sourceId] = new ExcitabilityModifier
                {
                    SourceId = sourceId,
                    AdditiveExcitability = additiveExcitability,
                    ExcitabilityMultiplier = RespMath.Clamp(excitabilityMultiplier, 0f, 5f),
                    ChemoSensitivityMultiplier = 1f,
                    Co2ThresholdOffsetMmHg = 0f,
                    RemainingDurationSec = durationSec
                };
            }
        }

        public void SetChemosensitivityModifier(
            string sourceId,
            float chemoSensitivityMultiplier,
            float co2ThresholdOffsetMmHg = 0f,
            float durationSec = -1f)
        {
            if (string.IsNullOrWhiteSpace(sourceId)) return;
            sourceId = sourceId.Trim();

            if (_excitabilityMods.TryGetValue(sourceId, out var existing))
            {
                existing.ChemoSensitivityMultiplier = RespMath.Clamp(chemoSensitivityMultiplier, 0f, 5f);
                existing.Co2ThresholdOffsetMmHg = RespMath.Clamp(co2ThresholdOffsetMmHg, -25f, 45f);
                existing.RemainingDurationSec = durationSec;
                _excitabilityMods[sourceId] = existing;
            }
            else
            {
                _excitabilityMods[sourceId] = new ExcitabilityModifier
                {
                    SourceId = sourceId,
                    AdditiveExcitability = 0f,
                    ExcitabilityMultiplier = 1f,
                    ChemoSensitivityMultiplier = RespMath.Clamp(chemoSensitivityMultiplier, 0f, 5f),
                    Co2ThresholdOffsetMmHg = RespMath.Clamp(co2ThresholdOffsetMmHg, -25f, 45f),
                    RemainingDurationSec = durationSec
                };
            }
        }

        public void SetFullModifier(ExcitabilityModifier modifier)
        {
            if (string.IsNullOrWhiteSpace(modifier.SourceId)) return;
            modifier.SourceId = modifier.SourceId.Trim();
            modifier.ExcitabilityMultiplier = RespMath.Clamp(modifier.ExcitabilityMultiplier, 0f, 5f);
            modifier.ChemoSensitivityMultiplier = RespMath.Clamp(modifier.ChemoSensitivityMultiplier, 0f, 5f);
            modifier.Co2ThresholdOffsetMmHg = RespMath.Clamp(modifier.Co2ThresholdOffsetMmHg, -25f, 45f);
            _excitabilityMods[modifier.SourceId] = modifier;
        }

        public bool RemoveExcitabilityModifier(string sourceId)
        {
            if (string.IsNullOrWhiteSpace(sourceId)) return false;
            return _excitabilityMods.Remove(sourceId.Trim());
        }

        public void SetPulmonaryFailureOverride(string sourceId, bool active, float durationSec = -1f)
        {
            if (string.IsNullOrWhiteSpace(sourceId)) return;
            sourceId = sourceId.Trim();

            if (!active)
            {
                _failureOverrides.Remove(sourceId);
                return;
            }

            _failureOverrides[sourceId] = new PulmonaryFailureOverride
            {
                SourceId = sourceId,
                Active = true,
                RemainingDurationSec = durationSec
            };
        }

        public bool HasExternalPulmonaryFailure()
        {
            foreach (var kv in _failureOverrides)
            {
                if (kv.Value.Active) return true;
            }
            return false;
        }

        public void Tick(float dt)
        {
            if (dt <= 0f) return;

            if (_excitabilityMods.Count > 0)
            {
                _expiredKeys.Clear();
                foreach (var kv in _excitabilityMods)
                {
                    var m = kv.Value;
                    if (m.RemainingDurationSec >= 0f)
                    {
                        m.RemainingDurationSec -= dt;
                        if (m.RemainingDurationSec <= 0f)
                        {
                            _expiredKeys.Add(kv.Key);
                        }
                        else
                        {
                            _excitabilityMods[kv.Key] = m;
                        }
                    }
                }
                for (int i = 0; i < _expiredKeys.Count; i++)
                    _excitabilityMods.Remove(_expiredKeys[i]);
            }

            if (_failureOverrides.Count > 0)
            {
                _expiredKeys.Clear();
                foreach (var kv in _failureOverrides)
                {
                    var f = kv.Value;
                    if (f.RemainingDurationSec >= 0f)
                    {
                        f.RemainingDurationSec -= dt;
                        if (f.RemainingDurationSec <= 0f)
                        {
                            _expiredKeys.Add(kv.Key);
                        }
                        else
                        {
                            _failureOverrides[kv.Key] = f;
                        }
                    }
                }
                for (int i = 0; i < _expiredKeys.Count; i++)
                    _failureOverrides.Remove(_expiredKeys[i]);
            }
        }

        public void ComputeAggregate(
            out float totalAdditive,
            out float totalMultiplier,
            out float totalChemoSensitivityMult,
            out float totalCo2ThresholdOffset)
        {
            totalAdditive = 0f;
            totalMultiplier = 1f;
            totalChemoSensitivityMult = 1f;
            totalCo2ThresholdOffset = 0f;

            foreach (var kv in _excitabilityMods)
            {
                var m = kv.Value;
                totalAdditive += m.AdditiveExcitability;
                totalMultiplier *= m.ExcitabilityMultiplier;
                totalChemoSensitivityMult *= m.ChemoSensitivityMultiplier;
                totalCo2ThresholdOffset += m.Co2ThresholdOffsetMmHg;
            }

            totalMultiplier = RespMath.Clamp(totalMultiplier, 0f, 5f);
            totalChemoSensitivityMult = RespMath.Clamp(totalChemoSensitivityMult, 0f, 5f);
            totalCo2ThresholdOffset = RespMath.Clamp(totalCo2ThresholdOffset, -30f, 50f);
        }

        public void ClearAll()
        {
            _excitabilityMods.Clear();
            _failureOverrides.Clear();
        }

        public List<ExcitabilityModifier> ExportExcitabilityModifiers()
        {
            var list = new List<ExcitabilityModifier>(_excitabilityMods.Count);
            foreach (var kv in _excitabilityMods)
                list.Add(kv.Value);
            return list;
        }

        public List<PulmonaryFailureOverride> ExportFailureOverrides()
        {
            var list = new List<PulmonaryFailureOverride>(_failureOverrides.Count);
            foreach (var kv in _failureOverrides)
                list.Add(kv.Value);
            return list;
        }
    }
}
