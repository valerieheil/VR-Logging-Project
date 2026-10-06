// ============================================================
//  FeatureExtractor.cs   (NEW in v2)
//  Turns the raw event stream of ONE session into a fixed set of
//  behavioural features (one number each). Runs automatically at
//  export. See docs/FEATURE_DEFINITIONS.md for formulas & rationale.
//
//  DESIGN PRINCIPLES
//   * Features are computed ONLY from the logged events, never from
//     live scene state -> every value can be recomputed later from
//     the raw CSV/JSON (reproducibility).
//   * Pure C#: no Unity API calls in here -> unit-testable outside
//     Unity, and the same logic can be ported 1:1 to Python/R.
//   * Undefined values are NaN (written as empty cells), never 0.
//     "Never looked at a trigger" is a different fact from
//     "looked for 0 s"; latencies are right-censored at session end.
//   * Every analysis parameter is stored with the features
//     (min episode length, proximity radius, epochs) so results
//     are traceable to the exact settings used.
// ============================================================

using System;
using System.Collections.Generic;
using System.Globalization;

public enum StimulusCategory { Trigger, Neutral, Other }

[Serializable]
public class FeatureSettings
{
    [UnityEngine.Tooltip("Gaze episodes shorter than this (s) are ignored. Head-gaze sweeps across " +
                         "an object produce very short 'hits' that are not attention. Default 0.10 s.")]
    public float minGazeEpisodeSec = 0.10f;

    [UnityEngine.Tooltip("Radius (m, horizontal) of the proximity zone around each stimulus.")]
    public float proximityRadiusM = 1.0f;

    [UnityEngine.Tooltip("Number of equal time windows for within-session change (e.g. 3 = thirds).")]
    public int withinSessionEpochs = 3;

    [UnityEngine.Tooltip("Bias indices (ratios) are only computed when trigger + neutral time is at least " +
                         "this many seconds; below that a few seconds flip the ratio between 0 and 1.")]
    public float minBiasDenominatorSec = 5f;
}

/// <summary>One named numeric feature plus its codebook entry.</summary>
public class Feature
{
    public string name;
    public double value;          // NaN = not defined / did not occur
    public string unit;
    public string domain;         // Session | Gaze | Interaction | Movement | Approach | DataQuality
    public string description;

    public Feature(string name, double value, string unit, string domain, string description)
    {
        this.name = name; this.value = value; this.unit = unit;
        this.domain = domain; this.description = description;
    }

    public string ValueString()
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return "";
        return value.ToString("0.####", CultureInfo.InvariantCulture);
    }
}

public static class FeatureExtractor
{
    public const string FEATURE_VERSION = "1.0.0";

    public const string CAT_TRIGGER      = "Trigger";
    public const string CAT_NEUTRAL      = "Neutral";
    public const string CAT_OTHER        = "Other";
    public const string CAT_UNCLASSIFIED = "Unclassified";

    // ── internal types ───────────────────────────────────────
    private struct Episode
    {
        public string obj, cat;
        public double on, off;
        public double Dur => off - on;
    }

    private struct Sample
    {
        public double t, x, y, z;
        public Sample(double t, double x, double y, double z) { this.t = t; this.x = x; this.y = y; this.z = z; }
    }

    private class StimTrack
    {
        public string id;
        public string cat;
        public List<Sample> positions = new List<Sample>();

        public Sample PositionAt(double t)
        {
            // last known position at or before t; before the first log use the first one
            Sample p = positions[0];
            for (int i = 0; i < positions.Count && positions[i].t <= t; i++) p = positions[i];
            return p;
        }
    }

    // ────────────────────────────────────────────────────────
    //  Main entry point
    // ────────────────────────────────────────────────────────
    public static List<Feature> Extract(List<LogEvent> events, double sessionDuration, FeatureSettings s)
    {
        if (s == null) s = new FeatureSettings();
        if (sessionDuration <= 0) sessionDuration = events.Count > 0 ? events[events.Count - 1].timestamp : 0;
        int nEpochs = Math.Max(1, s.withinSessionEpochs);

        // ── Pass 1: stimulus registry (aliases -> category, positions) ──
        var aliasToStim = new Dictionary<string, StimTrack>();
        var stims       = new Dictionary<string, StimTrack>();
        var grabFallback = new Dictionary<string, string>(); // objectID -> category from GrabTracker.isTrigger

        foreach (var e in events)
        {
            if (e.eventType == "StimulusInfo" || e.eventType == "StimulusMove")
            {
                var kv = ParseExtra(e.extra);
                string cat = kv.TryGetValue("category", out var c) ? NormaliseCategory(c) : CAT_OTHER;

                if (!stims.TryGetValue(e.objectID, out var st))
                {
                    st = new StimTrack { id = e.objectID, cat = cat };
                    stims[e.objectID] = st;
                }
                st.cat = cat;
                st.positions.Add(new Sample(e.timestamp, e.posX, e.posY, e.posZ));

                aliasToStim[e.objectID] = st;
                if (kv.TryGetValue("aliases", out var al))
                    foreach (var a in al.Split('|'))
                        if (a.Length > 0) aliasToStim[a] = st;
            }
            else if (e.eventType == "Grab" || e.eventType == "Release")
            {
                var kv = ParseExtra(e.extra);
                if (kv.TryGetValue("isTrigger", out var it) && !string.IsNullOrEmpty(e.objectID))
                    grabFallback[e.objectID] =
                        it.Trim().Equals("True", StringComparison.OrdinalIgnoreCase) ? CAT_TRIGGER : CAT_NEUTRAL;
            }
        }

        Func<string, string> CategoryOf = obj =>
        {
            if (string.IsNullOrEmpty(obj)) return CAT_UNCLASSIFIED;
            if (aliasToStim.TryGetValue(obj, out var st)) return st.cat;
            if (grabFallback.TryGetValue(obj, out var c)) return c;
            return CAT_UNCLASSIFIED;
        };

        // ── Pass 2: episodes and samples ─────────────────────
        var gazeEp = new List<Episode>();
        var grabEp = new List<Episode>();
        var grabCount = new Dictionary<string, int> { { CAT_TRIGGER, 0 }, { CAT_NEUTRAL, 0 } };
        var firstGrab = new Dictionary<string, double> { { CAT_TRIGGER, double.NaN }, { CAT_NEUTRAL, double.NaN } };
        var head  = new List<Sample>();
        var hands = new List<Sample>();

        var openGaze = new Dictionary<string, double>();
        var openGrab = new Dictionary<string, double>();
        int unmatched = 0;

        foreach (var e in events)
        {
            switch (e.eventType)
            {
                case "GazeEnter":
                    if (!openGaze.ContainsKey(e.objectID)) openGaze[e.objectID] = e.timestamp;
                    break;

                case "GazeExit":
                    if (openGaze.TryGetValue(e.objectID, out double gOn))
                    {
                        openGaze.Remove(e.objectID);
                        gazeEp.Add(new Episode { obj = e.objectID, cat = CategoryOf(e.objectID), on = gOn, off = e.timestamp });
                    }
                    else
                    {
                        // exit without enter: recover onset from the logged duration
                        unmatched++;
                        gazeEp.Add(new Episode { obj = e.objectID, cat = CategoryOf(e.objectID),
                                                 on = e.timestamp - e.duration, off = e.timestamp });
                    }
                    break;

                case "Grab":
                {
                    string cat = CategoryOf(e.objectID);
                    if (grabCount.ContainsKey(cat))
                    {
                        grabCount[cat]++;
                        if (double.IsNaN(firstGrab[cat])) firstGrab[cat] = e.timestamp;
                    }
                    if (!openGrab.ContainsKey(e.objectID)) openGrab[e.objectID] = e.timestamp;
                    break;
                }

                case "Release":
                    if (openGrab.TryGetValue(e.objectID, out double rOn))
                    {
                        openGrab.Remove(e.objectID);
                        grabEp.Add(new Episode { obj = e.objectID, cat = CategoryOf(e.objectID), on = rOn, off = e.timestamp });
                    }
                    else unmatched++;
                    break;

                case "SessionStart":
                case "HeadMove":
                    head.Add(new Sample(e.timestamp, e.posX, e.posY, e.posZ));
                    break;

                case "LeftHandPos":
                case "RightHandPos":
                    hands.Add(new Sample(e.timestamp, e.posX, e.posY, e.posZ));
                    break;
            }
        }

        // Close episodes still open at export (e.g. patient was looking at
        // a bottle when A was pressed) at the session end; count them.
        int gazeOpen = openGaze.Count, grabOpen = openGrab.Count;
        foreach (var kv in openGaze)
            gazeEp.Add(new Episode { obj = kv.Key, cat = CategoryOf(kv.Key), on = kv.Value, off = sessionDuration });
        foreach (var kv in openGrab)
            grabEp.Add(new Episode { obj = kv.Key, cat = CategoryOf(kv.Key), on = kv.Value, off = sessionDuration });

        // Apply minimum gaze episode duration
        var gazeValid = gazeEp.FindAll(g => g.Dur >= s.minGazeEpisodeSec);
        gazeValid.Sort((a, b) => a.on.CompareTo(b.on));
        head.Sort((a, b) => a.t.CompareTo(b.t));

        var F = new List<Feature>();
        double D = sessionDuration;

        // ── Session ──────────────────────────────────────────
        F.Add(new Feature("session_duration_s", D, "s", "Session", "Session length from start to export."));
        F.Add(new Feature("n_events", events.Count, "count", "Session", "Number of raw events logged."));

        // ── Gaze (attention) ─────────────────────────────────
        var dwell = new Dictionary<string, double>();
        foreach (var cat in new[] { CAT_TRIGGER, CAT_NEUTRAL })
        {
            var eps = gazeValid.FindAll(g => g.cat == cat);
            double sum = 0, max = double.NaN;
            foreach (var g in eps) { sum += g.Dur; max = double.IsNaN(max) ? g.Dur : Math.Max(max, g.Dur); }
            dwell[cat] = sum;
            string c = cat.ToLowerInvariant();

            F.Add(new Feature($"gaze_{c}_dwell_s", sum, "s", "Gaze",
                $"Total head-gaze dwell time on {c} objects (episodes >= min_gaze_episode_s)."));
            F.Add(new Feature($"gaze_{c}_dwell_pct", D > 0 ? 100.0 * sum / D : double.NaN, "%", "Gaze",
                $"Dwell on {c} objects as percent of session duration."));
            F.Add(new Feature($"gaze_{c}_episodes", eps.Count, "count", "Gaze",
                $"Number of separate gaze episodes on {c} objects."));
            F.Add(new Feature($"gaze_{c}_mean_episode_s", eps.Count > 0 ? sum / eps.Count : double.NaN, "s", "Gaze",
                $"Mean gaze episode duration on {c} objects."));
            F.Add(new Feature($"gaze_{c}_max_episode_s", max, "s", "Gaze",
                $"Longest single gaze episode on {c} objects."));
            F.Add(new Feature($"gaze_{c}_first_latency_s", eps.Count > 0 ? eps[0].on : double.NaN, "s", "Gaze",
                $"Time from session start to first gaze episode on a {c} object (empty = never)."));
        }

        double unclassified = 0;
        foreach (var g in gazeValid) if (g.cat == CAT_UNCLASSIFIED) unclassified += g.Dur;
        F.Add(new Feature("gaze_unclassified_dwell_s", unclassified, "s", "Gaze",
            "Dwell on gaze targets without a StimulusObject/GrabTracker category (check scene setup if > 0)."));

        int nTrigStim = 0, nNeuStim = 0;
        foreach (var st in stims.Values) { if (st.cat == CAT_TRIGGER) nTrigStim++; else if (st.cat == CAT_NEUTRAL) nNeuStim++; }
        bool hasNeutralControl = nNeuStim > 0 || grabFallback.ContainsValue(CAT_NEUTRAL);

        double tn = dwell[CAT_TRIGGER] + dwell[CAT_NEUTRAL];
        F.Add(new Feature("gaze_bias_index",
            hasNeutralControl && tn >= s.minBiasDenominatorSec ? dwell[CAT_TRIGGER] / tn : double.NaN, "ratio 0-1", "Gaze",
            "Trigger dwell / (trigger + neutral dwell). 0.5 = no preference, > 0.5 = more attention on triggers. " +
            "Empty if no neutral control objects exist or trigger + neutral dwell < min_bias_denominator_s."));

        double firstTarget = double.NaN;
        foreach (var g in gazeValid)
            if (g.cat == CAT_TRIGGER || g.cat == CAT_NEUTRAL) { firstTarget = g.cat == CAT_TRIGGER ? 1 : 0; break; }
        F.Add(new Feature("gaze_first_target_trigger", hasNeutralControl ? firstTarget : double.NaN, "0/1", "Gaze",
            "1 if the first fixated stimulus was a trigger, 0 if neutral (initial orienting)."));

        for (int k = 0; k < nEpochs; k++)
        {
            double a = D * k / nEpochs, b = D * (k + 1) / nEpochs, sum = 0;
            foreach (var g in gazeValid) if (g.cat == CAT_TRIGGER) sum += Overlap(g.on, g.off, a, b);
            F.Add(new Feature($"gaze_trigger_dwell_pct_e{k + 1}", b > a ? 100.0 * sum / (b - a) : double.NaN, "%", "Gaze",
                $"Trigger dwell as percent of time window {k + 1}/{nEpochs} of the session (within-session change)."));
        }

        // ── Interaction ──────────────────────────────────────
        foreach (var cat in new[] { CAT_TRIGGER, CAT_NEUTRAL })
        {
            var eps = grabEp.FindAll(g => g.cat == cat);
            double sum = 0; foreach (var g in eps) sum += g.Dur;
            string c = cat.ToLowerInvariant();
            F.Add(new Feature($"grab_{c}_count", grabCount[cat], "count", "Interaction", $"Number of grabs of {c} objects."));
            F.Add(new Feature($"grab_{c}_hold_s", sum, "s", "Interaction", $"Total time {c} objects were held."));
            F.Add(new Feature($"grab_{c}_mean_hold_s", eps.Count > 0 ? sum / eps.Count : double.NaN, "s", "Interaction",
                $"Mean hold duration per grab of {c} objects."));
            F.Add(new Feature($"grab_{c}_first_latency_s", firstGrab[cat], "s", "Interaction",
                $"Time from session start to first grab of a {c} object (empty = never)."));
        }

        // ── Movement ─────────────────────────────────────────
        double path3d = 0, pathH = 0;
        for (int i = 1; i < head.Count; i++)
        {
            double dx = head[i].x - head[i - 1].x, dy = head[i].y - head[i - 1].y, dz = head[i].z - head[i - 1].z;
            path3d += Math.Sqrt(dx * dx + dy * dy + dz * dz);
            pathH  += Math.Sqrt(dx * dx + dz * dz);
        }
        double covered = head.Count > 0 ? Math.Max(0, D - Math.Max(0, head[0].t)) : 0;
        F.Add(new Feature("head_path_m", head.Count > 1 ? path3d : double.NaN, "m", "Movement",
            "Head path length in 3-D (sum of distances between logged head positions)."));
        F.Add(new Feature("head_path_horizontal_m", head.Count > 1 ? pathH : double.NaN, "m", "Movement",
            "Head path length on the floor plane (x/z) = distance walked."));
        F.Add(new Feature("head_mean_speed_mps", covered > 0 && head.Count > 1 ? pathH / covered : double.NaN, "m/s", "Movement",
            "Horizontal path length / tracked time."));

        // ── Approach / avoidance ─────────────────────────────
        var zoneTime = new Dictionary<string, double>();
        foreach (var cat in new[] { CAT_TRIGGER, CAT_NEUTRAL })
        {
            var tracks = new List<StimTrack>();
            foreach (var st in stims.Values) if (st.cat == cat && st.positions.Count > 0) tracks.Add(st);
            string c = cat.ToLowerInvariant();

            double minD = double.NaN, wSum = 0, wT = 0, inZone = 0, firstEntry = double.NaN;
            var zoneEpoch = new double[nEpochs];

            if (tracks.Count > 0)
            {
                for (int i = 0; i < head.Count; i++)
                {
                    double t0 = Math.Max(0, head[i].t);
                    double t1 = i + 1 < head.Count ? head[i + 1].t : D;
                    double d = NearestHorizontal(head[i], tracks, t0);
                    minD = double.IsNaN(minD) ? d : Math.Min(minD, d);

                    if (t1 <= t0) continue;              // sample-and-hold: position valid until next sample
                    wSum += d * (t1 - t0); wT += t1 - t0;

                    if (d <= s.proximityRadiusM)
                    {
                        inZone += t1 - t0;
                        if (double.IsNaN(firstEntry)) firstEntry = t0;
                        for (int k = 0; k < nEpochs; k++)
                            zoneEpoch[k] += Overlap(t0, t1, D * k / nEpochs, D * (k + 1) / nEpochs);
                    }
                }
            }
            bool ok = tracks.Count > 0 && head.Count > 0;
            zoneTime[cat] = ok ? inZone : double.NaN;

            F.Add(new Feature($"prox_{c}_min_dist_m", ok ? minD : double.NaN, "m", "Approach",
                $"Closest horizontal head distance to any {c} object."));
            F.Add(new Feature($"prox_{c}_mean_dist_m", ok && wT > 0 ? wSum / wT : double.NaN, "m", "Approach",
                $"Time-weighted mean horizontal distance to the nearest {c} object."));
            F.Add(new Feature($"prox_{c}_zone_s", ok ? inZone : double.NaN, "s", "Approach",
                $"Time spent within proximity_radius_m of a {c} object."));
            F.Add(new Feature($"prox_{c}_zone_pct", ok && D > 0 ? 100.0 * inZone / D : double.NaN, "%", "Approach",
                $"Time within proximity zone of a {c} object as percent of session."));
            F.Add(new Feature($"prox_{c}_first_entry_s", ok ? firstEntry : double.NaN, "s", "Approach",
                $"Time until first entering the proximity zone of a {c} object (empty = never)."));

            double minHand = double.NaN;
            if (tracks.Count > 0)
                foreach (var h in hands)
                {
                    double d = Nearest3D(h, tracks, h.t);
                    minHand = double.IsNaN(minHand) ? d : Math.Min(minHand, d);
                }
            F.Add(new Feature($"hand_{c}_min_dist_m", minHand, "m", "Approach",
                $"Closest 3-D distance of either hand to any {c} object (reaching)."));

            if (cat == CAT_TRIGGER)
                for (int k = 0; k < nEpochs; k++)
                    F.Add(new Feature($"prox_trigger_zone_pct_e{k + 1}",
                        ok ? 100.0 * zoneEpoch[k] / (D / nEpochs) : double.NaN, "%", "Approach",
                        $"Time in trigger proximity zone as percent of time window {k + 1}/{nEpochs}."));
        }

        double zt = zoneTime[CAT_TRIGGER], zn = zoneTime[CAT_NEUTRAL];
        F.Add(new Feature("approach_bias_index",
            !double.IsNaN(zt) && !double.IsNaN(zn) && zt + zn >= s.minBiasDenominatorSec ? zt / (zt + zn) : double.NaN, "ratio 0-1", "Approach",
            "Trigger zone time / (trigger + neutral zone time). 0.5 = no preference, > 0.5 = closer to triggers. " +
            "Empty if combined zone time < min_bias_denominator_s."));

        // ── Data quality ─────────────────────────────────────
        F.Add(new Feature("dq_n_trigger_stimuli", nTrigStim, "count", "DataQuality", "Trigger objects registered via StimulusObject."));
        F.Add(new Feature("dq_n_neutral_stimuli", nNeuStim, "count", "DataQuality", "Neutral control objects registered via StimulusObject."));
        F.Add(new Feature("dq_gaze_open_at_end", gazeOpen, "count", "DataQuality", "Gaze episodes still open at export (closed at session end)."));
        F.Add(new Feature("dq_grab_open_at_end", grabOpen, "count", "DataQuality", "Objects still held at export (closed at session end)."));
        F.Add(new Feature("dq_unmatched_events", unmatched, "count", "DataQuality", "Exit/Release events without matching Enter/Grab."));
        F.Add(new Feature("dq_gaze_episodes_discarded", gazeEp.Count - gazeValid.Count, "count", "DataQuality",
            "Gaze episodes shorter than min_gaze_episode_s (excluded)."));
        F.Add(new Feature("dq_head_samples", head.Count, "count", "DataQuality", "Head position samples (incl. SessionStart)."));
        F.Add(new Feature("dq_head_coverage_pct", D > 0 && head.Count > 0 ? 100.0 * covered / D : 0, "%", "DataQuality",
            "Share of the session for which a head position is known."));
        F.Add(new Feature("dq_hand_samples", hands.Count, "count", "DataQuality", "Hand position samples."));

        // ── Parameters used (for traceability) ───────────────
        F.Add(new Feature("param_min_gaze_episode_s", s.minGazeEpisodeSec, "s", "Parameter", "Minimum gaze episode duration used."));
        F.Add(new Feature("param_proximity_radius_m", s.proximityRadiusM, "m", "Parameter", "Proximity zone radius used."));
        F.Add(new Feature("param_epochs", nEpochs, "count", "Parameter", "Number of within-session time windows."));
        F.Add(new Feature("param_min_bias_denominator_s", s.minBiasDenominatorSec, "s", "Parameter",
            "Minimum trigger + neutral time required to compute a bias index."));

        return F;
    }

    // ── helpers ──────────────────────────────────────────────

    public static Dictionary<string, string> ParseExtra(string extra)
    {
        var d = new Dictionary<string, string>();
        if (string.IsNullOrEmpty(extra)) return d;
        foreach (var part in extra.Split(','))
        {
            int i = part.IndexOf('=');
            if (i > 0) d[part.Substring(0, i).Trim()] = part.Substring(i + 1).Trim();
        }
        return d;
    }

    private static string NormaliseCategory(string c)
    {
        if (c.Equals(CAT_TRIGGER, StringComparison.OrdinalIgnoreCase)) return CAT_TRIGGER;
        if (c.Equals(CAT_NEUTRAL, StringComparison.OrdinalIgnoreCase)) return CAT_NEUTRAL;
        return CAT_OTHER;
    }

    private static double Overlap(double a0, double a1, double b0, double b1)
        => Math.Max(0, Math.Min(a1, b1) - Math.Max(a0, b0));

    private static double NearestHorizontal(Sample p, List<StimTrack> tracks, double t)
    {
        double best = double.MaxValue;
        foreach (var st in tracks)
        {
            var q = st.PositionAt(t);
            double dx = p.x - q.x, dz = p.z - q.z;
            best = Math.Min(best, Math.Sqrt(dx * dx + dz * dz));
        }
        return best;
    }

    private static double Nearest3D(Sample p, List<StimTrack> tracks, double t)
    {
        double best = double.MaxValue;
        foreach (var st in tracks)
        {
            var q = st.PositionAt(t);
            double dx = p.x - q.x, dy = p.y - q.y, dz = p.z - q.z;
            best = Math.Min(best, Math.Sqrt(dx * dx + dy * dy + dz * dz));
        }
        return best;
    }
}
