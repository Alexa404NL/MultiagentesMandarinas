using UnityEngine;
using TMPro;
using UnityEngine.UI;
using System.Collections.Generic;

public class DialogueManager : MonoBehaviour
{
    [Header("Phase 2: QNA")]
    [SerializeField] private GameObject interviewPanel;
    [SerializeField] private TMP_Text questionText;
    [SerializeField] private TMP_InputField answerInput;
    [SerializeField] private Button submitButton;

    [Header("Phase 3: Dialogue")]
    [SerializeField] private GameObject dialoguePanel;
    [SerializeField] private Image characterSprite;
    [SerializeField] private TMP_Text characterName;
    [SerializeField] private TMP_Text dialogueText;
    [SerializeField] private Button nextButton;
    [SerializeField] private TextAsset dialogueJson;
    [SerializeField] private List<CharacterSprite> characterSprites;

  
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
        submitButton.onClick.AddListener(OnSubmitAnswer);
        nextButton.onClick.AddListener(OnNextDialogueLine);
        
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
        for (int i = 0; i < collectedAnswers.Count; i++)
        {
            Debug.Log($"{collectedAnswers[i].question}: {collectedAnswers[i].answer}");
        }
        // need to save answers to JSON here
        
        // the loading screen scene should go HERE

        SwitchToDialogueMode(); 
    }

    // DIALOGUE PHASE

    private void SwitchToDialogueMode()
    {
        interviewPanel.SetActive(false);
        dialoguePanel.SetActive(true);

        // dialogue is received as a JSON
        ConversationWrapper wrapper = JsonUtility.FromJson<ConversationWrapper>(dialogueJson.text);
        
        dialogueQueue.Clear();
        foreach (var line in wrapper.conversation)
        {
            dialogueQueue.Enqueue(line);
        }

        OnNextDialogueLine();
    }

    private void OnNextDialogueLine()
    {
        if (dialogueQueue.Count == 0)
        {
            Debug.Log("Conversation ended.");
            return;
        }

        DialogueLine currentLine = dialogueQueue.Dequeue();

        characterName.text = currentLine.characterId;
        dialogueText.text = currentLine.text;

        Sprite s = GetSpriteById(currentLine.characterId);
        if (s != null)
        {
            characterSprite.sprite = s;
            characterSprite.gameObject.SetActive(true);
        }
        else
        {
            characterSprite.gameObject.SetActive(false);
        }
    }

    private Sprite GetSpriteById(string id)
    {
        foreach (var profile in characterSprites)
        {
            if (profile.characterId == id) return profile.portrait;
        }
        return null;
    }
}

// DATA MODELS

[System.Serializable] // Qna answers for entrepeneur
public class QnAData { public string question; public string answer; }

[System.Serializable] // Character sprite and id
public class CharacterSprite { public string characterId; public Sprite portrait; }

[System.Serializable] // Line of dialogue and character speaking
public class DialogueLine { public string characterId; public string text; }

[System.Serializable] // Needed for JSON decoding apparnently
public class ConversationWrapper { public List<DialogueLine> conversation; }