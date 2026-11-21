using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Controls customer order flow: generates orders, sets the stencil guide on the target,
/// and calculates coin rewards when the player submits their carving.
/// </summary>
public class CustomerManager : MonoBehaviour
{
    public static CustomerManager Instance { get; private set; }

    [Header("Dependencies")]
    [SerializeField] private CarveScorer carveScorer;
    [SerializeField] private InventoryManager inventoryManager;
    [SerializeField] private Stacker stacker;

    [Header("Orders")]
    [SerializeField] private List<CustomerOrder> predefinedOrders = new List<CustomerOrder>();
    [SerializeField] private bool loopOrders = true;

    [Header("Current Order State")]
    public CustomerOrder currentOrder;
    public int currentOrderIndex = 0;

    // Events for UI
    public event Action<CustomerOrder> OnNewOrder;
    public event Action<CarveScore, int> OnOrderCompleted; // score, coinsEarned

    private void Awake()
    {
        if (Instance == null) Instance = this;
        else Destroy(gameObject);

        if (!carveScorer) carveScorer = FindObjectOfType<CarveScorer>();
        if (!inventoryManager) inventoryManager = FindObjectOfType<InventoryManager>();
        if (!stacker) stacker = FindObjectOfType<Stacker>();
    }

    private void Start()
    {
        // Load default templates from Resources/Carvings if empty
        if (predefinedOrders.Count == 0)
        {
            GenerateDefaultOrders();
        }

        // When CarveScorer finishes scoring, process reward
        if (carveScorer != null)
        {
            // Ensure devMode is false so templates and guides are used
            carveScorer.devMode = false;
            carveScorer.showGuide = true;
            carveScorer.Scored += HandleScored;
        }
    }

    private void OnDestroy()
    {
        if (carveScorer != null)
        {
            carveScorer.Scored -= HandleScored;
        }
    }

    public void GenerateDefaultOrders()
    {
        TextAsset[] templates = Resources.LoadAll<TextAsset>("Carvings");
        if (templates != null && templates.Length > 0)
        {
            string[] names = { "Spooky Sally", "Jack O'Lantern", "Mayor Bob", "Goth Gary" };
            string[] requests = {
                "Make me a classic spooky smile!",
                "I need something extra terrifying for the kids tonight.",
                "Can you carve this carefully? I'm entering a neighborhood contest.",
                "Give me sharp teeth and angry eyes!"
            };

            for (int i = 0; i < templates.Length; i++)
            {
                predefinedOrders.Add(new CustomerOrder
                {
                    customerName = names[i % names.Length],
                    orderDialogue = requests[i % requests.Length],
                    stencilTemplate = templates[i],
                    baseCoinReward = 50 + (i * 10),
                    perfectBonus = 25
                });
            }
        }
    }

    /// <summary>
    /// Called when a pumpkin arrives on the table to apply the current order's stencil guide.
    /// </summary>
    public void ApplyCurrentOrderStencil()
    {
        if (currentOrder == null)
        {
            NextOrder();
            return;
        }

        if (carveScorer != null && currentOrder.stencilTemplate != null)
        {
            carveScorer.devMode = false;
            carveScorer.showGuide = true;
            carveScorer.SetTemplate(currentOrder.stencilTemplate);
        }

        OnNewOrder?.Invoke(currentOrder);
    }

    public void NextOrder()
    {
        if (predefinedOrders.Count == 0) return;

        if (currentOrderIndex >= predefinedOrders.Count)
        {
            if (loopOrders) currentOrderIndex = 0;
            else return;
        }

        currentOrder = predefinedOrders[currentOrderIndex];
        currentOrderIndex++;

        if (carveScorer != null && currentOrder.stencilTemplate != null)
        {
            carveScorer.devMode = false;
            carveScorer.showGuide = true;
            carveScorer.SetTemplate(currentOrder.stencilTemplate);
        }

        OnNewOrder?.Invoke(currentOrder);
    }

    private void HandleScored(CarveScore score)
    {
        if (currentOrder == null) return;

        int coinsEarned = CalculatePayout(score, currentOrder);
        if (inventoryManager != null)
        {
            inventoryManager.AddCoins(coinsEarned);
        }

        OnOrderCompleted?.Invoke(score, coinsEarned);
        // Do NOT call NextOrder() here immediately.
        // Wait until the player dismisses the results modal ("NEXT CUSTOMER").
    }

    public int CalculatePayout(CarveScore score, CustomerOrder order)
    {
        if (score.accuracy < order.minimumAcceptableAccuracy)
        {
            // Pity payout for low accuracy
            return Mathf.Max(5, Mathf.RoundToInt(order.baseCoinReward * 0.2f));
        }

        // Base reward scaled by accuracy squared for exponential reward on high skill
        float multiplier = Mathf.Clamp01(score.accuracy);
        int payout = Mathf.RoundToInt(order.baseCoinReward * multiplier);

        // Bonus for master carver (>= 90%)
        if (score.accuracy >= 0.90f)
        {
            payout += order.perfectBonus;
        }

        return payout;
    }

    /// <summary>
    /// Call this from a Submit UI button to finalize carving.
    /// </summary>
    public void SubmitCarving()
    {
        if (carveScorer != null)
        {
            carveScorer.Submit();
        }
    }
}
