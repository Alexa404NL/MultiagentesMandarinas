using UnityEngine;
using System.Collections.Generic;
using System.Text.RegularExpressions;

/// <summary>
/// Animalese speech synthesizer for Unity.
/// Generates Animal Crossing-style speech from text.
/// Based on animalese.js by Josh Simmons (https://github.com/acedio/animalese.js)
/// </summary>
public class Animalese : MonoBehaviour
{
    [Header("Audio Settings")]
    [SerializeField, Tooltip("The WAV file containing letter sounds (A-Z)")]
    private AudioClip letterLibrary;
    
    [SerializeField, Range(0.5f, 2.0f), Tooltip("Pitch multiplier - affects voice tone without changing speed (1.0 = normal, <1 = deeper, >1 = higher)")]
    private float pitch = 1.0f;
    
    [SerializeField, Tooltip("If true, shortens words to first and last letter")]
    private bool shortenWords = false;
    
    [SerializeField, Range(0.02f, 0.15f), Tooltip("Duration of each letter sound in seconds (for standalone Speak)")]
    private float letterDuration = 0.05f;
    
    [SerializeField, Range(0f, 1f), Tooltip("Volume of the speech")]
    private float volume = 0.6f;
    
    [SerializeField, Range(0.5f, 3.0f), Tooltip("Multiplier for typewriter sync duration (1.0 = exact match, 2.0 = audio plays 2x slower)")]
    private float durationMultiplier = 1.0f;

    [Header("Playback")]
    [SerializeField] private AudioSource audioSource;

    // Cached letter samples from the library
    private float[] letterLibraryData;
    private int librarySampleRate;
    private float libraryLetterDuration = 0.15f; // Duration of each letter in the library WAV
    private int librarySamplesPerLetter;
    private bool isInitialized = false;
    
    // Fade samples to prevent crackling (applied at start/end of each letter)
    private const int FADE_SAMPLES = 64;

    private void Awake()
    {
        Initialize();
    }

    /// <summary>
    /// Initializes the Animalese system by loading the letter library.
    /// </summary>
    public void Initialize()
    {
        if (isInitialized) return;
        
        if (letterLibrary == null)
        {
            // Try to load from Resources folder
            letterLibrary = Resources.Load<AudioClip>("animalese");
            if (letterLibrary == null)
            {
                Debug.LogError("Animalese: Letter library AudioClip not assigned and couldn't load from Resources/animalese!");
                return;
            }
            Debug.Log("Animalese: Loaded letter library from Resources/animalese");
        }

        if (audioSource == null)
        {
            audioSource = GetComponent<AudioSource>();
            if (audioSource == null)
            {
                audioSource = gameObject.AddComponent<AudioSource>();
                Debug.Log("Animalese: Created AudioSource component");
            }
        }

        // Extract audio data from the letter library
        librarySampleRate = letterLibrary.frequency;
        librarySamplesPerLetter = Mathf.FloorToInt(libraryLetterDuration * librarySampleRate);
        
        letterLibraryData = new float[letterLibrary.samples * letterLibrary.channels];
        letterLibrary.GetData(letterLibraryData, 0);
        
        isInitialized = true;
        Debug.Log($"Animalese: Initialized with {letterLibrary.samples} samples at {librarySampleRate}Hz, {librarySamplesPerLetter} samples per letter");
    }

    /// <summary>
    /// Generates an AudioClip from the given text using Animalese speech.
    /// Uses the internal letterDuration setting.
    /// </summary>
    public AudioClip GenerateSpeech(string text)
    {
        return GenerateSpeechWithDuration(text, letterDuration);
    }
    
    /// <summary>
    /// Generates an AudioClip from the given text with a specific duration per letter.
    /// This allows syncing with typewriter effect timing.
    /// Pitch is applied WITHOUT affecting duration (true pitch shifting).
    /// </summary>
    public AudioClip GenerateSpeechWithDuration(string text, float durationPerLetter)
    {
        return GenerateSpeechWithDurationAndPitch(text, durationPerLetter, pitch);
    }
    
    /// <summary>
    /// Generates an AudioClip with specific duration per letter AND custom pitch.
    /// Pitch affects voice tone without changing timing.
    /// </summary>
    public AudioClip GenerateSpeechWithDurationAndPitch(string text, float durationPerLetter, float voicePitch)
    {
        if (!isInitialized)
        {
            Initialize();
            if (!isInitialized) return null;
        }

        if (string.IsNullOrEmpty(text)) return null;

        // Each character (including spaces, punctuation) gets the same duration
        int outputSampleRate = librarySampleRate;
        int outputSamplesPerLetter = Mathf.FloorToInt(durationPerLetter * outputSampleRate);
        int totalSamples = text.Length * outputSamplesPerLetter;

        if (totalSamples <= 0) return null;

        // Generate audio data
        float[] audioData = new float[totalSamples];
        
        for (int charIndex = 0; charIndex < text.Length; charIndex++)
        {
            char c = char.ToUpper(text[charIndex]);
            
            if (c >= 'A' && c <= 'Z')
            {
                // Calculate where this letter starts in the library
                int letterIndex = c - 'A';
                int libraryLetterStart = librarySamplesPerLetter * letterIndex;
                
                // We need to read from the library at a rate determined by pitch,
                // but write to output at a rate determined by durationPerLetter.
                // This achieves pitch shifting without speed change.
                
                // How many source samples we'll read (pitch affects this)
                // Higher pitch = read faster through source = higher voice
                // Lower pitch = read slower through source = deeper voice
                int sourceSamplesToRead = Mathf.FloorToInt(outputSamplesPerLetter * voicePitch);
                
                for (int i = 0; i < outputSamplesPerLetter; i++)
                {
                    // Map output position to source position with pitch factor
                    float sourcePos = (float)i * voicePitch;
                    int sourceIndex = libraryLetterStart + Mathf.FloorToInt(sourcePos);
                    int destIndex = charIndex * outputSamplesPerLetter + i;
                    
                    if (sourceIndex < letterLibraryData.Length && destIndex < audioData.Length)
                    {
                        // Linear interpolation for smoother pitch shifting
                        float sample;
                        int nextSourceIndex = sourceIndex + 1;
                        if (nextSourceIndex < letterLibraryData.Length)
                        {
                            float frac = sourcePos - Mathf.Floor(sourcePos);
                            sample = letterLibraryData[sourceIndex] * (1f - frac) + letterLibraryData[nextSourceIndex] * frac;
                        }
                        else
                        {
                            sample = letterLibraryData[sourceIndex];
                        }
                        
                        // Apply fade in at start of letter
                        if (i < FADE_SAMPLES)
                        {
                            sample *= (float)i / FADE_SAMPLES;
                        }
                        // Apply fade out at end of letter
                        else if (i >= outputSamplesPerLetter - FADE_SAMPLES)
                        {
                            sample *= (float)(outputSamplesPerLetter - i) / FADE_SAMPLES;
                        }
                        
                        audioData[destIndex] = sample;
                    }
                }
            }
            // Non-letter characters = silence (array already 0)
        }

        // Create AudioClip from data
        AudioClip clip = AudioClip.Create("AnimaleSpeech", totalSamples, 1, outputSampleRate, false);
        clip.SetData(audioData, 0);
        
        Debug.Log($"Animalese: Generated clip for {text.Length} chars, duration {clip.length:F2}s, pitch {voicePitch:F2}");
        
        return clip;
    }

    /// <summary>
    /// Generates and plays Animalese speech for the given text.
    /// </summary>
    public void Speak(string text)
    {
        AudioClip clip = GenerateSpeech(text);
        if (clip != null && audioSource != null)
        {
            audioSource.Stop();
            audioSource.clip = clip;
            audioSource.volume = volume;
            audioSource.Play();
        }
    }

    /// <summary>
    /// Generates and plays Animalese speech synced to typewriter timing.
    /// Uses the default pitch setting from the component.
    /// </summary>
    /// <param name="text">The plain text (no rich text tags)</param>
    /// <param name="typewriterSpeed">Seconds per character in the typewriter effect</param>
    public void SpeakWithTypewriterSync(string text, float typewriterSpeed)
    {
        SpeakWithTypewriterSync(text, typewriterSpeed, pitch);
    }
    
    /// <summary>
    /// Generates and plays Animalese speech synced to typewriter timing with custom pitch.
    /// </summary>
    /// <param name="text">The plain text (no rich text tags)</param>
    /// <param name="typewriterSpeed">Seconds per character in the typewriter effect</param>
    /// <param name="voicePitch">Voice pitch (0.5 = deep, 1.0 = normal, 2.0 = high)</param>
    public void SpeakWithTypewriterSync(string text, float typewriterSpeed, float voicePitch)
    {
        if (string.IsNullOrEmpty(text))
        {
            Debug.LogWarning("Animalese: Cannot speak empty text");
            return;
        }
        
        // Apply duration multiplier to stretch the audio
        float adjustedDuration = typewriterSpeed * durationMultiplier;
        
        // Generate audio with duration matching the typewriter speed (with multiplier)
        AudioClip clip = GenerateSpeechWithDurationAndPitch(text, adjustedDuration, voicePitch);
        if (clip == null)
        {
            Debug.LogWarning("Animalese: Failed to generate speech clip");
            return;
        }
        
        if (audioSource == null)
        {
            Debug.LogError("Animalese: AudioSource is null!");
            return;
        }
        
        audioSource.Stop();
        audioSource.clip = clip;
        audioSource.volume = volume;
        audioSource.pitch = 1.0f; // Normal playback - duration is baked into the audio
        audioSource.Play();
        
        Debug.Log($"Animalese: Playing speech for {text.Length} chars, duration {clip.length:F2}s, pitch {voicePitch:F2}, multiplier {durationMultiplier:F2}");
    }

    /// <summary>
    /// Generates and plays Animalese speech with custom pitch.
    /// </summary>
    public void Speak(string text, float customPitch)
    {
        float originalPitch = pitch;
        pitch = customPitch;
        Speak(text);
        pitch = originalPitch;
    }

    /// <summary>
    /// Stops any currently playing speech.
    /// </summary>
    public void StopSpeaking()
    {
        if (audioSource != null)
        {
            audioSource.Stop();
        }
    }

    /// <summary>
    /// Returns true if speech is currently playing.
    /// </summary>
    public bool IsSpeaking()
    {
        return audioSource != null && audioSource.isPlaying;
    }

    /// <summary>
    /// Processes the input text for Animalese generation.
    /// </summary>
    private string ProcessText(string text)
    {
        if (shortenWords)
        {
            // Replace non-letters with spaces, split into words, shorten each word
            string lettersOnly = Regex.Replace(text, @"[^a-zA-Z]", " ");
            string[] words = lettersOnly.Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
            
            List<string> shortenedWords = new List<string>();
            foreach (string word in words)
            {
                shortenedWords.Add(ShortenWord(word));
            }
            
            return string.Join("", shortenedWords);
        }
        else
        {
            // Just remove non-letters
            return Regex.Replace(text, @"[^a-zA-Z ]", "");
        }
    }

    /// <summary>
    /// Shortens a word to its first and last letter.
    /// </summary>
    private string ShortenWord(string word)
    {
        if (string.IsNullOrEmpty(word)) return "";
        if (word.Length <= 1) return word;
        return word[0].ToString() + word[word.Length - 1].ToString();
    }

    // Properties for runtime adjustment
    public float Pitch
    {
        get => pitch;
        set => pitch = Mathf.Clamp(value, 0.5f, 2.0f);
    }

    public bool ShortenWords
    {
        get => shortenWords;
        set => shortenWords = value;
    }

    public float LetterDuration
    {
        get => letterDuration;
        set => letterDuration = Mathf.Clamp(value, 0.05f, 0.2f);
    }
}
