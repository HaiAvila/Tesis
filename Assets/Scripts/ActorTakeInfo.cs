using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

// Persistent (added to the Bootstrap object). Actor only (Ensayo Remoto and Ensayar Solo): which
// take this is. When one loads, its scene, shot and description float in front of them for a few
// seconds, and a small "Escena 1 · Shot 3" stays in the upper-right corner of the view.
public class ActorTakeInfo : MonoBehaviour
{
#if !DIRECTOR_BUILD
    const float SecondsBeforeMessage = 1f;
    const float MessageSeconds = 7f;

    static readonly Vector3 LabelOffset = new Vector3(0.195f, 0.12f, 0.5f);
    const float LabelScale = 0.0003f;
    const int LabelFontSize = 48;
    static readonly Vector2 LabelSize = new Vector2(620f, 76f);
    static readonly Color LabelBackground = new Color(0.05f, 0.05f, 0.08f, 0.6f);

    GameObject labelObject;
    Coroutine showCoroutine;

    void Awake()
    {
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    void OnDestroy()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (showCoroutine != null)
            StopCoroutine(showCoroutine);
        showCoroutine = null;

        bool actorFlow = Bootstrap.Mode == Bootstrap.AppMode.Remote || Bootstrap.Mode == Bootstrap.AppMode.Solo;
        if (actorFlow && Bootstrap.IsContentScene(scene.name))
            showCoroutine = StartCoroutine(ShowTake());
    }

    IEnumerator ShowTake()
    {
        yield return new WaitForSeconds(SecondsBeforeMessage);
        showCoroutine = null;

        string title = Bootstrap.IsFullScene
            ? $"Escena {Bootstrap.CurrentSceneNumber} · Escena completa"
            : $"Escena {Bootstrap.CurrentSceneNumber} · Shot {Bootstrap.CurrentShotNumber}";

        BuildLabel(title);

        string description = null;
        if (!Bootstrap.IsFullScene)
        {
            ShotListDatabase database = FindFirstObjectByType<ShotListDatabase>();
            Shot shot = database != null ? database.GetShot(Bootstrap.CurrentShotNumber) : null;
            description = shot?.Description;
        }

        FloatingMessage.ShowLong(string.IsNullOrWhiteSpace(description) ? title : $"{title}\n{description}", MessageSeconds);
    }

    void BuildLabel(string text)
    {
        if (labelObject != null)
            Destroy(labelObject);

        Camera head = Camera.main;
        if (head == null)
            return;

        labelObject = new GameObject("TakeLabel", typeof(RectTransform), typeof(Canvas), typeof(Image));
        labelObject.transform.SetParent(head.transform, false);
        labelObject.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;

        var rect = (RectTransform)labelObject.transform;
        rect.sizeDelta = LabelSize;
        rect.pivot = new Vector2(1f, 0.5f);
        rect.localPosition = LabelOffset;
        rect.localRotation = Quaternion.identity;
        rect.localScale = Vector3.one * LabelScale;

        Image background = labelObject.GetComponent<Image>();
        background.color = LabelBackground;
        background.raycastTarget = false;

        var textObject = new GameObject("Text", typeof(RectTransform), typeof(Text));
        textObject.transform.SetParent(rect, false);
        var textRect = (RectTransform)textObject.transform;
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = Vector2.zero;
        textRect.offsetMax = Vector2.zero;

        Text label = textObject.GetComponent<Text>();
        label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        label.text = text;
        label.fontSize = LabelFontSize;
        label.fontStyle = FontStyle.Bold;
        label.color = Color.white;
        label.alignment = TextAnchor.MiddleCenter;
        label.horizontalOverflow = HorizontalWrapMode.Overflow;
        label.raycastTarget = false;

        XRUISetup.MoveToUILayer(labelObject);
    }
#endif
}
