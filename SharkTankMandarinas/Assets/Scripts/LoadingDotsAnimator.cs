using UnityEngine;
using TMPro;
using System.Collections;

/// <summary>
/// Animates loading text with blinking dots (e.g., "Loading", "Loading.", "Loading..", "Loading...")
/// Attach this to a GameObject with a TMP_Text component.
/// </summary>
public class LoadingDotsAnimator : MonoBehaviour
{
    [Header("Settings")]
    [SerializeField, Tooltip("Base text without dots")]
    private string baseText = "Loading";
    
    [SerializeField, Tooltip("Time between dot changes in seconds")]
    private float dotInterval = 0.4f;
    
    [SerializeField, Tooltip("Maximum number of dots")]
    private int maxDots = 3;

    [Header("References")]
    [SerializeField, Tooltip("Text component to animate (auto-detected if not set)")]
    private TMP_Text textComponent;

    private Coroutine animationCoroutine;
    private int currentDots = 0;

    private void Awake()
    {
        // Auto-detect text component if not assigned
        if (textComponent == null)
        {
            textComponent = GetComponent<TMP_Text>();
        }
    }

    private void OnEnable()
    {
        // Start animation when enabled
        StartAnimation();
    }

    private void OnDisable()
    {
        // Stop animation when disabled
        StopAnimation();
    }

    /// <summary>
    /// Starts the dot animation.
    /// </summary>
    public void StartAnimation()
    {
        if (textComponent == null)
        {
            Debug.LogWarning("LoadingDotsAnimator: No TMP_Text component found!");
            return;
        }

        StopAnimation();
        animationCoroutine = StartCoroutine(AnimateDots());
    }

    /// <summary>
    /// Stops the dot animation.
    /// </summary>
    public void StopAnimation()
    {
        if (animationCoroutine != null)
        {
            StopCoroutine(animationCoroutine);
            animationCoroutine = null;
        }
    }

    private IEnumerator AnimateDots()
    {
        while (true)
        {
            // Build the text with current number of dots
            string dots = new string('.', currentDots);
            textComponent.text = baseText + dots;

            // Wait for interval
            yield return new WaitForSeconds(dotInterval);

            // Cycle through 0, 1, 2, 3 dots
            currentDots = (currentDots + 1) % (maxDots + 1);
        }
    }

    /// <summary>
    /// Sets the base text (without dots).
    /// </summary>
    public void SetBaseText(string text)
    {
        baseText = text;
    }
}
