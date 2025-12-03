using UnityEngine;
using UnityEngine.Networking;
using System.Collections;
using System.Collections.Generic;
using System.Text;

/// <summary>
/// Cliente para comunicarse con los agentes de CrewAI corriendo en Google Colab.
/// Envía el pitch del emprendedor y recibe las respuestas de los jueces.
/// </summary>
public class ColabAgentClient : MonoBehaviour
{
    [Header("Server Configuration")]
    [Tooltip("URL del servidor ngrok de Google Colab (ej: https://xyz.ngrok-free.dev)")]
    public string serverUrl = "https://submundane-unbenumbed-makayla.ngrok-free.dev";

    [Header("Debug")]
    [SerializeField] private bool logResponses = true;

    // Clase para serializar el mensaje que se envía al servidor
    [System.Serializable]
    private class MessagePayload
    {
        public string content;
        public string sender;
    }

    // Clase para deserializar la respuesta del servidor
    [System.Serializable]
    private class ServerResponse
    {
        public string status;
        public string response;
        public string sender;
        public string message;
    }

    // Clase para deserializar un mensaje del historial
    [System.Serializable]
    private class ConversationMessage
    {
        public string role;
        public string content;
    }

    // Wrapper para el array de mensajes (el servidor retorna un array directo)
    [System.Serializable]
    private class ConversationHistoryWrapper
    {
        public ConversationMessage[] messages;
    }

    // Formato que espera DialogueManager
    [System.Serializable]
    private class DialogueLine
    {
        public string characterId;
        public string content;
    }

    [System.Serializable]
    private class ConversationWrapper
    {
        public List<DialogueLine> conversation;
    }

    /// <summary>
    /// Envía el pitch del emprendedor al servidor de Colab.
    /// </summary>
    /// <param name="businessData">Diccionario con las respuestas del cuestionario</param>
    /// <param name="onSuccess">Callback cuando se recibe respuesta exitosa del juez - retorna el JSON de conversación</param>
    /// <param name="onError">Callback si hay un error</param>
    public void SendEntrepreneurPitch(Dictionary<string, string> businessData, System.Action<string> onSuccess, System.Action<string> onError)
    {
        // Construir el pitch del emprendedor usando los datos del cuestionario
        string pitch = BuildPitchFromData(businessData);
        StartCoroutine(SendMessageAndGetHistoryCoroutine(pitch, "Entrepreneur", onSuccess, onError));
    }

    /// <summary>
    /// Construye el texto del pitch usando las respuestas del cuestionario.
    /// </summary>
    private string BuildPitchFromData(Dictionary<string, string> data)
    {
        StringBuilder pitchBuilder = new StringBuilder();
        
        pitchBuilder.AppendLine($"Ladies and gentlemen of Shark Tank, thank you for this opportunity to present {GetValue(data, "name")}.");
        pitchBuilder.AppendLine();
        pitchBuilder.AppendLine(GetValue(data, "description"));
        pitchBuilder.AppendLine();
        pitchBuilder.AppendLine($"Our target market is: {GetValue(data, "target_market")}");
        pitchBuilder.AppendLine();
        pitchBuilder.AppendLine($"Our revenue model: {GetValue(data, "revenue_model")}");
        pitchBuilder.AppendLine();
        pitchBuilder.AppendLine($"Current traction: {GetValue(data, "current_traction")}");
        pitchBuilder.AppendLine();
        pitchBuilder.AppendLine($"Today, I'm seeking {GetValue(data, "investment_needed")}.");
        pitchBuilder.AppendLine();
        pitchBuilder.AppendLine($"We will use the funds for: {GetValue(data, "use_of_funds")}");
        pitchBuilder.AppendLine();
        pitchBuilder.AppendLine("Thank you for considering our proposal!");

        return pitchBuilder.ToString();
    }

    private string GetValue(Dictionary<string, string> dict, string key)
    {
        return dict.ContainsKey(key) ? dict[key] : $"[{key} not provided]";
    }

    /// <summary>
    /// Envía un mensaje al servidor y luego obtiene el historial completo de conversación.
    /// </summary>
    private IEnumerator SendMessageAndGetHistoryCoroutine(string content, string sender, System.Action<string> onSuccess, System.Action<string> onError)
    {
        // Primero enviar el mensaje
        bool messageSent = false;
        string sendError = null;

        yield return SendMessageCoroutine(content, sender, 
            (response) => { messageSent = true; },
            (error) => { sendError = error; });

        if (!messageSent || sendError != null)
        {
            onError?.Invoke(sendError ?? "Failed to send message");
            yield break;
        }

        // Esperar un poco para que el servidor procese
        yield return new WaitForSeconds(2f);

        // Luego obtener el historial completo
        yield return GetAndFormatConversationHistoryCoroutine(onSuccess, onError);
    }

    /// <summary>
    /// Envía un mensaje al servidor de Colab.
    /// </summary>
    private IEnumerator SendMessageCoroutine(string content, string sender, System.Action<string> onSuccess, System.Action<string> onError)
    {
        if (string.IsNullOrEmpty(serverUrl))
        {
            onError?.Invoke("Server URL is not configured. Please set the ngrok URL in the Inspector.");
            yield break;
        }

        // Crear el payload JSON
        MessagePayload payload = new MessagePayload
        {
            content = content,
            sender = sender
        };

        string jsonPayload = JsonUtility.ToJson(payload);

        if (logResponses)
        {
            Debug.Log($"Sending message to {serverUrl}/submit_message");
            Debug.Log($"Payload: {jsonPayload}");
        }

        // Crear la petición HTTP POST
        string url = serverUrl.TrimEnd('/') + "/submit_message";
        using (UnityWebRequest request = new UnityWebRequest(url, "POST"))
        {
            byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonPayload);
            request.uploadHandler = new UploadHandlerRaw(bodyRaw);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");

            // Enviar la petición
            yield return request.SendWebRequest();

            // Manejar la respuesta
            if (request.result == UnityWebRequest.Result.Success)
            {
                if (logResponses)
                {
                    Debug.Log($"Response received: {request.downloadHandler.text}");
                }

                try
                {
                    ServerResponse response = JsonUtility.FromJson<ServerResponse>(request.downloadHandler.text);
                    
                    if (response.status == "success" && !string.IsNullOrEmpty(response.response))
                    {
                        // El servidor respondió con la evaluación del juez
                        onSuccess?.Invoke(response.response);
                    }
                    else if (response.status == "success")
                    {
                        // Mensaje recibido pero sin respuesta inmediata
                        onSuccess?.Invoke("Message sent successfully. Waiting for judge's response...");
                    }
                    else
                    {
                        onError?.Invoke($"Server returned error status: {response.status}");
                    }
                }
                catch (System.Exception ex)
                {
                    Debug.LogError($"Error parsing server response: {ex.Message}");
                    onError?.Invoke($"Error parsing response: {ex.Message}");
                }
            }
            else
            {
                string errorMsg = $"Network error: {request.error}\nResponse code: {request.responseCode}";
                Debug.LogError(errorMsg);
                onError?.Invoke(errorMsg);
            }
        }
    }

    /// <summary>
    /// Obtiene el historial completo de conversación del servidor.
    /// </summary>
    public void GetConversationHistory(System.Action<string> onSuccess, System.Action<string> onError)
    {
        StartCoroutine(GetConversationHistoryCoroutine(onSuccess, onError));
    }

    /// <summary>
    /// Obtiene el historial del servidor y lo convierte al formato que espera DialogueManager.
    /// </summary>
    private IEnumerator GetAndFormatConversationHistoryCoroutine(System.Action<string> onSuccess, System.Action<string> onError)
    {
        bool historyReceived = false;
        string historyJson = null;
        string historyError = null;

        yield return GetConversationHistoryCoroutine(
            (json) => 
            {
                historyReceived = true;
                historyJson = json;
            },
            (error) =>
            {
                historyError = error;
            }
        );

        if (!historyReceived || historyError != null)
        {
            onError?.Invoke(historyError ?? "Failed to get conversation history");
            yield break;
        }

        // Convertir del formato del servidor al formato de DialogueManager
        try
        {
            string formattedJson = ConvertServerHistoryToDialogueFormat(historyJson);
            onSuccess?.Invoke(formattedJson);
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"Error formatting conversation history: {ex.Message}");
            onError?.Invoke($"Error formatting history: {ex.Message}");
        }
    }

    /// <summary>
    /// Convierte el historial del servidor (formato [{role, content}]) al formato de DialogueManager ({conversation: [{characterId, content}]}).
    /// </summary>
    private string ConvertServerHistoryToDialogueFormat(string serverJson)
    {
        // El servidor retorna un array directo, wrapearlo para JsonUtility
        string wrappedJson = "{\"messages\":" + serverJson + "}";
        ConversationHistoryWrapper serverHistory = JsonUtility.FromJson<ConversationHistoryWrapper>(wrappedJson);

        if (serverHistory == null || serverHistory.messages == null)
        {
            throw new System.Exception("Failed to parse server history");
        }

        // Convertir a formato DialogueManager
        ConversationWrapper dialogueFormat = new ConversationWrapper
        {
            conversation = new List<DialogueLine>()
        };

        foreach (var msg in serverHistory.messages)
        {
            dialogueFormat.conversation.Add(new DialogueLine
            {
                characterId = msg.role, // "Entrepreneur" o "Judge"
                content = msg.content
            });
        }

        return JsonUtility.ToJson(dialogueFormat, true);
    }

    private IEnumerator GetConversationHistoryCoroutine(System.Action<string> onSuccess, System.Action<string> onError)
    {
        if (string.IsNullOrEmpty(serverUrl))
        {
            onError?.Invoke("Server URL is not configured.");
            yield break;
        }

        string url = serverUrl.TrimEnd('/') + "/conversation_history";
        
        using (UnityWebRequest request = UnityWebRequest.Get(url))
        {
            yield return request.SendWebRequest();

            if (request.result == UnityWebRequest.Result.Success)
            {
                if (logResponses)
                {
                    Debug.Log($"Conversation history: {request.downloadHandler.text}");
                }
                onSuccess?.Invoke(request.downloadHandler.text);
            }
            else
            {
                string errorMsg = $"Error getting conversation history: {request.error}";
                Debug.LogError(errorMsg);
                onError?.Invoke(errorMsg);
            }
        }
    }

    /// <summary>
    /// Guarda el JSON de conversación en un archivo.
    /// </summary>
    /// <param name="json">JSON en formato DialogueManager</param>
    /// <param name="filename">Nombre del archivo (se guarda en Assets/Scripts/)</param>
    public void SaveConversationToFile(string json, string filename = "conversation_history.json")
    {
        try
        {
            string path = System.IO.Path.Combine(Application.dataPath, "Scripts", filename);
            System.IO.File.WriteAllText(path, json);
            Debug.Log($"Conversation saved to: {path}");
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"Error saving conversation to file: {ex.Message}");
        }
    }
}
