// =====================================================================
//  NATO C2 RTS Hybrid — SimulateLink16DropsCommand.cs
//  ---------------------------------------------------------------------
//  Editor command that drives a controlled denial-of-service experiment
//  on the STANAG 5066 ARQ layer: every 0.5 s for 10 s, ~30 % of the
//  currently-outstanding PPLI envelopes are NAK'd (dropped). The ARQ
//  layer reschedules each one for retransmission. The in-game HUD
//  (Link16BurstModeHud) and the PackingModeDebugWindow show the retry
//  count climb in real time.
//
//  This is the "production-hardening drill" — a fast way to prove the
//  net survives jamming / terrain mask / terminal drop-outs WITHOUT
//  needing to actually attack the radio physically.
//
//  Trigger: menu  NATO C2 → Link 16 → Simulate 30% drops 10s
//  Requires:
//      • Play mode (the simulator publishes envelopes only while play)
//      • Link16TdmaSimulator.useArqRetry = true (we flip this on for
//        the duration of the drill, then restore the prior value)
// =====================================================================

#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using NATO.C2.Net;

namespace NATO.C2.EditorTools
{
    public static class SimulateLink16DropsCommand
    {
        private const float DropPercent     = 30f;
        private const float TotalDurationSec = 10f;
        private const float TickIntervalSec  = 0.5f;

        // ----- running state -----
        private static bool   _running;
        private static float  _startTime;
        private static float  _nextTickAt;
        private static Link16TdmaSimulator _sim;
        private static Stanag5066ArqRetry  _arq;
        private static bool   _restoreArqFlag;
        // initial counters so we can show "+N retries during drill"
        private static int    _baselineRetries;
        private static int    _baselineFails;
        private static int    _baselineAcks;

        [MenuItem("NATO C2/Link 16/Simulate 30%% drops 10s", priority = 52)]
        public static void Run()
        {
            if (_running)
            {
                Debug.LogWarning("[SimulateLink16Drops] drill already running — wait for it to finish.");
                return;
            }
            if (!Application.isPlaying)
            {
                EditorUtility.DisplayDialog("Need Play Mode",
                    "The simulated-drops drill needs the simulator publishing envelopes. " +
                    "Enter Play mode first, then re-run.", "OK");
                return;
            }
            _sim = Object.FindAnyObjectByType<Link16TdmaSimulator>();
            if (_sim == null)
            {
                Debug.LogWarning("[SimulateLink16Drops] no Link16TdmaSimulator in scene.");
                return;
            }

            // Flip ARQ on for the drill — restore on exit.
            _restoreArqFlag = _sim.useArqRetry;
            _sim.useArqRetry = true;
            // Force the lazy-attach in the simulator to run NOW.
            _arq = _sim.GetComponent<Stanag5066ArqRetry>();
            if (_arq == null) _arq = _sim.gameObject.AddComponent<Stanag5066ArqRetry>();
            _arq.logEvents = true;

            _baselineRetries = _arq.TotalRetried;
            _baselineFails   = _arq.TotalFailed;
            _baselineAcks    = _arq.TotalAcked;

            _running     = true;
            _startTime   = Time.realtimeSinceStartup;
            _nextTickAt  = _startTime;

            EditorApplication.update += Tick;
            Debug.Log($"[SimulateLink16Drops] DRILL START — {DropPercent}% drops every {TickIntervalSec}s for {TotalDurationSec}s.");
        }

        private static void Tick()
        {
            if (!_running) return;
            float now = Time.realtimeSinceStartup;

            if (now - _startTime >= TotalDurationSec)
            {
                Stop();
                return;
            }
            if (now < _nextTickAt) return;
            _nextTickAt = now + TickIntervalSec;

            if (_arq != null)
            {
                int outstandingBefore = _arq.OutstandingCount;
                _arq.SimulateRandomDrops(DropPercent);
                Debug.Log($"[SimulateLink16Drops] tick t={now - _startTime:F1}s  " +
                          $"outstanding={outstandingBefore}  retries={_arq.TotalRetried - _baselineRetries}  " +
                          $"fails={_arq.TotalFailed - _baselineFails}");
            }
        }

        private static void Stop()
        {
            EditorApplication.update -= Tick;
            _running = false;
            if (_arq != null)
            {
                int retries = _arq.TotalRetried - _baselineRetries;
                int fails   = _arq.TotalFailed  - _baselineFails;
                int acks    = _arq.TotalAcked   - _baselineAcks;
                Debug.Log($"[SimulateLink16Drops] DRILL END — retries={retries}  fails={fails}  acks={acks}  " +
                          $"remaining-outstanding={_arq.OutstandingCount}");
                _arq.logEvents = false;
            }
            if (_sim != null) _sim.useArqRetry = _restoreArqFlag;
            _sim = null;
            _arq = null;
        }
    }
}
#endif
