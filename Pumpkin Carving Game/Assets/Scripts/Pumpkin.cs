
using UnityEngine;

public class Pumpkin : MonoBehaviour
{
    private Stacker stacker;

    public void Initialize(Stacker _stacker)
    {
        stacker = _stacker;
    }

    public void RemoveFromStack()
    {
        if (stacker != null)
            stacker.RemoveObject(gameObject);
    }
}
