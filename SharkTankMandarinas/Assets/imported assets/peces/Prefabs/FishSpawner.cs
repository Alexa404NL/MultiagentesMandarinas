using System.Collections;
using UnityEngine;

public class FishSpawner : MonoBehaviour
{
    public GameObject[] fishes ; 
    public float spawnRadius = 6f;
    public float minSpawnDelay = 0f;
    public float maxSpawnDelay = 0.1f;

 
    void Start()
    {
        StartCoroutine(SpawnFishRoutine());
    }

    IEnumerator SpawnFishRoutine()
    {
        while (true)
        {
            SpawnFish();
            float delay = Random.Range(minSpawnDelay, maxSpawnDelay);
            yield return new WaitForSeconds(delay);
        }
    }

    void SpawnFish()
    {
       Vector3 randomPos3 = transform.position + Random.insideUnitSphere * spawnRadius;
       Vector3 randomPos1 = transform.position + Random.insideUnitSphere * spawnRadius;
       Vector3 randomPos2 = transform.position + Random.insideUnitSphere * spawnRadius;
         
        int fishIndex = Random.Range(0, fishes.Length);
        Instantiate(fishes[fishIndex], randomPos1, Quaternion.identity);
        fishIndex = Random.Range(0, fishes.Length);
        Instantiate(fishes[fishIndex], randomPos2, Quaternion.identity);
        fishIndex = Random.Range(0, fishes.Length);
        Instantiate(fishes[fishIndex], randomPos3, Quaternion.identity);
    }
}