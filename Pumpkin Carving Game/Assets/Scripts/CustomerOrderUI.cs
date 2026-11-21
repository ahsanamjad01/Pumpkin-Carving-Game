using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Simple testing UI for customer orders, submit button, and score/reward display.
/// </summary>
public class CustomerOrderUI : MonoBehaviour
{
    [Header("Order Card Root (Hide during review)")]
    [SerializeField] private GameObject orderPanel;

    [Header("Order Info Display")]
    [SerializeField] private TextMeshProUGUI customerNameText;
    [SerializeField] private TextMeshProUGUI orderDialogueText;
    [SerializeField] private TextMeshProUGUI coinRewardText;

    [Header("Buttons")]
    [SerializeField] private Button submitButton;

    [Header("Feedback Modal / Banner")]
    [SerializeField] private GameObject resultPanel;
    [SerializeField] private TextMeshProUGUI resultTitleText;
    [SerializeField] private TextMeshProUGUI resultScoreText;
    [SerializeField] private TextMeshProUGUI resultCoinsText;
    [SerializeField] private Button nextCustomerButton;

    private void Start()
    {
        if (CustomerManager.Instance != null)
        {
            CustomerManager.Instance.OnNewOrder += UpdateOrderDisplay;
            CustomerManager.Instance.OnOrderCompleted += ShowResultModal;
            if (CustomerManager.Instance.currentOrder != null)
            {
                UpdateOrderDisplay(CustomerManager.Instance.currentOrder);
            }
        }

        if (submitButton != null)
        {
            submitButton.onClick.AddListener(OnSubmitClicked);
        }

        if (nextCustomerButton != null)
        {
            nextCustomerButton.onClick.AddListener(OnNextCustomerClicked);
        }

        if (resultPanel != null)
        {
            resultPanel.SetActive(false);
        }
    }

    private void OnDestroy()
    {
        if (CustomerManager.Instance != null)
        {
            CustomerManager.Instance.OnNewOrder -= UpdateOrderDisplay;
            CustomerManager.Instance.OnOrderCompleted -= ShowResultModal;
        }
    }

    private void UpdateOrderDisplay(CustomerOrder order)
    {
        if (order == null) return;
        if (customerNameText) customerNameText.text = order.customerName;
        if (orderDialogueText) orderDialogueText.text = $"\"{order.orderDialogue}\"";
        if (coinRewardText) coinRewardText.text = $"Reward: {order.baseCoinReward} Coins";
    }

    private void OnSubmitClicked()
    {
        if (CustomerManager.Instance != null)
        {
            CustomerManager.Instance.SubmitCarving();
        }
    }

    private void ShowResultModal(CarveScore score, int coinsEarned)
    {
        if (orderPanel != null) orderPanel.SetActive(false);
        if (resultPanel == null) return;

        resultPanel.SetActive(true);
        int percent = Mathf.RoundToInt(score.accuracy * 100f);

        if (resultScoreText) resultScoreText.text = $"ACCURACY: {percent}%";
        if (resultCoinsText) resultCoinsText.text = $"+{coinsEarned} COINS";

        if (resultTitleText)
        {
            if (percent >= 90) resultTitleText.text = "MASTERPIECE";
            else if (percent >= 70) resultTitleText.text = "GREAT JOB";
            else if (percent >= 40) resultTitleText.text = "SATISFACTORY";
            else resultTitleText.text = "CUSTOMER UNHAPPY";
        }
    }

    private void OnNextCustomerClicked()
    {
        if (resultPanel != null)
        {
            resultPanel.SetActive(false);
        }

        if (orderPanel != null)
        {
            orderPanel.SetActive(true);
        }

        // Advance to the next customer only after the player clicks Next Customer
        if (CustomerManager.Instance != null)
        {
            CustomerManager.Instance.NextOrder();
        }
    }
}
