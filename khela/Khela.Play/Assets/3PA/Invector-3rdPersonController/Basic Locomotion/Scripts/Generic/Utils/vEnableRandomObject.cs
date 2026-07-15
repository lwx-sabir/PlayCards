using UnityEngine;

public class vEnableRandomObject : MonoBehaviour
{
    public GameObject[] objects;
    public bool enableOnStart;

    System.Random random;

    protected void Awake()
    {
        random = new System.Random();
        if (enableOnStart)
            EnableObject();
    }

    public virtual void EnableObject()
    {
        if (objects == null || objects.Length == 0)
            return;

        int indexToEnable = random.Next(0, objects.Length);
        for (int i = 0; i < objects.Length; i++)
        {
            objects[i].SetActive(i == indexToEnable);
        }
    }

}
