using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public class ActorHomeMenuController : MonoBehaviour
{
    const int ButtonFontSize = 34;
    const float ButtonWidth = 360f;
    const float ButtonHeight = 90f;
    const float Spacing = 50f;
    const float CanvasWorldScale = 0.0025f;

    static readonly Vector3 CanvasPosition = new Vector3(19.858f, -10.293f, 50.552f);
    static readonly Vector3 CanvasEulerAngles = new Vector3(-15f, 216.61f, 0f);

    static readonly Color PanelColor = new Color(0.5f, 0.32f, 0.12f, 0.55f);
    static readonly Color TextColor = new Color(0.98f, 0.94f, 0.85f, 1f);

    Font uiFont;
    RectTransform canvasRect;
    GameObject buttonLayoutObject;

    void Start()
    {
        uiFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        var canvasObject = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        SceneManager.MoveGameObjectToScene(canvasObject, gameObject.scene);

        Canvas canvas = canvasObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = Camera.main;

        canvasRect = (RectTransform)canvasObject.transform;
        canvasRect.sizeDelta = new Vector2(900, 300);
        canvasRect.position = CanvasPosition;
        canvasRect.rotation = Quaternion.Euler(CanvasEulerAngles);
        canvasRect.localScale = Vector3.one * CanvasWorldScale;

        if (FindFirstObjectByType<EventSystem>() == null)
            new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));

        buttonLayoutObject = new GameObject("ButtonLayout", typeof(RectTransform), typeof(HorizontalLayoutGroup));
        buttonLayoutObject.transform.SetParent(canvasRect, false);
        var layoutRect = (RectTransform)buttonLayoutObject.transform;
        layoutRect.anchorMin = new Vector2(0.5f, 0.5f);
        layoutRect.anchorMax = new Vector2(0.5f, 0.5f);
        layoutRect.pivot = new Vector2(0.5f, 0.5f);
        layoutRect.sizeDelta = new Vector2(ButtonWidth * 2 + Spacing, ButtonHeight);

        HorizontalLayoutGroup layout = buttonLayoutObject.GetComponent<HorizontalLayoutGroup>();
        layout.spacing = Spacing;
        layout.childAlignment = TextAnchor.MiddleCenter;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = true;

        CreateButton(layoutRect, uiFont, "Ensayo Remoto", OnRemoteRehearsalSelected);
        CreateButton(layoutRect, uiFont, "Ensayar Solo", OnRehearseAloneSelected);

        LayoutRebuilder.ForceRebuildLayoutImmediate(layoutRect);
    }

    void OnDestroy()
    {
        if (RehearsalSessionSync.Instance != null)
            RehearsalSessionSync.Instance.ShotSelected -= OnDirectorSelectedShot;
    }

    void CreateButton(RectTransform parent, Font font, string label, UnityEngine.Events.UnityAction onClick)
    {
        var buttonObject = new GameObject($"Button_{label}", typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
        buttonObject.transform.SetParent(parent, false);
        buttonObject.GetComponent<Image>().color = PanelColor;
        buttonObject.GetComponent<LayoutElement>().preferredHeight = ButtonHeight;

        var textObject = new GameObject("Label", typeof(RectTransform), typeof(Text));
        textObject.transform.SetParent(buttonObject.transform, false);
        var textRect = (RectTransform)textObject.transform;
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = Vector2.zero;
        textRect.offsetMax = Vector2.zero;

        Text text = textObject.GetComponent<Text>();
        text.font = font;
        text.text = label;
        text.fontSize = ButtonFontSize;
        text.fontStyle = FontStyle.Bold;
        text.color = TextColor;
        text.alignment = TextAnchor.MiddleCenter;

        buttonObject.GetComponent<Button>().onClick.AddListener(onClick);
    }

    void OnRemoteRehearsalSelected()
    {
        if (buttonLayoutObject != null)
            Destroy(buttonLayoutObject);

        var waitingObject = new GameObject("WaitingMessage", typeof(RectTransform), typeof(Text));
        waitingObject.transform.SetParent(canvasRect, false);
        var waitingRect = (RectTransform)waitingObject.transform;
        waitingRect.anchorMin = new Vector2(0.5f, 0.5f);
        waitingRect.anchorMax = new Vector2(0.5f, 0.5f);
        waitingRect.pivot = new Vector2(0.5f, 0.5f);
        waitingRect.sizeDelta = new Vector2(ButtonWidth * 2 + Spacing, ButtonHeight);

        Text waitingText = waitingObject.GetComponent<Text>();
        waitingText.font = uiFont;
        waitingText.text = "Esperando selección del director...";
        waitingText.fontSize = ButtonFontSize;
        waitingText.fontStyle = FontStyle.Bold;
        waitingText.color = TextColor;
        waitingText.alignment = TextAnchor.MiddleCenter;

        if (RehearsalSessionSync.Instance != null)
        {
            RehearsalSessionSync.Instance.ResetBaseline();
            RehearsalSessionSync.Instance.ShotSelected += OnDirectorSelectedShot;
            // TEMPORARY diagnostic - remove once the "Ensayo Remoto" handoff is confirmed fixed.
            Debug.Log("ActorHomeMenuController: subscribed to RehearsalSessionSync.ShotSelected.");
        }
        else
        {
            Debug.LogError("ActorHomeMenuController: no RehearsalSessionSync instance found, cannot listen for the director's shot selection.");
        }
    }

    void OnDirectorSelectedShot(int shotNumber)
    {
        // TEMPORARY diagnostic - remove once the "Ensayo Remoto" handoff is confirmed fixed.
        Debug.Log($"ActorHomeMenuController: OnDirectorSelectedShot({shotNumber}) received.");

        if (RehearsalSessionSync.Instance == null)
            return;

        RehearsalSessionSync.Instance.ShotSelected -= OnDirectorSelectedShot;
        RehearsalSessionSync.Instance.LoadSceneForShot(shotNumber);
    }

    void OnRehearseAloneSelected()
    {
        SceneManager.LoadSceneAsync("SceneSelect");
    }
}
