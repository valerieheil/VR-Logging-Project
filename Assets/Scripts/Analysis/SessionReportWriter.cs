// ============================================================
//  SessionReportWriter.cs   (NEW in v2)
//  Writes the DERIVED outputs of a session (raw logs are written
//  by VRLogger as before):
//
//   <session>/<SessionID>_features.csv   one row: metadata + all features
//   <session>/<SessionID>_features.json  same, with units & descriptions
//   <session>/<SessionID>_summary.txt    plain-language therapist summary
//   VRLogs/<ParticipantID>/<ParticipantID>_sessions.csv
//                                        longitudinal table, one row per
//                                        session of that participant
//   VRLogs/feature_codebook.csv          data dictionary (name/unit/meaning)
//
//  The participant index is REBUILT from all session folders on every
//  export (not appended). That makes it idempotent: a double export, a
//  deleted session or a new feature version can never corrupt it.
//
//  Pure System.IO -> no Unity dependency, testable outside Unity.
//  All numbers use '.' as decimal separator; ';' separates columns.
// ============================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

public static class SessionReportWriter
{
    private const char   SEP = ';';
    private const string BOM = "\uFEFF";
    private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

    // Metadata columns that precede the features in every row
    private static readonly string[] META_COLUMNS =
    {
        "participant_id", "session_number", "session_id", "condition", "therapist_id",
        "started_at_local", "started_at_utc", "device", "app_version", "scene",
        "schema_version", "feature_version"
    };

    /// <param name="participantFolder">VRLogs/&lt;ID&gt; or null when no participant ID is set</param>
    public static void Write(string sessionFolder, string participantFolder, string logsRoot,
                             SessionMetadata meta, List<Feature> features)
    {
        var header = new List<string>(META_COLUMNS);
        header.AddRange(features.Select(f => f.name));
        var row = MetaValues(meta).Concat(features.Select(f => f.ValueString())).ToList();

        // 1) features.csv (header + one row)
        string csvPath = Path.Combine(sessionFolder, $"{meta.sessionID}_features.csv");
        File.WriteAllText(csvPath,
            BOM + string.Join(SEP.ToString(), header) + "\n" + string.Join(SEP.ToString(), row) + "\n",
            Encoding.UTF8);

        // 2) features.json
        File.WriteAllText(Path.Combine(sessionFolder, $"{meta.sessionID}_features.json"),
                          BuildJson(meta, features), Encoding.UTF8);

        // 3) codebook
        WriteCodebook(logsRoot, features);

        // 4) participant index (longitudinal) + history for the summary
        List<Dictionary<string, string>> history = null;
        if (participantFolder != null)
            history = RebuildParticipantIndex(participantFolder, meta.participantID, header);

        // 5) human-readable summary
        File.WriteAllText(Path.Combine(sessionFolder, $"{meta.sessionID}_summary.txt"),
                          BuildSummary(meta, features, history), Encoding.UTF8);
    }

    // ────────────────────────────────────────────────────────
    //  Participant index
    // ────────────────────────────────────────────────────────
    public static List<Dictionary<string, string>> RebuildParticipantIndex(
        string participantFolder, string participantID, List<string> currentHeader)
    {
        var rows = new List<Dictionary<string, string>>();
        var columns = new List<string>(currentHeader);

        foreach (var dir in Directory.GetDirectories(participantFolder))
        {
            foreach (var file in Directory.GetFiles(dir, "*_features.csv"))
            {
                try
                {
                    var lines = File.ReadAllLines(file, Encoding.UTF8)
                                    .Where(l => l.Trim().Length > 0).ToArray();
                    if (lines.Length < 2) continue;
                    var h = lines[0].TrimStart('\uFEFF').Split(SEP);
                    var v = lines[1].Split(SEP);
                    var d = new Dictionary<string, string>();
                    for (int i = 0; i < h.Length; i++)
                    {
                        d[h[i]] = i < v.Length ? v[i] : "";
                        if (!columns.Contains(h[i])) columns.Add(h[i]); // older/newer feature versions
                    }
                    rows.Add(d);
                }
                catch (Exception) { /* skip unreadable file; raw data is untouched */ }
            }
        }

        rows = rows.OrderBy(r => ParseInt(Get(r, "session_number")))
                   .ThenBy(r => Get(r, "started_at_utc"), StringComparer.Ordinal).ToList();

        var sb = new StringBuilder(BOM);
        sb.Append(string.Join(SEP.ToString(), columns)).Append('\n');
        foreach (var r in rows)
            sb.Append(string.Join(SEP.ToString(), columns.Select(c => Get(r, c)))).Append('\n');

        File.WriteAllText(Path.Combine(participantFolder, $"{participantID}_sessions.csv"), sb.ToString(), Encoding.UTF8);
        return rows;
    }

    // ────────────────────────────────────────────────────────
    //  Codebook
    // ────────────────────────────────────────────────────────
    private static void WriteCodebook(string logsRoot, List<Feature> features)
    {
        var sb = new StringBuilder(BOM);
        sb.Append("name;unit;domain;description;feature_version\n");
        foreach (var m in META_COLUMNS)
            sb.Append($"{m};;Metadata;Session metadata;{FeatureExtractor.FEATURE_VERSION}\n");
        foreach (var f in features)
            sb.Append($"{f.name};{f.unit};{f.domain};{Clean(f.description)};{FeatureExtractor.FEATURE_VERSION}\n");
        File.WriteAllText(Path.Combine(logsRoot, "feature_codebook.csv"), sb.ToString(), Encoding.UTF8);
    }

    // ────────────────────────────────────────────────────────
    //  JSON (hand-written: JsonUtility cannot serialise NaN as null)
    // ────────────────────────────────────────────────────────
    private static string BuildJson(SessionMetadata m, List<Feature> features)
    {
        var sb = new StringBuilder();
        sb.Append("{\n  \"metadata\": {\n");
        var mv = MetaValues(m).ToArray();
        for (int i = 0; i < META_COLUMNS.Length; i++)
            sb.Append($"    \"{META_COLUMNS[i]}\": \"{Esc(mv[i])}\",\n");
        sb.Append($"    \"notes\": \"{Esc(m.notes)}\"\n  }},\n  \"features\": [\n");
        for (int i = 0; i < features.Count; i++)
        {
            var f = features[i];
            string val = f.ValueString(); if (val == "") val = "null";
            sb.Append($"    {{ \"name\": \"{f.name}\", \"value\": {val}, \"unit\": \"{Esc(f.unit)}\", " +
                      $"\"domain\": \"{f.domain}\", \"description\": \"{Esc(f.description)}\" }}");
            sb.Append(i < features.Count - 1 ? ",\n" : "\n");
        }
        sb.Append("  ]\n}\n");
        return sb.ToString();
    }

    // ────────────────────────────────────────────────────────
    //  Therapist summary (plain text, opens anywhere)
    // ────────────────────────────────────────────────────────
    private static string BuildSummary(SessionMetadata m, List<Feature> F, List<Dictionary<string, string>> history)
    {
        var f = F.ToDictionary(x => x.name, x => x.value);
        Func<string, double> V = k => f.TryGetValue(k, out var v) ? v : double.NaN;
        double radius = V("param_proximity_radius_m");
        int epochs = (int)V("param_epochs");

        var sb = new StringBuilder();
        string line = new string('=', 66);
        sb.AppendLine(line);
        sb.AppendLine(" VR EXPOSURE SESSION - BEHAVIOURAL SUMMARY");
        sb.AppendLine(line);
        sb.AppendLine($" Participant : {Or(m.participantID, "(not set)")}    Session no.: {(m.sessionNumber > 0 ? m.sessionNumber.ToString() : "-")}");
        sb.AppendLine($" Session ID  : {m.sessionID}");
        sb.AppendLine($" Date        : {m.startedAtLocal}");
        sb.AppendLine($" Condition   : {Or(m.condition, "-")}    Therapist: {Or(m.therapistID, "-")}");
        sb.AppendLine($" Duration    : {Dur(V("session_duration_s"))}");
        sb.AppendLine();

        sb.AppendLine(" ATTENTION  (where the head was pointed)");
        sb.AppendLine($"   Trigger objects : {S(V("gaze_trigger_dwell_s"))} s  ({P(V("gaze_trigger_dwell_pct"))} of session), " +
                      $"{N(V("gaze_trigger_episodes"))} looks, longest {S(V("gaze_trigger_max_episode_s"))} s");
        sb.AppendLine($"   Neutral objects : {S(V("gaze_neutral_dwell_s"))} s  ({P(V("gaze_neutral_dwell_pct"))} of session), " +
                      $"{N(V("gaze_neutral_episodes"))} looks");
        sb.AppendLine($"   Bias index      : {R(V("gaze_bias_index"))}   (0.50 = equal; above 0.50 = more on triggers)");
        sb.AppendLine($"   First look at a trigger after: {Lat(V("gaze_trigger_first_latency_s"))}");
        sb.AppendLine($"   Trigger dwell per {epochs} time windows: {Series(V, "gaze_trigger_dwell_pct_e", epochs, true)}");
        sb.AppendLine();

        sb.AppendLine(" APPROACH / AVOIDANCE");
        sb.AppendLine($"   Closest distance to a trigger  : {M(V("prox_trigger_min_dist_m"))}   (neutral: {M(V("prox_neutral_min_dist_m"))})");
        sb.AppendLine($"   Time within {S(radius)} m of a trigger: {S(V("prox_trigger_zone_s"))} s ({P(V("prox_trigger_zone_pct"))})" +
                      $"   (neutral: {S(V("prox_neutral_zone_s"))} s)");
        sb.AppendLine($"   First entered trigger zone after: {Lat(V("prox_trigger_first_entry_s"))}");
        sb.AppendLine($"   Approach bias index: {R(V("approach_bias_index"))}   (0.50 = equal; above 0.50 = closer to triggers)");
        sb.AppendLine($"   Closest hand reach to a trigger: {M(V("hand_trigger_min_dist_m"))}");
        sb.AppendLine($"   Distance walked: {M(V("head_path_horizontal_m"))}");
        sb.AppendLine();

        sb.AppendLine(" INTERACTION");
        sb.AppendLine($"   Trigger objects picked up: {N(V("grab_trigger_count"))} times, held {S(V("grab_trigger_hold_s"))} s in total, " +
                      $"first after {Lat(V("grab_trigger_first_latency_s"))}");
        sb.AppendLine($"   Neutral objects picked up: {N(V("grab_neutral_count"))} times, held {S(V("grab_neutral_hold_s"))} s in total");
        sb.AppendLine();

        // ── change across sessions ──
        if (history != null)
        {
            var earlier = history.Where(r => ParseInt(Get(r, "session_number")) < m.sessionNumber).ToList();
            if (earlier.Count > 0)
            {
                var bl = earlier.First();
                var pr = earlier.Last();
                string blH = $"Baseline S{Get(bl, "session_number")}";
                string prH = $"Previous S{Get(pr, "session_number")}";
                sb.AppendLine(" CHANGE ACROSS SESSIONS");
                sb.AppendLine($"   {"Metric",-34}{blH,12}{prH,13}{"This S" + m.sessionNumber,11}");
                foreach (var km in KeyMetrics)
                    sb.AppendLine($"   {km[1],-34}{Hist(bl, km[0]),12}{Hist(pr, km[0]),13}{Fmt(V(km[0])),11}");
                sb.AppendLine($"   ({history.Count} sessions on record for this participant; full table in {m.participantID}_sessions.csv)");
                sb.AppendLine();
            }
        }

        // ── data quality ──
        sb.AppendLine(" DATA QUALITY");
        var warn = new List<string>();
        if (V("dq_n_neutral_stimuli") == 0 && double.IsNaN(V("gaze_bias_index")))
            warn.Add("No neutral control objects registered -> bias indices cannot be computed.");
        if (V("dq_n_trigger_stimuli") == 0)
            warn.Add("No StimulusObject trigger registered -> approach metrics unavailable (gaze/grab use GrabTracker.isTrigger).");
        if (V("gaze_unclassified_dwell_s") > 0)
            warn.Add($"{S(V("gaze_unclassified_dwell_s"))} s of gaze on objects without a category (add StimulusObject).");
        if (V("dq_unmatched_events") > 0)
            warn.Add($"{N(V("dq_unmatched_events"))} unmatched exit/release events.");
        if (V("dq_head_coverage_pct") < 90)
            warn.Add($"Head position known for only {P(V("dq_head_coverage_pct"))} of the session.");
        if (V("session_duration_s") < 60)
            warn.Add("Session shorter than 1 minute - consider excluding from analysis.");
        if (warn.Count == 0) sb.AppendLine("   No issues detected.");
        foreach (var w in warn) sb.AppendLine("   ! " + w);
        sb.AppendLine();

        sb.AppendLine(" NOTE: These are descriptive behavioural indicators recorded during the");
        sb.AppendLine(" session. They are not validated diagnostic measures and should be read");
        sb.AppendLine(" together with clinical judgement and questionnaire scores. 'Attention'");
        sb.AppendLine(" is based on head direction (no eye tracking).");
        sb.AppendLine($" Feature definitions v{FeatureExtractor.FEATURE_VERSION} - see feature_codebook.csv");
        sb.AppendLine(line);
        return sb.ToString();
    }

    private static readonly string[][] KeyMetrics =
    {
        new[] { "gaze_trigger_dwell_pct", "Trigger gaze (% of session)" },
        new[] { "gaze_bias_index", "Gaze bias index" },
        new[] { "gaze_trigger_first_latency_s", "First trigger look (s)" },
        new[] { "prox_trigger_zone_pct", "Time near triggers (%)" },
        new[] { "prox_trigger_min_dist_m", "Closest to trigger (m)" },
        new[] { "approach_bias_index", "Approach bias index" },
        new[] { "grab_trigger_count", "Trigger grabs (n)" },
        new[] { "grab_trigger_hold_s", "Trigger hold time (s)" },
        new[] { "session_duration_s", "Session duration (s)" },
    };

    // ── formatting helpers ───────────────────────────────────
    private static IEnumerable<string> MetaValues(SessionMetadata m) => new[]
    {
        m.participantID, m.sessionNumber > 0 ? m.sessionNumber.ToString(IC) : "", m.sessionID, m.condition,
        m.therapistID, m.startedAtLocal, m.startedAtUtc, m.device, m.appVersion, m.sceneName,
        m.schemaVersion, m.featureVersion
    }.Select(Clean);

    private static string Clean(string s) =>
        string.IsNullOrEmpty(s) ? "" : s.Replace(";", ",").Replace("\r", " ").Replace("\n", " ");

    private static string Esc(string s) =>
        string.IsNullOrEmpty(s) ? "" : s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");

    private static string Get(Dictionary<string, string> d, string k) => d.TryGetValue(k, out var v) ? v : "";
    private static int ParseInt(string s) => int.TryParse(s, NumberStyles.Integer, IC, out int i) ? i : int.MaxValue;
    private static string Or(string s, string alt) => string.IsNullOrEmpty(s) ? alt : s;

    private static string Fmt(double v) =>
        double.IsNaN(v) ? "-" :
        Math.Abs(v - Math.Round(v)) < 1e-9 ? v.ToString("0", IC) :
        v.ToString(Math.Abs(v) < 10 ? "0.00" : "0.0", IC);
    private static string Hist(Dictionary<string, string> r, string k)
        => double.TryParse(Get(r, k), NumberStyles.Float, IC, out double v) ? Fmt(v) : "-";
    private static string S(double v) => double.IsNaN(v) ? "-" : v.ToString("0.0", IC);
    private static string N(double v) => double.IsNaN(v) ? "-" : v.ToString("0", IC);
    private static string R(double v) => double.IsNaN(v) ? "n/a" : v.ToString("0.00", IC);
    private static string P(double v) => double.IsNaN(v) ? "-" : v.ToString("0.0", IC) + " %";
    private static string M(double v) => double.IsNaN(v) ? "n/a" : v.ToString("0.00", IC) + " m";
    private static string Lat(double v) => double.IsNaN(v) ? "never" : v.ToString("0.0", IC) + " s";
    private static string Dur(double v) => double.IsNaN(v) ? "-" : $"{(int)(v / 60)} min {(int)(v % 60)} s";

    private static string Series(Func<string, double> V, string prefix, int n, bool pct)
    {
        var parts = new List<string>();
        for (int k = 1; k <= n; k++) parts.Add(pct ? P(V(prefix + k)) : S(V(prefix + k)));
        return string.Join("  ->  ", parts);
    }
}
