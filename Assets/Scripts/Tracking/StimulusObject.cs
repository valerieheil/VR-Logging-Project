// ============================================================
//  StimulusObject.cs   (NEW in v2)
//  Declares what an object IS for the analysis:
//      Trigger  = addiction-related cue (beer bottle, wine glass ...)
//      Neutral  = matched control object (water bottle, soda can ...)
//      Other    = anything else you want tracked (door, table ...)
//
//  WHY: attentional bias and approach/avoidance are only
//  interpretable RELATIVE to a neutral control. "The patient looked
//  at beer for 40 s" means little; "40 s on beer vs 10 s on water"
//  is a bias. This component makes the log self-describing so the
//  feature extraction (and any later re-analysis) knows which
//  objectIDs are triggers and which are controls, and where they are.
//
//  HOW TO USE:
//    1. Add this component to every object of interest
//       (the same objects that carry GrabTracker and/or the gaze tag).
//    2. Choose the Category in the Inspector.
//    3. Give objects UNIQUE names (BeerBottle_1, BeerBottle_2,
//       WaterBottle_1 ...). Identical names cannot be told apart.
//
//  The existing trackers (GazeTracker, GrabTracker) are NOT changed.
//  This component logs:
//    StimulusInfo  at session start: position + category + aliases
//    StimulusMove  whenever the object is moved (e.g. carried) by
//                  more than moveLogThreshold metres
// ============================================================

using System.Collections.Generic;
using UnityEngine;

public class StimulusObject : MonoBehaviour
{
    [Header("Identity")]
    [Tooltip("ID used in the log. Defaults to the GameObject name.")]
    public string stimulusID = "";

    [Tooltip("Trigger = addiction cue, Neutral = matched control object, Other = anything else.")]
    public StimulusCategory category = StimulusCategory.Trigger;

    [Header("Position Logging")]
    [Tooltip("Log a StimulusMove event when the object moved more than this (metres). " +
             "Needed so approach distances stay correct when bottles are carried.")]
    public float moveLogThreshold = 0.05f;

    [Tooltip("How often to check for movement (seconds).")]
    public float moveCheckInterval = 0.25f;

    private Vector3 _lastLoggedPosition;
    private float   _timer;
    private bool    _registered;

    public string StimulusID => stimulusID;

    private void Awake()
    {
        if (string.IsNullOrEmpty(stimulusID))
            stimulusID = gameObject.name;
    }

    // Start (not OnEnable): guarantees VRLogger.Awake has already run.
    private void Start()
    {
        if (VRLogger.Instance == null)
        {
            Debug.LogWarning($"[StimulusObject] No VRLogger in scene - '{stimulusID}' not registered.");
            return;
        }
        VRLogger.Instance.RegisterStimulus(this);
        _registered = true;
    }

    private void OnDestroy()
    {
        if (_registered && VRLogger.Instance != null)
            VRLogger.Instance.UnregisterStimulus(this);
    }

    private void Update()
    {
        if (!_registered) return;

        _timer += Time.deltaTime;
        if (_timer < moveCheckInterval) return;
        _timer = 0f;

        if (Vector3.Distance(transform.position, _lastLoggedPosition) > moveLogThreshold)
            VRLogger.Instance.LogStimulus(this, "StimulusMove");
    }

    /// <summary>Called by VRLogger right after it logged this object's position.</summary>
    public void MarkPositionLogged()
    {
        _lastLoggedPosition = transform.position;
    }

    /// <summary>
    /// Builds the "extra" field: category plus every name under which this
    /// object can appear in the log. GazeTracker logs the name of the
    /// GameObject that owns the HIT COLLIDER (may be a child), GrabTracker
    /// logs its objectLabel, so all of them are listed as aliases.
    /// Separators are ',' and '|' because ';' is the CSV column separator.
    /// </summary>
    public string BuildExtra()
    {
        var aliases = new List<string>();
        AddUnique(aliases, stimulusID);
        AddUnique(aliases, gameObject.name);

        foreach (var col in GetComponentsInChildren<Collider>(true))
            AddUnique(aliases, col.gameObject.name);

        foreach (var grab in GetComponentsInChildren<GrabTracker>(true))
            AddUnique(aliases, grab.objectLabel);

        return $"category={category},aliases={string.Join("|", aliases.ToArray())}";
    }

    private static void AddUnique(List<string> list, string s)
    {
        if (string.IsNullOrEmpty(s)) return;
        s = s.Replace(";", "_").Replace(",", "_").Replace("|", "_");
        if (!list.Contains(s)) list.Add(s);
    }
}
