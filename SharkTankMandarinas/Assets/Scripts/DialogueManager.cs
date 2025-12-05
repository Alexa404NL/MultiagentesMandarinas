using UnityEngine;
using TMPro;
using UnityEngine.UI;
using System.Collections;
using System.Collections.Generic;
using System.Text;

public class DialogueManager : MonoBehaviour
{
    [Header("Server Configuration")]
    [SerializeField, Tooltip("Cliente para comunicarse con los agentes de Colab")]
    private ColabAgentClient colabClient;
    [SerializeField, Tooltip("Si es true, espera la respuesta del servidor antes de continuar al diálogo")]
    private bool waitForServerResponse = true;

    [Header("Loading Screen")]
    [SerializeField, Tooltip("Loading screen prefab/panel to show while waiting for server response")]
    private GameObject loadingScreen;
    [SerializeField, Tooltip("List of canvases to hide during loading")]
    private List<Canvas> canvasesToHideDuringLoading;
    [SerializeField, Tooltip("If true, hides all character models during loading")]
    private bool hideCharactersDuringLoading = true;

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

    [Header("Typewriter Effect")]
    [SerializeField, Tooltip("Enable typewriter effect to reveal text character by character")]
    private bool enableTypewriterEffect = true;
    [SerializeField, Tooltip("Time in seconds between each character")]
    private float typewriterSpeed = 0.03f;
    [SerializeField, Tooltip("If true, clicking Next while typing will instantly show full text instead of skipping to next line")]
    private bool allowSkipTypewriter = true;
    
    [Header("Animalese Speech")]
    [SerializeField, Tooltip("Enable Animal Crossing-style speech sounds")]
    private bool enableAnimalese = true;
    [SerializeField, Tooltip("The Animalese component for speech synthesis")]
    private Animalese animalese;
    
    // Typewriter state
    private Coroutine typewriterCoroutine = null;
    private bool isTyping = false;
    private string currentFullText = "";

  
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
        if (loadingScreen != null)
        {
            loadingScreen.SetActive(false);
        }

        submitButton.onClick.AddListener(OnSubmitAnswer);
        nextButton.onClick.AddListener(OnNextDialogueLine);
        
        // Listen for input field changes to enable/disable submit button
        answerInput.onValueChanged.AddListener(OnAnswerInputChanged);
        
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

    // LOADING SCREEN

    /// <summary>
    /// Shows the loading screen and hides characters/canvases.
    /// </summary>
    private void ShowLoadingScreen()
    {
        Debug.Log("Showing loading screen...");
        
        // Hide interview panel
        if (interviewPanel != null)
            interviewPanel.SetActive(false);
        
        // Hide dialogue panel
        if (dialoguePanel != null)
            dialoguePanel.SetActive(false);
        
        // Hide specified canvases
        if (canvasesToHideDuringLoading != null)
        {
            foreach (var canvas in canvasesToHideDuringLoading)
            {
                if (canvas != null)
                    canvas.gameObject.SetActive(false);
            }
        }
        
        // Hide all character models
        if (hideCharactersDuringLoading)
        {
            // Hide current active model
            if (currentModelInstance != null)
            {
                currentModelInstance.SetActive(false);
            }
            
            // Hide all idle instances
            foreach (var kvp in idleInstances)
            {
                if (kvp.Value != null)
                    kvp.Value.SetActive(false);
            }
            
            // Hide character model parent if it exists
            if (characterModelParent != null)
                characterModelParent.SetActive(false);
        }
        
        // Show loading screen
        if (loadingScreen != null)
        {
            loadingScreen.SetActive(true);
        }
        else
        {
            Debug.LogWarning("Loading screen not assigned!");
        }
    }

    /// <summary>
    /// Hides the loading screen and restores characters/canvases.
    /// </summary>
    private void HideLoadingScreen()
    {
        Debug.Log("Hiding loading screen...");
        
        // Hide loading screen
        if (loadingScreen != null)
        {
            loadingScreen.SetActive(false);
        }
        
        // Restore specified canvases
        if (canvasesToHideDuringLoading != null)
        {
            foreach (var canvas in canvasesToHideDuringLoading)
            {
                if (canvas != null)
                    canvas.gameObject.SetActive(true);
            }
        }
        
        // Restore character models
        if (hideCharactersDuringLoading)
        {
            // Restore character model parent
            if (characterModelParent != null)
                characterModelParent.SetActive(true);
            
            // Restore idle instances
            foreach (var kvp in idleInstances)
            {
                if (kvp.Value != null)
                    kvp.Value.SetActive(true);
            }
            
            // Note: currentModelInstance will be managed by SwitchToDialogueMode/ShowNextDialogueLine
        }
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
            
            // Disable submit button until user types something
            UpdateSubmitButtonState();
        }
        else
        {
            FinishInterview();
        }
    }

    /// <summary>
    /// Called when the answer input field text changes.
    /// </summary>
    private void OnAnswerInputChanged(string text)
    {
        UpdateSubmitButtonState();
    }

    /// <summary>
    /// Enables or disables the submit button based on whether the input has text.
    /// </summary>
    private void UpdateSubmitButtonState()
    {
        bool hasText = !string.IsNullOrWhiteSpace(answerInput.text);
        submitButton.interactable = hasText;
    }

    private void OnSubmitAnswer()
    {
        if (string.IsNullOrWhiteSpace(answerInput.text)) return;
        
        // Check bounds before accessing array
        if (questionIndex >= questionKeys.Length)
        {
            Debug.LogWarning("OnSubmitAnswer called but questionIndex is out of bounds. Already finished?");
            return;
        }

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

        // Show loading screen while waiting for server
        if (waitForServerResponse)
        {
            ShowLoadingScreen();
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
                        // Hide loading screen
                        HideLoadingScreen();
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
                        // Hide loading screen
                        HideLoadingScreen();
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
            // Hide loading if shown
            HideLoadingScreen();
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
        // If currently typing and skip is allowed, show full text and return
        if (isTyping && allowSkipTypewriter)
        {
            SkipTypewriter();
            return;
        }
        
        // Stop any ongoing typewriter coroutine
        if (typewriterCoroutine != null)
        {
            StopCoroutine(typewriterCoroutine);
            typewriterCoroutine = null;
            isTyping = false;
        }

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
        GameObject modelRef = GetModelPrefabById(currentLine.characterId);

        // Set character name - prefer display name from CharacterModel, fallback to characterId
        characterName.text = GetCharacterDisplayName(currentLine.characterId);
        
        // Convert markdown formatting to TMP rich text
        currentFullText = ConvertMarkdownToRichText(currentLine.content);
        
        // Start typewriter effect or show instantly
        if (enableTypewriterEffect)
        {
            typewriterCoroutine = StartCoroutine(TypewriterEffect(currentFullText, currentLine.characterId));
        }
        else
        {
            dialogueText.text = currentFullText;
        }

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

    private string GetCharacterDisplayName(string id)
    {
        if (string.IsNullOrEmpty(id)) return "Unknown";
        
        // Look for a CharacterModel with this id and use its displayName if set
        if (characterModels != null)
        {
            foreach (var profile in characterModels)
            {
                if (profile != null && profile.characterId == id)
                {
                    // Use displayName if set, otherwise fall back to characterId
                    if (!string.IsNullOrEmpty(profile.displayName))
                        return profile.displayName;
                    break;
                }
            }
        }
        
        // Fallback: use the characterId directly (e.g., "Judge1", "Entrepreneur")
        return id;
    }

    /// <summary>
    /// Gets the voice pitch for a specific character.
    /// Returns 1.0 (normal) if character not found.
    /// </summary>
    private float GetCharacterVoicePitch(string characterId)
    {
        if (string.IsNullOrEmpty(characterId) || characterModels == null)
            return 1.0f;
        
        foreach (var profile in characterModels)
        {
            if (profile != null && profile.characterId == characterId)
            {
                return profile.voicePitch;
            }
        }
        
        return 1.0f; // Default pitch
    }

    /// <summary>
    /// Converts markdown-style formatting to TextMeshPro rich text tags.
    /// Supports: **bold**, *italic*, ***bold italic***, __underline__, ~~strikethrough~~
    /// </summary>
    private string ConvertMarkdownToRichText(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        // Order matters: process longer patterns first to avoid partial matches
        
        // ***bold italic*** or ___bold italic___
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\*\*\*(.+?)\*\*\*", "<b><i>$1</i></b>");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"___(.+?)___", "<b><i>$1</i></b>");
        
        // **bold** or __bold__ (note: we use __ for underline below, so ** is bold)
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\*\*(.+?)\*\*", "<b>$1</b>");
        
        // *italic* or _italic_
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\*(.+?)\*", "<i>$1</i>");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"(?<![_])_([^_]+?)_(?![_])", "<i>$1</i>");
        
        // __underline__
        text = System.Text.RegularExpressions.Regex.Replace(text, @"__(.+?)__", "<u>$1</u>");
        
        // ~~strikethrough~~
        text = System.Text.RegularExpressions.Regex.Replace(text, @"~~(.+?)~~", "<s>$1</s>");

        return text;
    }

    /// <summary>
    /// Coroutine that reveals text character by character with typewriter effect.
    /// Properly handles rich text tags by not splitting them across frames.
    /// Plays Animalese speech sounds if enabled.
    /// </summary>
    private IEnumerator TypewriterEffect(string fullText, string speakingCharacterId = null)
    {
        isTyping = true;
        dialogueText.text = "";
        
        int i = 0;
        
        // Start animalese audio playback for the entire text
        if (enableAnimalese && animalese != null)
        {
            // Strip rich text tags for animalese generation
            string plainText = System.Text.RegularExpressions.Regex.Replace(fullText, "<.*?>", "");
            
            // Get character-specific pitch if available
            float voicePitch = GetCharacterVoicePitch(speakingCharacterId);
            
            // Speak the whole text with character's pitch
            animalese.SpeakWithTypewriterSync(plainText, typewriterSpeed, voicePitch);
        }
        
        while (i < fullText.Length)
        {
            // Check if we're at the start of a rich text tag
            if (fullText[i] == '<')
            {
                // Find the closing '>' and include the entire tag at once
                int closingIndex = fullText.IndexOf('>', i);
                if (closingIndex != -1)
                {
                    // Add the entire tag instantly
                    dialogueText.text += fullText.Substring(i, closingIndex - i + 1);
                    i = closingIndex + 1;
                    continue;
                }
            }
            
            // Add single character
            dialogueText.text += fullText[i];
            i++;
            
            yield return new WaitForSeconds(typewriterSpeed);
        }
        
        // Stop any remaining audio
        if (enableAnimalese && animalese != null)
        {
            animalese.StopSpeaking();
        }
        
        isTyping = false;
        typewriterCoroutine = null;
    }

    /// <summary>
    /// Skips the typewriter effect and shows the full text immediately.
    /// </summary>
    private void SkipTypewriter()
    {
        if (typewriterCoroutine != null)
        {
            StopCoroutine(typewriterCoroutine);
            typewriterCoroutine = null;
        }
        
        // Stop animalese sounds if playing
        if (animalese != null)
        {
            animalese.StopSpeaking();
        }
        
        dialogueText.text = currentFullText;
        isTyping = false;
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
public class CharacterModel { 
    public string characterId; 
    [Tooltip("Display name shown in dialogue UI. If empty, characterId is used.")]
    public string displayName;
    [Tooltip("Voice pitch for Animalese speech (0.5 = deep, 1.0 = normal, 2.0 = high)")]
    [Range(0.5f, 2.0f)]
    public float voicePitch = 1.0f;
    public GameObject prefab; 
    public GameObject activePrefab; 
    public GameObject idlePrefab; 
}

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