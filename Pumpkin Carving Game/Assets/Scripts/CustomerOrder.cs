using System;
using UnityEngine;

[System.Serializable]
public class CustomerOrder
{
    public string customerName = "Halloween Fan";
    [TextArea(2, 4)]
    public string orderDialogue = "Can you carve this spooky face for my porch?";
    
    [Tooltip("The template JSON to carve. Stored in Resources/Carvings")]
    public TextAsset stencilTemplate;
    
    [Header("Rewards")]
    public int baseCoinReward = 50;
    public int perfectBonus = 25; // Bonus for >= 90% accuracy
    public float minimumAcceptableAccuracy = 0.4f; // Below 40% customer pays reduced price
}
