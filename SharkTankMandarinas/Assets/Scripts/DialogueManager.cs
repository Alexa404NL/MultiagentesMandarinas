using UnityEngine;
using TMPro;
using UnityEngine.UI;
using System.Collections.Generic;
using System.Text;

public class DialogueManager : MonoBehaviour
{
    [Header("Server Configuration")]
    [SerializeField, Tooltip("Cliente para comunicarse con los agentes de Colab")]
    private ColabAgentClient colabClient;
    [SerializeField, Tooltip("Si es true, espera la respuesta del servidor antes de continuar al diálogo")]
    private bool waitForServerResponse = true;

    [Header("Phase 2: QNA")]
    [SerializeField] private GameObject interviewPanel;
    [SerializeField] private TMP_Text questionText;
    [SerializeField] private TMP_InputField answerInput;
    [SerializeField] private Button submitButton;

    [Header("Phase 3: Dialogue")]
    [SerializeField] private GameObject dialoguePanel;
    [SerializeField] private GameObject characterModelParent;
    [SerializeField] private TMP_Text characterName;
    [SerializeField] private TMP_Text dialogueText;
    [SerializeField] private TMP_Text pageIndicatorText;
    [SerializeField] private Button nextButton;
    [SerializeField, Tooltip("JSON estático de respaldo si no se puede obtener del servidor")]
    private TextAsset dialogueJson;
    
    // JSON dinámico recibido del servidor
    private string dynamicDialogueJson = null;
    [SerializeField] private List<CharacterModel> characterModels;
    // a small runtime lookup cache for fast prefab lookup
    private Dictionary<string, GameObject> characterModelCache = new Dictionary<string, GameObject>();
    // idle instances for characters (either scene objects or instantiated prefabs that represent idle/background versions)
    private Dictionary<string, GameObject> idleInstances = new Dictionary<string, GameObject>();
    private GameObject currentModelInstance = null;
    private string currentCharacterId = null;
    private bool currentModelIsScene = false;

    [Header("Pagination")]
    [SerializeField] private bool enablePagination = true;
    [SerializeField, Tooltip("A padding (in pixels) to spare from the available dialogue box height when paginating. Smaller values pack more text into each page.")]
    private float pageVerticalPadding = 2f;
    [SerializeField, Tooltip("When true paragraphs will be kept together when they fit; if false, paragraphs may be split across pages to maximize fill.")]
    private bool preserveParagraphs = true;
    [SerializeField, Tooltip("An optional RectTransform that defines the dialogue area to be used for pagination measurement. If not set, the manager will try to find a parent RectTransform called 'Dialogue Box'.")]
    private RectTransform dialogueBoxRect = null;
        [SerializeField, Tooltip("A small trailing string appended to pages when a paragraph is split across multiple pages (E.g. ' -'). Leave blank to disable.")]
        private string splitIndicator = " -";

  
    private string[] questions = new string[] { 
        "Business Name:", 
        "Business Description:", 
        "Target Market:",
        "Revenue Model:",
        "Current Traction:",
        "Investment Needed:",
        "Use of Funds:" 
    };
    private string[] questionKeys = new string[] { 
        "name", 
        "description", 
        "target_market",
        "revenue_model",
        "current_traction",
        "investment_needed",
        "use_of_funds" 
    };
    private int questionIndex = 0;
    private List<QnAData> collectedAnswers = new List<QnAData>();

    private Queue<DialogueLine> dialogueQueue = new Queue<DialogueLine>();

    private void Start()
    {
        // Auto-asignar ColabAgentClient si no está configurado
        if (colabClient == null)
        {
            colabClient = gameObject.GetComponent<ColabAgentClient>();
            if (colabClient == null)
            {
                colabClient = gameObject.AddComponent<ColabAgentClient>();
            }
        }

        submitButton.onClick.AddListener(OnSubmitAnswer);
        nextButton.onClick.AddListener(OnNextDialogueLine);
        // Auto-assign dialogueBoxRect if missing by searching for a parent RectTransform named "Dialogue Box" (case-insensitive)
        if (dialogueBoxRect == null && dialogueText != null)
        {
            Transform parent = dialogueText.transform.parent;
            while (parent != null)
            {
                var rt = parent as RectTransform;
                if (rt != null)
                {
                    if (string.Equals(rt.name, "Dialogue Box", System.StringComparison.OrdinalIgnoreCase) || string.Equals(rt.name, "dialogue box", System.StringComparison.OrdinalIgnoreCase))
                    {
                        dialogueBoxRect = rt;
                        break;
                    }
                }
                parent = parent.parent;
            }

            // If still null, fallback to immediate parent RectTransform
            if (dialogueBoxRect == null && dialogueText.rectTransform.parent != null)
                dialogueBoxRect = dialogueText.rectTransform.parent as RectTransform;
        }
        
        // Prepare character model lookup cache and hide any scene placed character models.
        if (characterModels != null)
        {
            foreach (var profile in characterModels)
            {
                if (profile == null || (profile.prefab == null && profile.activePrefab == null && profile.idlePrefab == null)) continue;
                // If this GameObject belongs to the scene (IsValid), treat it as a scene instance and hide it now.
                // For backward compatibility, profile.prefab is the old single prefab field; prefer the new fields if they exist.
                // Add any idle instances for profile.id
                if (profile.idlePrefab != null)
                {
                    if (profile.idlePrefab.scene.IsValid())
                    {
                        idleInstances[profile.characterId] = profile.idlePrefab;
                        try
                        {
                            profile.idlePrefab.SetActive(true); // keep idles visible by default
                            Debug.Log($"DialogueManager: Enabled idle scene model for id '{profile.characterId}' (name '{profile.idlePrefab.name}').");
                        }
                        catch (System.Exception ex)
                        {
                            Debug.LogWarning($"DialogueManager: Failed to enable idle scene model for id '{profile.characterId}' (name '{profile.idlePrefab.name}'): {ex.Message}");
                        }
                    }
                    else
                    {
                        // instantiate idle prefab into the scene and keep it visible
                        try
                        {
                            var idleInst = (characterModelParent != null) ? Instantiate(profile.idlePrefab, characterModelParent.transform) : Instantiate(profile.idlePrefab);
                            idleInstances[profile.characterId] = idleInst;
                            idleInst.SetActive(true);
                        }
                        catch (System.Exception ex)
                        {
                            Debug.LogWarning($"DialogueManager: Failed to instantiate idle prefab for id '{profile.characterId}': {ex.Message}");
                        }
                    }
                }
                if (profile.activePrefab == null)
                {
                    // Backwards compatibility: use 'prefab' as activePrefab if the new field is absent
                    profile.activePrefab = profile.prefab;
                }
                if (profile.idlePrefab == null && profile.prefab != null && profile.prefab.scene.IsValid())
                {
                    // If idle prefab wasn't set but the legacy prefab is scene instance, treat that as idle
                    idleInstances[profile.characterId] = profile.prefab;
                    try { profile.prefab.SetActive(true); } catch {}
                }
                // For active prefab: hide scene-placed active prefab
                if (profile.activePrefab != null && profile.activePrefab.scene.IsValid())
                {
                    // Hide active scene instance: we'll use it only when speaking
                    characterModelCache[profile.characterId] = profile.activePrefab;
                    try
                    {
                        profile.activePrefab.SetActive(false);
                        Debug.Log($"DialogueManager: Hid active scene model for id '{profile.characterId}' (name '{profile.activePrefab.name}').");
                    }
                    catch (System.Exception ex)
                    {
                        Debug.LogWarning($"DialogueManager: Failed to hide active scene model for id '{profile.characterId}' (name '{profile.activePrefab.name}'): {ex.Message}");
                    }
                }
                else
                {
                    // It's a prefab asset - we'll instantiate when needed.
                    characterModelCache[profile.characterId] = profile.activePrefab ?? profile.prefab;
                }
            }
        }

        // should actually start with a short conversation with the sharks
        // but we havent written it yet
        SwitchToQNAMode(); 
    }

    // QNA PHASE

    private void SwitchToQNAMode()
    {
        interviewPanel.SetActive(true);
        dialoguePanel.SetActive(false);
        
        ShowNextQuestion();
    }

    private void ShowNextQuestion()
    {
        if (questionIndex < questions.Length)
        {
            questionText.text = questions[questionIndex];
            answerInput.text = "";
            answerInput.ActivateInputField();
        }
        else
        {
            FinishInterview();
        }
    }

    private void OnSubmitAnswer()
    {
        if (string.IsNullOrWhiteSpace(answerInput.text)) return;

        QnAData data = new QnAData { question = questionKeys[questionIndex], answer = answerInput.text };
        collectedAnswers.Add(data);

        questionIndex++;
        ShowNextQuestion();
    }

    private void FinishInterview()
    {
        Debug.Log("QNA completed.");
        
        // Convertir las respuestas a un diccionario
        Dictionary<string, string> businessData = new Dictionary<string, string>();
        for (int i = 0; i < collectedAnswers.Count; i++)
        {
            Debug.Log($"{collectedAnswers[i].question}: {collectedAnswers[i].answer}");
            businessData[collectedAnswers[i].question] = collectedAnswers[i].answer;
        }

        // Enviar el pitch al servidor de Colab
        if (colabClient != null)
        {
            Debug.Log("Sending entrepreneur pitch to Colab server...");
            
            colabClient.SendEntrepreneurPitch(
                businessData,
                onSuccess: (conversationJson) => 
                {
                    Debug.Log("Received conversation history from server.");
                    
                    // Guardar el JSON dinámico para usarlo en el diálogo
                    dynamicDialogueJson = conversationJson;
                    
                    // Opcionalmente guardarlo a archivo para inspección
                    colabClient.SaveConversationToFile(conversationJson, "conversation_history.json");
                    
                    if (waitForServerResponse)
                    {
                        Debug.Log("Server responded. Proceeding to dialogue mode with dynamic conversation.");
                        // Cambiar al modo diálogo con el JSON dinámico
                        SwitchToDialogueMode();
                    }
                },
                onError: (error) => 
                {
                    Debug.LogError($"Error communicating with server: {error}");
                    Debug.LogWarning("Proceeding to dialogue mode with fallback static JSON.");
                    
                    // Si hay error, usar el JSON estático como fallback
                    dynamicDialogueJson = null;
                    
                    if (waitForServerResponse)
                    {
                        // Continuar al diálogo con JSON estático
                        SwitchToDialogueMode();
                    }
                }
            );
        }
        else
        {
            Debug.LogWarning("ColabAgentClient not configured. Skipping server communication.");
            dynamicDialogueJson = null;
        }
        
        // Si NO esperamos respuesta del servidor, continuar inmediatamente con JSON estático
        if (!waitForServerResponse)
        {
            SwitchToDialogueMode();
        }
        // Si waitForServerResponse = true, SwitchToDialogueMode() se llamará desde el callback onSuccess
    }

    // DIALOGUE PHASE

    private void SwitchToDialogueMode()
    {
        interviewPanel.SetActive(false);
        dialoguePanel.SetActive(true);

        // Usar el JSON dinámico del servidor si está disponible, si no usar el estático
        string jsonToUse = null;
        
        if (!string.IsNullOrEmpty(dynamicDialogueJson))
        {
            Debug.Log("Using dynamic conversation from server.");
            jsonToUse = dynamicDialogueJson;
        }
        else if (dialogueJson != null)
        {
            Debug.Log("Using static fallback conversation JSON.");
            jsonToUse = dialogueJson.text;
        }
        else
        {
            Debug.LogError("No conversation JSON available (neither dynamic nor static)!");
            return;
        }

        ConversationWrapper wrapper = JsonUtility.FromJson<ConversationWrapper>(jsonToUse);
        
        dialogueQueue.Clear();
        foreach (var line in wrapper.conversation)
        {
            if (enablePagination)
            {
                var pages = SplitDialogueLine(line);
                foreach (var p in pages) dialogueQueue.Enqueue(p);
            }
            else
            {
                // Ensure non-paginated line still has page metadata
                line.pageIndex = 1;
                line.pageCount = 1;
                dialogueQueue.Enqueue(line);
            }
        }

        OnNextDialogueLine();
    }

    private void OnNextDialogueLine()
    {
        if (dialogueQueue.Count == 0)
        {
            Debug.Log("Conversation ended.");
            // When conversation ends, hide any active model and show its idle counterpart (if any)
            if (!string.IsNullOrEmpty(currentCharacterId))
            {
                if (currentModelInstance != null)
                {
                    if (currentModelIsScene) currentModelInstance.SetActive(false);
                    else Destroy(currentModelInstance);
                    currentModelInstance = null;
                }
                if (idleInstances.TryGetValue(currentCharacterId, out var idlePrev))
                {
                    if (idlePrev != null) idlePrev.SetActive(true);
                }
                currentCharacterId = null;
            }
            return;
        }

        DialogueLine currentLine = dialogueQueue.Dequeue();

        // If the character changed since the last page, show the previous idle model if any
        if (!string.IsNullOrEmpty(currentCharacterId) && currentCharacterId != currentLine.characterId)
        {
            if (idleInstances.TryGetValue(currentCharacterId, out var idlePrev))
            {
                if (idlePrev != null) idlePrev.SetActive(true);
            }
        }

        characterName.text = currentLine.characterId;
        dialogueText.text = currentLine.content;

        GameObject modelRef = GetModelPrefabById(currentLine.characterId);
        // Only switch models if the character ID changed, otherwise leave current model in place
        if (currentCharacterId == currentLine.characterId)
        {
            // If we don't currently have an instance but a modelRef exists, create/enable it
            if (currentModelInstance == null && modelRef != null)
            {
                bool isSceneInstance = modelRef.scene.IsValid();
                if (isSceneInstance)
                {
                    modelRef.SetActive(true);
                    currentModelInstance = modelRef;
                    currentModelIsScene = true;
                }
                else
                {
                    if (characterModelParent != null)
                        currentModelInstance = Instantiate(modelRef, characterModelParent.transform);
                    else
                        currentModelInstance = Instantiate(modelRef);
                    currentModelInstance.transform.localPosition = Vector3.zero;
                    currentModelInstance.transform.localRotation = Quaternion.identity;
                    currentModelInstance.SetActive(true);
                    currentModelIsScene = false;
                }
            }
        }
        else if (modelRef != null)
        {
            // Hide the idle model for the new speaking character (if present)
            if (idleInstances.TryGetValue(currentLine.characterId, out var idleNew))
            {
                if (idleNew != null) idleNew.SetActive(false);
            }
            // Determine if the modelRef is a scene instance or a prefab asset.
            bool isSceneInstance = modelRef.scene.IsValid();

            // Clean up previous currentModelInstance
            if (currentModelInstance != null)
            {
                // Before hiding/destroying, make sure yapping is turned off.
                SetAnimatorYapping(currentModelInstance, false);
                if (currentModelIsScene)
                {
                    // Don't destroy scene instances, just hide them
                    currentModelInstance.SetActive(false);
                }
                else
                {
                    // Destroy the previously instantiated prefab instance
                    Destroy(currentModelInstance);
                }
                currentModelInstance = null;
            }

                if (isSceneInstance)
            {
                // Just enable the existing scene model instance. Do NOT reparent or change transform - it's placed as desired in the scene.
                modelRef.SetActive(true);
                currentModelInstance = modelRef;
                currentModelIsScene = true;
                    // Set animator 'yapping' true when model becomes active for speaking
                    SetAnimatorYapping(currentModelInstance, true);
            }
            else
            {
                // Instantiate the prefab asset under the characterModelParent (respect parented transform)
                if (characterModelParent != null)
                {
                    currentModelInstance = Instantiate(modelRef, characterModelParent.transform);
                }
                else
                {
                    // No parent specified - instantiate at origin
                    currentModelInstance = Instantiate(modelRef);
                }
                currentModelInstance.transform.localPosition = Vector3.zero;
                currentModelInstance.transform.localRotation = Quaternion.identity;
                currentModelInstance.SetActive(true);
                SetAnimatorYapping(currentModelInstance, true);
                currentModelIsScene = false;
            }
            currentCharacterId = currentLine.characterId;
        }
        else
        {
            // No model found - hide or destroy previous instance (only if the character changed)
            if (currentModelInstance != null && currentCharacterId != currentLine.characterId)
            {
                if (currentModelIsScene)
                {
                    currentModelInstance.SetActive(false);
                }
                else
                {
                    Destroy(currentModelInstance);
                }
                currentModelInstance = null;
            }
            currentCharacterId = currentLine.characterId;
        }
        // Update page indicator
        if (pageIndicatorText != null)
        {
            if (currentLine.pageCount > 1)
                pageIndicatorText.text = $"{currentLine.pageIndex}/{currentLine.pageCount}";
            else
                pageIndicatorText.text = "";
        }
    }

    private GameObject GetModelPrefabById(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        if (characterModelCache.TryGetValue(id, out var cached)) return cached;
        foreach (var profile in characterModels)
        {
            if (profile.characterId == id)
            {
                var refPrefab = profile.activePrefab ?? profile.prefab;
                characterModelCache[id] = refPrefab;
                return refPrefab;
            }
        }
        // found none
        characterModelCache[id] = null;
        return null;
    }

    private void SetAnimatorYapping(GameObject go, bool value)
    {
        if (go == null) return;
        // Prefer animator on root, otherwise search children
        Animator animator = go.GetComponent<Animator>();
        if (animator == null) animator = go.GetComponentInChildren<Animator>();
        if (animator == null) return;
        // Only set the parameter if it exists (avoid runtime errors with different configs)
        var parameters = animator.parameters;
        for (int i = 0; i < parameters.Length; i++)
        {
            if (parameters[i].name == "yapping" && parameters[i].type == AnimatorControllerParameterType.Bool)
            {
                animator.SetBool("yapping", value);
                return;
            }
        }
    }

    // Splits a single DialogueLine into multiple DialogueLine pages based on the UI height available.
    private List<DialogueLine> SplitDialogueLine(DialogueLine line)
    {
        var pages = new List<DialogueLine>();

        if (dialogueText == null || line == null || string.IsNullOrEmpty(line.content))
        {
            if (line != null) pages.Add(line);
            return pages;
        }

        // Ensure width/height are known. If height is zero, try to schedule pagination with fallback not to split.
        float width = dialogueText.rectTransform.rect.width;
        float maxHeight = dialogueText.rectTransform.rect.height - pageVerticalPadding + 5f;
        if (dialogueBoxRect != null)
        {
            var boxRect = dialogueBoxRect.rect;
            if (boxRect.width > 0f) width = boxRect.width;
            if (boxRect.height > 0f) maxHeight = boxRect.height - pageVerticalPadding;
        }

        // If height is zero (layout not computed), avoid splitting to prevent mis-splitting.
        if (maxHeight <= 0f)
        {
            pages.Add(line);
            return pages;
        }

        System.Action<string, bool> addPage = (string text, bool continues) =>
        {
            if (continues && !string.IsNullOrEmpty(splitIndicator))
                text = text + splitIndicator;

            pages.Add(new DialogueLine { characterId = line.characterId, content = text });
        };

        // We will try to pack multiple paragraphs in a single page if they fit.
        string[] paragraphs;
        if (preserveParagraphs)
            paragraphs = line.content.Split('\n');
        else
            paragraphs = new string[] { line.content.Replace('\n', ' ') };
        StringBuilder pageBuffer = new StringBuilder();

        foreach (var para in paragraphs)
        {
            string candidatePara = para; // paragraph text (no newline at the end for measurement)

            // If the paragraph fits into the current pageBuffer, append it; otherwise finalize and try to fit it alone (or split)
            string combinedCandidate = pageBuffer.Length == 0 ? candidatePara : pageBuffer.ToString() + "\n" + candidatePara;
            var combinedPref = dialogueText.GetPreferredValues(combinedCandidate, width, 0f);

            if (combinedPref.y <= maxHeight)
            {
                // It fits combined, so just keep it in the buffer
                if (pageBuffer.Length > 0)
                {
                    pageBuffer.Append('\n');
                }
                pageBuffer.Append(candidatePara);
                continue;
            }

            // Combined overflows. If the pageBuffer is not empty, finalize it first, then try to add the paragraph as a new page.
            if (pageBuffer.Length > 0)
            {
                addPage(pageBuffer.ToString(), false);
                pageBuffer.Clear();
            }

            // Now try to fit the entire paragraph alone
            var paraPref = dialogueText.GetPreferredValues(candidatePara, width, 0f);
            if (paraPref.y <= maxHeight)
            {
                // Paragraph fits alone in a fresh page; put it into pageBuffer (and we'll continue to try to pack following paragraphs)
                pageBuffer.Append(candidatePara);
                continue;
            }

            // Paragraph is too tall alone: split by words.
            var words = candidatePara.Split(' ');
            StringBuilder wordBuffer = new StringBuilder();
            for (int i = 0; i < words.Length; i++)
            {
                string word = words[i];
                string candidateWord = wordBuffer.Length == 0 ? word : wordBuffer.ToString() + " " + word;
                bool willContinueAfterCandidate = i < words.Length - 1; // more words will follow
                string measureCandidateWord = candidateWord;
                if (willContinueAfterCandidate && !string.IsNullOrEmpty(splitIndicator)) measureCandidateWord = candidateWord + splitIndicator;
                var pref = dialogueText.GetPreferredValues(measureCandidateWord, width, 0f);
                if (pref.y <= maxHeight)
                {
                    // Fit in wordBuffer
                    if (wordBuffer.Length > 0) wordBuffer.Append(' ');
                    wordBuffer.Append(word);
                }
                else
                {
                    // Overflow: flush current wordBuffer as a page if it has content
                    if (wordBuffer.Length > 0)
                    {
                        // Since there are more words to process, this page continues the paragraph
                        addPage(wordBuffer.ToString(), true);
                        wordBuffer.Clear();
                    }

                    // Try to fit the single word on a page
                    bool willContinueAfterWord = i < words.Length - 1; // more words remain
                    string measureSingle = willContinueAfterWord && !string.IsNullOrEmpty(splitIndicator) ? word + splitIndicator : word;
                    var singlePref = dialogueText.GetPreferredValues(measureSingle, width, 0f);
                    if (singlePref.y <= maxHeight)
                    {
                        // Word fits alone in a fresh page - start new wordBuffer
                        wordBuffer.Append(word);
                        continue;
                    }
                    else
                    {
                        // Word itself is too tall: split it by characters into chunks that fit
                        int startIndex = 0;
                        while (startIndex < word.Length)
                        {
                            int length = 1;
                            // expand length as much as it fits
                            while (startIndex + length <= word.Length)
                            {
                                string candidateChunk = word.Substring(startIndex, length);
                                // If this is not the final chunk of the word or there are more words after it, prepare measurement including the indicator
                                bool chunkWillContinue = (startIndex + length) < word.Length || i < words.Length - 1;
                                string measureChunk = chunkWillContinue && !string.IsNullOrEmpty(splitIndicator) ? candidateChunk + splitIndicator : candidateChunk;
                                var prefChunk = dialogueText.GetPreferredValues(measureChunk, width, 0f);
                                if (prefChunk.y <= maxHeight) length++;
                                else break;
                            }
                            length = Mathf.Max(1, length - 1);
                            string chunk = word.Substring(startIndex, length);
                            // Determine if the chunk continues within the paragraph
                            bool chunkMore = (startIndex + length) < word.Length || i < words.Length - 1;
                            addPage(chunk, chunkMore);
                            startIndex += length;
                        }
                    }
                }
            }

            // flush any remaining words from the wordBuffer
            if (wordBuffer.Length > 0)
            {
                addPage(wordBuffer.ToString(), false);
                wordBuffer.Clear();
            }

            // After dealing with an oversized paragraph, we will continue with the next paragraph.
        }

        // After processing all paragraphs, flush pageBuffer
        if (pageBuffer.Length > 0)
        {
            addPage(pageBuffer.ToString(), false);
            pageBuffer.Clear();
        }

        // Final leftover: (none) all buffers flushed earlier

        // If we couldn't split anything, return original line
        if (pages.Count == 0)
        {
            pages.Add(line);
        }

        // Fill in page index/meta for returned pages
        if (pages.Count > 0)
        {
            for (int i = 0; i < pages.Count; i++)
            {
                pages[i].pageIndex = i + 1;
                pages[i].pageCount = pages.Count;
            }
        }

        return pages;
    }
}

// DATA MODELS

[System.Serializable] // Qna answers for entrepeneur
public class QnAData { public string question; public string answer; }

[System.Serializable] // Character prefab and id
public class CharacterModel { public string characterId; public GameObject prefab; public GameObject activePrefab; public GameObject idlePrefab; }

[System.Serializable] // Line of dialogue and character speaking
public partial class DialogueLine { public string characterId; public string content; }

// Added to display page metadata when a DialogueLine is split into multiple pages
public partial class DialogueLine
{
    // 1-based index of this page within the group
    public int pageIndex = 1;
    // Total pages in this dialogue group
    public int pageCount = 1;
}

[System.Serializable] // Needed for JSON decoding apparnently
public class ConversationWrapper { public List<DialogueLine> conversation; }