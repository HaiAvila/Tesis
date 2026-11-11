using UnityEngine;
using UnityEngine.SceneManagement;

// Persistent (lives under Bootstrap, DontDestroyOnLoad). A glass chip in the upper-left corner of
// the view while a take is being recorded, with a pulsing red dot: "REC" for the director (from
// RehearsalRecorder), "Grabando" for the actor in Ensayo Remoto (from the recording state the
// director syncs, RehearsalSessionSync.IsRecording) - without it the actor can't tell a take has
// started. Head-locked, on the UI layer, so the viewfinder camera doesn't film it.
public class RecordingIndicator : MonoBehaviour
{
    // Upper left, about 22° out and 15° up, half a metre in front of the eyes.
    public static readonly Vector3 ChipOffset = new Vector3(-0.2f, 0.135f, 0.5f);
    const float PulsePeriod = 1.1f;
    const float PulseMinAlpha = 0.35f;
    static readonly Color RecordColor = new Color(0.95f, 0.22f, 0.22f, 1f);

    HudChip chip;
#if DIRECTOR_BUILD
    // "Pistas apagadas" while the director has the guide cues off, so a whole session isn't
    // recorded in the wrong condition by mistake. Under REC and the pods' indicator.
    static readonly Vector3 GuideOffOffset = ChipOffset + new Vector3(0f, -2f * GlassUI.ChipSpacing, 0f);
    static readonly Color GuideOffColor = new Color(1f, 0.72f, 0.3f, 1f);
    HudChip guideOffChip;

    void UpdateGuideOffChip()
    {
        bool show = Bootstrap.Mode == Bootstrap.AppMode.Remote
                    && RehearsalSessionSync.Instance != null && !RehearsalSessionSync.Instance.GuideCuesOn
                    && Bootstrap.IsContentScene(SceneManager.GetActiveScene().name);
        if (show && guideOffChip == null)
        {
            Camera head = Camera.main;
            if (head != null)
                guideOffChip = GlassUI.HudChip(head.transform, "GuideOffChip", GuideOffOffset, GlassUI.Icon.HapticsOff, "Pistas apagadas", GuideOffColor, 0f);
        }
        else if (!show && guideOffChip != null)
        {
            Destroy(guideOffChip.gameObject);
            guideOffChip = null;
        }
    }
#endif

    void Update()
    {
#if DIRECTOR_BUILD
        UpdateGuideOffChip();
#endif
        bool recording;
#if DIRECTOR_BUILD
        recording = RehearsalRecorder.Instance != null && RehearsalRecorder.Instance.IsRecording;
        const string label = "REC";
#else
        recording = Bootstrap.Mode == Bootstrap.AppMode.Remote
                    && RehearsalSessionSync.Instance != null && RehearsalSessionSync.Instance.IsRecording
                    && Bootstrap.IsContentScene(SceneManager.GetActiveScene().name);
        const string label = "Grabando";
#endif

        if (recording && chip == null)
        {
            Camera head = Camera.main;
            if (head == null)
                return;
            chip = GlassUI.HudChip(head.transform, "RecChip", ChipOffset, GlassUI.Icon.Record, label, RecordColor, 0f);
        }
        else if (!recording && chip != null)
        {
            Destroy(chip.gameObject);
            chip = null;
        }

        if (chip != null)
        {
            float t = (Mathf.Sin(Time.time * (2f * Mathf.PI / PulsePeriod)) + 1f) * 0.5f;
            Color color = RecordColor;
            color.a = Mathf.Lerp(PulseMinAlpha, 1f, t);
            chip.Icon.color = color;
        }
    }
}
