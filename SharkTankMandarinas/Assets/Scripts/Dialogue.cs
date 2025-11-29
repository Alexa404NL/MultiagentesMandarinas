using System.Collections.Generic;
using UnityEngine;
using TMPro;
using UnityEngine.UI;

// Simple, inspector-driven dialogue controller:
// - Assign the `conversation_history.json` TextAsset in the inspector
// - Assign two TMP_Text components: one for the character name/role, one for the dialogue body
// - Click anywhere or press Space/Enter to advance the conversation

public class Dialogue : MonoBehaviour
{
    [Header("Data")]
    [Tooltip("Assign the JSON file containing the conversation (TextAsset). The file provided in the project is conversation_history.json")] 
    public TextAsset conversationJson;

    [Header("UI")]
    [Tooltip("TextMeshPro role/name text. If you don't use TMP, leave this empty and use the Legacy text fields below.")]
    public TMP_Text roleText;
    [Tooltip("TextMeshPro content/body text. If you don't use TMP, leave this empty and use the Legacy text fields below.")]
    public TMP_Text contentText;

    [Space]
    [Tooltip("Legacy UnityEngine.UI.Text role/name text. Used if TextMeshPro fields are not assigned.")]
    public Text legacyRoleText;
    [Tooltip("Legacy UnityEngine.UI.Text content/body text. Used if TextMeshPro fields are not assigned.")]
    public Text legacyContentText;

    [Header("Behaviour")]
    [Tooltip("If true, loop back to the start when the last entry finishes")]
    public bool loop = false;

    [Tooltip("Enable a simple typewriter effect (quick) for line appearance")]
    public bool typewriter = false;

    [Header("Timed & Scrolling")]
    [Tooltip("How many seconds to wait between characters when typewriter = true (smaller = faster)")]
    public float typewriterSpeed = 0.005f;

    [Tooltip("If true, automatically advance to the next entry after the current line finishes (autoAdvanceDelay seconds)")]
    public bool autoAdvance = false;
    [Tooltip("Seconds to wait after the full line is shown before automatically advancing (if autoAdvance = true)")]
    public float autoAdvanceDelay = 1.5f;

    [Tooltip("Optional ScrollRect that contains the content text. If left empty the script will try to find an ancestor ScrollRect automatically.")]
    public ScrollRect contentScrollRect;
    [Tooltip("When true, the ScrollRect will be moved while characters appear so the newest part of the text is visible.")]
    public bool autoScrollOnType = true;
    [Tooltip("If true the ScrollRect will position to the top (1.0) when updating. If false the bottom (0.0) will be used.")]
    public bool autoScrollToTop = false;

    // internal state
    private List<ConversationEntry> entries = new List<ConversationEntry>();
    private int currentIndex = 0;
    private bool isTyping = false;
    private Coroutine autoAdvanceCoroutine = null;

    [System.Serializable]
    private class ConversationEntry
    {
        public string role;
        public string content;
    }

    [System.Serializable]
    private class Wrapper
    {
        public ConversationEntry[] entries;
    }

    void Start()
    {
        if (conversationJson == null)
        {
            Debug.LogError("Dialogue: conversationJson is not assigned. Please assign conversation_history.json (TextAsset) in the inspector.");
            return;
        }

        ParseJson(conversationJson.text);

        // Quick safety checks
        if (roleText == null && legacyRoleText == null)
        {
            Debug.LogWarning("Dialogue: No role/name text assigned (roleText / legacyRoleText). Role text will not be shown.");
        }
        if (contentText == null && legacyContentText == null)
        {
            Debug.LogError("Dialogue: No content text assigned (contentText / legacyContentText). Assign at least one UI target in the Inspector.");
            return;
        }

        // if contentScrollRect is missing, try find one up the hierarchy from content text
        if (contentScrollRect == null)
        {
            if (contentText != null)
                contentScrollRect = contentText.GetComponentInParent<ScrollRect>();
            if (contentScrollRect == null && legacyContentText != null)
                contentScrollRect = legacyContentText.GetComponentInParent<ScrollRect>();
        }

        ShowCurrentEntry();
    }

    void Update()
    {
        // Advance on primary mouse or Space/Enter
        if (!isTyping && (Input.GetMouseButtonDown(0) || Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.Return)))
        {
            Next();
        }
    }

    private void ParseJson(string json)
    {
        // JsonUtility can't parse a top-level array, so wrap it
        try
        {
            string wrapped = "{\"entries\":" + json + "}";
            var wrapper = JsonUtility.FromJson<Wrapper>(wrapped);
            if (wrapper != null && wrapper.entries != null)
            {
                entries = new List<ConversationEntry>(wrapper.entries);
            }
            else
            {
                Debug.LogWarning("Dialogue: parsed wrapper but found no entries");
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogError("Dialogue: failed to parse JSON: " + ex.Message);
        }
    }

    private void ShowCurrentEntry()
    {
        if (entries == null || entries.Count == 0)
        {
            Debug.LogWarning("Dialogue: No entries available to show.");
            return;
        }

        if (currentIndex < 0 || currentIndex >= entries.Count)
        {
            // Out of range: either stop or loop
            if (loop) currentIndex = 0; else return;
        }

        var e = entries[currentIndex];
        SetRoleText(e.role);
        if (typewriter)
        {
            StopAllCoroutines();
            if (autoAdvanceCoroutine != null) { StopCoroutine(autoAdvanceCoroutine); autoAdvanceCoroutine = null; }
            StartCoroutine(TypeText(e.content));
        }
        else
        {
            SetContentText(e.content);
        }
    }

    private System.Collections.IEnumerator TypeText(string text)
    {
        isTyping = true;
        // ensure newlines are converted and clear both targets
        text = text.Replace("\\n", "\n");
        if (contentText != null) contentText.text = "";
        if (legacyContentText != null) legacyContentText.text = "";
        // simple instant chunk writer (controlled by inspector)
        float delay = Mathf.Max(0.0001f, typewriterSpeed);
        foreach (char c in text)
        {
            if (contentText != null) contentText.text += c;
            if (legacyContentText != null) legacyContentText.text += c;

            // optionally auto-scroll while new characters are added
            if (contentScrollRect != null && autoScrollOnType)
            {
                Canvas.ForceUpdateCanvases();
                if (contentScrollRect.content != null)
                    LayoutRebuilder.ForceRebuildLayoutImmediate(contentScrollRect.content);
                contentScrollRect.verticalNormalizedPosition = autoScrollToTop ? 1f : 0f;
            }

            yield return new WaitForSeconds(delay);
        }
        isTyping = false;

        // when text has finished typing, maybe auto-advance
        if (autoAdvance)
        {
            if (autoAdvanceCoroutine != null) StopCoroutine(autoAdvanceCoroutine);
            autoAdvanceCoroutine = StartCoroutine(DelayedAdvance(autoAdvanceDelay));
        }
    }

    private System.Collections.IEnumerator DelayedAdvance(float seconds)
    {
        yield return new WaitForSeconds(seconds);
        autoAdvanceCoroutine = null;
        Next();
    }

    /// <summary>
    /// Advance to the next conversation entry
    /// </summary>
    public void Next()
    {
        if (isTyping)
        {
            // complete immediately
            StopAllCoroutines();
            string full = entries[currentIndex].content.Replace("\\n", "\n");
            SetContentText(full);
            isTyping = false;
            return;
        }

        currentIndex++;
        if (currentIndex >= entries.Count)
        {
            if (loop)
            {
                currentIndex = 0; 
            }
            else
            {
                // Reached the end — optionally hide UI or simply clamp to last
                currentIndex = entries.Count - 1;
                Debug.Log("Dialogue: reached end of conversation.");
                return;
            }
        }

        ShowCurrentEntry();
    }

    /// <summary>
    /// Jump to a specific index
    /// </summary>
    public void JumpTo(int index)
    {
        if (entries == null || entries.Count == 0) return;
        currentIndex = Mathf.Clamp(index, 0, entries.Count - 1);
        ShowCurrentEntry();
    }

    // Helper to set both TMP and legacy role text when present
    private void SetRoleText(string value)
    {
        if (value == null) value = string.Empty;
        if (roleText != null) roleText.text = value;
        if (legacyRoleText != null) legacyRoleText.text = value;
    }

    // Helper to set both TMP and legacy content text when present
    private void SetContentText(string value)
    {
        if (value == null) value = string.Empty;
        value = value.Replace("\\n", "\n");
        if (contentText != null) contentText.text = value;
        if (legacyContentText != null) legacyContentText.text = value;

        // make sure any ScrollRect updates to reflect new content size
        EnsureScrollUpdated();
    }

    // after setting the content, ensure the ScrollRect updates and optionally reposition
    private void EnsureScrollUpdated()
    {
        if (contentScrollRect == null) return;
        Canvas.ForceUpdateCanvases();
        if (contentScrollRect.content != null)
        {
            LayoutRebuilder.ForceRebuildLayoutImmediate(contentScrollRect.content);
        }
        contentScrollRect.verticalNormalizedPosition = autoScrollToTop ? 1f : 0f;
    }
}
