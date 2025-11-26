using UnityEngine;

public class fishaviour : MonoBehaviour
{
    public float minSpeed = 3f, maxSpeed = 8f;      
    private float actualSpeed;   
    public float lifetime = 8f;

    void Start()
    {
        actualSpeed = Random.Range(minSpeed, maxSpeed);
        Destroy(gameObject, lifetime);
    }

    void Update()
    {
        transform.Translate(-Vector3.right * actualSpeed * Time.deltaTime, Space.World);
        transform.rotation = Quaternion.LookRotation(Vector3.right);
    }
}