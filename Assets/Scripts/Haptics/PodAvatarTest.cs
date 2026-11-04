using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

// Diagnostics only - the PodAvatarTest scene, played on its own (no Bootstrap, XR rig or
// Normcore): the Y Bot driven by the 9 pods through PodAvatar. The camera is behind the
// avatar, looking the way the actor faced when calibrating, so the actor's left is the
// avatar's left on screen (not a mirror).
//
// On screen: each pod's state (data, IMU, calibration) and bone, the calibration steps with
// a big countdown, and a "Calibrar" button. K, or the button in the Inspector
// (PodAvatarTestInspector), also starts it - the keyboard only reaches the scene while the
// Game view has focus. The line count never depends on live state, so IMGUI's layout and
// draw passes always agree (see PodAxisTest).
public class PodAvatarTest : MonoBehaviour
{
    public PodAvatar podAvatar;

    InputAction calibrateAction;
    GUIStyle textStyle, smallStyle, bigStyle;

    // Read at the layout pass, reused by the others.
    PodAvatar.Phase guiPhase;
    bool guiFocused = true;
    readonly List<string> guiPodLines = new List<string>();
    string guiCollision = "";

    void Awake()
    {
        if (podAvatar == null)
            podAvatar = FindFirstObjectByType<PodAvatar>();

        calibrateAction = new InputAction("Calibrate", InputActionType.Button, "<Keyboard>/k");
        calibrateAction.performed += _ => StartCalibration();
        calibrateAction.Enable();
        Debug.Log("[PodAvatarTest] started. K or Calibrar: T-pose, arms down + slight squat, then palms together in front (if PodAvatar uses the third pose).");
    }

    void OnDestroy()
    {
        calibrateAction.Disable();
        calibrateAction.Dispose();
    }

    public void StartCalibration()
    {
        if (podAvatar != null && podAvatar.CurrentPhase == PodAvatar.Phase.Idle)
            podAvatar.StartCalibration();
    }

    string PodLine(PodAvatar.PodBone entry)
    {
        PodReceiver receiver = podAvatar.receiver;
        int id = entry.podId;
        string data = receiver == null ? "?" : receiver.HasOrientation(id) ? "<color=#7fe07f>datos</color>"
            : receiver.IsAlive(id) ? "<color=#ffaa33>sin IMU</color>" : "<color=#999999>sin datos</color>";

        string cal = "";
        if (podAvatar.Calibrations.TryGetValue(id, out PodAvatar.PodCalibration c))
            cal = c.ok ? "  ·  <color=#7fe07f>calibrado</color>" : $"  ·  <color=#ff7766>{c.problem}</color>";

        string health = "";
        if (receiver != null && receiver.TryGetHealth(id, out PodReceiver.PodHealth h))
        {
            string label = h.ToLabel();
            if (label.Length > 0)
                health = $"  ·  {label}";
        }

        return $"Pod {id}  {entry.bone}:  {data}{health}{cal}";
    }

    void OnGUI()
    {
        if (textStyle == null)
        {
            textStyle = new GUIStyle(GUI.skin.label) { fontSize = 16, richText = true, wordWrap = true };
            textStyle.normal.textColor = Color.white;
            smallStyle = new GUIStyle(textStyle) { fontSize = 14 };
            bigStyle = new GUIStyle(textStyle) { fontSize = 34, alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
        }

        if (podAvatar == null || podAvatar.receiver == null)
        {
            GUI.Label(new Rect(20, 20, 800, 30), "Falta PodAvatar o PodReceiver en la escena.", textStyle);
            return;
        }

        if (Event.current.type == EventType.Layout)
        {
            guiPhase = podAvatar.CurrentPhase;
            guiFocused = Application.isFocused;
            guiPodLines.Clear();
            foreach (PodAvatar.PodBone entry in podAvatar.mapping)
                guiPodLines.Add(PodLine(entry));
            AvatarCollision c = podAvatar.Collision;
            guiCollision = c == null || !podAvatar.preventInterpenetration || !podAvatar.IsCalibrated ? " "
                : c.LastContacts == 0 ? "<color=#999999>sin contactos</color>"
                : $"{c.LastContacts} contacto(s): hundido {c.LastMaxPenetrationBefore * 100f:F1} cm → {c.LastMaxPenetrationAfter * 100f:F1} cm";
        }

        GUILayout.BeginArea(new Rect(16, 12, Mathf.Min(Screen.width * 0.45f, 460f), Screen.height - 24));
        PodReceiver r = podAvatar.receiver;
        GUILayout.Label($"<b>Pods</b> — conectados según el receptor: {r.connectedPods} de {r.targetPods}", textStyle);
        foreach (string line in guiPodLines)
            GUILayout.Label(line, smallStyle);
        GUILayout.Space(8);

        GUI.enabled = guiPhase == PodAvatar.Phase.Idle;
        if (GUILayout.Button(podAvatar.IsCalibrated ? "Calibrar otra vez (K)" : "Calibrar (K)", GUILayout.Height(34)))
            StartCalibration();
        GUI.enabled = true;

        podAvatar.preventInterpenetration = GUILayout.Toggle(podAvatar.preventInterpenetration, " Evitar que las extremidades atraviesen el cuerpo");
        GUILayout.Label(guiCollision, smallStyle);

        GUILayout.Label(guiFocused ? " " : "<color=#ffaa33>La vista Game no tiene el foco: K no llega. Haz clic en ella o usa el botón.</color>", smallStyle);
        GUILayout.EndArea();

        // One instruction line, always there.
        string instruction;
        string countdown = "";
        switch (guiPhase)
        {
            case PodAvatar.Phase.CountdownTPose:
                instruction = "Ponte en <b>T</b>: brazos extendidos a los lados a la altura de los hombros, codos rectos, <b>palmas hacia abajo</b>, mirando hacia la pantalla.";
                countdown = $"{Mathf.CeilToInt(podAvatar.PhaseTimeLeft)}";
                break;
            case PodAvatar.Phase.CaptureTPose:
                instruction = "<b>Quieto</b> en T…";
                countdown = "●";
                break;
            case PodAvatar.Phase.CountdownSecond:
                // The arms must swing straight down sideways, without twisting: the heading of
                // each arm pod comes from the axis of that turn (PodAvatar.SecondPoseDelta).
                instruction = "Ahora: <b>baja los brazos de lado, sin girarlos</b>, hasta los costados (<b>palmas hacia los muslos</b>), y haz una <b>sentadilla ligera</b> inclinándote un poco hacia adelante.";
                countdown = $"{Mathf.CeilToInt(podAvatar.PhaseTimeLeft)}";
                break;
            case PodAvatar.Phase.CaptureSecond:
                instruction = "<b>Quieto</b> en la sentadilla…";
                countdown = "●";
                break;
            case PodAvatar.Phase.CountdownThird:
                instruction = "Ahora: párate derecho, <b>brazos estirados al frente</b> a la altura de los hombros, codos rectos, y <b>junta las palmas</b> (como aplaudiendo con los brazos rectos).";
                countdown = $"{Mathf.CeilToInt(podAvatar.PhaseTimeLeft)}";
                break;
            case PodAvatar.Phase.CaptureThird:
                instruction = "<b>Quieto</b> con las palmas juntas…";
                countdown = "●";
                break;
            default:
                instruction = podAvatar.IsCalibrated
                    ? "Listo: muévete y el avatar te sigue. Si algo se ve mal, vuelve a calibrar."
                    : podAvatar.UsesThirdPose
                        ? "Ponte frente a la pantalla y presiona <b>Calibrar</b>: la T, luego brazos abajo con sentadilla ligera, luego brazos al frente con las palmas juntas."
                        : "Ponte frente a la pantalla y presiona <b>Calibrar</b>: primero la T, luego brazos abajo con sentadilla ligera.";
                break;
        }

        GUI.Label(new Rect(Screen.width * 0.25f, Screen.height - 70, Screen.width * 0.72f, 60), $"<color=#9fd0ff>{instruction}</color>", textStyle);
        if (countdown.Length > 0)
            GUI.Label(new Rect(Screen.width * 0.5f - 60, Screen.height * 0.12f, 120, 60), countdown, bigStyle);
    }
}
