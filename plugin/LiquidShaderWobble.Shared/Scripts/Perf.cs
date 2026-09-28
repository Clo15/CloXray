using System.Diagnostics;
using UnityEngine;

namespace LiquidWobbleMPB
{
    /// <summary>
    /// How much of a frame CloXray costs, per named block, reported once a second.
    ///
    /// Only runs while the "Diagnostic log" setting is on: with it off, Start() returns 0 and Stop() is a single
    /// compare, so an ordinary session pays nothing. With it on, each block costs two Stopwatch reads (~50ns).
    /// The report prints ms PER FRAME (not per call) beside the measured frame time, because the only question
    /// worth answering for an "fps drop" report is what share of the frame CloXray takes - a block can look big per
    /// call and be irrelevant at one call a second, or look tiny and run six times a frame.
    /// Frame time is measured here (wall clock between frame marks), not Time.deltaTime, so a stall inside
    /// another plugin or the game still shows up in the denominator.
    /// </summary>
    internal static class Perf
    {
        internal const string WombLate = "womb.LateUpdate", WombPreCull = "womb.onPreCull", LiquidFeed = "liquid.feed",
                              HUpdate = "H.Update", HLate = "H.LateUpdate";
        // A leading '+' marks a SUB-block: timed inside one of the blocks above, so it is printed but NOT added to
        // the CloXray total (it is already inside its parent - summing both would double-count it).
        internal const string HSync = "+H.sync", HFill = "+H.fill", HMound = "+H.mound", HDome = "+H.dome",
                              HWatchFin = "+H.watchFinish", HPenState = "+H.penState", HCumFill = "+H.cumFill";
        internal const string PluginUpdate = "plugin.Update", MePumps = "+ME.pumps", StudioTick = "+studio.tick";
        private static readonly string[] Blocks = { WombLate, WombPreCull, LiquidFeed, HUpdate, HLate, PluginUpdate,
                                                    HSync, HFill, HMound, HDome, HWatchFin, HPenState, HCumFill,
                                                    MePumps, StudioTick };

        private static readonly long[] _ticks = new long[Blocks.Length];
        private static readonly int[] _calls = new int[Blocks.Length];
        private static int _frames, _lastMarkFrame = -1;
        private static long _spanStart;
        private static float _next;

        private static bool On { get { return LiquidWobbleMPBPlugin.CfgDebugLog; } }

        /// Timestamp to hand back to Stop(), or 0 when measuring is off.
        internal static long Start() { return On ? Stopwatch.GetTimestamp() : 0L; }

        internal static void Stop(string block, long t0)
        {
            if (t0 == 0L) return;
            long dt = Stopwatch.GetTimestamp() - t0;
            for (int i = 0; i < Blocks.Length; i++)
                if (ReferenceEquals(Blocks[i], block)) { _ticks[i] += dt; _calls[i]++; return; }
        }

        /// Called from the per-frame entry points; counts each frame once and prints the summary every second.
        internal static void FrameMark()
        {
            if (!On) { if (_frames != 0) Reset(); return; }
            if (Time.frameCount == _lastMarkFrame) return;
            _lastMarkFrame = Time.frameCount;
            if (_frames == 0) { _spanStart = Stopwatch.GetTimestamp(); _next = Time.unscaledTime + 1f; }
            _frames++;
            if (Time.unscaledTime < _next) return;

            float spanMs = (Stopwatch.GetTimestamp() - _spanStart) * 1000f / Stopwatch.Frequency;
            float frameMs = spanMs / Mathf.Max(1, _frames);
            float totalMs = 0f;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < Blocks.Length; i++)
            {
                if (_calls[i] == 0) continue;
                float ms = _ticks[i] * 1000f / Stopwatch.Frequency / _frames;
                if (Blocks[i][0] != '+') totalMs += ms;   // sub-blocks are inside a parent already counted
                sb.Append(' ').Append(Blocks[i]).Append('=').Append(ms.ToString("F3")).Append("ms");
                float perFrame = _calls[i] / (float)_frames;
                if (perFrame < 0.95f || perFrame > 1.05f) sb.Append('(').Append(perFrame.ToString("F2")).Append("/f)");
            }
            LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray PERF:" + (sb.Length == 0 ? " (nothing ran)" : sb.ToString())
                + "  | CloXray=" + totalMs.ToString("F3") + "ms of " + frameMs.ToString("F2") + "ms/frame ("
                + (frameMs > 1e-4f ? (100f * totalMs / frameMs).ToString("F1") : "?") + "%), "
                + (1000f / Mathf.Max(1e-4f, frameMs)).ToString("F0") + " fps over " + _frames + " frames");
            Reset();
        }

        private static void Reset()
        {
            for (int i = 0; i < Blocks.Length; i++) { _ticks[i] = 0L; _calls[i] = 0; }
            _frames = 0;
        }
    }
}
