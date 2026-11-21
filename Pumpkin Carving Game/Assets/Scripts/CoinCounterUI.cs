using TMPro;
using UnityEngine;

/// <summary>
/// Simple HUD component that automatically keeps the player's coin counter updated on screen.
/// </summary>
public class CoinCounterUI : MonoBehaviour
{
    [SerializeField] private TextMeshProUGUI coinsText;
    [SerializeField] private InventoryManager inventoryManager;

    private void Start()
    {
        if (!inventoryManager) inventoryManager = FindObjectOfType<InventoryManager>();

        if (inventoryManager != null)
        {
            inventoryManager.OnCoinsChanged += UpdateCoinDisplay;
            UpdateCoinDisplay(inventoryManager.Coins);
        }
        else if (coinsText != null)
        {
            coinsText.text = "0";
        }
    }

    private void OnDestroy()
    {
        if (inventoryManager != null)
        {
            inventoryManager.OnCoinsChanged -= UpdateCoinDisplay;
        }
    }

    private void UpdateCoinDisplay(int newCoins)
    {
        if (coinsText != null)
        {
            coinsText.text = $"{newCoins}";
        }
    }
}
