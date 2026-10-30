using System;
using UnityEngine;

/// <summary>Inventory and purchase operations used by shop UI and tool selection.</summary>
public class InventoryManager : MonoBehaviour
{
    public const int CutToolId = 0;
    public const int ScrapeToolId = 1;
    public const int PaintToolId = 2;

    [Serializable]
    public class Data
    {
        public int coins;
        public int[] unlockedTools;
    }

    [Serializable]
    public class Tools
    {
        public int id;
        public string name;
        public int price;
        public bool isUnlocked;
    }

    public Data data = new Data();
    public Tools[] tools =
    {
        new Tools { id = CutToolId, name = "Carving Tool", isUnlocked = true },
        new Tools { id = ScrapeToolId, name = "Scraper", price = 25 },
        new Tools { id = PaintToolId, name = "Paint Brush", price = 50 }
    };

    public int Coins { get { return data != null ? data.coins : 0; } }

    void Awake()
    {
        if (data == null) data = new Data();
        if (tools == null || tools.Length == 0)
        {
            tools = new[]
            {
                new Tools { id = CutToolId, name = "Carving Tool", isUnlocked = true },
                new Tools { id = ScrapeToolId, name = "Scraper", price = 25 },
                new Tools { id = PaintToolId, name = "Paint Brush", price = 50 }
            };
        }
        SyncUnlockedTools();
    }

    public bool IsToolUnlocked(int toolId)
    {
        Tools tool = FindTool(toolId);
        return tool != null && tool.isUnlocked;
    }

    public int GetToolPrice(int toolId)
    {
        Tools tool = FindTool(toolId);
        return tool != null ? Mathf.Max(0, tool.price) : -1;
    }

    public bool CanPurchase(int toolId)
    {
        Tools tool = FindTool(toolId);
        return tool != null && tool.price >= 0 && !tool.isUnlocked &&
            data != null && data.coins >= tool.price;
    }

    public bool TryPurchase(int toolId)
    {
        Tools tool = FindTool(toolId);
        return tool != null && TryPurchaseTool(tool);
    }

    bool TryPurchaseTool(Tools tool)
    {
        if (tool.price < 0 || tool.isUnlocked || data == null || data.coins < tool.price)
            return false;

        data.coins -= tool.price;
        tool.isUnlocked = true;
        SyncUnlockedTools();
        return true;
    }

    public bool SelectTool(int toolId, MeshHoleCutter cutter)
    {
        if (!cutter || !IsToolUnlocked(toolId)) return false;

        switch (toolId)
        {
            case CutToolId: cutter.SetMode(MeshHoleCutter.Mode.Cut); return true;
            case ScrapeToolId: cutter.SetMode(MeshHoleCutter.Mode.Scrape); return true;
            case PaintToolId: cutter.SetMode(MeshHoleCutter.Mode.Paint); return true;
            default: return false;
        }
    }

    public void AddCoins(int amount)
    {
        if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount));
        if (data == null) data = new Data();
        data.coins = checked(data.coins + amount);
    }

    public bool TrySpendCoins(int amount)
    {
        if (amount < 0 || data == null || data.coins < amount) return false;
        data.coins -= amount;
        return true;
    }

    Tools FindTool(int toolId)
    {
        if (tools == null) return null;
        for (int i = 0; i < tools.Length; i++)
            if (tools[i] != null && tools[i].id == toolId) return tools[i];
        return null;
    }

    void SyncUnlockedTools()
    {
        if (data == null) return;
        int unlockedCount = 0;
        for (int i = 0; tools != null && i < tools.Length; i++)
            if (tools[i] != null && tools[i].isUnlocked) unlockedCount++;

        int[] unlocked = new int[unlockedCount];
        int next = 0;
        for (int i = 0; tools != null && i < tools.Length; i++)
            if (tools[i] != null && tools[i].isUnlocked) unlocked[next++] = tools[i].id;
        data.unlockedTools = unlocked;
    }
}
